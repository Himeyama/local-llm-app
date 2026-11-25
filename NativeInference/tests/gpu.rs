//! Explicit GPU tests: cargo test --test gpu -- --ignored
//! Set LOCAL_ROCM_BIN to the matching ROCm SDK bin directory first.
#![cfg(windows)]
use local_rocm::{
    Result,
    rocm::{AttentionShape, DeltaShape, Gpu},
};
mod support;

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; long synthetic KV"]
fn long_batched_attention_matches_cpu_for_grouped_heads_and_mask() -> Result<()> {
    let gpu = gpu()?;
    let shape = AttentionShape {
        heads: 4,
        kv_heads: 2,
        head_dim: 256,
        rotary_dim: 64,
        rope_base: 10000.0,
        eps: 1e-6,
    };
    let position = 8193;
    let batch = 3;
    let capacity = position + batch;
    let key_value = |p: usize, h: usize| {
        half::f16::from_f32(((p % 13) as f32 - 6.0) * 0.02 + h as f32 * 0.03).to_f32()
    };
    let val_value = |p: usize, h: usize| {
        half::f16::from_f32(((p % 17) as f32 - 8.0) * 0.03 + h as f32 * 0.05).to_f32()
    };
    let mut key_bytes = Vec::with_capacity(capacity * 512 * 2);
    let mut value_bytes = Vec::with_capacity(capacity * 512 * 2);
    for p in 0..capacity {
        for h in 0..2 {
            for _ in 0..256 {
                key_bytes.extend_from_slice(
                    &half::f16::from_f32(key_value(p, h)).to_bits().to_le_bytes(),
                );
                value_bytes.extend_from_slice(
                    &half::f16::from_f32(val_value(p, h)).to_bits().to_le_bytes(),
                );
            }
        }
    }
    let keys = gpu.upload(&key_bytes)?;
    let values = gpu.upload(&value_bytes)?;
    let q_values: Vec<f32> = (0..batch)
        .flat_map(|_| (0..4).flat_map(|h| vec![0.08 + h as f32 * 0.03; 256]))
        .collect();
    let q = gpu.upload_f32(&q_values)?;
    let gate = gpu.upload_f32(&vec![0.0; batch * 4 * 256])?;
    let k: Vec<f32> = (position..capacity)
        .flat_map(|p| (0..2).flat_map(move |h| vec![key_value(p, h); 256]))
        .collect();
    let v: Vec<f32> = (position..capacity)
        .flat_map(|p| (0..2).flat_map(move |h| vec![val_value(p, h); 256]))
        .collect();
    let output = gpu
        .attention(
            &q,
            &gate,
            &gpu.upload_f32(&k)?,
            &gpu.upload_f32(&v)?,
            &keys,
            &values,
            position,
            capacity,
            shape,
        )?
        .read_f32()?;
    for row in 0..batch {
        for head in 0..4 {
            let kh = head / 2;
            let q = (0.08 + head as f32 * 0.03) as f64;
            let mut sum = 0.0;
            let mut weighted = 0.0;
            for p in 0..=position + row {
                let weight = (16.0 * q * key_value(p, kh) as f64).exp();
                sum += weight;
                weighted += weight * val_value(p, kh) as f64;
            }
            let expected = 0.5 * weighted / sum;
            assert!((output[(row * 4 + head) * 256] as f64 - expected).abs() < 1e-5);
        }
    }
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; long-context attention benchmark"]
fn long_prefill_attention_scratch_reuse() -> Result<()> {
    use std::time::Instant;
    let gpu = gpu()?;
    let shape = AttentionShape {
        heads: 24,
        kv_heads: 2,
        head_dim: 256,
        rotary_dim: 64,
        rope_base: 10000.0,
        eps: 1e-6,
    };
    let position = 42_429;
    let batch = 16;
    let capacity = position + batch;
    let keys = gpu.allocate(capacity * shape.kv_heads * shape.head_dim * 2)?;
    let values = gpu.allocate(capacity * shape.kv_heads * shape.head_dim * 2)?;
    gpu.clear(&keys)?;
    gpu.clear(&values)?;
    let q = gpu.upload_f32(&vec![0.1; batch * shape.heads * shape.head_dim])?;
    let gate = gpu.upload_f32(&vec![0.0; batch * shape.heads * shape.head_dim])?;
    let k = gpu.upload_f32(&vec![0.2; batch * shape.kv_heads * shape.head_dim])?;
    let v = gpu.upload_f32(&vec![1.0; batch * shape.kv_heads * shape.head_dim])?;
    gpu.synchronize()?;
    let start = Instant::now();
    let mut last = None;
    let mut after_first = 0;
    for index in 0..8 {
        last = Some(gpu.attention(&q, &gate, &k, &v, &keys, &values, position, capacity, shape)?);
        if index == 0 {
            after_first = gpu.allocation_stats().0;
        }
    }
    gpu.synchronize()?;
    eprintln!(
        "long prefill attention: {:.3}s / allocation stats {:?}",
        start.elapsed().as_secs_f64(),
        gpu.allocation_stats()
    );
    assert!(
        gpu.allocation_stats().0 <= after_first + 1,
        "large scratch allocation was not reused"
    );
    let output = last.unwrap().read_f32()?;
    let score = 256.0f64 * 0.1 * half::f16::from_f32(0.2).to_f64() / 16.0;
    for query in [0, 7, 15] {
        let count = (query + 1) as f64;
        let expected = 0.5 * count * score.exp() / (position as f64 + count * score.exp());
        assert!((output[query * shape.heads * shape.head_dim] as f64 - expected).abs() < 1e-6);
    }
    Ok(())
}

fn gpu() -> Result<Gpu> {
    Gpu::open(std::path::Path::new(&std::env::var("LOCAL_ROCM_BIN")?), 0)
}
fn close(actual: &[f32], expected: &[f64], tolerance: f64) {
    assert_eq!(actual.len(), expected.len());
    for (i, (&a, &b)) in actual.iter().zip(expected).enumerate() {
        assert!(
            a.is_finite() && (a as f64 - b).abs() < tolerance * (1.0 + b.abs()),
            "element {i}: GPU={a}, CPU={b}"
        );
    }
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; synthetic KV only"]
fn tiled_decode_attention_matches_cpu_for_partial_tiles_and_grouped_heads() -> Result<()> {
    let gpu = gpu()?;
    let shape = AttentionShape {
        heads: 4,
        kv_heads: 2,
        head_dim: 256,
        rotary_dim: 64,
        rope_base: 10000.0,
        eps: 1e-6,
    };
    let key = |p: usize, h: usize, d: usize| {
        (p % 31) as f32 * 0.01 - 0.15 + h as f32 * 0.05 + (d % 13) as f32 * 0.007
    };
    let value = |p: usize, h: usize, d: usize| {
        (p % 17) as f32 * 0.1 - 0.8 + h as f32 * 0.3 + d as f32 * 0.001
    };
    for length in [1, 31, 255, 256, 257, 1400, 2057] {
        let mut keys = Vec::new();
        let mut values = Vec::new();
        for p in 0..length {
            for h in 0..2 {
                for d in 0..256 {
                    keys.extend(half::f16::from_f32(key(p, h, d)).to_le_bytes());
                    values.extend(half::f16::from_f32(value(p, h, d)).to_le_bytes());
                }
            }
        }
        let queries: Vec<f32> = (0..4)
            .flat_map(|h| (0..256).map(move |d| 0.1 * (h + 1) as f32 + (d % 11) as f32 * 0.01))
            .collect();
        let gates: Vec<f32> = (0..1024).map(|i| i as f32 * 0.001 - 0.5).collect();
        let last_key: Vec<_> = (0..2)
            .flat_map(|h| (0..256).map(move |d| key(length - 1, h, d)))
            .collect();
        let last_value: Vec<_> = (0..2)
            .flat_map(|h| (0..256).map(move |d| value(length - 1, h, d)))
            .collect();
        let actual = gpu
            .attention(
                &gpu.upload_f32(&queries)?,
                &gpu.upload_f32(&gates)?,
                &gpu.upload_f32(&last_key)?,
                &gpu.upload_f32(&last_value)?,
                &gpu.upload(&keys)?,
                &gpu.upload(&values)?,
                length - 1,
                length,
                shape,
            )?
            .read_f32()?;
        let mut expected = Vec::new();
        for h in 0..4 {
            let kh = h / 2;
            let weights: Vec<_> = (0..length)
                .map(|p| {
                    ((0..256)
                        .map(|d| {
                            queries[h * 256 + d] as f64
                                * half::f16::from_f32(key(p, kh, d)).to_f64()
                        })
                        .sum::<f64>()
                        / 16.0)
                        .exp()
                })
                .collect();
            let total: f64 = weights.iter().sum();
            for d in 0..256 {
                let sum: f64 = (0..length)
                    .map(|p| weights[p] * half::f16::from_f32(value(p, kh, d)).to_f64())
                    .sum();
                expected.push(sum / total / (1.0 + (-(gates[h * 256 + d] as f64)).exp()));
            }
        }
        close(&actual, &expected, 5e-5);
    }
    Ok(())
}
#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN"]
fn delta_recurrence_uses_tiled_gguf_heads() -> Result<()> {
    let gpu = gpu()?;
    let shape = DeltaShape {
        key_heads: 2,
        value_heads: 6,
        head_dim: 128,
        conv_width: 4,
    };
    let qkv: Vec<f32> = (0..shape.channels())
        .map(|i| (i as f32 * 0.071).sin())
        .collect();
    let alpha = vec![0.2; 6];
    let beta = vec![-0.3; 6];
    let decay = vec![-0.4; 6];
    let bias = vec![0.1; 6];
    let mut state: Vec<f64> = (0..shape.state_elements())
        .map(|i| (i as f64 * 0.013).cos() * 0.01)
        .collect();
    let device_state = gpu.upload_f32(&state.iter().map(|&v| v as f32).collect::<Vec<_>>())?;
    let qkv_gpu = gpu.upload_f32(&qkv)?;
    let a = gpu.upload_f32(&alpha)?;
    let b = gpu.upload_f32(&beta)?;
    let d = gpu.upload_f32(&decay)?;
    let dt = gpu.upload_f32(&bias)?;
    for _ in 0..2 {
        let actual = gpu
            .delta_step(&qkv_gpu, &a, &b, &d, &dt, &device_state, shape)?
            .read_f32()?;
        let mut expected = vec![0.0; 6 * 128];
        for h in 0..6 {
            let kh = h % 2; // A grouped h/3 mapping must fail this fixture.
            let q: Vec<f64> = qkv[kh * 128..(kh + 1) * 128]
                .iter()
                .map(|&v| v as f64)
                .collect();
            let k: Vec<f64> = qkv[256 + kh * 128..256 + (kh + 1) * 128]
                .iter()
                .map(|&v| v as f64)
                .collect();
            let qnorm = (q.iter().map(|v| v * v).sum::<f64>() + 1e-6).sqrt() * 128.0f64.sqrt();
            let knorm = (k.iter().map(|v| v * v).sum::<f64>() + 1e-6).sqrt();
            let retention = (-0.4 * (1.0 + 0.3f64.exp()).ln()).exp();
            let rate = 1.0 / (1.0 + 0.3f64.exp());
            for v in 0..128 {
                let memory = (0..128)
                    .map(|j| state[(h * 128 + j) * 128 + v] * retention * k[j] / knorm)
                    .sum::<f64>();
                let correction = (qkv[512 + h * 128 + v] as f64 - memory) * rate;
                for j in 0..128 {
                    let i = (h * 128 + j) * 128 + v;
                    state[i] = state[i] * retention + k[j] / knorm * correction;
                    expected[h * 128 + v] += state[i] * q[j] / qnorm;
                }
            }
        }
        close(&actual, &expected, 3e-5);
        close(&device_state.read_f32()?, &state, 3e-5);
    }
    Ok(())
}
#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN"]
fn convolution_keeps_history_and_norm_gates_after_normalization() -> Result<()> {
    let gpu = gpu()?;
    let shape = DeltaShape {
        key_heads: 1,
        value_heads: 2,
        head_dim: 128,
        conv_width: 4,
    };
    let n = shape.channels();
    let state = gpu.allocate(n * 3 * 4)?;
    let weights: Vec<f32> = (0..n * 4).map(|i| [0.1, 0.2, 0.3, 0.4][i % 4]).collect();
    let w = gpu.upload_f32(&weights)?;
    for step in 1..=5 {
        let x = gpu.upload_f32(&vec![step as f32; n])?;
        let value = (0..4)
            .map(|j| ((step as i32 - 3 + j as i32).max(0) as f64) * [0.1, 0.2, 0.3, 0.4][j])
            .sum::<f64>();
        let expected = value / (1.0 + (-value).exp());
        close(
            &gpu.conv_step(&x, &w, &state, shape)?.read_f32()?,
            &vec![expected; n],
            1e-6,
        );
    }
    let x = gpu.upload_f32(&vec![2.0; 256])?;
    let z = gpu.upload_f32(&vec![0.5; 256])?;
    let w = gpu.upload_f32(&vec![3.0; 128])?;
    let expected = 2.0 / (4.0f64 + 1e-6).sqrt() * 3.0 * 0.5 / (1.0 + (-0.5f64).exp());
    close(
        &gpu.gated_norm(&x, &z, &w, shape, 1e-6)?.read_f32()?,
        &vec![expected; 256],
        1e-6,
    );
    Ok(())
}
#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN"]
fn attention_reads_only_written_half_precision_cache_entries() -> Result<()> {
    let gpu = gpu()?;
    let shape = AttentionShape {
        heads: 2,
        kv_heads: 1,
        head_dim: 256,
        rotary_dim: 64,
        rope_base: 10_000_000.0,
        eps: 1e-6,
    };
    let query = gpu.upload_f32(&vec![0.1; 512])?;
    let gate = gpu.upload_f32(&vec![0.0; 512])?;
    let keys = gpu.allocate(4 * 256 * 2)?;
    let values = gpu.allocate(4 * 256 * 2)?;
    for pos in 0..3 {
        let key = gpu.upload_f32(&vec![0.2; 256])?;
        let value = gpu.upload_f32(&vec![(pos + 1) as f32; 256])?;
        let actual = gpu
            .attention(&query, &gate, &key, &value, &keys, &values, pos, 4, shape)?
            .read_f32()?;
        // Equal keys produce uniform attention; sigmoid(0) multiplies by 1/2.
        close(&actual, &vec![(pos + 2) as f64 / 4.0; 512], 1e-6);
    }
    assert!(
        gpu.attention(&query, &gate, &query, &query, &keys, &values, 4, 4, shape)
            .is_err()
    );
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; tiny synthetic weights only"]
fn pooled_buffers_preserve_stream_order_and_public_zero_initialization() -> Result<()> {
    let gpu = gpu()?;
    let one = gpu.upload_f32(&vec![1.0; 256])?;
    let mut value = gpu.upload_f32(&vec![2.0; 256])?;
    for _ in 0..100 {
        value = gpu.add(&value, &one)?;
    }
    close(&value.read_f32()?, &vec![102.0; 256], 0.0 + 1e-9);
    drop(value);
    close(&gpu.allocate(256 * 4)?.read_f32()?, &vec![0.0; 256], 1e-9);
    let (allocations, reuses, _) = gpu.allocation_stats();
    assert!(
        allocations < 8 && reuses > 95,
        "allocations={allocations}, reuses={reuses}"
    );
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; changing scratch shapes only"]
fn growing_attention_scratch_does_not_exhaust_pool_size_classes() -> Result<()> {
    let gpu = gpu()?;
    let one = gpu.upload_f32(&vec![1.0; 256])?;
    let mut value = gpu.upload_f32(&vec![0.0; 256])?;
    for length in (8..=1400).step_by(8) {
        let scratch = gpu.allocate(length * 24 * 8 * 4)?;
        for _ in 0..4 {
            value = gpu.add(&value, &one)?;
        }
        drop(scratch);
    }
    gpu.synchronize()?;
    let (allocations, _, cached) = gpu.allocation_stats();
    assert!(
        allocations < 24,
        "growing shapes caused {allocations} allocations"
    );
    assert!(
        cached < 4 * 1024 * 1024,
        "obsolete scratch sizes consumed {cached} bytes"
    );
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; tiny synthetic weights only"]
fn previous_checkpoint_reuses_unchanged_history_after_edit() -> Result<()> {
    use local_rocm::model::Model;
    let fixture = support::ToyModel::create()?;
    let file = fixture.open()?;
    let sdk = std::env::var("LOCAL_ROCM_BIN")?;
    let mut model = Model::load(&file, std::path::Path::new(&sdk), 64)?;
    for token in [1, 2, 3] {
        model.forward(token, false)?;
    }
    model.save_checkpoint()?;
    for token in [4, 5] {
        model.forward(token, false)?;
    }
    model.save_checkpoint()?;
    model.forward(6, true)?;
    let edited = [1, 2, 3, 7, 6];
    assert_eq!(model.prepare_prompt(&edited)?, 3);
    model.forward(7, false)?;
    let reused = model.forward(6, true)?.unwrap();
    // The newer checkpoint's KV prefix was overwritten by the edited branch.
    assert_eq!(model.prepare_prompt(&[1, 2, 3, 4, 5, 6])?, 3);
    model.reset()?;
    for (index, token) in edited.into_iter().enumerate() {
        model.forward(token, index == edited.len() - 1)?;
    }
    let full = model.cached_logits().unwrap();
    close(
        &reused,
        &full.iter().map(|&v| v as f64).collect::<Vec<_>>(),
        1e-5,
    );
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; tiny synthetic weights only"]
fn cached_model_matches_full_replay_after_rewind_reset_and_failed_token() -> Result<()> {
    use local_rocm::model::Model;
    let fixture = support::ToyModel::create()?;
    let file = fixture.open()?;
    let sdk = std::env::var("LOCAL_ROCM_BIN")?;
    let mut model = Model::load(&file, std::path::Path::new(&sdk), 64)?;
    let target = [1, 2, 3, 6, 7];
    for &token in &[1, 2, 3] {
        model.forward(token, false)?;
    }
    model.save_checkpoint()?;
    model.forward(4, false)?;
    model.forward(5, true)?;
    assert_eq!(model.prepare_prompt(&target)?, 3);
    model.forward(6, false)?;
    let cached = model.forward(7, true)?.unwrap();
    assert_eq!(model.prepare_prompt(&target)?, target.len());
    assert_eq!(&**model.cached_logits().as_ref().unwrap(), &*cached);

    model.reset()?;
    for (i, &token) in target.iter().enumerate() {
        model.forward(token, i + 1 == target.len())?;
    }
    let reference = model.cached_logits().unwrap();
    close(
        &cached,
        &reference.iter().map(|&v| v as f64).collect::<Vec<_>>(),
        1e-5,
    );

    model.reset()?;
    for &token in &[1, 2, 3] {
        model.forward(token, false)?;
    }
    model.save_checkpoint()?;
    assert!(model.forward(999, true).is_err());
    assert_eq!(model.prepare_prompt(&target)?, 3);
    model.forward(6, false)?;
    close(
        &model.forward(7, true)?.unwrap(),
        &reference.iter().map(|&v| v as f64).collect::<Vec<_>>(),
        1e-5,
    );

    // Reset overwrites the old KV prefix. A stale recurrent snapshot must no
    // longer be eligible even if the previous conversation is selected again.
    assert_eq!(model.prepare_prompt(&[0, 0])?, 0);
    model.forward(0, false)?;
    model.forward(0, true)?;
    assert_eq!(model.prepare_prompt(&target)?, 0);
    for &token in &[1, 2] {
        model.forward(token, false)?;
    }
    assert_eq!(model.prepare_prompt(&target)?, 2);
    for (i, &token) in target[2..].iter().enumerate() {
        model.forward(token, i == 2)?;
    }
    close(
        &model.cached_logits().unwrap(),
        &reference.iter().map(|&v| v as f64).collect::<Vec<_>>(),
        1e-5,
    );
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; small matrices only"]
fn batched_quantized_projections_match_cpu_dot_products() -> Result<()> {
    use local_rocm::quant::TensorType;
    let gpu = gpu()?;
    let columns = 1280; // Five K blocks exercise an uneven four-wave assignment.
    let rows = 5;
    for kind in [
        TensorType::F32,
        TensorType::F16,
        TensorType::Bf16,
        TensorType::Q8_0,
        TensorType::Q4K,
        TensorType::Q5K,
        TensorType::Q6K,
    ] {
        let bytes = match kind {
            TensorType::F32 => (0..columns * rows)
                .flat_map(|i| ((i as f32 * 0.13).sin() * 0.03).to_le_bytes())
                .collect::<Vec<_>>(),
            TensorType::F16 => (0..columns * rows)
                .flat_map(|i| half::f16::from_f32((i as f32 * 0.13).sin() * 0.03).to_le_bytes())
                .collect(),
            TensorType::Bf16 => (0..columns * rows)
                .flat_map(|i| half::bf16::from_f32((i as f32 * 0.13).sin() * 0.03).to_le_bytes())
                .collect(),
            _ => {
                let size =
                    columns * rows / kind.block_elements() as usize * kind.block_bytes() as usize;
                let mut bytes: Vec<u8> = (0..size).map(|i| ((i * 37 + 11) % 256) as u8).collect();
                for block in bytes.chunks_exact_mut(kind.block_bytes() as usize) {
                    let scale = if kind == TensorType::Q6K { 208 } else { 0 };
                    block[scale..scale + 2]
                        .copy_from_slice(&half::f16::from_f32(0.02).to_le_bytes());
                    if matches!(kind, TensorType::Q4K | TensorType::Q5K) {
                        block[2..4].copy_from_slice(&half::f16::from_f32(0.03).to_le_bytes());
                    }
                }
                bytes
            }
        };
        let reference = kind.decode(&bytes)?;
        let weights = gpu.upload(&bytes)?;
        for batch in [1, 2, 3, 8, 32, 128, 256] {
            let input: Vec<f32> = (0..columns * batch)
                .map(|i| (i as f32 * 0.37).cos())
                .collect();
            let input_gpu = gpu.upload_f32(&input)?;
            let actual = if batch == 1 {
                gpu.matvec(&weights, kind, &input_gpu, rows, columns)?
            } else {
                gpu.matmul(&weights, kind, &input_gpu, rows, columns, batch)?
            }
            .read_f32()?;
            for b in 0..batch {
                for r in 0..rows {
                    let products: Vec<f64> = reference[r * columns..(r + 1) * columns]
                        .iter()
                        .zip(&input[b * columns..(b + 1) * columns])
                        .map(|(&w, &x)| {
                            if batch > 1 && gpu.prefill_uses_half(kind) {
                                half::f16::from_f32(w).to_f64() * half::f16::from_f32(x).to_f64()
                            } else {
                                w as f64 * x as f64
                            }
                        })
                        .collect();
                    let expected: f64 = products.iter().sum();
                    let bound = products.iter().map(|v| v.abs()).sum::<f64>().max(1.0) * 2e-5;
                    assert!(
                        (actual[b * rows + r] as f64 - expected).abs() < bound,
                        "{kind:?} batch {batch} row {r}: GPU {} / CPU {expected}",
                        actual[b * rows + r]
                    );
                }
            }
        }
    }
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; tiny synthetic weights only"]
fn batched_prefill_matches_sequential_model_and_checkpoint_restore() -> Result<()> {
    use local_rocm::model::Model;
    let fixture = support::ToyModel::create()?;
    let file = fixture.open()?;
    let sdk = std::env::var("LOCAL_ROCM_BIN")?;
    let mut model = Model::load(&file, std::path::Path::new(&sdk), 512)?;
    let tokens: Vec<u32> = [1, 3, 4, 2, 7, 6, 0, 5, 2, 1, 7]
        .into_iter()
        .cycle()
        .take(259)
        .collect();
    for (i, &token) in tokens.iter().enumerate() {
        model.forward(token, i + 1 == tokens.len())?;
    }
    let expected: Vec<f64> = model
        .cached_logits()
        .unwrap()
        .iter()
        .map(|&v| v as f64)
        .collect();
    model.reset()?;
    let batch = model.prefill_batch_size();
    let mut actual = None;
    for chunk in tokens.chunks(batch) {
        actual = model.forward_tokens(chunk, true)?;
    }
    let actual = actual.unwrap();
    close(&actual, &expected, 2e-5);
    assert_eq!(model.position, tokens.len());

    model.reset()?;
    model.forward_tokens(&tokens[..3], false)?;
    model.save_checkpoint()?;
    model.forward_tokens(&[0, 0, 0, 0], false)?;
    assert_eq!(model.prepare_prompt(&tokens)?, 3);
    close(
        &{
            let mut scores = None;
            for chunk in tokens[3..].chunks(batch) {
                scores = model.forward_tokens(chunk, true)?;
            }
            scores.unwrap()
        },
        &expected,
        2e-5,
    );
    assert!(model.forward_tokens(&[], false).is_err());
    assert!(model.forward_tokens(&[0; 257], false).is_err());
    Ok(())
}

#[test]
#[ignore = "Requires an AMD GPU and LOCAL_ROCM_BIN; tiny synthetic vectors only"]
fn batched_attention_masks_future_tokens_with_an_existing_kv_prefix() -> Result<()> {
    let gpu = gpu()?;
    let shape = AttentionShape {
        heads: 2,
        kv_heads: 1,
        head_dim: 256,
        rotary_dim: 64,
        rope_base: 10000.0,
        eps: 1e-6,
    };
    let keys = gpu.allocate(4 * 256 * 2)?;
    let values = gpu.allocate(4 * 256 * 2)?;
    let q = gpu.upload_f32(&vec![0.1; 512])?;
    let gate = gpu.upload_f32(&vec![0.0; 512])?;
    let k = gpu.upload_f32(&vec![0.2; 256])?;
    let v = gpu.upload_f32(&vec![10.0; 256])?;
    gpu.attention(&q, &gate, &k, &v, &keys, &values, 0, 4, shape)?;
    let q = gpu.upload_f32(&vec![0.1; 3 * 512])?;
    let gate = gpu.upload_f32(&vec![0.0; 3 * 512])?;
    let k = gpu.upload_f32(&vec![0.2; 3 * 256])?;
    let v: Vec<f32> = (1..=3).flat_map(|i| vec![i as f32; 256]).collect();
    let actual = gpu
        .attention(
            &q,
            &gate,
            &k,
            &gpu.upload_f32(&v)?,
            &keys,
            &values,
            1,
            4,
            shape,
        )?
        .read_f32()?;
    let expected: Vec<f64> = [2.75, 13.0 / 6.0, 2.0]
        .into_iter()
        .flat_map(|v| vec![v; 512])
        .collect();
    close(&actual, &expected, 1e-6);
    Ok(())
}
