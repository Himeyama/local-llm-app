use local_rocm::{Result, gguf::Gguf};
use serde_json::json;

fn main() {
    if let Err(error) = run() {
        eprintln!("local-rocm: {error}");
        std::process::exit(1);
    }
}
fn run() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();
    match args.get(1).map(String::as_str) {
        #[cfg(windows)]
        Some("model-bench") => {
            local_rocm::benchmarks::model_run(
                std::path::Path::new(args.get(2).ok_or(
                    "Usage: local-rocm model-bench MODEL.gguf ROCM_BIN [DECODE_EVALUATIONS]",
                )?),
                std::path::Path::new(args.get(3).ok_or("Missing ROCm bin directory")?),
                args.get(4)
                    .map(|value| value.parse())
                    .transpose()?
                    .unwrap_or(50),
            )?
        }
        #[cfg(windows)]
        Some("prefill-bench") => local_rocm::benchmarks::prefill_projection(std::path::Path::new(
            args.get(2)
                .ok_or("Usage: local-rocm prefill-bench ROCM_BIN")?,
        ))?,
        #[cfg(windows)]
        Some("gpu-bench") => local_rocm::benchmarks::gpu_loop(std::path::Path::new(
            args.get(2).ok_or("Usage: local-rocm gpu-bench ROCM_BIN")?,
        ))?,
        #[cfg(windows)]
        Some("serve") => {
            let path = args
                .get(2)
                .ok_or("Usage: local-rocm serve MODEL.gguf ROCM_BIN [CONTEXT] [HOST] [PORT]")?;
            let sdk = args.get(3).ok_or("Missing ROCm bin directory")?;
            let capacity = args.get(4).map(|v| v.parse()).transpose()?.unwrap_or(65536);
            let host = args.get(5).map(String::as_str).unwrap_or("127.0.0.1");
            let port = args.get(6).map(|v| v.parse()).transpose()?.unwrap_or(9931);
            local_rocm::server::serve(
                std::path::Path::new(path),
                std::path::Path::new(sdk),
                host,
                port,
                capacity,
            )?;
        }
        #[cfg(windows)]
        Some("gpu-check") => {
            let directory = args.get(2).ok_or("Usage: local-rocm gpu-check ROCM_BIN")?;
            let gpu = local_rocm::rocm::Gpu::open(std::path::Path::new(directory), 0)?;
            let (free, total) = gpu.memory_info()?;
            println!(
                "VRAM: {:.1} GiB free / {:.1} GiB total",
                free as f64 / 1073741824.0,
                total as f64 / 1073741824.0
            );
            for kind in [
                local_rocm::quant::TensorType::F32,
                local_rocm::quant::TensorType::F16,
                local_rocm::quant::TensorType::Bf16,
                local_rocm::quant::TensorType::Q8_0,
                local_rocm::quant::TensorType::Q4K,
                local_rocm::quant::TensorType::Q5K,
                local_rocm::quant::TensorType::Q6K,
            ] {
                let bytes: Vec<u8> = (0..kind.block_bytes() * 2)
                    .map(|i| ((i * 37 + 11) % 127) as u8)
                    .collect();
                let expected = kind.decode(&bytes)?;
                let packed = gpu.upload(&bytes)?;
                let decoded = gpu.decode(&packed, kind)?.read_f32()?;
                for (i, (&a, &b)) in expected.iter().zip(&decoded).enumerate() {
                    if (a - b).abs() > 0.0001 * a.abs().max(1.0) {
                        return Err(format!("{kind:?} decode[{i}]: CPU {a}, HIP {b}").into());
                    }
                }
                let columns = expected.len();
                let x: Vec<f32> = (0..columns).map(|i| (i as f32 * 0.13).sin()).collect();
                let reference: f32 = expected.iter().zip(&x).map(|(a, b)| a * b).sum();
                let dot = gpu
                    .matvec(&packed, kind, &gpu.upload_f32(&x)?, 1, columns)?
                    .read_f32()?[0];
                if (reference - dot).abs()
                    > 0.002
                        * expected
                            .iter()
                            .zip(&x)
                            .map(|(a, b)| (a * b).abs())
                            .sum::<f32>()
                            .max(1.0)
                {
                    return Err(format!("{kind:?} matvec: CPU {reference}, HIP {dot}").into());
                }
                println!("PASS {kind:?}: decode and matvec");
            }
            gpu.synchronize()?;
            println!("PASS HIPRTC / HIP on {}", gpu.name);
        }
        Some("inspect") => {
            let model = Gguf::open(args.get(2).ok_or("Usage: local-rocm inspect MODEL.gguf")?)?;
            let metadata = model
                .metadata
                .iter()
                .filter(|(key, _)| !key.starts_with("tokenizer."))
                .collect::<std::collections::BTreeMap<_, _>>();
            println!(
                "{}",
                serde_json::to_string_pretty(
                    &json!({"metadata": metadata, "tensors": model.tensors.values().collect::<Vec<_>>(), "data_offset": model.data_offset})
                )?
            );
        }
        Some("tokenize") => {
            let file = Gguf::open(
                args.get(2)
                    .ok_or("Usage: local-rocm tokenize MODEL.gguf TEXT")?,
            )?;
            let tokenizer = local_rocm::tokenizer::Tokenizer::from_gguf(&file)?;
            let text = args.get(3).ok_or("Missing text")?;
            let ids = tokenizer.encode(text)?;
            if tokenizer.decode_bytes(&ids)? != text.as_bytes() {
                return Err("Tokenizer roundtrip failed".into());
            }
            println!("{}", serde_json::to_string(&ids)?);
        }
        #[cfg(windows)]
        Some("generate") => {
            let file = Gguf::open(
                args.get(2)
                    .ok_or("Usage: local-rocm generate MODEL.gguf ROCM_BIN PROMPT [MAX_TOKENS]")?,
            )?;
            let sdk = args.get(3).ok_or("Missing ROCm bin directory")?;
            let prompt = args.get(4).ok_or("Missing prompt")?;
            let max_tokens: usize = args.get(5).map(|s| s.parse()).transpose()?.unwrap_or(32);
            if !(1..=4096).contains(&max_tokens) {
                return Err("MAX_TOKENS must be 1..4096".into());
            }
            let tokenizer = local_rocm::tokenizer::Tokenizer::from_gguf(&file)?;
            let text = format!(
                "<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"
            );
            let ids = tokenizer.encode(&text)?;
            let mut model = local_rocm::model::Model::load(
                &file,
                std::path::Path::new(sdk),
                ids.len() + max_tokens,
            )?;
            let started = std::time::Instant::now();
            let mut logits = None;
            for (i, &id) in ids.iter().enumerate() {
                logits = model.forward(id, i + 1 == ids.len())?;
            }
            eprintln!(
                "prefill: {} tokens in {:.2}s",
                ids.len(),
                started.elapsed().as_secs_f64()
            );
            let mut output = Vec::new();
            for i in 0..max_tokens {
                let scores = logits.take().ok_or("Missing logits")?;
                let token = scores
                    .iter()
                    .enumerate()
                    .max_by(|a, b| a.1.total_cmp(b.1))
                    .ok_or("Empty logits")?
                    .0 as u32;
                if token == tokenizer.eos || tokenizer.token_text(token)? == "<|im_end|>" {
                    break;
                }
                output.push(token);
                eprintln!("token {i}: {}", tokenizer.token_text(token)?);
                if i + 1 < max_tokens {
                    logits = model.forward(token, true)?;
                }
            }
            println!(
                "{}",
                String::from_utf8_lossy(&tokenizer.decode_bytes(&output)?)
            );
            eprintln!("total: {:.2}s", started.elapsed().as_secs_f64());
        }
        _ => println!(
            "local-rocm: inspect | tokenize | gpu-check | gpu-bench | prefill-bench | model-bench | generate | serve\nSee NativeInference/README.md for arguments and supported capabilities."
        ),
    }
    Ok(())
}
