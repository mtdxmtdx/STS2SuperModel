import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from simulate_public_baseline import simulate


class BaselineSimulationTests(unittest.TestCase):
    def test_defend_updates_block_energy_and_hand(self):
        initial = {
            "step": 0,
            "normalized_action_id": "get_combat_snapshot",
            "public_observation": {
                "energy": 3,
                "discard_pile_count": 0,
                "player": {"energy": 3, "block": 0},
                "hand": [{"instance_id": "card:DEFEND:000", "id": "DEFEND", "cost": 1, "type": "Skill", "stats": {"block": 5}}],
                "action_candidates": [{"action_id": "play:card:DEFEND:000:none", "source_instance_id": "card:DEFEND:000"}],
                "enemies": [],
            },
        }
        action = {
            "step": 1,
            "normalized_action_id": "play_card:card:DEFEND:000:none",
        }
        result = simulate([initial, action])[1]["public_observation"]
        self.assertEqual(result["energy"], 2)
        self.assertEqual(result["player"]["block"], 5)
        self.assertEqual(result["hand"], [])
        self.assertEqual(result["discard_pile_count"], 1)


if __name__ == "__main__":
    unittest.main()
