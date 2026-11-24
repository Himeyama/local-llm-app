"""Experimental prompt transform; never mutates the original transcript."""
import copy
import json


def deduplicate(messages, minimum_chars=1000):
    """Replace identical repeated read_file results, provided their original remains in context."""
    result = copy.deepcopy(messages)
    calls = {}
    seen = {}
    for message in result:
        if message.get("role") == "assistant":
            for call in message.get("tool_calls", []):
                function = call.get("function", {})
                try:
                    args = json.loads(function.get("arguments", "{}"))
                except (ValueError, TypeError):
                    continue
                if function.get("name") == "read_file" and isinstance(args, dict) and isinstance(args.get("path"), str):
                    calls[call.get("id")] = args["path"]
        if message.get("role") != "tool":
            continue
        call_id = message.get("tool_call_id")
        path = calls.get(call_id)
        content = message.get("content")
        if path is None or not isinstance(content, str) or len(content) < minimum_chars:
            continue
        key = (path, content)
        if key in seen:
            message["content"] = (f"Unchanged file {path}: identical to the complete read_file result "
                                  f"with tool_call_id {seen[key]}. Refer to that earlier result.")
        else:
            seen[key] = call_id
    return result
