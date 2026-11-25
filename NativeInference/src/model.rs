//! Single-sequence Qwen35 text decoder. MTP tensors are excluded from the main
//! network. All projections run on HIP with packed quantized weights in VRAM.
use crate::{
    Result,
    gguf::Gguf,
    prompt_cache::{self, Reuse},
    quant::TensorType,
    rocm::{AttentionShape, Buffer, DeltaShape, Gpu},
};
use std::{collections::BTreeMap, path::Path, rc::Rc};

struct Weight {
    data: Buffer,
    kind: TensorType,
    shape: Vec<u64>,
}
enum LayerState {
    Delta {
        convolution: Buffer,
        recurrent: Buffer,
    },
    Attention {
        keys: Buffer,
        values: Buffer,
    },
}
struct Checkpoint {
    tokens: Vec<u32>,
    // Attention KV entries before the checkpoint remain in the live cache.
    // Only recurrent states need a snapshot to make rewinding exact.
    recurrent: Vec<Option<(Buffer, Buffer)>>,
    valid: bool,
}
pub struct Model {
    gpu: Gpu,
    weights: BTreeMap<String, Weight>,
    states: Vec<LayerState>,
    attention: AttentionShape,
    delta: DeltaShape,
    width: usize,
    vocabulary: usize,
    pub capacity: usize,
    pub position: usize,
    tokens: Vec<u32>,
    current_valid: bool,
    last_logits: Option<Rc<Vec<f32>>>,
    checkpoint: Option<Checkpoint>,
    previous_checkpoint: Option<Checkpoint>,
}
impl Model {
    pub fn load(file: &Gguf, sdk_bin: &Path, capacity: usize) -> Result<Self> {
        if file.string("general.architecture")? != "qwen35" {
            return Err("Only qwen35 is supported".into());
        }
        let width = file.integer("qwen35.embedding_length")?;
        let blocks = file.integer("qwen35.block_count")?;
        let draft = if file.metadata.contains_key("qwen35.nextn_predict_layers") {
            file.integer("qwen35.nextn_predict_layers")?
        } else {
            0
        };
        let layers = blocks
            .checked_sub(draft)
            .filter(|&n| n > 0)
            .ok_or("Invalid main/draft layer counts")?;
        let attention = AttentionShape {
            heads: file.integer("qwen35.attention.head_count")?,
            kv_heads: file.integer("qwen35.attention.head_count_kv")?,
            head_dim: file.integer("qwen35.attention.key_length")?,
            rotary_dim: file.integer("qwen35.rope.dimension_count")?,
            rope_base: file.number("qwen35.rope.freq_base")?,
            eps: file.number("qwen35.attention.layer_norm_rms_epsilon")?,
        };
        let delta = DeltaShape {
            key_heads: file.integer("qwen35.ssm.group_count")?,
            value_heads: file.integer("qwen35.ssm.time_step_rank")?,
            head_dim: file.integer("qwen35.ssm.state_size")?,
            conv_width: file.integer("qwen35.ssm.conv_kernel")?,
        };
        if capacity == 0
            || capacity > file.integer("qwen35.context_length")?
            || !(1..=128).contains(&layers)
            || !(1..=16384).contains(&width)
            || attention.head_dim != 256
            || !(1..=128).contains(&attention.heads)
            || !(1..=attention.heads).contains(&attention.kv_heads)
            || attention.heads % attention.kv_heads != 0
            || attention.rotary_dim == 0
            || attention.rotary_dim > 256
            || attention.rotary_dim % 2 != 0
            || !attention.rope_base.is_finite()
            || attention.rope_base <= 0.0
            || !attention.eps.is_finite()
            || attention.eps <= 0.0
            || delta.head_dim != 128
            || !(2..=16).contains(&delta.conv_width)
            || !(1..=128).contains(&delta.value_heads)
            || !(1..=128).contains(&delta.key_heads)
            || delta.value_heads % delta.key_heads != 0
        {
            return Err("Unsupported model dimensions or context capacity".into());
        }
        let embedding = file.tensor("token_embd.weight")?;
        if embedding.dimensions.len() != 2
            || embedding.dimensions[0] != width as u64
            || !(1..=1_000_000).contains(&embedding.dimensions[1])
        {
            return Err("Invalid token embedding shape".into());
        }
        let gpu = Gpu::open(sdk_bin, 0)?;
        let mut required = 1024 * 1024 * 1024u64; // Workspace and driver allocation margin.
        for (name, tensor) in &file.tensors {
            let is_draft = name
                .strip_prefix("blk.")
                .and_then(|s| s.split('.').next())
                .and_then(|n| n.parse::<usize>().ok())
                .is_some_and(|i| i >= layers);
            if !is_draft {
                required = required
                    .checked_add(tensor.bytes)
                    .ok_or("Model memory size overflow")?;
            }
        }
        for i in 0..layers {
            let state_bytes = if file.tensors.contains_key(&format!("blk.{i}.ssm_a")) {
                (delta.channels() * (delta.conv_width - 1) + delta.state_elements()) * 4
            } else {
                capacity * attention.kv_heads * attention.head_dim * 2 * 2
            };
            required = required
                .checked_add(state_bytes as u64)
                .ok_or("State memory size overflow")?;
        }
        let (free, _) = gpu.memory_info()?;
        if required > free as u64 {
            return Err(format!("Insufficient VRAM: need approximately {:.1} GiB, {:.1} GiB free. Reduce context capacity or stop another GPU workload.",required as f64/1073741824.0,free as f64/1073741824.0).into());
        }
        eprintln!("loading model: {} on {}", file.path.display(), gpu.name);
        let mut weights = BTreeMap::new();
        for (name, tensor) in &file.tensors {
            if let Some(index) = name
                .strip_prefix("blk.")
                .and_then(|s| s.split('.').next())
                .and_then(|n| n.parse::<usize>().ok())
            {
                if index >= layers {
                    continue;
                }
            }
            let data = gpu.upload(&file.read_tensor(tensor)?)?;
            weights.insert(
                name.clone(),
                Weight {
                    data,
                    kind: tensor.kind,
                    shape: tensor.dimensions.clone(),
                },
            );
        }
        let vocabulary = usize::try_from(file.tensor("token_embd.weight")?.dimensions[1])?;
        let mut states = Vec::with_capacity(layers);
        for i in 0..layers {
            states.push(if weights.contains_key(&format!("blk.{i}.ssm_a")) {
                LayerState::Delta {
                    convolution: gpu.allocate(delta.channels() * (delta.conv_width - 1) * 4)?,
                    recurrent: gpu.allocate(delta.state_elements() * 4)?,
                }
            } else {
                let bytes = capacity
                    .checked_mul(attention.kv_heads * attention.head_dim * 2)
                    .ok_or("KV cache size overflow")?;
                LayerState::Attention {
                    keys: gpu.allocate(bytes)?,
                    values: gpu.allocate(bytes)?,
                }
            });
        }
        gpu.synchronize()?;
        eprintln!(
            "model buffer: {} layers, {} tokens of KV capacity",
            layers, capacity
        );
        eprintln!("decode attention: {}", gpu.decode_attention_mode());
        Ok(Self {
            gpu,
            weights,
            states,
            attention,
            delta,
            width,
            vocabulary,
            capacity,
            position: 0,
            tokens: Vec::new(),
            current_valid: true,
            last_logits: None,
            checkpoint: None,
            previous_checkpoint: None,
        })
    }
    fn weight(&self, name: &str) -> Result<&Weight> {
        self.weights
            .get(name)
            .ok_or_else(|| format!("Missing loaded weight: {name}").into())
    }
    fn vector(&self, name: &str) -> Result<&Buffer> {
        let weight = self.weight(name)?;
        if weight.kind != TensorType::F32 {
            return Err(format!("Expected F32 norm/state weight: {name}").into());
        }
        Ok(&weight.data)
    }
    fn project(&self, name: &str, x: &Buffer) -> Result<Buffer> {
        let weight = self.weight(name)?;
        if weight.shape.len() != 2 {
            return Err(format!("Expected matrix: {name}").into());
        }
        let columns = usize::try_from(weight.shape[0])?;
        let rows = usize::try_from(weight.shape[1])?;
        let stride = columns
            .checked_mul(4)
            .filter(|&n| n > 0)
            .ok_or("Invalid projection width")?;
        if x.len_bytes() % stride != 0 {
            return Err("Projection input shape mismatch".into());
        }
        let batch = x.len_bytes() / stride;
        if batch > 1 {
            return self
                .gpu
                .matmul(&weight.data, weight.kind, x, rows, columns, batch);
        }
        self.gpu.matvec(&weight.data, weight.kind, x, rows, columns)
    }
    pub fn reset(&mut self) -> Result<()> {
        self.current_valid = false;
        // A different prompt will overwrite KV entries belonging to the old
        // checkpoint. Invalidate it before resetting or starting new kernels.
        if let Some(checkpoint) = &mut self.checkpoint {
            checkpoint.valid = false;
        }
        if let Some(checkpoint) = &mut self.previous_checkpoint {
            checkpoint.valid = false;
        }
        for state in &self.states {
            if let LayerState::Delta {
                convolution,
                recurrent,
            } = state
            {
                self.gpu.clear(convolution)?;
                self.gpu.clear(recurrent)?;
            }
        }
        // Cached attention entries after position are never read; zeroing multi-GB
        // KV storage is unnecessary. Recurrent state must always be reset.
        self.position = 0;
        self.tokens.clear();
        self.last_logits = None;
        self.current_valid = true;
        Ok(())
    }
    pub fn prepare_prompt(&mut self, prompt: &[u32]) -> Result<usize> {
        let checkpoint = [&self.checkpoint, &self.previous_checkpoint]
            .into_iter()
            .flatten()
            .filter(|c| {
                c.valid
                    && !c.tokens.is_empty()
                    && prompt.len() > c.tokens.len()
                    && prompt.starts_with(&c.tokens)
            })
            .max_by_key(|c| c.tokens.len())
            .map(|c| c.tokens.as_slice());
        match prompt_cache::choose(
            prompt,
            &self.tokens,
            self.current_valid,
            self.last_logits.is_some(),
            checkpoint,
        ) {
            Reuse::Current(count) => Ok(count),
            Reuse::Checkpoint(count) => {
                self.current_valid = false;
                self.last_logits = None;
                let checkpoint = [&self.checkpoint, &self.previous_checkpoint]
                    .into_iter()
                    .flatten()
                    .find(|c| c.valid && c.tokens.len() == count && prompt.starts_with(&c.tokens))
                    .ok_or("Missing checkpoint")?;
                for (state, saved) in self.states.iter().zip(&checkpoint.recurrent) {
                    if let (
                        LayerState::Delta {
                            convolution,
                            recurrent,
                        },
                        Some((saved_conv, saved_recurrent)),
                    ) = (state, saved)
                    {
                        self.gpu.copy(saved_conv, convolution)?;
                        self.gpu.copy(saved_recurrent, recurrent)?;
                    }
                }
                self.tokens = checkpoint.tokens.clone();
                self.position = count;
                for checkpoint in [&mut self.checkpoint, &mut self.previous_checkpoint]
                    .into_iter()
                    .flatten()
                {
                    if checkpoint.tokens.len() > count {
                        checkpoint.valid = false;
                    }
                }
                self.current_valid = true;
                self.last_logits = None;
                Ok(count)
            }
            Reuse::Reset => {
                self.reset()?;
                Ok(0)
            }
        }
    }
    /// Report matching token counts only; never log the prompt itself.
    pub fn checkpoint_overlap(&self, prompt: &[u32]) -> Option<(usize, usize)> {
        [&self.checkpoint, &self.previous_checkpoint]
            .into_iter()
            .flatten()
            .filter(|checkpoint| checkpoint.valid)
            .map(|checkpoint| {
                let common = checkpoint
                    .tokens
                    .iter()
                    .zip(prompt)
                    .take_while(|(a, b)| a == b)
                    .count();
                (common, checkpoint.tokens.len())
            })
            .max_by_key(|(common, _)| *common)
    }
    pub fn cached_logits(&self) -> Option<Rc<Vec<f32>>> {
        self.last_logits.clone()
    }
    pub fn save_checkpoint(&mut self) -> Result<()> {
        if !self.current_valid {
            return Err("Cannot checkpoint an incomplete token".into());
        }
        if self
            .checkpoint
            .as_ref()
            .is_some_and(|c| c.valid && c.tokens == self.tokens)
        {
            return Ok(());
        }
        if self.checkpoint.as_ref().is_some_and(|c| c.valid) {
            std::mem::swap(&mut self.checkpoint, &mut self.previous_checkpoint);
        }
        if self.checkpoint.is_none() {
            let recurrent = self
                .states
                .iter()
                .map(|state| -> Result<_> {
                    Ok(match state {
                        LayerState::Delta {
                            convolution,
                            recurrent,
                        } => Some((
                            self.gpu.allocate(convolution.len_bytes())?,
                            self.gpu.allocate(recurrent.len_bytes())?,
                        )),
                        LayerState::Attention { .. } => None,
                    })
                })
                .collect::<Result<Vec<_>>>()?;
            self.checkpoint = Some(Checkpoint {
                tokens: Vec::new(),
                recurrent,
                valid: false,
            });
        }
        let checkpoint = self
            .checkpoint
            .as_mut()
            .ok_or("Missing checkpoint storage")?;
        checkpoint.valid = false;
        for (state, saved) in self.states.iter().zip(&checkpoint.recurrent) {
            if let (
                LayerState::Delta {
                    convolution,
                    recurrent,
                },
                Some((saved_conv, saved_recurrent)),
            ) = (state, saved)
            {
                self.gpu.copy(convolution, saved_conv)?;
                self.gpu.copy(recurrent, saved_recurrent)?;
            }
        }
        checkpoint.tokens = self.tokens.clone();
        checkpoint.valid = true;
        Ok(())
    }
    pub fn prefill_batch_size(&self) -> usize {
        prefill_batch_for_position(
            self.gpu.prefill_batch_size(),
            self.position,
            self.attention.heads,
        )
    }
    pub fn allocation_stats(&self) -> (u64, u64, usize) {
        self.gpu.allocation_stats()
    }
    pub fn prefill_precision(&self) -> &'static str {
        if self.gpu.prefill_uses_half(TensorType::Q4K) {
            "FP16 operands / FP32 accumulation"
        } else {
            "FP32"
        }
    }
    pub fn begin_profile(&self) {
        self.gpu.begin_profile();
    }
    pub fn end_profile(&self) -> Result<BTreeMap<String, (usize, f64)>> {
        self.gpu.end_profile()
    }
    /// Consumes exactly one token. Logits are omitted during prompt ingestion.
    pub fn forward(&mut self, token: u32, logits: bool) -> Result<Option<Rc<Vec<f32>>>> {
        self.forward_tokens(&[token], logits)
    }
    /// Prefill a bounded batch of consecutive tokens. Recurrent updates remain ordered;
    /// matrix projections reuse each decoded weight across the token batch.
    pub fn forward_tokens(&mut self, tokens: &[u32], logits: bool) -> Result<Option<Rc<Vec<f32>>>> {
        if !(1..=self.prefill_batch_size()).contains(&tokens.len()) {
            return Err("Input token batch exceeds configured prefill size".into());
        }
        self.current_valid = false;
        self.last_logits = None;
        let result = self.forward_inner(tokens, logits)?;
        self.tokens.extend_from_slice(tokens);
        self.current_valid = true;
        self.last_logits = result.clone();
        Ok(result)
    }
    fn forward_inner(&mut self, tokens: &[u32], logits: bool) -> Result<Option<Rc<Vec<f32>>>> {
        let batch = tokens.len();
        if self
            .position
            .checked_add(batch)
            .is_none_or(|end| end > self.capacity)
        {
            return Err("context_length_exceeded".into());
        }
        let embedding = self.weight("token_embd.weight")?;
        let mut x = if batch == 1 {
            self.gpu.embedding(
                &embedding.data,
                embedding.kind,
                tokens[0] as usize,
                self.width,
                self.vocabulary,
            )?
        } else {
            self.gpu.embeddings(
                &embedding.data,
                embedding.kind,
                tokens,
                self.width,
                self.vocabulary,
            )?
        };
        for (i, state) in self.states.iter().enumerate() {
            let prefix = format!("blk.{i}");
            let normalized = self.gpu.norm(
                &x,
                self.vector(&format!("{prefix}.attn_norm.weight"))?,
                self.attention.eps,
            )?;
            let mixed = match state {
                LayerState::Delta {
                    convolution,
                    recurrent,
                } => {
                    let qkv = self.project(&format!("{prefix}.attn_qkv.weight"), &normalized)?;
                    let gate = self.project(&format!("{prefix}.attn_gate.weight"), &normalized)?;
                    let alpha = self.project(&format!("{prefix}.ssm_alpha.weight"), &normalized)?;
                    let beta = self.project(&format!("{prefix}.ssm_beta.weight"), &normalized)?;
                    let convolved = self.gpu.conv_step(
                        &qkv,
                        self.vector(&format!("{prefix}.ssm_conv1d.weight"))?,
                        convolution,
                        self.delta,
                    )?;
                    let updated = self.gpu.delta_step(
                        &convolved,
                        &alpha,
                        &beta,
                        self.vector(&format!("{prefix}.ssm_a"))?,
                        self.vector(&format!("{prefix}.ssm_dt.bias"))?,
                        recurrent,
                        self.delta,
                    )?;
                    let gated = self.gpu.gated_norm(
                        &updated,
                        &gate,
                        self.vector(&format!("{prefix}.ssm_norm.weight"))?,
                        self.delta,
                        self.attention.eps,
                    )?;
                    self.project(&format!("{prefix}.ssm_out.weight"), &gated)?
                }
                LayerState::Attention { keys, values } => {
                    let q = self.project(&format!("{prefix}.attn_q.weight"), &normalized)?;
                    let k = self.project(&format!("{prefix}.attn_k.weight"), &normalized)?;
                    let v = self.project(&format!("{prefix}.attn_v.weight"), &normalized)?;
                    let (q, gate) = self.gpu.prepare_qk(
                        &q,
                        self.vector(&format!("{prefix}.attn_q_norm.weight"))?,
                        self.attention,
                        self.position,
                        true,
                    )?;
                    let (k, _) = self.gpu.prepare_qk(
                        &k,
                        self.vector(&format!("{prefix}.attn_k_norm.weight"))?,
                        self.attention,
                        self.position,
                        false,
                    )?;
                    let attended = self.gpu.attention(
                        &q,
                        &gate,
                        &k,
                        &v,
                        keys,
                        values,
                        self.position,
                        self.capacity,
                        self.attention,
                    )?;
                    self.project(&format!("{prefix}.attn_output.weight"), &attended)?
                }
            };
            x = self.gpu.add(&x, &mixed)?;
            let normalized = self.gpu.norm(
                &x,
                self.vector(&format!("{prefix}.post_attention_norm.weight"))?,
                self.attention.eps,
            )?;
            let gate = self.project(&format!("{prefix}.ffn_gate.weight"), &normalized)?;
            let up = self.project(&format!("{prefix}.ffn_up.weight"), &normalized)?;
            let hidden = self.gpu.swiglu(&gate, &up)?;
            let down = self.project(&format!("{prefix}.ffn_down.weight"), &hidden)?;
            x = self.gpu.add(&x, &down)?;
        }
        self.position += batch;
        if !logits {
            return Ok(None);
        }
        let x = if batch > 1 {
            self.gpu.row(&x, self.width, batch - 1)?
        } else {
            x
        };
        let normalized =
            self.gpu
                .norm(&x, self.vector("output_norm.weight")?, self.attention.eps)?;
        let result = self.project("output.weight", &normalized)?.read_f32()?;
        if result.iter().any(|v| !v.is_finite()) {
            return Err("Non-finite model logits; generation aborted".into());
        }
        Ok(Some(Rc::new(result)))
    }
}

/// Share weight expansion across prompt tokens, limiting Attention scratch to
/// 64 MiB where possible. At least one token must fit even for large contexts.
fn prefill_batch_for_position(maximum: usize, position: usize, heads: usize) -> usize {
    let mut batch = maximum;
    while batch > 1 && (position + batch) * heads * batch * 4 > 64 * 1024 * 1024 {
        batch /= 2;
    }
    batch
}

#[cfg(test)]
mod tests {
    use super::prefill_batch_for_position;

    #[test]
    fn prefill_limits_attention_memory_at_long_contexts() {
        // The production 27B model has 24 query heads. A fixed 128-token
        // batch at its context limit would need about 1.5 GiB for scores alone.
        assert_eq!(prefill_batch_for_position(128, 1378, 24), 128);
        assert_eq!(prefill_batch_for_position(256, 42_429, 24), 16);
        assert_eq!(prefill_batch_for_position(128, 131_072 - 128, 24), 4);
        assert_eq!(prefill_batch_for_position(8, 1378, 24), 8);
    }
}
