"""Native finite healing public evidence -> strict v2 schema/forward/inference; no backward or fitting."""
import copy
import json
from pathlib import Path
import sys
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data_v2 import validate_record
from nosl.inference_v2 import InferenceV2
from nosl.model_v2 import StudentV2, public_extension_features
from nosl.schema import SchemaError
from nosl.schema_regen import PublicRegenController
from nosl.schema_v2 import load_config, validate_public


class FiniteRegenV2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.v2.engineering.json")
        cls.publics = [json.loads(line) for line in (ROOT / "tests/python/fixtures/finite-regen-public-v2.jsonl").read_text().splitlines()]
        cls.record = json.loads((ROOT / "tests/python/fixtures/finite-regen-record-v2.jsonl").read_text())

    def test_native_healing_preserves_start_reference_and_reaches_forward_only(self):
        validate_record(self.record, self.config)
        model = StudentV2(self.config).eval()
        before = {key: value.clone() for key, value in model.state_dict().items()}
        runner = InferenceV2(self.config)
        for public in self.publics:
            validate_public(public, self.config)
            self.assertEqual(public["observation"]["startHp"], 50)
            self.assertEqual(public["controller_context"]["deadlinePlayerTurn"], 6)
            with torch.no_grad(): output = model(public)
            self.assertTrue(torch.isfinite(output["value"]).all())
            self.assertTrue(torch.isfinite(output["hp_distribution"]).all())
            prediction = runner.predict(public)
            self.assertIn(prediction["status"], ("MODEL_UNTRAINED", "PLAN_FINISHED"))
            self.assertIsNone(prediction["selected_action"])
            self.assertIsNone(prediction.get("plan_predictions"))
        self.assertEqual(self.publics[1]["observation"]["hp"], 55)
        self.assertEqual(self.publics[-1]["observation"]["hp"], 65)
        self.assertEqual(prediction["status"], "PLAN_FINISHED")
        self.assertTrue(all(torch.equal(before[k], v) for k, v in model.state_dict().items()))
        self.assertTrue(all(p.grad is None for p in model.parameters()))
        terminal = json.loads((ROOT / "tests/python/fixtures/finite-regen-terminal-v2.json").read_text())
        self.assertEqual(runner.predict(terminal)["status"], "PLAN_FINISHED")
        self.assertEqual(runner.predict(self.publics[0])["status"], "INVALID_INPUT")

    def test_distinct_context_is_encoded_and_hunt_remains_no_healing(self):
        public = copy.deepcopy(self.publics[0])
        inactive = copy.deepcopy(public); inactive["controller_context"] = {"status": "inactive"}
        self.assertNotEqual(public_extension_features(public, 64), public_extension_features(inactive, 64))
        hunt = json.loads((ROOT / "tests/python/fixtures/finite-hunt-record-v2.jsonl").read_text())["public_input"]
        hunt["observation"]["maxHp"] += 1
        hunt["controller_context"]["anchor"]["observation"]["maxHp"] += 1
        hunt["observation"]["hp"] += 1
        with self.assertRaisesRegex(SchemaError, "Hunt mechanics scope"): validate_public(hunt, self.config)

    def test_immutable_deadline_and_finished_exit_cannot_reopen(self):
        controller = PublicRegenController(self.publics[0], self.config)
        controller.advance(self.publics[1]); controller.advance(self.publics[-1])
        reset = copy.deepcopy(self.publics[-1]); reset["controller_context"].update(status="active", exitReason=None)
        with self.assertRaisesRegex(SchemaError, "cannot reopen"): controller.advance(reset)
        wrong = copy.deepcopy(self.publics[0]); wrong["controller_context"]["deadlinePlayerTurn"] += 1
        with self.assertRaisesRegex(SchemaError, "fixed deadline"): validate_public(wrong, self.config)
        reset = copy.deepcopy(self.publics[1])
        reset["controller_context"]["anchor"] = {**copy.deepcopy(reset), "schema_version": "nosl.student.public.v1", "controller_context": {"status": "inactive"}}
        reset["controller_context"].update(startPlayerTurn=2, initialRegen=4, observedEvents=[])
        with self.assertRaisesRegex(SchemaError, "cannot replace"): PublicRegenController(self.publics[0], self.config).advance(reset)
        expired = copy.deepcopy(self.publics[0]); event = {"kind": "player_turn", "detail": "7"}
        expired["observation"]["turn"] = 7; expired["observation"]["history"].append(event)
        expired["controller_context"].update(lastObservedPlayerTurn=7, observedEvents=[event])
        answer = InferenceV2(self.config).predict(expired)
        self.assertEqual(answer["status"], "PLAN_ABORTED"); self.assertEqual(answer["reason"], "fixed_deadline_expired")

    def test_danger_and_defense_departure_exit_without_claiming_learned_action(self):
        for change in ("intent", "defense"):
            public = copy.deepcopy(self.publics[0])
            if change == "intent": public["observation"]["enemies"][0]["intents"][0]["damage"] = 6
            else:
                for index, action in enumerate(public["candidate_actions"]):
                    if action["kind"] == "play" and public["observation"]["hand"][action["slot"]]["id"] == "Finesse":
                        public["legal_mask"][index] = False
            answer = InferenceV2(self.config).predict(public)
            self.assertEqual(answer["status"], "PLAN_ABORTED")
            self.assertIn(answer["reason"], ("reviewed_safety_scope_failed", "no_safe_defense"))
            self.assertIsNone(answer["selected_action"])

    def test_private_fields_and_false_finish_claims_are_rejected(self):
        for field in ("seed", "success_probability", "extra_net_hp_loss", "counterfactualHp"):
            public = copy.deepcopy(self.publics[0]); public["controller_context"][field] = 1
            with self.assertRaises(SchemaError): validate_public(public, self.config)
        public = copy.deepcopy(self.publics[0]); public["controller_context"]["anchor"]["observation"]["rng"] = 23
        with self.assertRaises(SchemaError): validate_public(public, self.config)
        public = copy.deepcopy(self.publics[0]); public["controller_context"].update(status="finished", exitReason="full_hp")
        with self.assertRaisesRegex(SchemaError, "actual public healing"): validate_public(public, self.config)

    def test_finished_status_cannot_bypass_scope_or_committed_history(self):
        public = copy.deepcopy(self.publics[-1]); public["observation"]["maxHp"] += 1
        with self.assertRaisesRegex(SchemaError, "actual public healing"): validate_public(public, self.config)
        for status in ("active", "finished", "aborted"):
            public = copy.deepcopy(self.publics[0]); public["observation"].update(hp=65, powers=[])
            public["controller_context"].update(status=status, exitReason=None if status == "active" else "regen_exhausted")
            with self.assertRaisesRegex(SchemaError, "contradicts observed history"): validate_public(public, self.config)
            self.assertEqual(InferenceV2(self.config).predict(public)["status"], "INVALID_INPUT")
        public = copy.deepcopy(self.publics[1]); public["controller_context"]["anchor"]["observation"]["turn"] = 2
        public["controller_context"].update(startPlayerTurn=2, deadlinePlayerTurn=7)
        with self.assertRaisesRegex(SchemaError, "player-turn history"): validate_public(public, self.config)
        public = copy.deepcopy(self.publics[1])
        public["observation"].update(hp=50, powers=copy.deepcopy(self.publics[0]["observation"]["powers"]))
        with self.assertRaisesRegex(SchemaError, "contradicts observed history"): validate_public(public, self.config)
        controller = PublicRegenController(self.publics[0], self.config)
        with self.assertRaisesRegex(SchemaError, "contradicts observed history"): controller.advance(public)

    def test_all_learned_targets_remain_unavailable_for_whole_plan_evidence(self):
        validate_record(self.record, self.config)
        self.assertFalse(any(self.record["targets"]["plan"]["masks"].values()))
        self.assertTrue(all(not any(row["masks"].values()) for row in self.record["targets"]["actions"]))
        self.assertFalse(self.record["audit_only"]["trainable"])
        for head in ("specified_success_probability", "extra_net_hp_loss"):
            row = copy.deepcopy(self.record)
            row["targets"]["plan"].update(label_scope="whole_plan_from_anchor")
            row["targets"]["plan"][head] = 1
            row["targets"]["plan"]["masks"][head] = True
            with self.assertRaises(SchemaError): validate_record(row, self.config)
        row = copy.deepcopy(self.record)
        action = row["targets"]["actions"][0]
        action.update(expected_final_hp=65, quality="objective_value_unresolved", allocated_worlds=2, completed_worlds=2)
        action["masks"]["expected_final_hp"] = True
        with self.assertRaisesRegex(SchemaError, "no applicable learned target"): validate_record(row, self.config)

    def test_trained_manifest_cannot_expose_hunt_predictions_for_regen(self):
        # Forward-only status exercise. No fitted weights or training claim is produced.
        runner = InferenceV2(self.config, StudentV2(self.config),
            {"trained": True, "optimizer_steps": 1, "status": "EXPERIMENTAL_UNPROMOTED"}, allow_experimental=True)
        prediction = runner.predict(self.publics[0])
        self.assertEqual(prediction["status"], "PLAN_POLICY_UNVALIDATED")
        self.assertIsNone(prediction["plan_predictions"])
        self.assertIsNone(prediction["selected_action"])


if __name__ == "__main__": unittest.main()
