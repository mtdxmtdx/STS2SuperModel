"""Real bounded engine fixtures -> v2 labels/features/backward/standalone states."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data_v2 import validate_record
from nosl.inference import Inference
from nosl.inference_v2 import InferenceV2
from nosl.model_v2 import StudentV2, public_extension_features
from nosl.schema import SchemaError, validate_public as validate_v1
from nosl.schema_v2 import PublicHuntController, load_config, validate_public
from nosl.smoke_v2 import decision_loss, smoke


def fixture(name):
    return json.loads((ROOT / "tests/python/fixtures" / name).read_text())


class StudentV2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.v2.engineering.json")

    def setUp(self):
        self.record = fixture("finite-hunt-record-v2.jsonl")
        self.public = self.record["public_input"]

    def test_real_complete_unresolved_and_continuation_records(self):
        for name in ("finite-hunt-record-v2.jsonl", "finite-hunt-unresolved-v2.jsonl", "finite-hunt-continuation-v2.jsonl"):
            validate_record(fixture(name), self.config)
        target = self.record["targets"]["plan"]
        self.assertEqual(target["specified_success_probability"], 1)
        self.assertEqual(target["extra_net_hp_loss"], 0)
        self.assertEqual(target["allocated_worlds"], 2)
        self.assertTrue(all(not any(r["masks"].values()) for r in self.record["targets"]["actions"]))
        missing = fixture("finite-hunt-unresolved-v2.jsonl")["targets"]["plan"]
        self.assertEqual(missing["allocated_worlds"], 2)
        self.assertIsNone(missing["specified_success_probability"])
        self.assertIsNone(missing["extra_net_hp_loss"])
        later = fixture("finite-hunt-continuation-v2.jsonl")["targets"]["plan"]
        self.assertEqual(later["label_scope"], "unavailable")
        self.assertFalse(any(later["masks"].values()))

    def test_labels_cannot_move_off_anchor_or_drop_unresolved_mass(self):
        later = fixture("finite-hunt-continuation-v2.jsonl")
        later["targets"]["plan"] = copy.deepcopy(self.record["targets"]["plan"])
        with self.assertRaisesRegex(SchemaError, "exact original"): validate_record(later, self.config)
        self.record["targets"]["plan"]["paired_completed_worlds"] = 1
        with self.assertRaisesRegex(SchemaError, "unresolved"): validate_record(self.record, self.config)
        missing = fixture("finite-hunt-unresolved-v2.jsonl")
        missing["targets"]["plan"]["extra_net_hp_loss"] = 0
        with self.assertRaisesRegex(SchemaError, "null"): validate_record(missing, self.config)

    def test_zero_optimizer_backward_reaches_both_plan_heads_and_context(self):
        torch.manual_seed(103)
        model = StudentV2(self.config)
        before = {k: v.clone() for k, v in model.state_dict().items()}
        loss, terms = decision_loss(model(self.public), self.record["targets"], self.public, self.config)
        loss.backward()
        for name, head in model.plan_heads.items():
            self.assertGreater(head.weight.grad.abs().sum().item(), 0, name)
        self.assertGreater(model.context[0].weight.grad.abs().sum().item(), 0)
        self.assertTrue(torch.isfinite(loss))
        self.assertTrue(all(torch.isfinite(p.grad).all() for p in model.parameters() if p.grad is not None))
        self.assertTrue(all(torch.equal(before[k], v) for k, v in model.state_dict().items()))
        result = smoke([self.record], self.config)
        self.assertEqual(result["optimizer_steps"], 0)
        self.assertFalse(result["weights_written"])
        self.assertFalse(result["safe_learned_plan_execution_verified"])

    def test_no_supervision_is_not_an_invented_zero_target(self):
        record = fixture("finite-hunt-unresolved-v2.jsonl")
        with self.assertRaisesRegex(ValueError, "no real supervised"): smoke([record], self.config)

    def test_teacher_seed_audit_never_changes_features_or_predictions(self):
        torch.manual_seed(105); model = StudentV2(self.config).eval()
        changed = copy.deepcopy(self.record)
        changed["audit_only"] = {"sampler_seed": 89921, "eligibility": "eligible", "teacher_value": -999}
        validate_record(changed, self.config)
        with torch.no_grad(): a, b = model(self.public), model(changed["public_input"])
        for name in a:
            if isinstance(a[name], dict):
                for head in a[name]: self.assertTrue(torch.equal(a[name][head], b[name][head]))
            else: self.assertTrue(torch.equal(a[name], b[name]))

    def test_context_rejects_teacher_numbers_and_hidden_nested_fields(self):
        for key in ("seed", "eligibility", "extra_net_hp_loss", "success_probability", "searchConfidence"):
            public = copy.deepcopy(self.public); public["controller_context"][key] = 1
            with self.assertRaises(SchemaError): validate_public(public, self.config)
        self.public["controller_context"]["anchor"]["observation"]["rng"] = 19
        with self.assertRaises(SchemaError): validate_public(self.public, self.config)

    def test_revisions_are_not_learned_identifiers(self):
        changed = copy.deepcopy(self.public)
        for public in (changed, changed["controller_context"]["anchor"]):
            for action in public["candidate_actions"]: action["revision"] += 99
            for event in public["observation"]["history"]:
                if event["kind"] == "action":
                    detail = json.loads(event["detail"]); detail["revision"] += 99; event["detail"] = json.dumps(detail)
        self.assertEqual(public_extension_features(self.public, 64), public_extension_features(changed, 64))

    def test_masked_candidates_do_not_affect_plan_features_or_predictions(self):
        padded = copy.deepcopy(self.public)
        for public in (padded, padded["controller_context"]["anchor"]):
            action = copy.deepcopy(next(a for a in public["candidate_actions"] if a["kind"] == "play"))
            action["target"] = -2
            public["candidate_actions"].append(action); public["legal_mask"].append(False)
        validate_public(padded, self.config)
        self.assertEqual(public_extension_features(self.public, 64), public_extension_features(padded, 64))
        torch.manual_seed(211); model = StudentV2(self.config).eval()
        with torch.no_grad(): original, extra = model(self.public), model(padded)
        self.assertTrue(torch.isneginf(extra["ranking_score"][-1]))
        for head in original["plan"]:
            self.assertTrue(torch.allclose(original["plan"][head], extra["plan"][head], atol=1e-7))

    def test_terminal_player_turn_cannot_rewind_to_claim_deadline(self):
        runner = InferenceV2(self.config); runner.predict(self.public)
        terminal = fixture("finite-hunt-terminal-v2.json")
        terminal["terminal_public"]["events"].extend([{"kind": "player_turn", "detail": "3"}, {"kind": "player_turn", "detail": "1"}])
        self.assertEqual(runner.predict(terminal)["status"], "INVALID_INPUT")

    def test_public_fixed_deadline_expires_without_a_new_budget(self):
        controller = PublicHuntController(self.public, self.config)
        later = copy.deepcopy(self.public)
        later["observation"]["turn"] = 3
        event = {"kind": "player_turn", "detail": "3"}
        later["observation"]["history"].append(event)
        later["controller_context"].update(lastObservedPlayerTurn=3, observedEvents=[event])
        expired = controller.advance(later)
        self.assertEqual(expired["controller_context"]["status"], "aborted")
        self.assertEqual(expired["controller_context"]["exitReason"], "fixed_deadline_expired")
        self.assertEqual(expired["controller_context"]["deadlinePlayerTurn"], 2)
        with self.assertRaisesRegex(SchemaError, "cannot reopen"): controller.advance(later)
        reset = copy.deepcopy(self.public)
        reset["controller_context"]["hpSafetyFloor"] = 9
        reset["controller_context"]["templateId"] = "nosl-finite-hunt-next-turn-v1:hp-floor=9"
        with self.assertRaisesRegex(SchemaError, "cannot replace"): controller.advance(reset)

    def test_guard_and_terminal_preserve_safety_abort_despite_incidental_success(self):
        self.public["controller_context"]["hpSafetyFloor"] = self.public["observation"]["hp"]
        self.public["controller_context"]["templateId"] = "nosl-finite-hunt-next-turn-v1:hp-floor=" + str(self.public["observation"]["hp"])
        runner = InferenceV2(self.config)
        result = runner.predict(self.public)
        self.assertEqual(result["status"], "PLAN_ABORTED")
        settled = runner.predict(fixture("finite-hunt-terminal-v2.json"))
        self.assertEqual(settled["status"], "PLAN_ABORTED")
        self.assertEqual(settled["reason"], "public_hp_safety_guard")

    def test_terminal_uses_real_reward_and_fixed_deadline(self):
        runner = InferenceV2(self.config)
        self.assertEqual(runner.predict(self.public)["status"], "MODEL_UNTRAINED")
        terminal = fixture("finite-hunt-terminal-v2.json")
        self.assertEqual(runner.predict(terminal)["status"], "PLAN_FINISHED")
        self.assertEqual(runner.predict(self.public)["status"], "INVALID_INPUT")
        runner = InferenceV2(self.config); runner.predict(self.public)
        terminal["terminal_public"]["extra_card_rewards_offered"] = 0
        self.assertEqual(runner.predict(terminal)["status"], "PLAN_ABORTED")

    def test_cannot_claim_finished_on_live_packet(self):
        self.public["controller_context"].update(status="finished", exitReason="specified_reward_observed")
        self.assertEqual(InferenceV2(self.config).predict(self.public)["status"], "INVALID_INPUT")

    def test_legacy_records_and_inference_stay_legacy(self):
        old = fixture("real-teacher-v2.jsonl")["public_input"]
        validate_v1(old, self.config["base_config"])
        self.assertEqual(Inference(self.config["base_config"]).predict(old)["status"], "MODEL_UNTRAINED")
        with self.assertRaises(SchemaError): validate_v1(self.public, self.config["base_config"])
        with self.assertRaises(SchemaError): validate_public(old, self.config)

    def native_public(self):
        public = copy.deepcopy(fixture("real-teacher-v2.jsonl")["public_input"])
        public["schema_version"] = "nosl.student.public.v2"
        obs = public["observation"]
        detail = {"schemaVersion": "nosl.native-entry-assets.v1", "hp": obs["startHp"], "maxHp": obs["maxHp"],
                  "gold": obs["startGold"], "deck": obs["hand"], "relics": obs["relicStates"], "potions": obs["potions"],
                  "maxEnergy": 3, "potionSlots": len(obs["potions"]), "orbSlots": 0, "cardRemovalsUsed": 0}
        obs["history"].insert(1, {"kind": "native_entry_assets", "detail": json.dumps(detail)})
        return public

    def test_native_entry_is_validated_and_encoded_not_silently_dropped(self):
        public = self.native_public(); validate_public(public, self.config)
        with self.assertRaises(SchemaError): validate_v1({**public, "schema_version": "nosl.student.public.v1"}, self.config["base_config"])
        changed = copy.deepcopy(public)
        event = changed["observation"]["history"][1]; detail = json.loads(event["detail"])
        detail["cardRemovalsUsed"] = 2; event["detail"] = json.dumps(detail)
        validate_public(changed, self.config)
        self.assertNotEqual(public_extension_features(public, 64), public_extension_features(changed, 64))
        torch.manual_seed(109); model = StudentV2(self.config).eval()
        with torch.no_grad(): self.assertFalse(torch.equal(model(public)["value"], model(changed)["value"]))
        detail["seed"] = 991; event["detail"] = json.dumps(detail)
        with self.assertRaises(SchemaError): validate_public(changed, self.config)

    def test_actual_native_worker_entry_packet_reaches_new_student(self):
        public = fixture("native-entry-public-v2.json")
        validate_public(public, self.config)
        self.assertEqual(public["observation"]["history"][1]["kind"], "native_entry_assets")
        with torch.no_grad(): output = StudentV2(self.config)(public)
        self.assertTrue(torch.isfinite(output["value"]).all())
        self.assertEqual(InferenceV2(self.config).predict(public)["status"], "MODEL_UNTRAINED")

    def test_actual_five_forced_event_packets_validate_and_encode_public_context(self):
        path = ROOT / "tests/python/fixtures/forced-event-public-v2.jsonl"
        owners = set(); model = StudentV2(self.config).eval()
        for line in path.read_text().splitlines():
            public = json.loads(line); validate_public(public, self.config)
            event = next(e for e in public["observation"]["history"] if e["kind"] == "forced_event_context")
            owners.add(json.loads(event["detail"])["owner"])
            with torch.no_grad(): self.assertTrue(torch.isfinite(model(public)["value"]).all())
            changed = copy.deepcopy(public)
            event = next(e for e in changed["observation"]["history"] if e["kind"] == "forced_event_context")
            detail = json.loads(event["detail"]); detail["floor"] += 1; event["detail"] = json.dumps(detail)
            self.assertNotEqual(public_extension_features(public, 64), public_extension_features(changed, 64))
            detail["rewardOptions"] = ["private"]; event["detail"] = json.dumps(detail)
            with self.assertRaises(SchemaError): validate_public(changed, self.config)
        self.assertEqual(owners, {"BattlewornDummy", "DenseVegetation", "PunchOff", "FakeMerchant", "TheLanternKey"})

    def test_merchant_inventory_is_bounded_observed_context(self):
        publics = [json.loads(line) for line in (ROOT / "tests/python/fixtures/forced-event-public-v2.jsonl").read_text().splitlines()]
        public = next(p for p in publics if p["observation"]["history"][1]["kind"] == "event_merchant_inventory_revealed")
        for mutation in ("private", "missing", "reordered"):
            changed = copy.deepcopy(public); history = changed["observation"]["history"]
            if mutation == "private":
                detail = json.loads(history[1]["detail"]); detail[0]["seed"] = 992; history[1]["detail"] = json.dumps(detail)
            elif mutation == "missing": history.pop(1)
            else: history[1], history[2] = history[2], history[1]
            with self.assertRaises(SchemaError): validate_public(changed, self.config)

    def test_native_entry_duplicate_position_and_nested_private_card_rejected(self):
        public = self.native_public()
        public["observation"]["history"].insert(2, public["observation"]["history"][1])
        with self.assertRaises(SchemaError): validate_public(public, self.config)
        public = self.native_public(); event = public["observation"]["history"][1]; detail = json.loads(event["detail"])
        detail["deck"][0]["privateOrder"] = 4; event["detail"] = json.dumps(detail)
        with self.assertRaises(SchemaError): validate_public(public, self.config)

    def test_standalone_import_has_no_simulator_search_or_training(self):
        script = "import sys; from nosl.inference_v2 import InferenceV2; assert not any(x in sys.modules for x in ('nosl.train','nosl.data','nosl.data_v2','nosl.smoke_v2','clr')); print('standalone')"
        result = subprocess.run([sys.executable, "-c", script], cwd=ROOT, capture_output=True, text=True,
                                env={**__import__('os').environ, "PYTHONPATH": str(ROOT / "python")}, check=True)
        self.assertEqual(result.stdout.strip(), "standalone")


if __name__ == "__main__": unittest.main()
