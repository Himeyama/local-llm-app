//! A tiny, deterministic three-layer GGUF for GPU model-state tests. It exercises
//! the production decoder without loading another 27B model beside the GUI.
use local_rocm::{Result, gguf::Gguf};
use serde_json::{Value, json};
use std::{
    fs,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};

pub struct ToyModel(PathBuf);
impl Drop for ToyModel {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
impl ToyModel {
    pub fn create() -> Result<Self> {
        let metadata = json!({
            "general.architecture":"qwen35","qwen35.embedding_length":256,
            "qwen35.block_count":3,"qwen35.context_length":512,"qwen35.nextn_predict_layers":0,
            "qwen35.attention.head_count":1,"qwen35.attention.head_count_kv":1,
            "qwen35.attention.key_length":256,"qwen35.rope.dimension_count":64,
            "qwen35.rope.freq_base":10000.0,"qwen35.attention.layer_norm_rms_epsilon":0.000001,
            "qwen35.ssm.group_count":1,"qwen35.ssm.time_step_rank":1,
            "qwen35.ssm.state_size":128,"qwen35.ssm.conv_kernel":4
        });
        let mut tensors = Vec::<(String, Vec<u64>, Vec<f32>)>::new();
        let mut add = |name: String, shape: Vec<u64>| {
            let count = shape.iter().product::<u64>() as usize;
            let phase = tensors.len() as f32 * 0.19;
            let values = (0..count)
                .map(|i| {
                    if name.contains("norm.weight") {
                        1.0
                    } else if name.ends_with("ssm_a") {
                        -0.4
                    } else if name.ends_with("ssm_dt.bias") {
                        0.1
                    } else if name.ends_with("ssm_conv1d.weight") {
                        [0.1, 0.2, 0.3, 0.4][i % 4]
                    } else {
                        (i as f32 * 0.073 + phase).sin() * 0.03
                    }
                })
                .collect();
            tensors.push((name, shape, values));
        };
        add("token_embd.weight".into(), vec![256, 8]);
        add("output.weight".into(), vec![256, 8]);
        add("output_norm.weight".into(), vec![256]);
        for layer in 0..3 {
            let p = format!("blk.{layer}");
            for name in ["attn_norm.weight", "post_attention_norm.weight"] {
                add(format!("{p}.{name}"), vec![256]);
            }
            for name in ["ffn_gate.weight", "ffn_up.weight", "ffn_down.weight"] {
                add(format!("{p}.{name}"), vec![256, 256]);
            }
            if layer != 1 {
                for (name, shape) in [
                    ("attn_qkv.weight", vec![256, 384]),
                    ("attn_gate.weight", vec![256, 128]),
                    ("ssm_alpha.weight", vec![256, 1]),
                    ("ssm_beta.weight", vec![256, 1]),
                    ("ssm_conv1d.weight", vec![4, 384]),
                    ("ssm_a", vec![1]),
                    ("ssm_dt.bias", vec![1]),
                    ("ssm_norm.weight", vec![128]),
                    ("ssm_out.weight", vec![128, 256]),
                ] {
                    add(format!("{p}.{name}"), shape);
                }
            } else {
                for (name, shape) in [
                    ("attn_q.weight", vec![256, 512]),
                    ("attn_k.weight", vec![256, 256]),
                    ("attn_v.weight", vec![256, 256]),
                    ("attn_output.weight", vec![256, 256]),
                    ("attn_q_norm.weight", vec![256]),
                    ("attn_k_norm.weight", vec![256]),
                ] {
                    add(format!("{p}.{name}"), shape);
                }
            }
        }
        let fields = metadata.as_object().unwrap();
        let mut bytes = b"GGUF".to_vec();
        bytes.extend(3u32.to_le_bytes());
        bytes.extend((tensors.len() as u64).to_le_bytes());
        bytes.extend((fields.len() as u64).to_le_bytes());
        for (key, value) in fields {
            string(&mut bytes, key);
            match value {
                Value::String(value) => {
                    bytes.extend(8u32.to_le_bytes());
                    string(&mut bytes, value);
                }
                Value::Number(n) if n.is_u64() => {
                    bytes.extend(4u32.to_le_bytes());
                    bytes.extend((n.as_u64().unwrap() as u32).to_le_bytes());
                }
                Value::Number(n) => {
                    bytes.extend(6u32.to_le_bytes());
                    bytes.extend((n.as_f64().unwrap() as f32).to_le_bytes());
                }
                _ => unreachable!(),
            }
        }
        let mut payload = Vec::new();
        for (name, shape, values) in tensors {
            payload.resize(payload.len().div_ceil(32) * 32, 0);
            string(&mut bytes, &name);
            bytes.extend((shape.len() as u32).to_le_bytes());
            for dimension in shape {
                bytes.extend(dimension.to_le_bytes());
            }
            bytes.extend(0u32.to_le_bytes());
            bytes.extend((payload.len() as u64).to_le_bytes());
            for value in values {
                payload.extend(value.to_le_bytes());
            }
        }
        bytes.resize(bytes.len().div_ceil(32) * 32, 0);
        bytes.extend(payload);
        let path = std::env::temp_dir().join(format!(
            "native-toy-{}-{}.gguf",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos()
        ));
        fs::write(&path, bytes)?;
        Ok(Self(path))
    }
    pub fn open(&self) -> Result<Gguf> {
        Gguf::open(&self.0)
    }
}
fn string(bytes: &mut Vec<u8>, value: &str) {
    bytes.extend((value.len() as u64).to_le_bytes());
    bytes.extend(value.as_bytes());
}
