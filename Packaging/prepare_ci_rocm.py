"""Prepare the pinned AMD Windows wheels for the installer build on CI."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
packages = root / "bin/installer-tools/amd-wheels"
base = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2"
subprocess.run([sys.executable, "-m", "pip", "install", "--no-deps", "--target", str(packages),
                f"{base}/rocm_sdk_core-7.2.0.dev0-py3-none-win_amd64.whl",
                f"{base}/rocm_sdk_libraries_custom-7.2.0.dev0-py3-none-win_amd64.whl"], check=True)

def unique_bin(filename):
    candidates = list(packages.rglob(filename))
    if len(candidates) != 1:
        raise RuntimeError(f"Expected one {filename}, found {candidates}")
    return candidates[0].parent

sdk_bin = unique_bin("amdhip64_7.dll")
blas_bin = unique_bin("rocblas.dll")
subprocess.run(["cargo", "build", "--locked", "--release", "--manifest-path", str(root / "NativeInference/Cargo.toml")], check=True)
subprocess.run([sys.executable, str(root / "NativeInference/prepare_runtime.py"), "--sdk-root", str(sdk_bin.parent)], check=True)
subprocess.run([sys.executable, str(root / "NativeInference/prepare_blas.py"), "--source-bin", str(blas_bin)], check=True)
