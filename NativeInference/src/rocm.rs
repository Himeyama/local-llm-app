//! Minimal dynamically loaded HIP boundary. No ggml, llama.cpp, or PyTorch code.
//!
//! Libraries outlive every allocation through `Rc`. Buffers are deliberately not
//! Send/Sync: a GPU context belongs to the inference worker that created it.
use crate::{Result, quant::TensorType};
use std::{
    cell::RefCell,
    collections::BTreeMap,
    ffi::{CStr, CString, c_void},
    path::Path,
    ptr,
    rc::Rc,
};
mod blas;
mod memory;
mod ops;
mod profile;
pub use ops::{AttentionShape, DeltaShape};

type Handle = *mut c_void;
// rocmrc owns the ROCm ABI declarations. libloading is used only to load
// explicit SDK files with Windows dependency-search flags before crate dispatch.
pub(super) use rocmrc::hip::sys as hip;
struct Library {
    _handle: libloading::Library,
}
fn sdk_path(path: &Path) -> Result<std::path::PathBuf> {
    let canonical = path.canonicalize()?;
    let text = canonical.to_str().ok_or("ROCm SDK path must be Unicode")?;
    // AMD libraries append forward-slash relative asset paths to their DLL path.
    // The Windows extended-length prefix disables slash normalization, so keep
    // ordinary absolute paths when loading these DLLs and their kernel assets.
    if let Some(unc) = text.strip_prefix(r"\\?\UNC\") {
        return Ok(format!(r"\\{unc}").into());
    }
    Ok(text.strip_prefix(r"\\?\").unwrap_or(text).into())
}
impl Library {
    fn open(path: &Path) -> Result<Self> {
        let path = sdk_path(path)?;
        let handle =
            unsafe { libloading::os::windows::Library::load_with_flags(&path, 0x100 | 0x800) }
                .map_err(|error| format!("Cannot load {}: {error}", path.display()))?;
        Ok(Self {
            _handle: handle.into(),
        })
    }
}
struct Api {
    _runtime: Library,
    _compiler: Library,
    _builtins: Library,
    _kpack: Option<Library>,
}
impl Api {
    fn check(&self, code: hip::hipError_t, operation: &str) -> Result<()> {
        if code == hip::hipError_t::hipSuccess {
            return Ok(());
        }
        let reason = unsafe { CStr::from_ptr(hip::hipGetErrorString(code)) }.to_string_lossy();
        Err(format!("{operation}: {reason} ({code:?})").into())
    }
}

pub struct Buffer {
    pointer: Handle,
    bytes: usize,
    api: Rc<Api>,
    pool: Rc<memory::Pool>,
    capacity: usize,
}
impl Buffer {
    pub fn len_bytes(&self) -> usize {
        self.bytes
    }
    pub fn read_f32(&self) -> Result<Vec<f32>> {
        if self.bytes % 4 != 0 {
            return Err("Buffer is not an f32 vector".into());
        }
        let mut values = vec![0.0f32; self.bytes / 4];
        self.api.check(
            unsafe {
                hip::hipMemcpy(
                    values.as_mut_ptr().cast(),
                    self.pointer,
                    self.bytes,
                    hip::hipMemcpyKind::hipMemcpyDeviceToHost,
                )
            },
            "hipMemcpy device-to-host",
        )?;
        Ok(values)
    }
}
impl Drop for Buffer {
    fn drop(&mut self) {
        self.pool.release(self.pointer, self.capacity);
    }
}

pub struct Gpu {
    // Drop rocBLAS before its HIP library/context.
    blas: Option<blas::Blas>,
    api: Rc<Api>,
    pool: Rc<memory::Pool>,
    module: hip::hipModule_t,
    kernels: BTreeMap<&'static str, hip::hipFunction_t>,
    pub name: String,
    decoded_workspace: RefCell<Option<Buffer>>,
    profile: RefCell<Option<profile::Profile>>,
    half_prefill: bool,
    decode_attention_tiling: bool,
}
impl Gpu {
    /// `sdk_bin` must contain a matching HIP runtime and HIPRTC distribution.
    pub fn open(sdk_bin: &Path, device: i32) -> Result<Self> {
        let sdk_bin = sdk_path(sdk_bin)?;
        let blas_directory = std::env::var_os("LOCAL_ROCM_BLAS_BIN")
            .map(std::path::PathBuf::from)
            .or_else(|| {
                sdk_bin
                    .join("rocblas.dll")
                    .is_file()
                    .then(|| sdk_bin.clone())
            })
            .or_else(|| {
                sdk_bin
                    .parent()
                    .map(|root| root.join("blas/bin"))
                    .filter(|bin| bin.join("rocblas.dll").is_file())
            });
        let mut directories = vec![sdk_bin.clone()];
        if let Some(directory) = &blas_directory {
            directories.push(sdk_path(directory)?);
        }
        rocmrc::configure_library_directories(directories)?;
        let runtime = Library::open(&sdk_bin.join("amdhip64_7.dll"))?;
        let compiler = Library::open(&sdk_bin.join("hiprtc0702.dll"))?;
        let builtins = Library::open(&sdk_bin.join("hiprtc-builtins0702.dll"))?;
        let kpack = if sdk_bin.join("rocm_kpack.dll").is_file() {
            Some(Library::open(&sdk_bin.join("rocm_kpack.dll"))?)
        } else {
            None
        };
        let api = Rc::new(Api {
            _runtime: runtime,
            _compiler: compiler,
            _builtins: builtins,
            _kpack: kpack,
        });
        api.check(unsafe { hip::hipSetDevice(device) }, "hipSetDevice")?;
        let mut name = [0i8; 256];
        api.check(
            unsafe { hip::hipDeviceGetName(name.as_mut_ptr(), name.len() as i32, device) },
            "hipDeviceGetName",
        )?;
        let name = unsafe { CStr::from_ptr(name.as_ptr()) }
            .to_string_lossy()
            .into_owned();
        let code = compile(include_str!("kernels.hip"))?;
        let mut module = ptr::null_mut();
        api.check(
            unsafe { hip::hipModuleLoadData(&mut module, code.as_ptr().cast()) },
            "hipModuleLoadData",
        )?;
        let pool = Rc::new(memory::Pool::new(api.clone()));
        let mut gpu = Self {
            blas: None,
            api,
            pool,
            module,
            kernels: BTreeMap::new(),
            name,
            decoded_workspace: RefCell::new(None),
            profile: RefCell::new(None),
            half_prefill: std::env::var_os("LOCAL_ROCM_PREFILL_F32")
                .is_none_or(|value| value != "1"),
            decode_attention_tiling: std::env::var_os("LOCAL_ROCM_TILED_ATTENTION")
                .is_none_or(|value| value != "0"),
        };
        if let Some(directory) = blas_directory {
            gpu.blas = Some(blas::Blas::open(&directory)?);
        }
        for name in [
            "decode",
            "decode_half",
            "float_to_half",
            "matvec",
            "quantized_matvec",
            "matmul",
            "rms_norm",
            "add",
            "swiglu",
            "embedding",
            "embeddings",
            "conv_step",
            "delta_step",
            "gated_norm",
            "prepare_qk",
            "store_kv",
            "attention_scores",
            "attention_output",
            "attention_softmax",
            "attention_value_tiles",
            "attention_reduce_tiles",
        ] {
            let c_name = CString::new(name)?;
            let mut function = ptr::null_mut();
            gpu.api.check(
                unsafe { hip::hipModuleGetFunction(&mut function, module, c_name.as_ptr()) },
                name,
            )?;
            gpu.kernels.insert(name, function);
        }
        Ok(gpu)
    }
    pub fn allocate(&self, bytes: usize) -> Result<Buffer> {
        let buffer = self.output(bytes)?;
        self.api.check(
            unsafe { hip::hipMemset(buffer.pointer, 0, bytes) },
            "hipMemset",
        )?;
        Ok(buffer)
    }
    /// Only use for results whose kernels/copies overwrite every logical byte.
    fn output(&self, bytes: usize) -> Result<Buffer> {
        if bytes == 0 {
            return Err("Cannot allocate an empty GPU buffer".into());
        }
        let (pointer, capacity) = self.pool.acquire(bytes)?;
        let buffer = Buffer {
            pointer,
            bytes,
            api: self.api.clone(),
            pool: self.pool.clone(),
            capacity,
        };
        Ok(buffer)
    }
    pub fn allocation_stats(&self) -> (u64, u64, usize) {
        self.pool.statistics()
    }
    pub fn prefill_batch_size(&self) -> usize {
        if self.blas.is_some() { 256 } else { 8 }
    }
    pub fn decode_attention_mode(&self) -> &'static str {
        if self.decode_attention_tiling {
            "tiled FP32 (128-token tiles)"
        } else {
            "serial FP32"
        }
    }
    pub fn prefill_uses_half(&self, kind: TensorType) -> bool {
        self.blas.is_some()
            && self.half_prefill
            && !matches!(kind, TensorType::F32 | TensorType::Bf16)
    }
    pub fn copy(&self, source: &Buffer, target: &Buffer) -> Result<()> {
        self.own(source)?;
        self.own(target)?;
        if source.bytes != target.bytes {
            return Err("Device copy shape mismatch".into());
        }
        if source.pointer == target.pointer {
            return Ok(());
        }
        self.api.check(
            unsafe {
                hip::hipMemcpy(
                    target.pointer,
                    source.pointer,
                    source.bytes,
                    hip::hipMemcpyKind::hipMemcpyDeviceToDevice,
                )
            },
            "hipMemcpy device-to-device",
        )
    }
    pub fn upload(&self, bytes: &[u8]) -> Result<Buffer> {
        let buffer = self.output(bytes.len())?;
        self.api.check(
            unsafe {
                hip::hipMemcpy(
                    buffer.pointer,
                    bytes.as_ptr().cast(),
                    bytes.len(),
                    hip::hipMemcpyKind::hipMemcpyHostToDevice,
                )
            },
            "hipMemcpy host-to-device",
        )?;
        Ok(buffer)
    }
    pub fn upload_f32(&self, values: &[f32]) -> Result<Buffer> {
        // f32 is plain data without padding; the slice remains borrowed for the synchronous copy.
        self.upload(unsafe {
            std::slice::from_raw_parts(values.as_ptr().cast(), std::mem::size_of_val(values))
        })
    }
    pub fn synchronize(&self) -> Result<()> {
        self.api.check(
            unsafe { hip::hipDeviceSynchronize() },
            "hipDeviceSynchronize",
        )
    }
    pub fn memory_info(&self) -> Result<(usize, usize)> {
        let mut free = 0;
        let mut total = 0;
        self.api.check(
            unsafe { hip::hipMemGetInfo(&mut free, &mut total) },
            "hipMemGetInfo",
        )?;
        Ok((free, total))
    }
    pub fn begin_profile(&self) {
        *self.profile.borrow_mut() = Some(profile::Profile::default());
    }
    pub fn end_profile(&self) -> Result<BTreeMap<String, (usize, f64)>> {
        self.synchronize()?;
        self.profile
            .borrow_mut()
            .take()
            .ok_or("GPU profiling is not active")?
            .summary()
    }
    fn measured<T>(&self, name: &str, operation: impl FnOnce() -> Result<T>) -> Result<T> {
        let timer = if self.profile.borrow().is_some() {
            Some(profile::Timer::start(self.api.clone())?)
        } else {
            None
        };
        let result = operation()?;
        if let Some(timer) = timer {
            timer.stop()?;
            self.profile
                .borrow_mut()
                .as_mut()
                .ok_or("Missing GPU profile")?
                .0
                .push((name.to_owned(), timer));
        }
        Ok(result)
    }
    fn launch(&self, name: &str, blocks: u32, threads: u32, args: &mut [Handle]) -> Result<()> {
        let function = *self.kernels.get(name).ok_or("Unknown kernel")?;
        self.measured(name, || {
            self.api.check(
                unsafe {
                    hip::hipModuleLaunchKernel(
                        function,
                        blocks,
                        1,
                        1,
                        threads,
                        1,
                        1,
                        0,
                        ptr::null_mut(),
                        args.as_mut_ptr(),
                        ptr::null_mut(),
                    )
                },
                name,
            )
        })
    }
    fn own(&self, buffer: &Buffer) -> Result<()> {
        if !Rc::ptr_eq(&self.api, &buffer.api) {
            return Err("Buffer belongs to a different GPU context".into());
        }
        Ok(())
    }
    pub fn decode(&self, source: &Buffer, kind: TensorType) -> Result<Buffer> {
        self.own(source)?;
        if source.bytes % kind.block_bytes() as usize != 0 {
            return Err("Truncated GPU quantization block".into());
        }
        let n = source.bytes / kind.block_bytes() as usize * kind.block_elements() as usize;
        let result = self.output(n.checked_mul(4).ok_or("GPU buffer overflow")?)?;
        let mut input = source.pointer;
        let mut output = result.pointer;
        let mut tag = kind as u32;
        let mut count = u32::try_from(n)?;
        self.launch(
            "decode",
            count.div_ceil(256),
            256,
            &mut [
                arg(&mut input),
                arg(&mut output),
                arg(&mut tag),
                arg(&mut count),
            ],
        )?;
        Ok(result)
    }
    pub fn matvec(
        &self,
        weights: &Buffer,
        kind: TensorType,
        input: &Buffer,
        rows: usize,
        columns: usize,
    ) -> Result<Buffer> {
        self.own(weights)?;
        self.own(input)?;
        if columns == 0 || rows == 0 || columns % kind.block_elements() as usize != 0 {
            return Err("Invalid matvec dimensions".into());
        }
        let expected = rows
            .checked_mul(columns / kind.block_elements() as usize)
            .and_then(|v| v.checked_mul(kind.block_bytes() as usize))
            .ok_or("Matvec shape overflow")?;
        if weights.bytes != expected || input.bytes != columns * 4 {
            return Err("Matvec buffer shape mismatch".into());
        }
        let result = self.output(rows * 4)?;
        if kind == TensorType::F32
            && let Some(blas) = &self.blas
        {
            self.measured("rocblas_gemv", || {
                blas.matvec(weights, input, &result, rows, columns)
            })?;
            return Ok(result);
        }
        let mut w = weights.pointer;
        let mut x = input.pointer;
        let mut y = result.pointer;
        let mut tag = kind as u32;
        let mut cols = u32::try_from(columns)?;
        let quantized = matches!(kind, TensorType::Q4K | TensorType::Q5K | TensorType::Q6K);
        self.launch(
            if quantized {
                "quantized_matvec"
            } else {
                "matvec"
            },
            u32::try_from(rows)?,
            if quantized { 128 } else { 256 },
            &mut [
                arg(&mut w),
                arg(&mut x),
                arg(&mut y),
                arg(&mut tag),
                arg(&mut cols),
            ],
        )?;
        Ok(result)
    }
    pub fn matmul(
        &self,
        weights: &Buffer,
        kind: TensorType,
        input: &Buffer,
        rows: usize,
        columns: usize,
        batch: usize,
    ) -> Result<Buffer> {
        self.own(weights)?;
        self.own(input)?;
        if !(1..=self.prefill_batch_size()).contains(&batch)
            || rows == 0
            || columns == 0
            || columns % kind.block_elements() as usize != 0
        {
            return Err("Invalid batched projection shape".into());
        }
        let bytes = rows
            .checked_mul(columns / kind.block_elements() as usize)
            .and_then(|n| n.checked_mul(kind.block_bytes() as usize))
            .ok_or("Matrix shape overflow")?;
        if weights.bytes != bytes
            || input.bytes
                != batch
                    .checked_mul(columns)
                    .and_then(|n| n.checked_mul(4))
                    .ok_or("Input shape overflow")?
        {
            return Err("Batched projection buffer mismatch".into());
        }
        let result = self.output(
            batch
                .checked_mul(rows)
                .and_then(|n| n.checked_mul(4))
                .ok_or("Output shape overflow")?,
        )?;
        if let Some(blas) = &self.blas {
            if kind == TensorType::F32 {
                self.measured("rocblas_gemm", || {
                    blas.matmul(weights, input, &result, rows, columns, batch)
                })?;
            } else {
                // Packed weights remain resident. Reuse a single decoded matrix;
                // FP16 operands enable matrix units with FP32 accumulation.
                let half = self.prefill_uses_half(kind);
                let bytes = rows
                    .checked_mul(columns)
                    .and_then(|n| n.checked_mul(if half { 2 } else { 4 }))
                    .ok_or("Decoded matrix overflow")?;
                let mut workspace = self.decoded_workspace.borrow_mut();
                if workspace.as_ref().is_none_or(|b| b.bytes < bytes) {
                    *workspace = Some(self.output(bytes)?);
                }
                let decoded = workspace.as_ref().ok_or("Missing decoded workspace")?;
                let mut w = weights.pointer;
                let mut y = decoded.pointer;
                let mut tag = kind as u32;
                let mut count = u32::try_from(rows * columns)?;
                self.launch(
                    if half { "decode_half" } else { "decode" },
                    count.div_ceil(if half { 2048 } else { 256 }),
                    256,
                    &mut [arg(&mut w), arg(&mut y), arg(&mut tag), arg(&mut count)],
                )?;
                if half {
                    let half_input = self.output(batch * columns * 2)?;
                    let mut x = input.pointer;
                    let mut converted = half_input.pointer;
                    let mut n = u32::try_from(batch * columns)?;
                    self.launch(
                        "float_to_half",
                        n.div_ceil(256),
                        256,
                        &mut [arg(&mut x), arg(&mut converted), arg(&mut n)],
                    )?;
                    self.measured("rocblas_gemm_half", || {
                        blas.matmul_half(decoded, &half_input, &result, rows, columns, batch)
                    })?;
                } else {
                    self.measured("rocblas_gemm", || {
                        blas.matmul(decoded, input, &result, rows, columns, batch)
                    })?;
                }
            }
            return Ok(result);
        }
        let mut w = weights.pointer;
        let mut x = input.pointer;
        let mut y = result.pointer;
        let mut tag = kind as u32;
        let mut cols = u32::try_from(columns)?;
        let mut count = u32::try_from(batch)?;
        self.launch(
            "matmul",
            u32::try_from(rows)?,
            256,
            &mut [
                arg(&mut w),
                arg(&mut x),
                arg(&mut y),
                arg(&mut tag),
                arg(&mut cols),
                arg(&mut count),
            ],
        )?;
        Ok(result)
    }
    pub fn row(&self, source: &Buffer, width: usize, index: usize) -> Result<Buffer> {
        self.own(source)?;
        let bytes = width
            .checked_mul(4)
            .filter(|&n| n > 0)
            .ok_or("Invalid row width")?;
        let offset = index.checked_mul(bytes).ok_or("Row offset overflow")?;
        if offset
            .checked_add(bytes)
            .is_none_or(|end| end > source.bytes)
        {
            return Err("Row exceeds buffer".into());
        }
        let result = self.output(bytes)?;
        // Bounds checked above; HIP device pointers are used only by the HIP API.
        let pointer = source.pointer.cast::<u8>().wrapping_add(offset);
        self.api.check(
            unsafe {
                hip::hipMemcpy(
                    result.pointer,
                    pointer.cast(),
                    bytes,
                    hip::hipMemcpyKind::hipMemcpyDeviceToDevice,
                )
            },
            "hipMemcpy device row",
        )?;
        Ok(result)
    }
}
impl Drop for Gpu {
    fn drop(&mut self) {
        let _ = self.synchronize();
        unsafe {
            hip::hipModuleUnload(self.module);
        }
    }
}

/// Kernel argument arrays contain addresses of argument storage, not argument values.
fn arg<T>(value: &mut T) -> Handle {
    (value as *mut T).cast()
}

fn compile(source: &str) -> Result<Vec<u8>> {
    use rocmrc::hiprtc::{result, sys};
    struct Program(sys::hiprtcProgram);
    impl Drop for Program {
        fn drop(&mut self) {
            let _ = result::destroy_program(self.0);
        }
    }
    let source = CString::new(source)?;
    let program = Program(result::create_program(&source, Some(c"local_rocm.hip"))?);
    if let Err(error) = result::compile_program(program.0, &["-std=c++17"]) {
        let log = result::get_program_log(program.0)?
            .into_iter()
            .map(|c| c as u8)
            .collect::<Vec<_>>();
        return Err(format!("HIPRTC {error}: {}", String::from_utf8_lossy(&log)).into());
    }
    Ok(result::get_hsaco(program.0)?
        .into_iter()
        .map(|c| c as u8)
        .collect())
}
