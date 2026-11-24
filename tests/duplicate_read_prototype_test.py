import copy
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from duplicate_read_prototype import deduplicate


def call(call_id, path="a.txt", name="read_file"):
    return {"role": "assistant", "tool_calls": [{"id": call_id, "function": {"name": name, "arguments": '{"path":"' + path + '"}'}}]}


def result(call_id, content):
    return {"role": "tool", "tool_call_id": call_id, "content": content}


class DuplicateReadTests(unittest.TestCase):
    def test_only_exact_repeats_are_replaced_without_mutating_history(self):
        body = "abc" * 500
        source = [call("a"), result("a", body), call("b"), result("b", body),
                  call("c"), result("c", body + "changed"), call("d", "other"), result("d", body),
                  call("e", name="execute_command"), result("e", body)]
        original = copy.deepcopy(source)
        output = deduplicate(source)
        self.assertEqual(source, original)
        self.assertEqual(output[1]["content"], body)
        self.assertIn("tool_call_id a", output[3]["content"])
        self.assertEqual(output[5]["content"], body + "changed")
        self.assertEqual(output[7]["content"], body)
        self.assertEqual(output[9]["content"], body)

    def test_missing_previous_result_and_small_output_are_not_replaced(self):
        source = [call("a"), result("a", "small"), call("b"), result("b", "small")]
        self.assertEqual(deduplicate(source), source)
        self.assertEqual(deduplicate(source[2:]), source[2:])


if __name__ == "__main__":
    unittest.main()
