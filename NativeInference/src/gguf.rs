//! Bounded GGUF v3 reader. Model weights stay on disk until explicitly requested.
use crate::{Result, quant::TensorType};
use serde::Serialize;
use serde_json::Value;
use std::{
    collections::BTreeMap,
    fs::File,
    io::{BufReader, Read, Seek, SeekFrom},
    path::{Path, PathBuf},
};

const MAX_STRING_BYTES: u64 = 16 * 1024 * 1024;
const MAX_ARRAY_ITEMS: u64 = 4 * 1024 * 1024;

#[derive(Debug, Clone, Serialize)]
pub struct Tensor {
    pub name: String,
    /// GGUF stores the contiguous dimension first (opposite of Rust row-major shape).
    pub dimensions: Vec<u64>,
    pub kind: TensorType,
    pub offset: u64,
    pub bytes: u64,
}

#[derive(Debug)]
pub struct Gguf {
    pub path: PathBuf,
    pub metadata: BTreeMap<String, Value>,
    pub tensors: BTreeMap<String, Tensor>,
    pub data_offset: u64,
}

impl Gguf {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref().to_owned();
        let file = File::open(&path)?;
        let file_size = file.metadata()?.len();
        let mut reader = Reader(BufReader::new(file));
        let mut magic = [0; 4];
        reader.0.read_exact(&mut magic)?;
        if &magic != b"GGUF" || reader.u32()? != 3 {
            return Err("Expected a little-endian GGUF v3 file".into());
        }
        let tensor_count = reader.u64()?;
        let metadata_count = reader.u64()?;
        if tensor_count > 100_000 || metadata_count > 100_000 {
            return Err("Unreasonable GGUF entry count".into());
        }
        let mut metadata = BTreeMap::new();
        for _ in 0..metadata_count {
            let key = reader.string()?;
            let tag = reader.u32()?;
            let value = reader.value(tag, 0)?;
            if metadata.insert(key, value).is_some() {
                return Err("Duplicate GGUF metadata key".into());
            }
        }
        let mut tensors = BTreeMap::new();
        for _ in 0..tensor_count {
            let name = reader.string()?;
            let rank = reader.u32()?;
            if !(1..=4).contains(&rank) {
                return Err(format!("Invalid rank {rank} for {name}").into());
            }
            let dimensions = (0..rank)
                .map(|_| reader.u64())
                .collect::<Result<Vec<_>>>()?;
            let kind = TensorType::try_from(reader.u32()?)?;
            let offset = reader.u64()?;
            let elements = dimensions
                .iter()
                .try_fold(1u64, |n, &d| n.checked_mul(d))
                .ok_or("Tensor size overflow")?;
            if dimensions.contains(&0) || dimensions[0] % kind.block_elements() != 0 {
                return Err(format!("Invalid block shape for {name}").into());
            }
            let bytes = (elements / kind.block_elements())
                .checked_mul(kind.block_bytes())
                .ok_or("Tensor byte size overflow")?;
            let tensor = Tensor {
                name: name.clone(),
                dimensions,
                kind,
                offset,
                bytes,
            };
            if tensors.insert(name, tensor).is_some() {
                return Err("Duplicate GGUF tensor name".into());
            }
        }
        let alignment = metadata
            .get("general.alignment")
            .and_then(Value::as_u64)
            .unwrap_or(32);
        if alignment == 0 || !alignment.is_power_of_two() || alignment > 65536 {
            return Err("Invalid GGUF alignment".into());
        }
        let position = reader.0.stream_position()?;
        let data_offset = position
            .checked_add(alignment - 1)
            .ok_or("Offset overflow")?
            & !(alignment - 1);
        let mut ranges = Vec::new();
        for tensor in tensors.values() {
            let start = data_offset
                .checked_add(tensor.offset)
                .ok_or("Offset overflow")?;
            let end = start.checked_add(tensor.bytes).ok_or("Offset overflow")?;
            if tensor.offset % alignment != 0 || end > file_size {
                return Err(format!("Tensor {} exceeds file or is misaligned", tensor.name).into());
            }
            ranges.push((start, end));
        }
        ranges.sort_unstable();
        if ranges.windows(2).any(|w| w[0].1 > w[1].0) {
            return Err("Overlapping GGUF tensors".into());
        }
        Ok(Self {
            path,
            metadata,
            tensors,
            data_offset,
        })
    }

    pub fn string(&self, key: &str) -> Result<&str> {
        self.metadata
            .get(key)
            .and_then(Value::as_str)
            .ok_or_else(|| format!("Missing string metadata: {key}").into())
    }
    pub fn integer(&self, key: &str) -> Result<usize> {
        self.metadata
            .get(key)
            .and_then(Value::as_u64)
            .and_then(|v| usize::try_from(v).ok())
            .ok_or_else(|| format!("Missing integer metadata: {key}").into())
    }
    pub fn number(&self, key: &str) -> Result<f32> {
        self.metadata
            .get(key)
            .and_then(Value::as_f64)
            .map(|v| v as f32)
            .ok_or_else(|| format!("Missing numeric metadata: {key}").into())
    }
    pub fn tensor(&self, name: &str) -> Result<&Tensor> {
        self.tensors
            .get(name)
            .ok_or_else(|| format!("Missing tensor: {name}").into())
    }
    pub fn read_tensor(&self, tensor: &Tensor) -> Result<Vec<u8>> {
        let mut file = File::open(&self.path)?;
        let offset = self
            .data_offset
            .checked_add(tensor.offset)
            .ok_or("Tensor offset overflow")?;
        let end = offset
            .checked_add(tensor.bytes)
            .ok_or("Tensor size overflow")?;
        if end > file.metadata()?.len() {
            return Err("Tensor exceeds the current file size".into());
        }
        file.seek(SeekFrom::Start(offset))?;
        let mut bytes = vec![0; usize::try_from(tensor.bytes)?];
        file.read_exact(&mut bytes)?;
        Ok(bytes)
    }
}

struct Reader(BufReader<File>);
impl Reader {
    fn bytes<const N: usize>(&mut self) -> Result<[u8; N]> {
        let mut b = [0; N];
        self.0.read_exact(&mut b)?;
        Ok(b)
    }
    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.bytes()?))
    }
    fn u64(&mut self) -> Result<u64> {
        Ok(u64::from_le_bytes(self.bytes()?))
    }
    fn string(&mut self) -> Result<String> {
        let length = self.u64()?;
        if length > MAX_STRING_BYTES {
            return Err("GGUF string exceeds reader limit".into());
        }
        let mut bytes = vec![0; length as usize];
        self.0.read_exact(&mut bytes)?;
        Ok(String::from_utf8(bytes)?)
    }
    fn value(&mut self, tag: u32, depth: u8) -> Result<Value> {
        Ok(match tag {
            0 => Value::from(self.bytes::<1>()?[0]),
            1 => Value::from(self.bytes::<1>()?[0] as i8),
            2 => Value::from(u16::from_le_bytes(self.bytes()?)),
            3 => Value::from(i16::from_le_bytes(self.bytes()?)),
            4 => Value::from(self.u32()?),
            5 => Value::from(i32::from_le_bytes(self.bytes()?)),
            6 => {
                let n = f32::from_le_bytes(self.bytes()?);
                if !n.is_finite() {
                    return Err("Non-finite metadata".into());
                }
                Value::from(n)
            }
            7 => match self.bytes::<1>()?[0] {
                0 => Value::Bool(false),
                1 => Value::Bool(true),
                _ => return Err("Invalid GGUF boolean".into()),
            },
            8 => Value::from(self.string()?),
            9 => {
                if depth != 0 {
                    return Err("Nested GGUF arrays are unsupported".into());
                }
                let item_tag = self.u32()?;
                let length = self.u64()?;
                if length > MAX_ARRAY_ITEMS {
                    return Err("GGUF array exceeds reader limit".into());
                }
                Value::Array(
                    (0..length)
                        .map(|_| self.value(item_tag, depth + 1))
                        .collect::<Result<_>>()?,
                )
            }
            10 => Value::from(self.u64()?),
            11 => Value::from(i64::from_le_bytes(self.bytes()?)),
            12 => {
                let n = f64::from_le_bytes(self.bytes()?);
                if !n.is_finite() {
                    return Err("Non-finite metadata".into());
                }
                Value::from(n)
            }
            _ => return Err(format!("Unknown GGUF metadata type: {tag}").into()),
        })
    }
}
