"""Real-model HTTP check; starts only the native executable and stops its child.

Run: python -X utf8 NativeInference/tests/api_smoke.py --rocm-bin ROCM_BIN
This is opt-in: it loads the 27B model onto the GPU.
"""
import argparse
import concurrent.futures
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rocm-bin", required=True)
    parser.add_argument("--port", type=int, default=19931)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    executable = root / "NativeInference/target/release/local-rocm.exe"
    model = root / "models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf"
    base = f"http://127.0.0.1:{args.port}"

    def request(path, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        return urllib.request.urlopen(
            urllib.request.Request(base + path, data=data, headers={"Content-Type": "application/json"}),
            timeout=180,
        )

    def chat(text, **options):
        body = {"messages": [{"role": "user", "content": text}], "max_tokens": 32, "reasoning_effort": "none"}
        body.update(options)
        with request("/v1/chat/completions", body) as response:
            return json.load(response)

    log_path = root / "NativeInference/api-smoke.log"
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            [str(executable), "serve", str(model), args.rocm_bin, "2048", "127.0.0.1", str(args.port)],
            stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        try:
            deadline = time.monotonic() + 90
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f"Native server exited; see {log_path}")
                try:
                    with request("/health") as response:
                        assert json.load(response)["backend"] == "native-rocm"
                    break
                except (OSError, urllib.error.URLError):
                    if time.monotonic() > deadline:
                        raise TimeoutError(f"Native server did not become ready; see {log_path}")
                    time.sleep(0.5)
            with request("/props") as response:
                props = json.load(response)
                assert props["default_generation_settings"]["n_ctx"] == 2048
                assert props["modalities"]["vision"] is False
            print("PASS: health / props", flush=True)

            with concurrent.futures.ThreadPoolExecutor() as pool:
                pending = pool.submit(chat, "What is 2 + 2? Reply with just the number.")
                time.sleep(1)
                start = time.monotonic()
                with request("/health") as response:
                    assert json.load(response)["status"] == "ok"
                assert time.monotonic() - start < 2, "Health checks blocked behind inference"
                result = pending.result()
                assert result["choices"][0]["message"]["content"].strip() == "4", result
                assert result["usage"]["total_tokens"] > 0
            print("PASS: arithmetic / usage / health during inference", flush=True)

            result = chat("日本の首都を都市名だけで答えてください。")
            assert "東京" in result["choices"][0]["message"]["content"], result
            print("PASS: Japanese generation / state reset", flush=True)

            # Exercise large batches, then restore the stable prompt checkpoint
            # through HTTP. Context accounting still includes cached tokens.
            long_text = (
                "Read this text, then follow the final instruction.\n"
                + "The local inference server processes text on an AMD GPU. " * 90
                + "\nReply with just OK."
            )
            first = chat(long_text, max_tokens=4)
            second = chat(long_text, max_tokens=4)
            first_usage, second_usage = first["usage"], second["usage"]
            assert first_usage["prompt_tokens"] > 256, first_usage
            assert first_usage["prompt_tokens_details"]["cached_tokens"] == 0, first_usage
            assert second_usage["prompt_tokens_details"]["cached_tokens"] >= second_usage["prompt_tokens"] - 16, second_usage
            assert first["choices"][0]["message"] == second["choices"][0]["message"], (first, second)
            print("PASS: long prefill / checkpoint reuse / identical cached answer", flush=True)

            # The GUI stores visible assistant content (including leading blank
            # lines), but removes reasoning_content from the next API request.
            reasoned = chat(long_text, max_tokens=128, reasoning_effort="xhigh")
            message = reasoned["choices"][0]["message"]
            assert message.get("reasoning_content"), reasoned
            assert message["content"].strip(), reasoned
            history = [
                {"role": "user", "content": long_text},
                {"role": "assistant", "content": message["content"]},
                {"role": "user", "content": "Reply with just NEXT."},
            ]
            continued_body = {"messages": history, "max_tokens": 8, "reasoning_effort": "none"}
            with request("/v1/chat/completions", continued_body) as response:
                continued = json.load(response)
            cached = continued["usage"]["prompt_tokens_details"]["cached_tokens"]
            assert cached >= reasoned["usage"]["prompt_tokens"] - 16, continued
            assert "NEXT" in continued["choices"][0]["message"]["content"], continued

            # An unrelated conversation resets state; compare a full replay of
            # the same continued history against its cached evaluation.
            chat("Reply with just RESET.", max_tokens=8)
            with request("/v1/chat/completions", continued_body) as response:
                replayed = json.load(response)
            assert replayed["usage"]["prompt_tokens_details"]["cached_tokens"] == 0, replayed
            assert replayed["choices"][0]["message"] == continued["choices"][0]["message"], (continued, replayed)
            print(f"PASS: GUI reasoning removed / continued conversation reused {cached} tokens / full replay matches", flush=True)

            body = {"messages": [{"role": "user", "content": "Reply with just OK."}],
                    "max_tokens": 16, "reasoning_effort": "none", "stream": True,
                    "stream_options": {"include_usage": True}}
            content, usage, done = "", None, False
            with request("/v1/chat/completions", body) as response:
                for line in response:
                    if not line.startswith(b"data: "):
                        continue
                    value = line[6:].strip()
                    if value == b"[DONE]":
                        done = True
                        break
                    event = json.loads(value)
                    if event["choices"]:
                        content += event["choices"][0]["delta"].get("content", "")
                    usage = event.get("usage", usage)
            assert done and "OK" in content and usage["completion_tokens"] > 0, (content, usage, done)
            print("PASS: SSE / UTF-8 / final usage / DONE", flush=True)

            result = chat("Call get_weather for Tokyo. Do not answer from memory.", max_tokens=128,
                          tools=[{"type": "function", "function": {"name": "get_weather",
                                  "description": "Get current weather for a city",
                                  "parameters": {"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}}}])
            call = result["choices"][0]["message"]["tool_calls"][0]
            assert call["function"]["name"] == "get_weather", result
            assert "Tokyo" in json.loads(call["function"]["arguments"])["city"], result
            print("PASS: structured tool call", flush=True)

            for body in [{"messages": [{"role": "user", "content": [{"type": "image_url"}]}]},
                         {"messages": [{"role": "user", "content": "hi"}], "max_tokens": 4096}]:
                try:
                    request("/v1/chat/completions", body)
                    raise AssertionError("Unsupported input unexpectedly accepted")
                except urllib.error.HTTPError as error:
                    assert error.code == 400
            print("PASS: explicit image / context errors", flush=True)
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)


if __name__ == "__main__":
    main()
