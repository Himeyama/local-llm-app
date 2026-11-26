"""Build the self-contained Windows payload and an HSIS 0.11.2 installer."""
import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import urllib.request
import uuid
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PACKAGING = ROOT / "Packaging"
LOCK = json.loads((PACKAGING / "toolchain.json").read_text(encoding="utf-8"))
TOOLS = ROOT / "bin/installer-tools"
OUTPUT = ROOT / "publish/installer"


def run(arguments, **options):
    print("実行:", subprocess.list2cmdline([str(arg) for arg in arguments]), flush=True)
    subprocess.run([str(arg) for arg in arguments], cwd=ROOT, check=True, **options)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def copy_tree(source, destination, exclude=()):
    source = source.resolve()
    destination = destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("コピー元と配布先は別のディレクトリツリーにしてください。")
    if not source.is_dir():
        raise FileNotFoundError(source)
    for item in sorted(source.rglob("*")):
        relative = item.relative_to(source)
        if any(relative == path or path in relative.parents for path in exclude):
            continue
        if item.is_symlink() or item.is_junction():
            raise ValueError(f"リンクは同梱しません: {item}")
        if item.is_file():
            target = destination / item.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, target)


def prepare_hsis(source, skip_build):
    source = source.resolve()
    if not source.exists():
        source.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--branch", LOCK["hsis"]["tag"], "--depth", "1", LOCK["hsis"]["repository"], source])
    commit = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if commit != LOCK["hsis"]["commit"]:
        raise ValueError("hsis のソースが指定の v0.11.2 コミットではありません。")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise ValueError("hsis ソースに変更があります。公式の固定ソースを指定してください。")
    destination = TOOLS / "hsis-cli"
    executable = destination / "hsis.exe"
    if not skip_build:
        run(["dotnet", "publish", source / "src/Hsis.Cli/Hsis.Cli.csproj", "-c", "Release", "-r", "win-x64",
             "--self-contained", "true", "-o", destination, "-p:NuGetAudit=false"])
    if not executable.is_file():
        raise FileNotFoundError(executable)
    template_version = (destination / "Template/Directory.Build.props").read_text(encoding="utf-8")
    if "<Version>0.11.2</Version>" not in template_version:
        raise ValueError("hsis のテンプレートが v0.11.2 ではありません。")
    return executable


def prepare_node(destination):
    node = LOCK["node"]
    archive = TOOLS / Path(node["url"]).name
    archive.parent.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        print(f"公式 Node.js {node['version']} を取得しています…", flush=True)
        temporary = archive.with_suffix(".download")
        with urllib.request.urlopen(node["url"], timeout=60) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        if sha256(temporary) != node["sha256"]:
            raise ValueError("Node.js の SHA-256 が一致しません。")
        temporary.replace(archive)
    if sha256(archive) != node["sha256"]:
        raise ValueError("保存済み Node.js の SHA-256 が一致しません。")
    prefix = f"node-v{node['version']}-win-x64/"
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as package:
        for name in ["node.exe", "LICENSE", "README.md"]:
            (destination / name).write_bytes(package.read(prefix + name))


def dotnet_licenses(destination):
    assets = json.loads((ROOT / "obj/project.assets.json").read_text(encoding="utf-8"))
    for name, library in assets["libraries"].items():
        if library.get("type") != "package":
            continue
        folder = next((Path(root) / library["path"] for root in assets["packageFolders"] if (Path(root) / library["path"]).is_dir()), None)
        if folder is None:
            continue
        for item in folder.iterdir():
            if item.is_file() and any(word in item.name.lower() for word in ["license", "notice", "copyright"]):
                target = destination / name.replace("/", "-") / item.name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(item, target)


def stage_payload(version):
    stage = OUTPUT / ("payload-" + datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
    app = stage / "app"
    app.mkdir(parents=True)
    run(["dotnet", "publish", ROOT / "LocalLlm.Gui.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", f"-p:Version={version}", "-o", app])
    prepare_node(app / "runtime/node")
    dotnet_licenses(app / "licenses/dotnet")
    shutil.copy2(PACKAGING / "利用方法.txt", app / "利用方法.txt")
    (app / "local-llm.install.json").write_text(json.dumps({
        "application": "LocalLlmGui", "version": version, "hsis_version": LOCK["hsis"]["version"],
        "default_server": r"runtime\native\local-rocm.exe", "server_included": False, "models_included": False,
        "node_version": LOCK["node"]["version"]}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    required = ["LocalLlm.Gui.exe", "LocalLlm.Gui.pri", "App.xbf", "MainWindow.xbf", "Controls/WorkspaceExplorerView.xbf",
                "Start-LlamaServer.ps1", "Start-RocmServer.ps1", "runtime/node/node.exe",
                "BrowserSearch/search.mjs", "BrowserSearch/node_modules/@playwright/mcp/package.json", "Editor/index.html"]
    for name in required:
        if not (app / name).is_file():
            raise FileNotFoundError(f"配布に必要なファイルがありません: {name}")
    files = sorted(item for item in app.rglob("*") if item.is_file())
    validate_exclusions(app)
    print("配布ファイルの SHA-256 を計算しています…", flush=True)
    inventory = [{"path": item.relative_to(app).as_posix(), "size": item.stat().st_size, "sha256": sha256(item)} for item in files]
    (stage / "payload-manifest.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PAYLOAD={app}", flush=True)
    print(f"同梱: {len(files)} files / {sum(item['size'] for item in inventory) / 1024**3:.2f} GiB", flush=True)
    return app


def validate_exclusions(app):
    if any(item.suffix.lower() == ".gguf" or item.name.lower() in {"local-rocm.exe", "llama-server.exe"}
           for item in app.rglob("*") if item.is_file()) or any(
               (app / path).exists() for path in ("runtime/llama.cpp", "runtime/native", "runtime/rocm")):
        raise ValueError("モデルまたは推論サーバーが誤って同梱されています。")


def create_uninstall_include(app):
    lines = ["; Generated from the payload. Never recursively delete user-added files."]
    for item in sorted(app.rglob("*")):
        if item.is_file():
            relative = str(item.relative_to(app))
            if any(char in relative for char in ['"', '$', '\n', '\r']):
                raise ValueError(f"非対応のファイル名: {relative}")
            lines.append(f'Delete "$INSTDIR\\app\\{relative}"')
    for item in sorted((item for item in app.rglob("*") if item.is_dir()), key=lambda item: len(item.parts), reverse=True):
        lines.append(f'RMDir "$INSTDIR\\app\\{item.relative_to(app)}"')
    lines.append('RMDir "$INSTDIR\\app"')
    target = app.parent / "uninstall-files.nsh"
    target.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=ET.parse(ROOT / "LocalLlm.Gui.csproj").findtext("./PropertyGroup/Version"))
    parser.add_argument("--hsis-source", type=Path, default=TOOLS / "hsis-v0.11.2")
    parser.add_argument("--skip-build", action="store_true", help="Reuse hsis binary; GUI is still published")
    parser.add_argument("--stage-only", action="store_true")
    parser.add_argument("--payload", type=Path, help="Package an existing staged payload")
    args = parser.parse_args()
    project_version = ET.parse(ROOT / "LocalLlm.Gui.csproj").findtext("./PropertyGroup/Version")
    if args.version != project_version:
        raise ValueError("Installer version must match LocalLlm.Gui.csproj")
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version) or any(int(part) > 65535 for part in args.version.split(".")):
        raise ValueError("Version must be MAJOR.MINOR.PATCH, each component <= 65535")
    app = args.payload.resolve(strict=True) if args.payload else stage_payload(args.version)
    if not app.is_relative_to(OUTPUT.resolve()) or app.name != "app":
        raise ValueError("パッケージ元は publish/installer 配下の app に限定します。")
    marker = json.loads((app / "local-llm.install.json").read_text(encoding="utf-8"))
    if marker["version"] != args.version:
        raise ValueError("ペイロードのバージョンが一致しません。")
    if marker.get("server_included") is not False:
        raise ValueError("推論サーバーを同梱しないペイロードが必要です。")
    validate_exclusions(app)
    info = {"version": args.version, "payload": str(app), "hsis_version": LOCK["hsis"]["version"]}
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / "latest-build.json").write_text(json.dumps(info, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.stage_only:
        return
    hsis = prepare_hsis(args.hsis_source, args.skip_build)
    uninstall = create_uninstall_include(app)
    destination = app.parent / "distribution"
    destination.mkdir(exist_ok=True)
    previous = set(destination.glob("Setup-*/*-Setup.exe"))
    environment = os.environ.copy()
    seven_zip = shutil.which("7z") or str(Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "7-Zip/7z.exe")
    if "HSIS_7ZIP_PATH" not in environment and Path(seven_zip).is_file():
        environment["HSIS_7ZIP_PATH"] = seven_zip
    run([hsis, "build", PACKAGING / "LocalLlmGui.nsi", "--output", destination,
         f"-DAPP_VERSION={args.version}", f"-DSOURCE_DIR={app}", f"-DUNINSTALL_FILE={uninstall}"], env=environment)
    installers = list(set(destination.glob("Setup-*/*-Setup.exe")) - previous)
    if len(installers) != 1:
        raise RuntimeError("生成されたセットアップが一意に決まりません。")
    installer = installers[0]
    checksum = sha256(installer)
    installer.with_suffix(".exe.sha256").write_text(f"{checksum}  {installer.name}\n", encoding="ascii")
    info.update({"installer": str(installer), "sha256": checksum, "size": installer.stat().st_size})
    stable = OUTPUT / installer.name
    shutil.copy2(installer, stable)
    shutil.copy2(installer.with_suffix(".exe.sha256"), stable.with_suffix(".exe.sha256"))
    info["installer"] = str(stable)
    readme = OUTPUT / f"LocalLlmGui-{args.version}-win-x64-README.txt"
    shutil.copy2(PACKAGING / "利用方法.txt", readme)
    info["readme"] = str(readme)
    (OUTPUT / "latest-build.json").write_text(json.dumps(info, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"INSTALLER={installer}\nSHA256={checksum}", flush=True)


if __name__ == "__main__":
    main()
