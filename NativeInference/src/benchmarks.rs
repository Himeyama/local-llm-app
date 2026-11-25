//! Small GPU pipeline benchmark; does not load or contact a model server.
use crate::{Result, quant::TensorType, rocm::Gpu};
use std::{path::Path, time::Instant};

pub fn gpu_loop(sdk: &Path) -> Result<()> {
    let gpu = Gpu::open(sdk, 0)?;
    let width = 2048;
    let weights: Vec<f32> = (0..width * width)
        .map(|i| (i as f32 * 0.017).sin() * 0.001)
        .collect();
    let weights = gpu.upload_f32(&weights)?;
    let norm = gpu.upload_f32(&vec![1.0; width])?;
    for trial in 0..3 {
        let mut input = gpu.upload_f32(&vec![0.5; width])?;
        gpu.synchronize()?;
        let start = Instant::now();
        for _ in 0..120 {
            let projected = gpu.matvec(&weights, TensorType::F32, &input, width, width)?;
            let normalized = gpu.norm(&projected, &norm, 1e-6)?;
            input = gpu.add(&normalized, &input)?;
        }
        let result = input.read_f32()?;
        if result.iter().any(|v| !v.is_finite()) {
            return Err("Non-finite benchmark output".into());
        }
        println!(
            "trial {}: 120 matrix/norm/add iterations in {:.3} ms; checksum {:.6}",
            trial + 1,
            start.elapsed().as_secs_f64() * 1000.0,
            result.iter().map(|&x| x as f64).sum::<f64>()
        );
    }
    println!(
        "allocation statistics (new, reused, cached bytes): {:?}",
        gpu.allocation_stats()
    );
    Ok(())
}

/// A projection with the real model's FFN shape. Compare eight separate GEMVs
/// with one eight-token quantized GEMM; this is not end-to-end model throughput.
pub fn prefill_projection(sdk: &Path) -> Result<()> {
    let gpu = Gpu::open(sdk, 0)?;
    let columns = 5120;
    let rows = 17408;
    let batch = 8;
    let kind = TensorType::Q4K;
    let mut bytes: Vec<u8> = (0..rows * columns / 256 * 144)
        .map(|i| ((i * 37 + 11) % 256) as u8)
        .collect();
    for block in bytes.chunks_exact_mut(144) {
        block[..2].copy_from_slice(&half::f16::from_f32(0.02).to_le_bytes());
        block[2..4].copy_from_slice(&half::f16::from_f32(0.03).to_le_bytes());
    }
    let weights = gpu.upload(&bytes)?;
    let input: Vec<f32> = (0..columns * batch)
        .map(|i| (i as f32 * 0.031).cos())
        .collect();
    let vectors = input
        .chunks_exact(columns)
        .map(|values| gpu.upload_f32(values))
        .collect::<Result<Vec<_>>>()?;
    let packed = gpu.upload_f32(&input)?;
    for trial in 0..3 {
        gpu.synchronize()?;
        let start = Instant::now();
        let mut separate = Vec::new();
        for _ in 0..4 {
            separate = vectors
                .iter()
                .map(|x| gpu.matvec(&weights, kind, x, rows, columns))
                .collect::<Result<Vec<_>>>()?;
        }
        gpu.synchronize()?;
        let scalar_ms = start.elapsed().as_secs_f64() * 1000.0;
        let start = Instant::now();
        let mut combined = None;
        for _ in 0..4 {
            combined = Some(gpu.matmul(&weights, kind, &packed, rows, columns, batch)?);
        }
        gpu.synchronize()?;
        let batched_ms = start.elapsed().as_secs_f64() * 1000.0;
        let reference = separate
            .iter()
            .map(|x| x.read_f32())
            .collect::<Result<Vec<_>>>()?
            .concat();
        let actual = combined.unwrap().read_f32()?;
        if reference
            .iter()
            .zip(&actual)
            .any(|(&a, &b)| (a - b).abs() > 0.001 * a.abs().max(1.0))
        {
            return Err("Batched projection differs from separate vectors".into());
        }
        println!(
            "trial {}: 32 token projections / separate {:.3} ms / batched {:.3} ms / {:.2}x; values match",
            trial + 1,
            scalar_ms,
            batched_ms,
            scalar_ms / batched_ms
        );
    }
    Ok(())
}

/// Fixed whole-model workload for before/after comparisons, including a real
/// 131072-token KV allocation, a stable checkpoint and configurable decode count.
pub fn model_run(path: &Path, sdk: &Path, decode_evaluations: usize) -> Result<()> {
    if !(1..=4096).contains(&decode_evaluations) {
        return Err("Decode evaluations must be 1..4096".into());
    }
    use crate::{gguf::Gguf, model::Model, tokenizer::Tokenizer};
    let file = Gguf::open(path)?;
    let tokenizer = Tokenizer::from_gguf(&file)?;
    let prompt_tokens = std::env::var("LOCAL_ROCM_BENCH_PROMPT_TOKENS")
        .ok()
        .map(|s| s.parse::<usize>())
        .transpose()?
        .unwrap_or(1356);
    let profile_at = std::env::var("LOCAL_ROCM_BENCH_PROFILE_AT")
        .ok()
        .map(|s| s.parse::<usize>())
        .transpose()?
        .unwrap_or(1);
    if !(3..=131_000).contains(&prompt_tokens) || profile_at >= prompt_tokens {
        return Err("Invalid benchmark prompt length or profile position".into());
    }
    let mut tokens = tokenizer.encode(
        &"This is a fixed performance measurement of the local inference engine. "
            .repeat(prompt_tokens / 4 + 1),
    )?;
    tokens.truncate(prompt_tokens);
    if tokens.len() != prompt_tokens {
        return Err("Benchmark prompt is too short".into());
    }
    let mut model = Model::load(&file, sdk, 131072)?;
    let profile = std::env::var_os("LOCAL_ROCM_PROFILE").is_some();
    let started = Instant::now();
    let mut cursor = 0;
    let mut scores = None;
    let mut prefill_profiled = false;
    while cursor < tokens.len() {
        let checkpoint_at = prompt_tokens - 2;
        let boundary = if cursor < checkpoint_at {
            checkpoint_at
        } else {
            tokens.len()
        };
        let end = (cursor + model.prefill_batch_size()).min(boundary);
        let profile_batch = profile && cursor >= profile_at && !prefill_profiled;
        if profile_batch {
            eprintln!("GPU profile position {cursor}, batch {}", end - cursor);
            prefill_profiled = true;
            model.begin_profile();
        }
        scores = model.forward_tokens(&tokens[cursor..end], end == tokens.len())?;
        if profile_batch {
            eprintln!(
                "GPU profile prefill (calls, ms): {}",
                serde_json::to_string(&model.end_profile()?)?
            );
        }
        cursor = end;
        if cursor == checkpoint_at {
            model.save_checkpoint()?;
        }
        if cursor % 1024 == 0 {
            eprintln!(
                "benchmark prefill: {cursor}/{prompt_tokens} ({:.2}s)",
                started.elapsed().as_secs_f64()
            );
        }
    }
    let prefill_seconds = started.elapsed().as_secs_f64();
    let decode_start = Instant::now();
    let mut generated = Vec::new();
    for step in 0..decode_evaluations {
        let logits = scores.take().ok_or("Missing benchmark logits")?;
        let token = logits
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.total_cmp(b.1))
            .ok_or("Empty logits")?
            .0 as u32;
        generated.push(token);
        if profile && step == 0 {
            model.begin_profile();
        }
        scores = model.forward(token, true)?;
        if profile && step == 0 {
            eprintln!(
                "GPU profile decode (calls, ms): {}",
                serde_json::to_string(&model.end_profile()?)?
            );
        }
        if (step + 1) % 200 == 0 {
            eprintln!(
                "benchmark decode: {}/{decode_evaluations} ({:.2}s)",
                step + 1,
                decode_start.elapsed().as_secs_f64()
            );
        }
    }
    let decode_seconds = decode_start.elapsed().as_secs_f64();
    println!(
        "{}",
        serde_json::to_string_pretty(
            &serde_json::json!({"prompt_tokens":prompt_tokens,"prefill_seconds":prefill_seconds,"decode_evaluations":decode_evaluations,"decode_seconds":decode_seconds,"total_seconds":started.elapsed().as_secs_f64(),"allocation_statistics":model.allocation_stats(),"generated_ids":generated})
        )?
    );
    Ok(())
}
