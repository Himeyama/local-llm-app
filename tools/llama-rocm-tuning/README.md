# llama.cpp b12751 ROCm: R9700 experiment

`b12751-rdna4-wave4.patch` applies to upstream llama.cpp b12751 (commit
`532d67426`). It changes the RDNA4 Q4_K and Q6_K single-column MMVQ kernel
from eight wavefronts per block to four. Other types, batch sizes and GPU
architectures retain the upstream settings. Apply it at the root of an
unmodified b12751 source tree with `git apply`.

The experiment is specific to the tested AMD Radeon AI PRO R9700 and
Qwen3.8-27B-Uncensored-Q4_K_M model. It is not a general RDNA4 tuning claim.
The patch changes the floating-point reduction order; outputs can differ.
It has not been integrated into the GUI's default server.

## Measurements (2026-11-26)

All comparisons used a locally rebuilt b12751 HIP backend, full GPU
offload, FP16 KV, flash attention, batch 2048, microbatch 512, no speculation,
and sequential processes. `llama-bench -p 0 -n 128 -r 5` yielded:

| Order | Upstream | Patched |
| --- | ---: | ---: |
| baseline then patched | 32.458 | 32.814 tokens/s |
| patched then baseline | 32.551 | 32.763 tokens/s |

These runs used the existing `runtime/llama.cpp` and `runtime/rocm/blas/bin`
DLL directories. The per-run samples are in `b12751-r9700-results.json`.
With the ROCm 10 SDK DLL directory instead, `-d 8192 -n 128 -r 3` measured
31.609 versus 31.752 tokens/s. A separate `-n 128 -r 3` comparison of the
final DLL with the same SDK measured 32.725 versus 32.780 tokens/s. These
are small gains; they do not establish that the main inference bottleneck
has been eliminated.

An experimental two-wave version scored 32.643 tokens/s. Another version
also using four waves for two to four columns reduced a single Japanese MTP
sample from 51.13 to 48.88 tokens/s; neither variant is in the patch.

## Correctness and limits

The upstream `test-backend-ops` CPU comparisons passed 206 supported
Q4_K/Q6_K `MUL_MAT` cases and 147 supported `MUL_MAT_FUSION` cases. The
final rebuilt DLL was tested again with the ROCm 10 SDK DLLs. Unsupported
cases were skipped by the upstream test runner.

For one Japanese raw-completion prompt, the baseline and patched backends
produced identical 201-token MTP output. With MTP disabled, they diverged
at token 182. This is consistent with a change in reduction order but the
precise propagation through the model is not established. No broad
model-quality or long-context accuracy study was performed.

## Follow-up: source of the divergence

The baseline and patched backends each produced identical output in two
repeated runs. With the identical prompt and 181 generated tokens, the
182nd-token candidates had the following softmax probabilities (`temperature`
set to -1 for greedy selection with probabilities reported):

| Candidate | Baseline | Patched |
| --- | ---: | ---: |
| ` for` | 30.620% | 29.493% |
| ` (` | 30.069% | 30.392% |

To isolate the kernel from the model, `b12751-deterministic-probe.patch`
adds deterministic inputs and an opt-in binary output dump to the upstream
test runner. For a Q4_K, f32, m=1023, n=1, k=4096 matmul, the two CPU
reference outputs matched bit-for-bit; 734 of the 1,023 GPU values differed,
with a maximum absolute difference of 7.62939453125e-6. Repeating either
backend yielded identical bytes. The change to `calc_nwarps` changes how the
kernel distributes blocks among threads and combines FP32 partial sums, so
the observed kernel difference is consistent with floating-point rounding.
The intermediate operations that amplify that difference into the measured
logit gap were not individually traced. The scores and test measurements are
in `b12751-cause-evidence.json`.

The tested final runtime is `runtime/llama-b12751-final` and requires the
matching ROCm 10 SDK binaries on `PATH`; these runtime files are not tracked
by Git. Its `ggml-hip.dll` is built for `gfx1201` only. Do not replace the
multi-architecture official backend with it on an untested GPU.

The upstream HIP build was configured with `GGML_BACKEND_DL=ON`,
`GGML_NATIVE=OFF`, `GGML_CPU=OFF`, `GGML_HIP=ON`, and
`AMDGPU_TARGETS=gfx1201`, and built in Release mode against ROCm 10.0.0.
The existing official CPU backend DLL supplies the CPU reference tests.
For the runtime prepared in this workspace, reproduce the checks as follows:

```powershell
$sdkBin = (Resolve-Path runtime/rocm10-build-env/Lib/site-packages/_rocm_sdk_devel/bin).Path
$env:PATH = "$sdkBin;$env:PATH"
$test = 'runtime/llama-b12751-final/test-backend-ops.exe'
& $test test -b ROCm0 -o MUL_MAT -p 'type_a=q4_K|type_a=q6_K'
& $test test -b ROCm0 -o MUL_MAT_FUSION -p 'type=q4_K|type=q6_K'
```
