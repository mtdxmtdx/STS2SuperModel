import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from verify_shadow_trace import verify


class ShadowTraceTests(unittest.TestCase):
    def row(self, hp=30):
        return {
            "step": 0,
            "normalized_action_id": "get_combat_snapshot",
            "pre_state_hash": "a",
            "post_state_hash": "b",
            "public_observation": {
                "round": 1,
                "energy": 3,
                "max_energy": 3,
                "player": {"hp": hp, "block": 0},
                "draw_pile_count": 5,
                "discard_pile_count": 0,
                "hand": [{"instance_id": "card:STRIKE:000"}],
                "action_candidates": [{"action_id": "end_turn"}],
            },
        }

    def test_identical_traces_match(self):
        result = verify([self.row()], [self.row()])
        self.assertTrue(result["match"])
        self.assertEqual(result["mismatches"], [])

    def test_observation_difference_is_reported(self):
        result = verify([self.row()], [self.row(hp=29)])
        self.assertFalse(result["match"])
        self.assertTrue(any(item["field"] == "public_observation.player.hp" for item in result["mismatches"]))


if __name__ == "__main__":
    unittest.main()
