use local_rocm::{Result, gguf::Gguf};
use std::{
    fs,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};
struct Fixture(PathBuf);
impl Fixture {
    fn new(bytes: &[u8]) -> Self {
        let path = std::env::temp_dir().join(format!(
            "native-gguf-{}-{}.gguf",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::write(&path, bytes).unwrap();
        Self(path)
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
fn string(bytes: &mut Vec<u8>, value: &str) {
    bytes.extend((value.len() as u64).to_le_bytes());
    bytes.extend(value.as_bytes());
}
fn valid() -> Vec<u8> {
    let mut bytes = b"GGUF".to_vec();
    bytes.extend(3u32.to_le_bytes());
    bytes.extend(1u64.to_le_bytes());
    bytes.extend(1u64.to_le_bytes());
    string(&mut bytes, "general.architecture");
    bytes.extend(8u32.to_le_bytes());
    string(&mut bytes, "fixture");
    string(&mut bytes, "vector");
    bytes.extend(1u32.to_le_bytes());
    bytes.extend(4u64.to_le_bytes());
    bytes.extend(0u32.to_le_bytes());
    bytes.extend(0u64.to_le_bytes());
    bytes.resize(bytes.len().div_ceil(32) * 32, 0);
    for value in [1.0f32, 2.0, 3.0, 4.0] {
        bytes.extend(value.to_le_bytes());
    }
    bytes
}
#[test]
fn reads_tensor_directory_and_exact_payload() -> Result<()> {
    let fixture = Fixture::new(&valid());
    let model = Gguf::open(&fixture.0)?;
    assert_eq!(model.string("general.architecture")?, "fixture");
    let tensor = model.tensor("vector")?;
    assert_eq!(tensor.dimensions, vec![4]);
    assert_eq!(
        tensor.kind.decode(&model.read_tensor(tensor)?)?,
        vec![1.0, 2.0, 3.0, 4.0]
    );
    Ok(())
}
#[test]
fn rejects_truncation_wrong_version_and_unbounded_counts() {
    let good = valid();
    for end in [0, 4, 20, good.len() - 1] {
        let fixture = Fixture::new(&good[..end]);
        assert!(Gguf::open(&fixture.0).is_err());
    }
    for (offset, value) in [(4, 4u64), (8, 100_001u64), (16, 100_001u64)] {
        let mut bytes = good.clone();
        if offset == 4 {
            bytes[4..8].copy_from_slice(&(value as u32).to_le_bytes());
        } else {
            bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
        }
        let fixture = Fixture::new(&bytes);
        assert!(Gguf::open(&fixture.0).is_err());
    }
}
