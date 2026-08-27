import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from trace_to_training import normalize


class TraceToTrainingTests(unittest.TestCase):
    def test_public_row_without_teacher_is_uncalculable(self):
        row = {
            "trace_id": "trace-1", "trace_schema": 1, "game_version": "v0.111.0",
            "game_commit": "41cef1ea", "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0", "step": 1, "post_state_hash": "h1",
            "public_observation": {"round": 1, "hand": [], "player": {}, "context": {}},
        }
        result = normalize([row])
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["confidence"], "Uncalculable")
        self.assertEqual(result[0]["teacher_best_actions"], [])

    def test_teacher_row_is_not_leaked_as_public_action(self):
        base = {
            "trace_id": "trace-2", "trace_schema": 1, "game_version": "v0.111.0",
            "game_commit": "41cef1ea", "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0", "step": 2, "post_state_hash": "h2",
        }
        public = base | {"observation_view": "public", "public_observation": {"round": 1, "hand": [], "player": {}, "context": {}}}
        teacher = base | {"observation_view": "teacher", "pre_state_hash": "h2", "teacher_snapshot": {"draw_pile": [{"id": "SECRET"}], "rng_raw_words_exposed": False}}
        result = normalize([public, teacher])
        self.assertEqual(result[0]["teacher_state_reference"], "h2")
        self.assertNotIn("SECRET", json.dumps(result[0]["public_state"]))


if __name__ == "__main__":
    unittest.main()
