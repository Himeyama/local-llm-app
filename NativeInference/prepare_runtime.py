"""Prepare a self-contained local runtime from an already installed AMD SDK.

No packages are downloaded and no Python/PyTorch runtime is bundled. Read-only
AMD files use hard links on the same volume; cross-volume installs use copies.
Removing the source SDK does not remove these files.
"""
import argparse
import errno
import os
from pathlib import Path
import shutil


def prepare(sdk_root: Path, repository: Path) -> None:
    source = sdk_root.resolve(strict=True)
    destination = (repository / "runtime/rocm").resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("Source SDK and destination must be separate directory trees")
    if not (source / "bin/amdhip64_7.dll").is_file():
        raise FileNotFoundError("Expected ROCm 7 SDK bin/amdhip64_7.dll")
    if not list((source / "bin").glob("hiprtc0*.dll")):
        raise FileNotFoundError("Expected the matching HIPRTC DLL in SDK bin")
    executable = repository / "NativeInference/target/release/local-rocm.exe"
    if not executable.is_file():
        raise FileNotFoundError("Build NativeInference in release mode first")

    linked = copied = 0
    for folder in ("bin", "include", "lib", "libexec", "share", ".info"):
        for item in sorted((source / folder).rglob("*")):
            if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
                raise ValueError(f"SDK links are not followed: {item}")
            if not item.is_file():
                continue
            target = destination / item.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                if os.path.samefile(item, target):
                    continue
                raise FileExistsError(f"Refusing to overwrite an existing runtime asset: {target}")
            try:
                os.link(item, target)
                linked += 1
            except OSError as error:
                if error.errno != errno.EXDEV:
                    raise
                shutil.copy2(item, target)
                copied += 1

    native_directory = repository / "runtime/native"
    native_directory.mkdir(parents=True, exist_ok=True)
    shutil.copy2(executable, native_directory / executable.name)
    print(f"Prepared {destination}: {linked} hard links, {copied} copies")
    print(f"Executable: {native_directory / executable.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-root", required=True, type=Path)
    args = parser.parse_args()
    prepare(args.sdk_root, Path(__file__).resolve().parent.parent)


if __name__ == "__main__":
    main()
