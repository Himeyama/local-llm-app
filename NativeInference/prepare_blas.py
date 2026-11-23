"""Install existing AMD rocBLAS DLLs and matching kernel assets; no downloads."""
import argparse
import errno
import os
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-bin", type=Path, required=True)
    args = parser.parse_args()
    source = args.source_bin.resolve(strict=True)
    repository = Path(__file__).resolve().parent.parent
    destination = (repository / "runtime/rocm/blas/bin").resolve()
    if not destination.is_relative_to(repository):
        raise ValueError("Runtime destination must stay inside the repository")
    # rocBLAS loads hipBLASLt and both libraries load GPU-specific assets.
    libraries = [source / "rocblas.dll", source / "libhipblaslt.dll"]
    asset_roots = [source / "rocblas", source / "hipblaslt"]
    if not all(item.is_file() for item in libraries):
        raise FileNotFoundError("Expected rocblas.dll and libhipblaslt.dll")
    if not all(item.is_dir() for item in asset_roots):
        raise FileNotFoundError("Expected rocblas/ and hipblaslt/ kernel assets")
    items = libraries + [item for root in asset_roots for item in sorted(root.rglob("*"))]
    count = 0
    for item in items:
        if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
            raise ValueError(f"SDK links are not followed: {item}")
        if not item.is_file():
            continue
        target = destination / item.relative_to(source)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if os.path.samefile(item, target):
                continue
            raise FileExistsError(f"Refusing to overwrite {target}")
        try:
            os.link(item, target)
        except OSError as error:
            if error.errno != errno.EXDEV:
                raise
            shutil.copy2(item, target)
        count += 1
    print(f"Prepared rocBLAS: {count} files at {destination}")


if __name__ == "__main__":
    main()
