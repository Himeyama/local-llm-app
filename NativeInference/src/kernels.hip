// Independent GGUF kernels. One row per block keeps reduction order explicit.
// Weights remain quantized in VRAM and are decoded inside the dot product.
__device__ __forceinline__ float fp16(const unsigned char* b) {
    unsigned short bits = b[0] | (static_cast<unsigned short>(b[1]) << 8);
    return float(__builtin_bit_cast(_Float16, bits));
}
__device__ __forceinline__ float weight(const unsigned char* w, unsigned tag, unsigned long long i) {
    if (tag == 0) return ((const float*)w)[i];
    if (tag == 1) return fp16(w + i * 2);
    if (tag == 30) {
        unsigned bits = unsigned(w[i * 2] | (unsigned(w[i * 2 + 1]) << 8)) << 16;
        return __builtin_bit_cast(float, bits);
    }
    if (tag == 8) {
        const auto* b = w + (i / 32) * 34;
        return fp16(b) * reinterpret_cast<const signed char*>(b + 2)[i % 32];
    }
    unsigned j = i % 256;
    if (tag == 12 || tag == 13) {
        const auto* b = w + (i / 256) * (tag == 12 ? 144 : 176);
        const auto* s = b + 4;
        unsigned g = j / 32;
        unsigned scale = g < 4 ? s[g] & 63 : (s[g+4] & 15) | ((s[g-4] >> 6) << 4);
        unsigned min = g < 4 ? s[g+4] & 63 : (s[g+4] >> 4) | ((s[g] >> 6) << 4);
        unsigned packed = b[(tag == 12 ? 16 : 48) + (j/64)*32 + j%32];
        unsigned q = j%64 < 32 ? packed & 15 : packed >> 4;
        if (tag == 13) q |= ((b[16+j%32] >> g) & 1) << 4;
        return fp16(b) * scale * q - fp16(b+2) * min;
    }
    // Q6_K: four 32-element quarters share two low-nibble planes.
    const auto* b = w + (i / 256) * 210;
    unsigned h = j/128, lane = j%32, quarter = (j%128)/32;
    unsigned packed = b[h*64 + lane + (quarter%2)*32];
    unsigned low = quarter < 2 ? packed & 15 : packed >> 4;
    unsigned high = (b[128+h*32+lane] >> (quarter*2)) & 3;
    int q = int(low | (high << 4)) - 32;
    return fp16(b+208) * ((const signed char*)(b+192))[j/16] * q;
}
extern "C" __global__ void decode(const unsigned char* w, float* y, unsigned tag, unsigned n) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) y[i] = weight(w, tag, i);
}
template<unsigned Tag>
__device__ __forceinline__ void decode_half_impl(const unsigned char* w, _Float16* y, unsigned n) {
    unsigned t = blockIdx.x * blockDim.x + threadIdx.x;
    unsigned base = (t / 32) * 256 + t % 32;
    #pragma unroll
    for (unsigned group = 0; group < 8; group++) {
        unsigned i = base + group * 32;
        if (i < n) y[i] = (_Float16)weight(w, Tag, i);
    }
}
extern "C" __global__ void decode_half(const unsigned char* w, _Float16* y, unsigned tag, unsigned n) {
    switch (tag) {
        case 1: decode_half_impl<1>(w,y,n); break;
        case 8: decode_half_impl<8>(w,y,n); break;
        case 12: decode_half_impl<12>(w,y,n); break;
        case 13: decode_half_impl<13>(w,y,n); break;
        case 14: decode_half_impl<14>(w,y,n); break;
    }
}
extern "C" __global__ void float_to_half(const float* x, _Float16* y, unsigned n) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) y[i] = (_Float16)x[i];
}

// Each wave handles an entire quantization block. A lane owns the eight
// values at offsets lane + 32*g, sharing block metadata across those values.
// Four waves cooperate on a row, instead of decoding one scalar per thread.
template<unsigned Tag>
__device__ __forceinline__ void quantized_matvec_impl(const unsigned char* w, const float* x, float* y, unsigned cols, float* sums) {
    unsigned row = blockIdx.x, t = threadIdx.x;
    unsigned lane = t % 32, wave = t / 32;
    unsigned blocks = cols / 256;
    float sum = 0;
    for (unsigned block = wave; block < blocks; block += 4) {
        unsigned long long offset = ((unsigned long long)row * blocks + block) * 256;
        #pragma unroll
        for (unsigned group = 0; group < 8; group++) {
            unsigned j = lane + group * 32;
            sum += weight(w, Tag, offset + j) * x[block * 256 + j];
        }
    }
    // Wave reductions avoid a block-wide barrier at every reduction stage.
    for (unsigned step = 16; step; step /= 2) sum += __shfl_down(sum, step, 32);
    if (lane == 0) sums[wave] = sum;
    __syncthreads();
    if (t == 0) y[row] = (sums[0] + sums[1]) + (sums[2] + sums[3]);
}
extern "C" __global__ void quantized_matvec(const unsigned char* w,const float* x,float* y,unsigned tag,unsigned cols) {
    __shared__ float sums[4];
    switch (tag) {
        case 12: quantized_matvec_impl<12>(w,x,y,cols,sums); break;
        case 13: quantized_matvec_impl<13>(w,x,y,cols,sums); break;
        case 14: quantized_matvec_impl<14>(w,x,y,cols,sums); break;
    }
}

template<unsigned Tag>
__device__ __forceinline__ void matvec_impl(const unsigned char* w, const float* x, float* y, unsigned cols, float* sums) {
    unsigned row = blockIdx.x, t = threadIdx.x;
    float sum = 0;
    for (unsigned c = t; c < cols; c += 256) {
        sum += weight(w, Tag, (unsigned long long)row * cols + c) * x[c];
    }
    sums[t] = sum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) sums[t] += sums[t + stride];
        __syncthreads();
    }
    if (t == 0) y[row] = sums[0];
}
// Up to eight prompt tokens share each decoded weight. This avoids reading and
// unpacking the entire model once per token during prefill.
template<unsigned Tag>
__device__ __forceinline__ void matmul_impl(const unsigned char* w, const float* x, float* y, unsigned cols, unsigned batch, float sums[8][256]) {
    unsigned row = blockIdx.x, t = threadIdx.x;
    float accumulators[8] = {};
    for (unsigned c = t; c < cols; c += 256) {
        float value = weight(w, Tag, (unsigned long long)row * cols + c);
        for (unsigned b = 0; b < 8; b++) if (b < batch) accumulators[b] += value * x[b * cols + c];
    }
    for (unsigned b = 0; b < 8; b++) if (b < batch) sums[b][t] = accumulators[b];
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) {
            for (unsigned b = 0; b < 8; b++) if (b < batch) sums[b][t] += sums[b][t + stride];
        }
        __syncthreads();
    }
    if (t == 0) {
        for (unsigned b = 0; b < 8; b++) if (b < batch) y[b * gridDim.x + row] = sums[b][0];
    }
}

// Select storage once per kernel, so the inner dot product has a constant type.
extern "C" __global__ void matvec(const unsigned char* w,const float* x,float* y,unsigned tag,unsigned cols) {
    __shared__ float sums[256];
    switch (tag) {
        case 0: matvec_impl<0>(w,x,y,cols,sums); break;
        case 1: matvec_impl<1>(w,x,y,cols,sums); break;
        case 8: matvec_impl<8>(w,x,y,cols,sums); break;
        case 12: matvec_impl<12>(w,x,y,cols,sums); break;
        case 13: matvec_impl<13>(w,x,y,cols,sums); break;
        case 14: matvec_impl<14>(w,x,y,cols,sums); break;
        case 30: matvec_impl<30>(w,x,y,cols,sums); break;
    }
}
extern "C" __global__ void matmul(const unsigned char* w,const float* x,float* y,unsigned tag,unsigned cols,unsigned batch) {
    __shared__ float sums[8][256];
    switch (tag) {
        case 0: matmul_impl<0>(w,x,y,cols,batch,sums); break;
        case 1: matmul_impl<1>(w,x,y,cols,batch,sums); break;
        case 8: matmul_impl<8>(w,x,y,cols,batch,sums); break;
        case 12: matmul_impl<12>(w,x,y,cols,batch,sums); break;
        case 13: matmul_impl<13>(w,x,y,cols,batch,sums); break;
        case 14: matmul_impl<14>(w,x,y,cols,batch,sums); break;
        case 30: matmul_impl<30>(w,x,y,cols,batch,sums); break;
    }
}
extern "C" __global__ void rms_norm(const float* x, const float* w, float* y, unsigned n, float eps) {
    __shared__ float sums[256];
    x += blockIdx.x * n;
    y += blockIdx.x * n;
    unsigned t = threadIdx.x;
    float sum = 0;
    for (unsigned i = t; i < n; i += 256) sum += x[i] * x[i];
    sums[t] = sum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) sums[t] += sums[t + stride];
        __syncthreads();
    }
    float scale = rsqrtf(sums[0] / n + eps);
    for (unsigned i = t; i < n; i += 256) y[i] = x[i] * scale * w[i];
}
extern "C" __global__ void add(const float* x, const float* y, float* out, unsigned n) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) out[i] = x[i] + y[i];
}
extern "C" __global__ void swiglu(const float* gate, const float* up, float* out, unsigned n) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) out[i] = gate[i] / (1 + expf(-gate[i])) * up[i];
}

extern "C" __global__ void embedding(const unsigned char* w, float* y, unsigned tag, unsigned long long offset, unsigned n) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) y[i] = weight(w, tag, offset + i);
}
extern "C" __global__ void embeddings(const unsigned char* w, const unsigned* tokens, float* y, unsigned tag, unsigned width, unsigned count) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < count) y[i] = weight(w, tag, (unsigned long long)tokens[i / width] * width + i % width);
}

// State stores only previous inputs, oldest first; each channel is independent.
extern "C" __global__ void conv_step(const float* x, const float* w, float* state, float* y, unsigned channels, unsigned width, unsigned batch) {
    unsigned c = blockIdx.x * blockDim.x + threadIdx.x;
    if (c >= channels) return;
    for (unsigned b = 0; b < batch; b++) {
        float sum = x[c] * w[c * width + width - 1];
        for (unsigned j = 0; j < width - 1; j++) {
            sum += state[c * (width - 1) + j] * w[c * width + j];
        }
        for (unsigned j = 0; j < width - 2; j++) {
            state[c * (width - 1) + j] = state[c * (width - 1) + j + 1];
        }
        state[c * (width - 1) + width - 2] = x[c];
        y[c] = sum / (1 + expf(-sum));
        x += channels;
        y += channels;
    }
}

// One block per value head, one thread per value dimension. Each thread owns
// a disjoint column of the recurrent matrix, so updates require no atomics.
extern "C" __global__ void delta_step(const float* qkv, const float* alpha, const float* beta, const float* decay, const float* bias, float* state, float* y, unsigned key_heads, unsigned value_heads, unsigned batch) {
    __shared__ float q[128], k[128], qs[128], ks[128];
    // GGUF conversion stores value heads in tiled order, unlike the grouped
    // order of Hugging Face checkpoints: v-head h uses k-head (h % key_heads).
    unsigned h = blockIdx.x, t = threadIdx.x, key_head = h % key_heads;
    for (unsigned b = 0; b < batch; b++) {
        float qi = qkv[key_head * 128 + t];
        float ki = qkv[key_heads * 128 + key_head * 128 + t];
        qs[t] = qi * qi;
        ks[t] = ki * ki;
        __syncthreads();
        for (unsigned stride = 64; stride; stride /= 2) {
            if (t < stride) {
                qs[t] += qs[t + stride];
                ks[t] += ks[t + stride];
            }
            __syncthreads();
        }
        q[t] = qi * rsqrtf(qs[0] + 1e-6f) * rsqrtf(128.0f);
        k[t] = ki * rsqrtf(ks[0] + 1e-6f);
        __syncthreads();

        float a = alpha[h] + bias[h];
        float softplus = fmaxf(a, 0.0f) + log1pf(expf(-fabsf(a)));
        float retention = expf(decay[h] * softplus); // GGUF stores -exp(A_log).
        float rate = 1 / (1 + expf(-beta[h]));
        float memory = 0;
        for (unsigned j = 0; j < 128; j++) memory += state[(h * 128 + j) * 128 + t] * retention * k[j];
        float delta = (qkv[2 * key_heads * 128 + h * 128 + t] - memory) * rate;
        float output = 0;
        for (unsigned j = 0; j < 128; j++) {
            unsigned index = (h * 128 + j) * 128 + t;
            float updated = state[index] * retention + k[j] * delta;
            state[index] = updated;
            output += updated * q[j];
        }
        y[h * 128 + t] = output;
        // All threads must finish reading shared Q/K before the next token.
        __syncthreads();
        qkv += (2 * key_heads + value_heads) * 128;
        alpha += value_heads;
        beta += value_heads;
        y += value_heads * 128;
    }
}
extern "C" __global__ void gated_norm(const float* x,const float* gate,const float* w,float* y,float eps) {
    __shared__ float sum[128];
    unsigned t = threadIdx.x, i = blockIdx.x * 128 + t;
    sum[t] = x[i] * x[i];
    __syncthreads();
    for (unsigned stride = 64; stride; stride /= 2) {
        if (t < stride) sum[t] += sum[t + stride];
        __syncthreads();
    }
    y[i] = x[i] * rsqrtf(sum[0] / 128 + eps) * w[t] * gate[i] / (1 + expf(-gate[i]));
}
extern "C" __global__ void prepare_qk(const float* x,const float* w,float* y,float* gate,unsigned position,unsigned rotary,float base,float eps,unsigned query,unsigned heads) {
    __shared__ float sum[256],normalized[256];
    unsigned t = threadIdx.x, h = blockIdx.x, offset = h * 256 * (query ? 2 : 1);
    position += h / heads;
    float v = x[offset + t];
    sum[t] = v * v;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) sum[t] += sum[t + stride];
        __syncthreads();
    }
    normalized[t] = v * rsqrtf(sum[0] / 256 + eps) * w[t];
    __syncthreads();
    float out = normalized[t];
    if (t < rotary) {
        unsigned half = rotary / 2, j = t % half;
        float angle = position * powf(base, -2.0f * j / rotary);
        float other = t < half ? -normalized[t + half] : normalized[t - half];
        out = out * cosf(angle) + other * sinf(angle);
    }
    y[h * 256 + t] = out;
    gate[h * 256 + t] = query ? x[offset + 256 + t] : 0.0f;
}
extern "C" __global__ void store_kv(const float* k,const float* v,_Float16* kc,_Float16* vc,unsigned position,unsigned width,unsigned count) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < count) {
        kc[(unsigned long long)position * width + i] = k[i];
        vc[(unsigned long long)position * width + i] = v[i];
    }
}
extern "C" __global__ void attention_scores(const float* q,const _Float16* kc,float* scores,unsigned length,unsigned heads,unsigned kv_heads,unsigned batch,unsigned position) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= length * heads * batch) return;
    unsigned h = i / length, p = i % length, kh = (h % heads) / (heads / kv_heads);
    if (p > position + h / heads) {
        scores[i] = -__builtin_inff();
        return;
    }
    float sum = 0;
    for (unsigned j = 0; j < 256; j++) sum += q[h * 256 + j] * kc[((unsigned long long)p * kv_heads + kh) * 256 + j];
    scores[i] = sum / 16.0f;
}
// For decode, adjacent lanes cooperate on one dot product. This makes the
// FP16 key loads contiguous instead of striding by an entire KV vector.
extern "C" __global__ void attention_scores_decode(const float* q,const _Float16* kc,float* scores,unsigned length,unsigned heads,unsigned kv_heads) {
    unsigned lane = threadIdx.x % 32;
    unsigned i = blockIdx.x * (blockDim.x / 32) + threadIdx.x / 32;
    if (i >= length * heads) return;
    unsigned h = i / length, p = i % length, kh = h / (heads / kv_heads);
    float sum = 0;
    for (unsigned j = lane; j < 256; j += 32)
        sum += q[h * 256 + j] * kc[((unsigned long long)p * kv_heads + kh) * 256 + j];
    for (unsigned step = 16; step; step /= 2) sum += __shfl_down(sum, step, 32);
    if (lane == 0) scores[i] = sum / 16.0f;
}
extern "C" __global__ void attention_output(float* scores,const _Float16* vc,const float* gate,float* y,unsigned length,unsigned heads,unsigned kv_heads) {
    __shared__ float reduction[256];
    unsigned h = blockIdx.x, t = threadIdx.x, kh = (h % heads) / (heads / kv_heads);
    float maximum = -__builtin_inff();
    for (unsigned p = t; p < length; p += 256) maximum = fmaxf(maximum, scores[h * length + p]);
    reduction[t] = maximum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) reduction[t] = fmaxf(reduction[t], reduction[t + stride]);
        __syncthreads();
    }
    maximum = reduction[0];
    __syncthreads();
    float sum = 0;
    for (unsigned p = t; p < length; p += 256) {
        float probability = expf(scores[h * length + p] - maximum);
        scores[h * length + p] = probability;
        sum += probability;
    }
    reduction[t] = sum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) reduction[t] += reduction[t + stride];
        __syncthreads();
    }
    float out = 0;
    float inverse_sum = 1.0f / reduction[0];
    for (unsigned p = 0; p < length; p++) {
        out += (scores[h * length + p] * inverse_sum) * vc[((unsigned long long)p * kv_heads + kh) * 256 + t];
    }
    y[h * 256 + t] = out / (1 + expf(-gate[h * 256 + t]));
}

// Decode has few query heads. Split the value sum over sequence tiles so long
// histories can occupy more GPU blocks, then reduce the small partial results.
extern "C" __global__ void attention_softmax(float* scores, unsigned length) {
    __shared__ float reduction[256];
    unsigned h = blockIdx.x, t = threadIdx.x;
    float maximum = -__builtin_inff();
    for (unsigned p = t; p < length; p += 256) maximum = fmaxf(maximum, scores[h * length + p]);
    reduction[t] = maximum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) reduction[t] = fmaxf(reduction[t], reduction[t + stride]);
        __syncthreads();
    }
    maximum = reduction[0];
    __syncthreads();
    float sum = 0;
    for (unsigned p = t; p < length; p += 256) {
        float value = expf(scores[h * length + p] - maximum);
        scores[h * length + p] = value;
        sum += value;
    }
    reduction[t] = sum;
    __syncthreads();
    for (unsigned stride = 128; stride; stride /= 2) {
        if (t < stride) reduction[t] += reduction[t + stride];
        __syncthreads();
    }
    float inverse = 1.0f / reduction[0];
    for (unsigned p = t; p < length; p += 256) scores[h * length + p] *= inverse;
}
extern "C" __global__ void attention_value_tiles(const float* scores,const _Float16* vc,float* partial,unsigned length,unsigned heads,unsigned kv_heads,unsigned tiles) {
    // Adjacent blocks share a sequence tile, improving value-cache reuse among
    // query heads that use the same KV head.
    unsigned tile = blockIdx.x / heads, h = blockIdx.x % heads, d = threadIdx.x;
    unsigned kh = h / (heads / kv_heads);
    float sum = 0;
    unsigned end = (tile + 1) * 128;
    if (end > length) end = length;
    for (unsigned p = tile * 128; p < end; p++) sum += scores[h * length + p] * vc[((unsigned long long)p * kv_heads + kh) * 256 + d];
    partial[(h * tiles + tile) * 256 + d] = sum;
}
extern "C" __global__ void attention_reduce_tiles(const float* partial,const float* gate,float* y,unsigned heads,unsigned tiles) {
    unsigned i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= heads * 256) return;
    unsigned h = i / 256, d = i % 256;
    float sum = 0;
    for (unsigned tile = 0; tile < tiles; tile++) sum += partial[(h * tiles + tile) * 256 + d];
    y[i] = sum / (1 + expf(-gate[i]));
}
