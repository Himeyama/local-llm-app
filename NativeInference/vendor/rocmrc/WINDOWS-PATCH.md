# Local Windows loader adaptation

Upstream: rocmrc 0.5.1, https://github.com/Reis-McMillan/rocmrc

The upstream MIT / Apache-2.0 licenses are retained in this directory.
Generated HIP, HIPRTC and rocBLAS bindings, algorithms and safe wrappers are unchanged.

The local change is confined to `src/lib.rs`: callers can configure immutable,
explicit DLL directories; the Windows loader recognizes ROCm 7's
`amdhip64_7.dll` and `hiprtc0702.dll` names instead of Linux-style sonames.
This avoids creating renamed copies of AMD DLLs or changing the process PATH.
Normal upstream loading behavior is retained on other platforms.
