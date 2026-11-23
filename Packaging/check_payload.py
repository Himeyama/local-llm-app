"""Check a staged/extracted application without touching user settings or models."""
import argparse
import json
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("payload", type=Path)
    parser.add_argument("--gpu", action="store_true")
    args = parser.parse_args()
    app = args.payload.resolve(strict=True)
    marker = json.loads((app / "local-llm.install.json").read_text(encoding="utf-8"))
    assert marker["default_server"] == r"runtime\native\local-rocm.exe"
    assert marker["models_included"] is False
    assert not list(app.rglob("*.gguf"))
    assert not (app / "runtime/llama.cpp").exists()
    for relative in ["LocalLlm.Gui.exe", "LocalLlm.Gui.runtimeconfig.json", "LocalLlm.Gui.pri",
                     "App.xbf", "MainWindow.xbf", "Controls/WorkspaceExplorerView.xbf",
                     "Microsoft.UI.Xaml.dll", "Start-LlamaServer.ps1",
                     "runtime/native/local-rocm.exe", "runtime/node/LICENSE", "BrowserSearch/node_modules/@playwright/mcp/package.json"]:
        assert (app / relative).is_file(), relative
    config = json.loads((app / "LocalLlm.Gui.runtimeconfig.json").read_text())
    assert "includedFrameworks" in config["runtimeOptions"], "The .NET runtime must be self-contained"

    environment = os.environ.copy()
    windows = Path(os.environ["SystemRoot"])
    environment["PATH"] = os.pathsep.join([str(windows / "System32"), str(windows)])
    for name in list(environment):
        if name.upper().startswith(("LOCAL_ROCM", "HIP_PATH", "ROCM_PATH", "ROCBLAS_TENSILE", "HIPBLASLT_TENSILE")):
            environment.pop(name)
    node = app / "runtime/node/node.exe"
    result = subprocess.check_output([str(node), "--version"], env=environment, text=True).strip()
    assert result == "v" + marker["node_version"], result
    javascript = "import {normalizeResults} from './search.mjs'; const rows=normalizeResults([{title:'日本語の検索結果',url:'https://example.com/',snippet:'抜粋も日本語です。'}]); if(rows[0].title!=='日本語の検索結果')process.exit(1);console.log('PASS: bundled Node / Playwright MCP module import / Japanese text');"
    subprocess.run([str(node), "--input-type=module", "-e", javascript], cwd=app / "BrowserSearch", env=environment, check=True)
    if args.gpu:
        executable = app / "runtime/native/local-rocm.exe"
        sdk = app / "runtime/rocm/bin"
        subprocess.run([str(executable), "gpu-check", str(sdk)], cwd=app, env=environment, check=True)
        subprocess.run([str(executable), "prefill-bench", str(sdk)], cwd=app, env=environment, check=True)
    print("PASS: native default / model exclusion / self-contained .NET / packaged runtime paths", flush=True)


if __name__ == "__main__":
    main()
