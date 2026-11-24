#!/usr/bin/env python3
"""Run a small OpenAI-compatible prompt-cache experiment with synthetic data."""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid


def call(url, key, payload):
    request = urllib.request.Request(url, data=json.dumps(payload).encode(), method="POST",
                                     headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError("API returned HTTP " + str(error.code) + ": " + error.read(2000).decode("utf-8", "replace")) from error
    usage = result.get("usage", {})
    prompt = usage.get("prompt_tokens")
    cached = usage.get("prompt_tokens_details", {}).get("cached_tokens")
    metrics = {"prompt_tokens": prompt, "cached_tokens": cached,
            "completion_tokens": usage.get("completion_tokens"),
            "seconds": round(time.monotonic() - started, 2),
            "finish_reason": result.get("choices", [{}])[0].get("finish_reason")}
    return metrics, result.get("choices", [{}])[0].get("message", {}).get("content") or ""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="gpt-6-luna")
    parser.add_argument("--base-url", default="https://api.openai.com/v1")
    args = parser.parse_args()
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        parser.error("Set OPENAI_API_KEY in the environment")
    # Different nonce per arm avoids cross-arm cache reuse. No workspace data is sent.
    source = "".join(f"line {i:05d}: function process_item returns the input unchanged.\n" for i in range(2400))
    results = {}
    for label, size in (("full", len(source)), ("page", 12000)):
        prefix = "Synthetic test " + uuid.uuid4().hex + "\n" + source[:size]
        messages = [{"role": "system", "content": "Answer the user's question briefly."},
                    {"role": "user", "content": prefix + "\nReply OK."}]
        arm = []
        answer = ""
        for step in range(2):
            if step:
                messages = messages + [{"role": "assistant", "content": answer},
                                       {"role": "user", "content": "Reply OK again."}]
            metrics, answer = call(args.base_url.rstrip("/") + "/chat/completions", key,
                                   {"model": args.model, "messages": messages, "max_completion_tokens": 128})
            arm.append(metrics)
        results[label] = arm
    print(json.dumps({"model": args.model, "note": "Synthetic prompt; measured API usage, not a local llama-server benchmark.", "results": results}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, urllib.error.URLError, TimeoutError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
