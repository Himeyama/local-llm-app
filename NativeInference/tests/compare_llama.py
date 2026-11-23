"""Opt-in llama.cpp baseline. Loads one 27B model; always stops its own child.

Use the exact token IDs of the Rust model-bench workload, FP16 KV, 131072
context capacity, full GPU offload, greedy sampling, and no speculation.
"""
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request


def main():
    root = Path(__file__).resolve().parents[2]
    target = root / "NativeInference/target"
    model = root / "models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf"
    text = "This is a fixed performance measurement of the local inference engine. " * 300
    encoded = subprocess.check_output(
        [str(target / "release/local-rocm.exe"), "tokenize", str(model), text],
        text=True, encoding="utf-8",
    )
    tokens = json.loads(encoded)[:1356]
    assert len(tokens) == 1356
    port = 19932

    def request(path, body=None):
        data = None if body is None else json.dumps(body).encode()
        with urllib.request.urlopen(urllib.request.Request(
            f"http://127.0.0.1:{port}{path}", data=data,
            headers={"Content-Type": "application/json"},
        ), timeout=300) as response:
            return json.load(response)

    command = [
        str(root / "runtime/llama.cpp/llama-server.exe"), "-m", str(model),
        "-ngl", "99", "-c", "131072", "-np", "1", "-b", "2048", "-ub", "512",
        "-fa", "on", "-ctk", "f16", "-ctv", "f16", "--spec-type", "none",
        "--no-warmup", "--host", "127.0.0.1", "--port", str(port), "--no-webui",
    ]
    with (target / "llama-comparison.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, stdout=log, stderr=log,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 180
            while True:
                if process.poll() is not None:
                    raise RuntimeError("llama-server exited; see target/llama-comparison.log")
                try:
                    request("/health")
                    break
                except (OSError, urllib.error.URLError):
                    if time.monotonic() >= deadline:
                        raise TimeoutError("llama-server startup timed out")
                    time.sleep(0.5)
            started = time.monotonic()
            # 51 emitted tokens require 50 autoregressive forward evaluations;
            # the first emitted token comes directly from the prompt logits.
            result = request("/completion", {
                "prompt": tokens, "n_predict": 51, "temperature": 0,
                "ignore_eos": True, "cache_prompt": False, "stream": False,
                "return_tokens": True,
            })
            report = {"command": command, "prompt_ids": tokens,
                      "wall_seconds": time.monotonic() - started, "response": result}
            (target / "llama-comparison.json").write_text(
                json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(json.dumps({"wall_seconds": report["wall_seconds"],
                              "timings": result.get("timings"),
                              "tokens_evaluated": result.get("tokens_evaluated"),
                              "tokens_predicted": result.get("tokens_predicted")}, indent=2), flush=True)
        finally:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
