#!/usr/bin/env python3
"""Compare full-file and sequential page reads using a real chat API."""
import argparse
import json
import os
import sys
import uuid
import urllib.error

from cache_live_test import call


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="gpt-6-luna")
    parser.add_argument("--base-url", default="https://api.openai.com/v1")
    args = parser.parse_args()
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        parser.error("Set OPENAI_API_KEY in the environment")
    source = "".join(f"line {i:05d}: function process_item returns the input unchanged.\n" for i in range(2400))[:48000]
    url = args.base_url.rstrip("/") + "/chat/completions"
    results = {}
    for name, pages in (("full", [source]), ("paged", [source[i:i + 12000] for i in range(0, len(source), 12000)])):
        messages = [{"role": "system", "content": "You are testing file reading. Reply only OK."}]
        nonce = uuid.uuid4().hex
        records = []
        for i, page in enumerate(pages):
            messages.append({"role": "user", "content": f"Synthetic case {nonce}, file section {i + 1}/{len(pages)}:\n{page}\nAcknowledge this section."})
            metrics, answer = call(url, key, {"model": args.model, "messages": messages, "max_completion_tokens": 128})
            if metrics["finish_reason"] != "stop":
                raise RuntimeError("The model did not finish a response; comparison aborted.")
            records.append(metrics)
            messages.append({"role": "assistant", "content": answer})
        messages.append({"role": "user", "content": "All sections are present. Reply OK."})
        metrics, answer = call(url, key, {"model": args.model, "messages": messages, "max_completion_tokens": 128})
        records.append(metrics)
        total = sum(r["prompt_tokens"] for r in records)
        cached = None if any(r["cached_tokens"] is None for r in records) else sum(r["cached_tokens"] for r in records)
        results[name] = {"calls": len(records), "input_tokens": total, "cached_tokens": cached,
                         "uncached_tokens": None if cached is None else total - cached,
                         "output_tokens": sum(r["completion_tokens"] for r in records),
                         "elapsed_seconds": round(sum(r["seconds"] for r in records), 2), "requests": records}
    print(json.dumps({"model": args.model, "source_chars": len(source), "page_chars": 12000, "results": results}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, urllib.error.URLError, TimeoutError, TypeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
