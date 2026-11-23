"""Inspect and extract HSISPKG3 without installing shortcuts/registry entries."""
import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("installer", type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    installer = args.installer.resolve(strict=True)
    expected = json.loads(args.manifest.read_text(encoding="utf-8"))
    destination = ROOT / "publish/installer" / ("verification-" + datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
    destination.mkdir(parents=True)
    archive = destination / "payload.7z"
    with installer.open("rb") as stream:
        stream.seek(-32, 2)
        magic, bundle_size, payload_size, mode = struct.unpack("<8sqqq", stream.read(32))
        assert magic == b"HSISPKG3" and mode == 0
        assert bundle_size + payload_size + 32 == installer.stat().st_size
        stream.seek(bundle_size)
        assert stream.read(8) == b"HSIS7Z01"
        metadata_size = struct.unpack("<I", stream.read(4))[0]
        assert 0 < metadata_size < 64 * 1024 * 1024
        manifest = json.loads(stream.read(metadata_size))
        remaining = payload_size - metadata_size - 12
        with archive.open("wb") as output:
            while remaining:
                chunk = stream.read(min(4 * 1024 * 1024, remaining))
                if not chunk:
                    raise ValueError("Truncated installer payload")
                output.write(chunk)
                remaining -= len(chunk)
    files = manifest["Files"]
    paths = {item["Destination"].replace("\\", "/") for item in files}
    assert not any(path.lower().endswith(".gguf") or "runtime/llama.cpp/" in path.lower() for path in paths)
    expected_paths = {"app/" + item["path"] for item in expected}
    assert paths - expected_paths <= {".hsis/Application.ico"}, paths - expected_paths
    assert expected_paths <= paths
    for index, item in enumerate(files):
        assert item["Entry"] == f"files/{index}" and item.get("RuntimePath") is None
    for operation in manifest["Uninstall"]:
        assert operation["Command"] != "rmdir" or "/r" not in operation["Arguments"], "Recursive uninstall would remove user data"
        assert not any("$LOCALAPPDATA" in argument or "$APPDATA" in argument for argument in operation["Arguments"])
    deleted = {operation["Arguments"][0].replace("\\", "/") for operation in manifest["Uninstall"] if operation["Command"] == "delete"}
    assert {"$INSTDIR/" + path for path in paths} <= deleted
    registrations = [item for item in manifest["Install"] if item["Command"] == "writeregstr"]
    assert any(item["Arguments"][2] == "DisplayName" and item["Arguments"][3] == "Local LLM" for item in registrations)
    assert any(item["Arguments"][2] == "UninstallString" and item["Arguments"][3] == '"$INSTDIR\\Uninstall.exe"' for item in registrations)

    tar = Path(os.environ["SystemRoot"]) / "System32/tar.exe"
    names = subprocess.check_output([str(tar), "-tf", str(archive)], text=True, encoding="utf-8").splitlines()
    allowed = {"", "files"} | {item["Entry"] for item in files}
    assert all(name.removeprefix("./").rstrip("/") in allowed for name in names)
    raw = destination / "raw"
    raw.mkdir()
    subprocess.run([str(tar), "-xf", str(archive), "-C", str(raw)], check=True)
    installed = destination / "installed"
    for item in files:
        target = (installed / item["Destination"].replace("\\", "/")).resolve()
        if not target.is_relative_to(installed.resolve()):
            raise ValueError("Installer destination escaped verification folder")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(raw / item["Entry"], target)
    app = installed / "app"
    for item in expected:
        path = app / item["path"]
        assert path.stat().st_size == item["size"] and digest(path) == item["sha256"], item["path"]
    report = {"installer": str(installer), "sha256": digest(installer), "files_verified": len(expected),
              "hsis_format": "HSISPKG3", "payload": str(app), "models_included": False,
              "user_data_preserving_uninstall": True, "registry_or_shortcuts_modified": False}
    (destination / "verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)


if __name__ == "__main__":
    main()
