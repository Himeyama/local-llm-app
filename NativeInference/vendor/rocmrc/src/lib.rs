//! Minimal safe HIP/ROCm bindings, modeled after `cudarc`.

pub mod hip;
pub mod hiprtc;

#[cfg(feature = "hipblaslt")]
pub mod hipblaslt;
#[cfg(feature = "rocblas")]
pub mod rocblas;

pub use hip::{
    DevicePtr, DevicePtrMut, DeviceRepr, DeviceSlice, EventWaitFlags, ExternalMemory, HipContext,
    HipError, HipEvent, HipFunction, HipGraph, HipModule, HipSlice, HipStream, HipUnifiedSlice,
    HipUnifiedView, HipUnifiedViewMut, HipView, HipViewMut, HostSlice, LaunchArgs, LaunchConfig,
    MappedBuffer, MemAttachFlags, PinnedHostSlice, Profiler, PushKernelArg, StreamKind, SyncOnDrop,
    ValidAsZeroBits, profiler_start, profiler_stop,
};
pub use hiprtc::{HiprtcError, Hsaco};

#[cfg(feature = "rocblas")]
pub use rocblas::{
    Asum, AsumConfig, Axpy, AxpyConfig, Copy, CopyConfig, Dot, DotConfig, Gemm, GemmConfig, Gemv,
    GemvConfig, Nrm2, Nrm2Config, Operation, RocBlas, RocblasError, Scal, ScalConfig,
    StridedBatchedConfig,
};

#[cfg(feature = "hipblaslt")]
pub use hipblaslt::{
    Activation, HipBlasLT, HipblasError, Matmul, MatmulConfig, MatmulShared, Workspace,
};

#[cfg(feature = "dynamic-loading")]
static LIBRARY_DIRECTORIES: std::sync::OnceLock<Vec<std::path::PathBuf>> = std::sync::OnceLock::new();

/// Windows integration: configure explicit SDK directories before the first HIP
/// call. The upstream loaders cache library handles, so changing SDKs afterwards
/// is intentionally rejected. The generated ABI bindings are unchanged.
#[cfg(feature = "dynamic-loading")]
pub fn configure_library_directories(directories: Vec<std::path::PathBuf>) -> Result<(), &'static str> {
    if let Some(existing) = LIBRARY_DIRECTORIES.get() {
        return if existing == &directories { Ok(()) } else { Err("ROCm SDK directories already configured") };
    }
    LIBRARY_DIRECTORIES.set(directories).map_err(|_| "ROCm SDK initialization raced")
}

#[cfg(feature = "dynamic-loading")]
pub fn get_lib_name_candidates(lib_name: &str) -> Vec<String> {
    use std::env::consts::{DLL_PREFIX, DLL_SUFFIX};

    let major = env!("ROCM_MAJOR_VERSION");
    let minor = env!("ROCM_MINOR_VERSION");

    #[cfg(windows)]
    if let Some(directories) = LIBRARY_DIRECTORIES.get() {
        let mut names = Vec::new();
        if lib_name == "amdhip64" { names.push(format!("amdhip64_{major}.dll")); }
        if lib_name == "hiprtc" { names.push(format!("hiprtc{major:0>2}{minor:0>2}.dll")); }
        names.push(format!("{lib_name}.dll"));
        return directories.iter().flat_map(|directory| names.iter().map(move |name| directory.join(name).to_string_lossy().into_owned())).collect();
    }

    let rocm_path = std::env::var("ROCM_PATH").unwrap_or_else(|_| "/opt/rocm".to_string());

    let stems = [
        // Bare soname; resolved via LD_LIBRARY_PATH / ldconfig.
        format!("{DLL_PREFIX}{lib_name}{DLL_SUFFIX}"),
        // Major-versioned soname (e.g. librocblas.so.4).
        format!("{DLL_PREFIX}{lib_name}{DLL_SUFFIX}.{major}"),
        // Common alternative majors to try as fallbacks.
        format!("{DLL_PREFIX}{lib_name}{DLL_SUFFIX}.0"),
        format!("{DLL_PREFIX}{lib_name}{DLL_SUFFIX}.1"),
        // Exact patch soname.
        format!("{DLL_PREFIX}{lib_name}{DLL_SUFFIX}.{major}.{minor}"),
    ];

    let mut out = Vec::with_capacity(stems.len() * 2);
    for stem in &stems {
        out.push(stem.clone()); // search via loader
        out.push(format!("{rocm_path}/lib/{stem}")); // explicit ROCm install
    }
    out
}

#[cfg(feature = "dynamic-loading")]
pub fn panic_no_lib_found(name: &str, tried: &[String]) -> ! {
    panic!(
        "could not load dynamic library {name}; tried {} candidates: {tried:?}",
        tried.len()
    );
}
