//! Storage layouts and CPU reference decoding for GGUF quantization blocks.
//! These functions are also the numerical oracle for the independent HIP kernels.
use crate::Result;
use half::{bf16, f16};
use serde::Serialize;

#[derive(Debug, Copy, Clone, PartialEq, Eq, Serialize)]
#[repr(u32)]
pub enum TensorType {
    F32 = 0,
    F16 = 1,
    Q8_0 = 8,
    Q4K = 12,
    Q5K = 13,
    Q6K = 14,
    Bf16 = 30,
}
impl TryFrom<u32> for TensorType {
    type Error = Box<dyn std::error::Error + Send + Sync>;
    fn try_from(value: u32) -> Result<Self> {
        Ok(match value {
            0 => Self::F32,
            1 => Self::F16,
            8 => Self::Q8_0,
            12 => Self::Q4K,
            13 => Self::Q5K,
            14 => Self::Q6K,
            30 => Self::Bf16,
            _ => {
                return Err(format!(
                    "Unsupported GGUF tensor type {value}; refusing to reinterpret its bytes"
                )
                .into());
            }
        })
    }
}
impl TensorType {
    pub const fn block_elements(self) -> u64 {
        match self {
            Self::Q8_0 => 32,
            Self::Q4K | Self::Q5K | Self::Q6K => 256,
            _ => 1,
        }
    }
    pub const fn block_bytes(self) -> u64 {
        match self {
            Self::F32 => 4,
            Self::F16 | Self::Bf16 => 2,
            Self::Q8_0 => 34,
            Self::Q4K => 144,
            Self::Q5K => 176,
            Self::Q6K => 210,
        }
    }
    pub fn decode(self, bytes: &[u8]) -> Result<Vec<f32>> {
        if bytes.len() % self.block_bytes() as usize != 0 {
            return Err("Truncated quantization block".into());
        }
        Ok(bytes
            .chunks_exact(self.block_bytes() as usize)
            .flat_map(|block| {
                (0..self.block_elements() as usize).map(move |i| self.element(block, i))
            })
            .collect())
    }
    fn element(self, b: &[u8], i: usize) -> f32 {
        let h = |offset| f16::from_le_bytes([b[offset], b[offset + 1]]).to_f32();
        match self {
            Self::F32 => f32::from_le_bytes(b.try_into().unwrap()),
            Self::F16 => h(0),
            Self::Bf16 => bf16::from_le_bytes([b[0], b[1]]).to_f32(),
            Self::Q8_0 => h(0) * b[2 + i] as i8 as f32,
            Self::Q4K | Self::Q5K => {
                let group = i / 32;
                let scales = &b[4..16];
                let (scale, min) = if group < 4 {
                    (scales[group] & 63, scales[group + 4] & 63)
                } else {
                    (
                        (scales[group + 4] & 15) | ((scales[group - 4] >> 6) << 4),
                        (scales[group + 4] >> 4) | ((scales[group] >> 6) << 4),
                    )
                };
                let q_offset = if self == Self::Q5K { 48 } else { 16 };
                let packed = b[q_offset + (i / 64) * 32 + i % 32];
                let mut q = if i % 64 < 32 {
                    packed & 15
                } else {
                    packed >> 4
                };
                if self == Self::Q5K {
                    q |= ((b[16 + i % 32] >> group) & 1) << 4;
                }
                h(0) * scale as f32 * q as f32 - h(2) * min as f32
            }
            Self::Q6K => {
                let half = i / 128;
                let lane = i % 32;
                let quarter = (i % 128) / 32;
                let low_byte = b[half * 64 + lane + (quarter % 2) * 32];
                let low = if quarter < 2 {
                    low_byte & 15
                } else {
                    low_byte >> 4
                };
                let high = (b[128 + half * 32 + lane] >> (quarter * 2)) & 3;
                let q = (low | (high << 4)) as i32 - 32;
                h(208) * b[192 + i / 16] as i8 as f32 * q as f32
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn q8_signed_values_and_scale() {
        let mut b = vec![0; 34];
        b[..2].copy_from_slice(&f16::from_f32(0.5).to_le_bytes());
        b[2] = 128;
        b[3] = 127;
        let values = TensorType::Q8_0.decode(&b).unwrap();
        assert_eq!(&values[..3], &[-64.0, 63.5, 0.0]);
        assert!(TensorType::Q8_0.decode(&b[..33]).is_err());
    }
    #[test]
    fn k_blocks_zero_and_scale_groups() {
        for kind in [TensorType::Q4K, TensorType::Q5K, TensorType::Q6K] {
            let mut b = vec![0; kind.block_bytes() as usize];
            assert!(kind.decode(&b).unwrap().iter().all(|&v| v == 0.0));
            if kind != TensorType::Q6K {
                b[..2].copy_from_slice(&f16::ONE.to_le_bytes());
                b[4] = 2;
                b[if kind == TensorType::Q5K { 48 } else { 16 }] = 3;
                assert_eq!(kind.decode(&b).unwrap()[0], 6.0);
            }
        }
    }
}
