#!/usr/bin/env python3
"""Estimate prefix reuse for JSONL prompts or run a synthetic file-read scenario."""
import argparse
import json
from pathlib import Path


def estimate(text):
    # Same fallback heuristic as ChatCompaction.EstimateText, not a tokenizer.
    ascii_count = sum(ord(c) < 128 for c in text)
    return (ascii_count + 2) // 3 + ((len(text) - ascii_count) * 3 + 1) // 2


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def prompt(record):
    # An explicit, deterministic proxy for a chat template; provider templates vary.
    return "model:" + encode(record["model"]) + "\ntools:" + encode(record.get("tools", [])) + "\n" + "".join(
        "message:" + encode(message) + "\n" for message in record["messages"]
    )


def simulate(records):
    previous = ""
    rows = []
    for record in records:
        current = prompt(record)
        prefix = 0
        while prefix < min(len(previous), len(current)) and previous[prefix] == current[prefix]:
            prefix += 1
        total = estimate(current)
        cached = min(total, estimate(current[:prefix]))
        rows.append((total, cached))
        previous = current
    total = sum(row[0] for row in rows)
    cached = sum(row[1] for row in rows)
    return {"requests": len(rows), "estimated_input_tokens": total,
            "estimated_reused_tokens": cached, "estimated_new_tokens": total - cached,
            "estimated_reuse_percent": round(100 * cached / total, 1) if total else 0,
            "per_request": [{"input": a, "reused": b} for a, b in rows]}


def scenario(page):
    # Same user request and number of turns. The relevant portion is assumed to
    # be in the first page; this is not an agent-quality benchmark.
    text = ("public void ProcessItem() { /* implementation */ }\n" * 4000)[:160000]
    content = text[:12000] if page else text
    if page:
        content = encode({"content": content, "offset": 0, "next_offset": 12000, "total_chars": len(text)})
    messages = [{"role": "system", "content": "Use tools as needed; reuse previous results."},
                {"role": "user", "content": "Inspect the file and run focused checks."}]
    tools = [{"type": "function", "function": {"name": "read_file", "parameters": {"type": "object"}}}]
    records = []
    def add():
        records.append(json.loads(encode({"model": "local-model", "tools": tools, "messages": messages})))
    add()
    messages.extend([{"role": "assistant", "tool_calls": [{"id": "1", "type": "function", "function": {"name": "read_file", "arguments": '{"path":"large.cs"}'}}]},
                     {"role": "tool", "tool_call_id": "1", "content": content}])
    add()
    for i in range(3):
        messages.extend([{"role": "assistant", "content": "Checking " + str(i), "tool_calls": [{"id": str(i + 2), "type": "function", "function": {"name": "check", "arguments": "{}"}}]},
                         {"role": "tool", "tool_call_id": str(i + 2), "content": "Check completed."}])
        add()
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("jsonl", nargs="?", type=Path, help="One JSON request per line, with model, tools and messages")
    args = parser.parse_args()
    if args.jsonl:
        records = [json.loads(line) for line in args.jsonl.read_text(encoding="utf-8").splitlines() if line.strip()]
        result = simulate(records)
    else:
        result = {"full_file": simulate(scenario(False)), "paged_file": simulate(scenario(True))}
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
