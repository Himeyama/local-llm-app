//! Shape-checked operations used by the Qwen model, separate from the C ABI.
use super::{Buffer, Gpu, arg, hip};
use crate::{Result, quant::TensorType};

#[derive(Clone, Copy, Debug)]
pub struct DeltaShape {
    pub key_heads: usize,
    pub value_heads: usize,
    pub head_dim: usize,
    pub conv_width: usize,
}
impl DeltaShape {
    pub fn channels(self) -> usize {
        (2 * self.key_heads + self.value_heads) * self.head_dim
    }
    pub fn state_elements(self) -> usize {
        self.value_heads * self.head_dim * self.head_dim
    }
}
#[derive(Clone, Copy, Debug)]
pub struct AttentionShape {
    pub heads: usize,
    pub kv_heads: usize,
    pub head_dim: usize,
    pub rotary_dim: usize,
    pub rope_base: f32,
    pub eps: f32,
}

impl Gpu {
    fn batches(&self, input: &Buffer, width: usize) -> Result<usize> {
        self.own(input)?;
        let stride = width
            .checked_mul(4)
            .filter(|&n| n > 0)
            .ok_or("Invalid vector width")?;
        if input.bytes % stride != 0 || !(1..=256).contains(&(input.bytes / stride)) {
            return Err("Invalid batch shape (expected 1..256 tokens)".into());
        }
        Ok(input.bytes / stride)
    }
    fn floats(&self, buffer: &Buffer, count: usize) -> Result<()> {
        self.own(buffer)?;
        if buffer.bytes != count.checked_mul(4).ok_or("Shape overflow")? {
            return Err("GPU vector shape mismatch".into());
        }
        Ok(())
    }
    pub fn clear(&self, buffer: &Buffer) -> Result<()> {
        self.own(buffer)?;
        self.api.check(
            unsafe { hip::hipMemset(buffer.pointer, 0, buffer.bytes) },
            "hipMemset",
        )
    }
    pub fn embedding(
        &self,
        weights: &Buffer,
        kind: TensorType,
        token: usize,
        width: usize,
        vocabulary: usize,
    ) -> Result<Buffer> {
        self.own(weights)?;
        if token >= vocabulary
            || width % kind.block_elements() as usize != 0
            || weights.bytes
                != vocabulary * width / kind.block_elements() as usize * kind.block_bytes() as usize
        {
            return Err("Invalid embedding shape or token ID".into());
        }
        let result = self.output(width * 4)?;
        let mut w = weights.pointer;
        let mut y = result.pointer;
        let mut tag = kind as u32;
        let mut offset = u64::try_from(token * width)?;
        let mut count = u32::try_from(width)?;
        self.launch(
            "embedding",
            count.div_ceil(256),
            256,
            &mut [
                arg(&mut w),
                arg(&mut y),
                arg(&mut tag),
                arg(&mut offset),
                arg(&mut count),
            ],
        )?;
        Ok(result)
    }
    pub fn norm(&self, x: &Buffer, weights: &Buffer, eps: f32) -> Result<Buffer> {
        if !eps.is_finite() || eps <= 0.0 {
            return Err("Invalid RMS epsilon".into());
        }
        let width = weights.bytes / 4;
        self.floats(weights, width)?;
        let batch = self.batches(x, width)?;
        let result = self.output(x.bytes)?;
        let mut input = x.pointer;
        let mut w = weights.pointer;
        let mut output = result.pointer;
        let mut n = u32::try_from(width)?;
        let mut epsilon = eps;
        self.launch(
            "rms_norm",
            u32::try_from(batch)?,
            256,
            &mut [
                arg(&mut input),
                arg(&mut w),
                arg(&mut output),
                arg(&mut n),
                arg(&mut epsilon),
            ],
        )?;
        Ok(result)
    }
    pub fn embeddings(
        &self,
        weights: &Buffer,
        kind: TensorType,
        tokens: &[u32],
        width: usize,
        vocabulary: usize,
    ) -> Result<Buffer> {
        if !(1..=256).contains(&tokens.len()) || tokens.iter().any(|&t| t as usize >= vocabulary) {
            return Err("Invalid embedding tokens".into());
        }
        self.own(weights)?;
        if width == 0
            || width % kind.block_elements() as usize != 0
            || weights.bytes
                != vocabulary * width / kind.block_elements() as usize * kind.block_bytes() as usize
        {
            return Err("Invalid embedding matrix".into());
        }
        let bytes: Vec<u8> = tokens.iter().flat_map(|t| t.to_le_bytes()).collect();
        let ids = self.upload(&bytes)?;
        let result = self.output(tokens.len() * width * 4)?;
        let mut w = weights.pointer;
        let mut t = ids.pointer;
        let mut y = result.pointer;
        let mut tag = kind as u32;
        let mut dim = u32::try_from(width)?;
        let mut count = u32::try_from(tokens.len() * width)?;
        self.launch(
            "embeddings",
            count.div_ceil(256),
            256,
            &mut [
                arg(&mut w),
                arg(&mut t),
                arg(&mut y),
                arg(&mut tag),
                arg(&mut dim),
                arg(&mut count),
            ],
        )?;
        Ok(result)
    }
    fn binary(&self, name: &str, a: &Buffer, b: &Buffer) -> Result<Buffer> {
        self.floats(a, a.bytes / 4)?;
        self.floats(b, a.bytes / 4)?;
        let result = self.output(a.bytes)?;
        let mut left = a.pointer;
        let mut right = b.pointer;
        let mut output = result.pointer;
        let mut n = u32::try_from(a.bytes / 4)?;
        self.launch(
            name,
            n.div_ceil(256),
            256,
            &mut [
                arg(&mut left),
                arg(&mut right),
                arg(&mut output),
                arg(&mut n),
            ],
        )?;
        Ok(result)
    }
    pub fn add(&self, a: &Buffer, b: &Buffer) -> Result<Buffer> {
        self.binary("add", a, b)
    }
    pub fn swiglu(&self, gate: &Buffer, up: &Buffer) -> Result<Buffer> {
        self.binary("swiglu", gate, up)
    }

    pub fn conv_step(
        &self,
        input: &Buffer,
        weights: &Buffer,
        state: &Buffer,
        shape: DeltaShape,
    ) -> Result<Buffer> {
        if shape.conv_width < 2 {
            return Err("Convolution width must be at least 2".into());
        }
        let batch = self.batches(input, shape.channels())?;
        self.floats(weights, shape.channels() * shape.conv_width)?;
        self.floats(state, shape.channels() * (shape.conv_width - 1))?;
        let result = self.output(input.bytes)?;
        let mut x = input.pointer;
        let mut w = weights.pointer;
        let mut s = state.pointer;
        let mut y = result.pointer;
        let mut n = u32::try_from(shape.channels())?;
        let mut width = u32::try_from(shape.conv_width)?;
        let mut batch = u32::try_from(batch)?;
        self.launch(
            "conv_step",
            n.div_ceil(256),
            256,
            &mut [
                arg(&mut x),
                arg(&mut w),
                arg(&mut s),
                arg(&mut y),
                arg(&mut n),
                arg(&mut width),
                arg(&mut batch),
            ],
        )?;
        Ok(result)
    }
    #[allow(clippy::too_many_arguments)]
    pub fn delta_step(
        &self,
        qkv: &Buffer,
        alpha: &Buffer,
        beta: &Buffer,
        decay: &Buffer,
        bias: &Buffer,
        state: &Buffer,
        shape: DeltaShape,
    ) -> Result<Buffer> {
        if shape.head_dim != 128 || shape.key_heads == 0 || shape.value_heads % shape.key_heads != 0
        {
            return Err("Unsupported DeltaNet head shape".into());
        }
        let batch = self.batches(qkv, shape.channels())?;
        for b in [alpha, beta] {
            self.floats(b, shape.value_heads * batch)?;
        }
        for b in [decay, bias] {
            self.floats(b, shape.value_heads)?;
        }
        self.floats(state, shape.state_elements())?;
        let result = self.output(shape.value_heads * shape.head_dim * batch * 4)?;
        let mut q = qkv.pointer;
        let mut a = alpha.pointer;
        let mut b = beta.pointer;
        let mut d = decay.pointer;
        let mut dt = bias.pointer;
        let mut s = state.pointer;
        let mut y = result.pointer;
        let mut kh = u32::try_from(shape.key_heads)?;
        let mut vh = u32::try_from(shape.value_heads)?;
        let mut batch = u32::try_from(batch)?;
        self.launch(
            "delta_step",
            vh,
            128,
            &mut [
                arg(&mut q),
                arg(&mut a),
                arg(&mut b),
                arg(&mut d),
                arg(&mut dt),
                arg(&mut s),
                arg(&mut y),
                arg(&mut kh),
                arg(&mut vh),
                arg(&mut batch),
            ],
        )?;
        Ok(result)
    }
    pub fn gated_norm(
        &self,
        input: &Buffer,
        gate: &Buffer,
        weights: &Buffer,
        shape: DeltaShape,
        eps: f32,
    ) -> Result<Buffer> {
        let batch = self.batches(input, shape.value_heads * shape.head_dim)?;
        self.floats(gate, shape.value_heads * shape.head_dim * batch)?;
        self.floats(weights, shape.head_dim)?;
        if shape.head_dim != 128 {
            return Err("Unsupported gated norm width".into());
        }
        let result = self.output(input.bytes)?;
        let mut x = input.pointer;
        let mut z = gate.pointer;
        let mut w = weights.pointer;
        let mut y = result.pointer;
        let mut e = eps;
        self.launch(
            "gated_norm",
            u32::try_from(shape.value_heads * batch)?,
            128,
            &mut [
                arg(&mut x),
                arg(&mut z),
                arg(&mut w),
                arg(&mut y),
                arg(&mut e),
            ],
        )?;
        Ok(result)
    }
    /// Normalizes each head and applies text-only partial NeoX RoPE. Query rows
    /// contain adjacent [query, gate] vectors; key rows contain only keys.
    pub fn prepare_qk(
        &self,
        input: &Buffer,
        weights: &Buffer,
        shape: AttentionShape,
        position: usize,
        query: bool,
    ) -> Result<(Buffer, Buffer)> {
        if shape.head_dim != 256 || shape.rotary_dim > shape.head_dim || shape.rotary_dim % 2 != 0 {
            return Err("Unsupported rotary head shape".into());
        }
        let heads = if query { shape.heads } else { shape.kv_heads };
        let batch = self.batches(input, heads * shape.head_dim * if query { 2 } else { 1 })?;
        self.floats(weights, shape.head_dim)?;
        let result = self.output(heads * shape.head_dim * batch * 4)?;
        let gate = self.output(heads * shape.head_dim * batch * 4)?;
        let mut x = input.pointer;
        let mut w = weights.pointer;
        let mut y = result.pointer;
        let mut g = gate.pointer;
        let mut p = u32::try_from(position)?;
        let mut rotary = u32::try_from(shape.rotary_dim)?;
        let mut base = shape.rope_base;
        let mut eps = shape.eps;
        let mut q = u32::from(query);
        let mut head_count = u32::try_from(heads)?;
        self.launch(
            "prepare_qk",
            u32::try_from(heads * batch)?,
            256,
            &mut [
                arg(&mut x),
                arg(&mut w),
                arg(&mut y),
                arg(&mut g),
                arg(&mut p),
                arg(&mut rotary),
                arg(&mut base),
                arg(&mut eps),
                arg(&mut q),
                arg(&mut head_count),
            ],
        )?;
        Ok((result, gate))
    }
    #[allow(clippy::too_many_arguments)]
    pub fn attention(
        &self,
        query: &Buffer,
        gate: &Buffer,
        key: &Buffer,
        value: &Buffer,
        key_cache: &Buffer,
        value_cache: &Buffer,
        position: usize,
        capacity: usize,
        shape: AttentionShape,
    ) -> Result<Buffer> {
        if shape.head_dim != 256
            || shape.kv_heads == 0
            || shape.heads % shape.kv_heads != 0
            || position >= capacity
        {
            return Err("Invalid attention shape or position".into());
        }
        let kv = shape.kv_heads * shape.head_dim;
        let batch = self.batches(query, shape.heads * shape.head_dim)?;
        if position.checked_add(batch).is_none_or(|end| end > capacity) {
            return Err("Batch exceeds KV capacity".into());
        }
        self.floats(gate, shape.heads * shape.head_dim * batch)?;
        self.floats(key, kv * batch)?;
        self.floats(value, kv * batch)?;
        for cache in [key_cache, value_cache] {
            self.own(cache)?;
            if cache.bytes != capacity.checked_mul(kv * 2).ok_or("KV shape overflow")? {
                return Err("F16 KV cache shape mismatch".into());
            }
        }
        let mut k = key.pointer;
        let mut v = value.pointer;
        let mut kc = key_cache.pointer;
        let mut vc = value_cache.pointer;
        let mut pos = u32::try_from(position)?;
        let mut width = u32::try_from(kv)?;
        let mut count = u32::try_from(kv * batch)?;
        self.launch(
            "store_kv",
            count.div_ceil(256),
            256,
            &mut [
                arg(&mut k),
                arg(&mut v),
                arg(&mut kc),
                arg(&mut vc),
                arg(&mut pos),
                arg(&mut width),
                arg(&mut count),
            ],
        )?;
        let scores = self.output(shape.heads * batch * (position + batch) * 4)?;
        let result = self.output(shape.heads * shape.head_dim * batch * 4)?;
        let mut q = query.pointer;
        let mut s = scores.pointer;
        let mut length = u32::try_from(position + batch)?;
        let mut heads = u32::try_from(shape.heads)?;
        let mut kv_heads = u32::try_from(shape.kv_heads)?;
        let mut batch = u32::try_from(batch)?;
        if batch == 1 {
            self.launch(
                "attention_scores_decode",
                (heads * length).div_ceil(8),
                256,
                &mut [
                    arg(&mut q),
                    arg(&mut kc),
                    arg(&mut s),
                    arg(&mut length),
                    arg(&mut heads),
                    arg(&mut kv_heads),
                ],
            )?;
        } else if position >= 8192 {
            self.launch(
                "attention_scores_prefill",
                (heads * batch * length).div_ceil(8),
                256,
                &mut [
                    arg(&mut q),
                    arg(&mut kc),
                    arg(&mut s),
                    arg(&mut length),
                    arg(&mut heads),
                    arg(&mut kv_heads),
                    arg(&mut batch),
                    arg(&mut pos),
                ],
            )?;
        } else {
            self.launch(
                "attention_scores",
                (heads * batch * length).div_ceil(256),
                256,
                &mut [
                    arg(&mut q),
                    arg(&mut kc),
                    arg(&mut s),
                    arg(&mut length),
                    arg(&mut heads),
                    arg(&mut kv_heads),
                    arg(&mut batch),
                    arg(&mut pos),
                ],
            )?;
        }
        let mut g = gate.pointer;
        let mut y = result.pointer;
        if (self.decode_attention_tiling && batch == 1 && length >= 256)
            || (batch > 1 && position >= 8192)
        {
            let mut total_heads = heads * batch;
            self.launch(
                "attention_softmax",
                total_heads,
                256,
                &mut [arg(&mut s), arg(&mut length)],
            )?;
            let mut tiles = length.div_ceil(if batch == 1 { 128 } else { 512 });
            let partial =
                self.output(total_heads as usize * tiles as usize * shape.head_dim * 4)?;
            let mut p = partial.pointer;
            if batch == 1 {
                self.launch(
                    "attention_value_tiles",
                    total_heads * tiles,
                    256,
                    &mut [
                        arg(&mut s),
                        arg(&mut vc),
                        arg(&mut p),
                        arg(&mut length),
                        arg(&mut heads),
                        arg(&mut kv_heads),
                        arg(&mut tiles),
                    ],
                )?;
            } else {
                self.launch(
                    "attention_value_tiles_prefill",
                    total_heads * tiles,
                    256,
                    &mut [
                        arg(&mut s),
                        arg(&mut vc),
                        arg(&mut p),
                        arg(&mut length),
                        arg(&mut heads),
                        arg(&mut kv_heads),
                        arg(&mut batch),
                        arg(&mut tiles),
                    ],
                )?;
            }
            self.launch(
                "attention_reduce_tiles",
                total_heads,
                256,
                &mut [
                    arg(&mut p),
                    arg(&mut g),
                    arg(&mut y),
                    arg(&mut total_heads),
                    arg(&mut tiles),
                ],
            )?;
            return Ok(result);
        }
        self.launch(
            "attention_output",
            heads * batch,
            256,
            &mut [
                arg(&mut s),
                arg(&mut vc),
                arg(&mut g),
                arg(&mut y),
                arg(&mut length),
                arg(&mut heads),
                arg(&mut kv_heads),
            ],
        )?;
        Ok(result)
    }
}
