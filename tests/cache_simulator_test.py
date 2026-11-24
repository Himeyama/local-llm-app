import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("cache_simulator", Path(__file__).resolve().parents[1] / "tools" / "cache_simulator.py")
sim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sim)


class PrefixTests(unittest.TestCase):
    def test_append_and_invalidate(self):
        first = {"model": "m", "messages": [{"role": "user", "content": "hello"}]}
        second = {"model": "m", "messages": first["messages"] + [{"role": "assistant", "content": "world"}]}
        changed = {"model": "different", "messages": second["messages"]}
        result = sim.simulate([first, second, changed])["per_request"]
        self.assertEqual(result[0]["reused"], 0)
        self.assertEqual(result[1]["reused"], result[0]["input"])
        self.assertLess(result[2]["reused"], result[1]["reused"])

    def test_paging_reduces_work_in_synthetic_case(self):
        full = sim.simulate(sim.scenario(False))
        paged = sim.simulate(sim.scenario(True))
        self.assertEqual(full["requests"], paged["requests"])
        self.assertLess(paged["estimated_new_tokens"], full["estimated_new_tokens"])


if __name__ == "__main__":
    unittest.main()
