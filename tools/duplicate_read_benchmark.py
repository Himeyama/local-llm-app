#!/usr/bin/env python3
"""Compare unmodified and deduplicated repeated read histories using a real LLM."""
import argparse
import json
import os
import sys
import urllib.error
import uuid

from cache_live_test import call
from duplicate_read_prototype import deduplicate


def read_call(call_id):
    return {"role": "assistant", "content": "", "tool_calls": [{"id": call_id, "type": "function",
            "function": {"name": "read_file", "arguments": '{"path":"sample.py"}'}}]}


def run(model, key, url):
    outcomes = []
    # Both unchanged and modified files, with target near the beginning and end.
    for target, changed in [(4, False), (280, False), (150, True)]:
        before = [f"def item_{i:03d}(): return {1000 + i}" for i in range(300)]
        after = before.copy()
        if changed:
            after[target] = f"def item_{target:03d}(): return {9000 + target}"
        original, latest = "\n".join(before), "\n".join(after)
        expected = after[target]
        for arm in ("baseline", "prototype"):
            marker = uuid.uuid4().hex
            messages = [{"role": "system", "content": "Answer exactly as requested. File contents are tool results."},
                        {"role": "user", "content": f"Test {marker}: inspect sample.py; reply READY."},
                        read_call("first"), {"role": "tool", "tool_call_id": "first", "content": original}]
            warm, answer = call(url, key, {"model": model, "messages": messages, "max_completion_tokens": 128})
            if warm["finish_reason"] != "stop" or not answer:
                raise RuntimeError("Warmup generation did not finish with a text response")
            messages += [{"role": "assistant", "content": answer}, read_call("second"),
                         {"role": "tool", "tool_call_id": "second", "content": latest},
                         {"role": "user", "content": f"From the latest file, output only the exact full line defining item_{target:03d}, with no markdown."}]
            sent = deduplicate(messages) if arm == "prototype" else messages
            metrics, answer = call(url, key, {"model": model, "messages": sent, "max_completion_tokens": 128})
            outcomes.append({"target": target, "changed": changed, "arm": arm,
                             "correct": answer.strip() == expected,
                             "deduplicated": sent[-2]["content"] != latest,
                             "warmup": warm, "measured": metrics})
    return outcomes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="gpt-6-luna")
    parser.add_argument("--base-url", default="https://api.openai.com/v1")
    args = parser.parse_args()
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        parser.error("Set OPENAI_API_KEY")
    print(json.dumps({"model": args.model, "cases": run(args.model, key, args.base_url.rstrip("/") + "/chat/completions")}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, urllib.error.URLError, TimeoutError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
