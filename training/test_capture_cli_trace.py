import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from capture_cli_trace import LOCK


class CaptureContractTests(unittest.TestCase):
    def test_lock_contains_all_required_cli_metadata(self):
        self.assertEqual(LOCK["game_version"], "v0.111.0")
        self.assertEqual(LOCK["game_commit"], "41cef1ea")
        self.assertEqual(LOCK["version"], "0.2.0")

    def test_command_fixture_is_jsonl_compatible(self):
        command = {"cmd": "get_combat_snapshot", "view": "teacher"}
        encoded = json.dumps(command)
        self.assertEqual(json.loads(encoded), command)


if __name__ == "__main__":
    unittest.main()
