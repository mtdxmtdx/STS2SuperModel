"""Public identity/split regressions; no simulator, model, or optimizer needed."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "python"))
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME, canonical_public_input, public_input_digest
from nosl.data import canonical_object_digest, prepared_paths
import prepare_dataset as pipeline
from test_prepare_dataset import fixture


def equivalent_cross_battle_records():
    a, b = fixture(0), fixture(3)
    card = deepcopy(a["public_input"]["observation"]["hand"][0])
    defend = dict(card, id="DefendSilent", type="Skill")
    a["public_input"]["observation"]["unknownDraw"] = [{"card": card, "count": 2}, {"card": defend, "count": 3}]
    a["public_input"]["observation"]["drawCount"] = 5
    a["public_input"]["observation"]["history"].append({"kind": "power_changed", "detail": '{"target":"player","id":"StrengthPower","amount":2}'})
    b["public_input"] = deepcopy(a["public_input"])
    b["public_input"]["observation"]["history"][-1]["detail"] = '{ "amount":2.0, "id":"StrengthPower", "target":"player" }'
    b["public_input"]["observation"]["unknownDraw"] = [{"card": defend, "count": 3}, {"card": card, "count": 1}, {"card": deepcopy(card), "count": 1}]
    for action in b["public_input"]["candidate_actions"]:
        action["revision"] = 37
    return a, b


class PublicIdentityTests(unittest.TestCase):
    def test_semantically_equivalent_representations_have_one_identity(self):
        a, b = equivalent_cross_battle_records()
        self.assertEqual(public_input_digest(a["public_input"]), public_input_digest(b["public_input"]))

    def test_unknown_draw_order_and_count_partition_do_not_change_identity(self):
        a, b = equivalent_cross_battle_records()
        b["public_input"]["observation"]["history"] = deepcopy(a["public_input"]["observation"]["history"])
        self.assertEqual(canonical_public_input(a["public_input"])["observation"]["unknownDraw"],
                         canonical_public_input(b["public_input"])["observation"]["unknownDraw"])
        b["public_input"]["observation"]["unknownDraw"][0]["count"] += 1
        self.assertNotEqual(public_input_digest(a["public_input"]), public_input_digest(b["public_input"]))

    def test_candidate_revision_ignored_without_altering_execution_payload(self):
        a, b = fixture(0), fixture(3)
        b["public_input"] = deepcopy(a["public_input"])
        for action in b["public_input"]["candidate_actions"]:
            action["revision"] = 901
        before = deepcopy(b)
        groups, _, _ = pipeline.provenance_components([a, b])
        self.assertEqual(groups[0], groups[1])
        self.assertEqual(before, b)
        self.assertEqual(901, b["public_input"]["candidate_actions"][0]["revision"])

    def test_action_history_revision_ignored_but_action_choices_preserved(self):
        a = fixture()["public_input"]
        detail = {"revision": 1, "kind": "end_turn", "slot": -1, "target": -1, "selection": None}
        a["observation"]["history"].append({"kind": "action", "detail": json.dumps(detail)})
        b = deepcopy(a)
        detail["revision"] = 17
        b["observation"]["history"][-1]["detail"] = json.dumps(detail, indent=2)
        self.assertEqual(public_input_digest(a), public_input_digest(b))
        detail["kind"] = "play"
        b["observation"]["history"][-1]["detail"] = json.dumps(detail)
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))

    def test_known_draw_container_order_normalized_position_identity_preserved(self):
        a = fixture()["public_input"]
        card = a["observation"]["hand"][0]
        a["observation"]["knownDraw"] = [{"position": 0, "card": deepcopy(card)}, {"position": 1, "card": dict(card, id="DefendSilent")}]
        b = deepcopy(a)
        b["observation"]["knownDraw"].reverse()
        self.assertEqual(public_input_digest(a), public_input_digest(b))
        b["observation"]["knownDraw"][0]["position"], b["observation"]["knownDraw"][1]["position"] = 0, 1
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))

    def test_hand_history_candidate_and_ordered_selection_sequences_preserved(self):
        a = fixture()["public_input"]
        a["observation"]["hand"].append(dict(a["observation"]["hand"][0], id="DefendSilent"))
        a["observation"]["history"].append({"kind": "player_turn", "detail": "2"})
        for path in ("hand", "history", "candidate_actions"):
            b = deepcopy(a)
            (b["candidate_actions"] if path == "candidate_actions" else b["observation"][path]).reverse()
            self.assertNotEqual(public_input_digest(a), public_input_digest(b))
        a["candidate_actions"][0]["selection"] = [0, 1]
        b = deepcopy(a)
        b["candidate_actions"][0]["selection"] = [1, 0]
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))

    def test_public_turn_and_controller_deadline_remain_meaningful(self):
        a = fixture()["public_input"]
        b = deepcopy(a); b["observation"]["turn"] += 1
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))
        a["controller_context"] = {"status": "active", "deadline": 3}
        b = deepcopy(a); b["controller_context"]["deadline"] = 4
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))

    def test_only_history_detail_json_is_parsed_strings_and_booleans_preserved(self):
        self.assertNotEqual(public_input_digest({"category": "2"}), public_input_digest({"category": 2}))
        self.assertNotEqual(public_input_digest({"category": True}), public_input_digest({"category": 1}))
        self.assertNotEqual(public_input_digest({"text": '{"n":2}'}), public_input_digest({"text": '{"n":2.0}'}))
        self.assertEqual(public_input_digest({"number": -0.0}), public_input_digest({"number": 0}))
        a = fixture()["public_input"]
        a["observation"]["history"].append({"kind": "text", "detail": "categorical text"})
        b = deepcopy(a); b["observation"]["history"][-1]["detail"] += " "
        self.assertNotEqual(public_input_digest(a), public_input_digest(b))

    def test_generic_config_digest_is_not_redefined_as_public_identity(self):
        value = {"observation": {"history": [{"kind": "event", "detail": '{"x":1.0}'}]}}
        other = deepcopy(value); other["observation"]["history"][0]["detail"] = '{"x":1}'
        self.assertNotEqual(canonical_object_digest(value), canonical_object_digest(other))
        self.assertEqual(canonical_object_digest(value), pipeline.object_digest(value))
        self.assertEqual(public_input_digest(value), public_input_digest(other))

    def test_equivalent_cross_battle_openings_preserve_later_root_bridge(self):
        a, b = equivalent_cross_battle_records()
        later_a, later_b = fixture(1), fixture(4)
        for row in (a, later_a):
            row["audit_only"].update(source_run_group="A", source_combat_id="A", branch_family="A")
        for row in (b, later_b):
            row["audit_only"].update(source_run_group="B", source_combat_id="B", branch_family="B")
        groups, _, _ = pipeline.provenance_components([a, later_a, b, later_b])
        self.assertEqual(1, len(set(groups)))
        config = pipeline.read_json(ROOT / "configs/data_pipeline.v1.json")
        splits, report, rejected = pipeline.prepare([a, later_a, b, later_b], config, "pilot")
        self.assertEqual(3, report["accepted_roots"])
        self.assertEqual(1, report["duplicate_attempts"])
        self.assertEqual([3], [len(rows) for rows in splits.values() if rows])
        self.assertEqual("exact_public_input_duplicate", rejected[0]["reason"])

    def test_old_frozen_identity_state_and_manifest_cannot_silently_resume(self):
        config = pipeline.read_json(ROOT / "configs/data_pipeline.v1.json")
        _, report, _ = pipeline.prepare([fixture()], config, "pilot")
        self.assertEqual(PUBLIC_IDENTITY_SCHEME, report["split_state"]["lock"]["public_identity_scheme"])
        old = deepcopy(report["split_state"])
        old["lock"].pop("public_identity_scheme")
        old["lock"]["pipeline_version"] = "nosl.dataset.prepare.v2"
        with self.assertRaisesRegex(ValueError, "configuration_or_mode"):
            pipeline.prepare([fixture(1)], config, "pilot", old)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            pipeline.persist_batch(root, [fixture()], [{"path": "unit", "line": 1}], config, "pilot", [], False)
            manifest = json.loads((root / "manifest.json").read_text())
            self.assertEqual(PUBLIC_IDENTITY_SCHEME, manifest["public_identity_scheme"])
            manifest.pop("public_identity_scheme")
            (root / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(ValueError): pipeline.verify_manifest(root)
            with self.assertRaisesRegex(ValueError, "identity scheme unsupported"):
                prepared_paths(root, "train")


if __name__ == "__main__":
    unittest.main()
