"""Representation invariance, with zero fitting or optimizer steps."""
import copy
import json
from pathlib import Path
import sys
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.model import Student, feature_hash, public_history_features
from nosl.public_identity import public_input_digest
from nosl.schema import load_config, validate_public
from test_student import fixture_v2


class PublicNumericFeatureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        torch.manual_seed(1729)
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        cls.model = Student(cls.config).eval()

    def assert_same_outputs(self, left, right):
        validate_public(left, self.config)
        validate_public(right, self.config)
        with torch.no_grad():
            a, b = self.model(left), self.model(right)
        for head in a:
            self.assertTrue(torch.equal(a[head], b[head]), head)

    def test_integral_numeric_leaves_are_invariant(self):
        for integer, floating in ((2, 2.0), (-7, -7.0), (0, -0.0), (1000000, 1e6)):
            with self.subTest(integer=integer, floating=floating):
                self.assertEqual(feature_hash(integer, "scalar", 64), feature_hash(floating, "scalar", 64))
                self.assertEqual(feature_hash({"a": [integer, {"b": floating}]}, "nested", 64),
                                 feature_hash({"a": [floating, {"b": integer}]}, "nested", 64))

    def test_strings_booleans_null_and_fractional_values_remain_distinct(self):
        for left, right in ((2, "2"), ("2", "2.0"), ("02", "2"), (2, 2.5),
                            (0, False), (1, True), (0, None), ("Attack", "Skill")):
            with self.subTest(left=left, right=right):
                self.assertNotEqual(feature_hash(left, "value", 4096), feature_hash(right, "value", 4096))

    def test_json_objects_ignore_key_order_but_lists_keep_order(self):
        self.assertEqual(feature_hash({"x": 2, "y": [4, 7]}, "details", 4096),
                         feature_hash({"y": [4.0, 7.0], "x": 2.0}, "details", 4096))
        self.assertNotEqual(feature_hash([2, 3], "details", 4096), feature_hash([3, 2], "details", 4096))

    def test_embedded_event_json_normalizes_numeric_leaves_only(self):
        event = {"kind": "damage", "detail": '{"target":"player","blocked":0,"unblocked":2,"overkill":0,"hpAfter":58,"killed":false,"targetSlot":-2,"sourceSlot":0}'}
        other = {"kind": "damage", "detail": '{ "sourceSlot": 0, "targetSlot": -2, "killed": false, "hpAfter": 58.0, "overkill": -0.0, "unblocked": 2e0, "blocked": 0.0, "target": "player" }'}
        self.assertEqual(public_history_features(event, 64), public_history_features(other, 64))
        public = fixture_v2()["public_input"]
        public["observation"]["history"].append(event)
        equivalent = copy.deepcopy(public)
        equivalent["observation"]["history"][-1] = other
        self.assertEqual(public_input_digest(public), public_input_digest(equivalent))
        self.assert_same_outputs(public, equivalent)

    def test_accepted_public_details_have_identical_full_model_outputs(self):
        public = fixture_v2()["public_input"]
        obs = public["observation"]
        obs["counters"]["attacksPlayed"] = 2
        obs["hand"][0]["affliction"] = {"id": self.config["supported_afflictions"][0], "amount": 2}
        obs["powers"] = [{"id": "WeakPower", "amount": 2, "amountOnTurnStart": 2,
                          "skipNextDurationTick": False, "selectedCard": None,
                          "selectedUpgrade": None, "applierSlot": 0}]
        obs["history"].append({"kind": "card_played", "detail": json.dumps({
            "card": obs["hand"][0], "energySpent": 2, "starsSpent": 0, "resultPile": "Discard"})})
        equivalent = copy.deepcopy(public)
        other = equivalent["observation"]
        other["counters"]["attacksPlayed"] = 2.0
        other["hand"][0]["affliction"]["amount"] = 2.0
        other["powers"][0]["amount"] = 2.0
        detail = json.loads(other["history"][-1]["detail"])
        detail["card"]["affliction"]["amount"] = 2.0
        detail["energySpent"], detail["starsSpent"] = 2.0, -0.0
        other["history"][-1]["detail"] = json.dumps(detail)
        self.assert_same_outputs(public, equivalent)

    def test_event_categories_and_categorical_detail_strings_stay_distinct(self):
        self.assertNotEqual(public_history_features({"kind": "potion_used", "detail": "FirePotion"}, 4096),
                            public_history_features({"kind": "potion_used", "detail": "BlockPotion"}, 4096))
        self.assertNotEqual(feature_hash({"publicState": {"damage": "2"}}, "card", 4096),
                            feature_hash({"publicState": {"damage": "2.0"}}, "card", 4096))

    def test_action_revision_ignored_but_selection_order_preserved(self):
        detail = {"revision": 0, "kind": "choose", "slot": -1, "target": -1, "selection": [0, 1]}
        event = {"kind": "action", "detail": json.dumps(detail)}
        detail["revision"] = 99
        self.assertEqual(public_history_features(event, 4096), public_history_features({"kind": "action", "detail": json.dumps(detail)}, 4096))
        detail["selection"].reverse()
        self.assertNotEqual(public_history_features(event, 4096), public_history_features({"kind": "action", "detail": json.dumps(detail)}, 4096))

    def test_hand_and_history_order_remain_observable(self):
        public = fixture_v2()["public_input"]
        public["observation"]["history"] += [{"kind": "player_turn", "detail": "2"}, {"kind": "player_turn_ended", "detail": ""}]
        for field in ("hand", "history"):
            other = copy.deepcopy(public)
            if field == "hand":
                other["observation"][field].reverse()
            else:
                other["observation"][field][-2:] = reversed(other["observation"][field][-2:])
            with torch.no_grad(), self.subTest(field=field):
                self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_unknown_draw_count_partition_and_order_are_invariant(self):
        public = fixture_v2()["public_input"]
        card = public["observation"]["unknownDraw"][0]["card"]
        card["affliction"] = {"id": self.config["supported_afflictions"][0], "amount": 2}
        equivalent = copy.deepcopy(public)
        entries = equivalent["observation"]["unknownDraw"]
        split = entries.pop(0)
        entries.extend([{**copy.deepcopy(split), "count": 1}, {**copy.deepcopy(split), "count": 1}])
        entries[-1]["card"]["affliction"]["amount"] = 2.0
        entries.reverse()
        self.assertEqual(public_input_digest(public), public_input_digest(equivalent))
        self.assert_same_outputs(public, equivalent)

    def test_known_positions_and_revision_identity_match_model(self):
        public = fixture_v2()["public_input"]
        obs = public["observation"]
        obs["knownDraw"] = [{"position": 0, "card": copy.deepcopy(obs["hand"][0])},
                            {"position": 1, "card": copy.deepcopy(obs["hand"][1])}]
        obs["unknownDraw"], obs["drawCount"] = [], 2
        obs["history"].append({"kind": "action", "detail": json.dumps(public["candidate_actions"][0])})
        equivalent = copy.deepcopy(public)
        equivalent["observation"]["knownDraw"].reverse()
        for action in equivalent["candidate_actions"]:
            action["revision"] += 10
        detail = json.loads(equivalent["observation"]["history"][-1]["detail"])
        detail["revision"] += 10
        equivalent["observation"]["history"][-1]["detail"] = json.dumps(detail, indent=2)
        self.assertEqual(public_input_digest(public), public_input_digest(equivalent))
        self.assert_same_outputs(public, equivalent)
        equivalent["observation"]["knownDraw"][0]["position"] = 0
        equivalent["observation"]["knownDraw"][1]["position"] = 1
        self.assertNotEqual(public_input_digest(public), public_input_digest(equivalent))
        with torch.no_grad():
            self.assertFalse(torch.equal(self.model(public)["value"], self.model(equivalent)["value"]))


if __name__ == "__main__":
    unittest.main()
