"""V2 trainer engineering contracts. Every test forbids fitting and backward."""
from copy import deepcopy
import json
from pathlib import Path
import random
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.inference_v2 import (InferenceV2, action_policy_training_verified,
                               implementation_fingerprint as inference_fingerprint)
from nosl.model_v2 import StudentV2
from nosl.schema import HEADS, SchemaError
from nosl.schema_v2 import load_config
from nosl.train_v2 import (AUTHORIZATION_FORMAT, EXTRA_SOURCE_FILES, QUALITY_FORMAT, V2_TRAINING_SOURCES,
                           atomic_save, checkpoint_state, config_hash, evaluate, export_bundle,
                           empty_supervision_progress, _record_committed_supervision, supervision_coverage,
                           has_supervision, implementation_fingerprint, pilot_train, quality_gate,
                           restore_checkpoint, seed_everything)


def fixture(name):
    return json.loads((ROOT / "tests/python/fixtures" / name).read_text())


class V2TrainerNoFitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.v2.engineering.json")
        cls.config["base_config"]["hidden_dim"] = 16

    def setUp(self):
        for target in ("torch.Tensor.backward", "torch.optim.AdamW.step"):
            guard = patch(target, side_effect=AssertionError("no-fit engineering test attempted optimization"))
            guard.start()
            self.addCleanup(guard.stop)
        seed_everything(1729, 1)
        self.model = StudentV2(deepcopy(self.config))
        self.optimizer = torch.optim.AdamW(self.model.parameters(), lr=.001)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "checkpoint.pt"
        self.frozen = {"engineering_fixture": "random initialization; no training or authorization"}
        self.budget = {"max_steps": 2, "max_epochs": 1, "max_roots": 3}

    def save_zero(self):
        state = checkpoint_state(self.model, self.optimizer, self.frozen, self.budget, 0, 0, 0, [], [])
        atomic_save(state, self.path)
        return state

    def test_zero_step_roundtrip_restores_model_optimizer_and_rng_and_stays_untrained(self):
        state = self.save_zero()
        expected_random, expected_torch = random.random(), torch.rand(4)
        expected_weight = next(self.model.parameters()).detach().clone()
        with torch.no_grad(): next(self.model.parameters()).add_(1)
        self.optimizer.param_groups[0]["lr"] = .9
        seed_everything(77, 1)
        loaded = restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)
        self.assertEqual(random.random(), expected_random)
        self.assertTrue(torch.equal(torch.rand(4), expected_torch))
        self.assertTrue(torch.equal(next(self.model.parameters()), expected_weight))
        self.assertEqual(self.optimizer.param_groups[0]["lr"], .001)
        self.assertEqual(loaded["optimizer_steps"], 0)
        self.assertFalse(loaded["trained"])
        self.assertFalse(loaded["formal_training_run"])
        self.assertFalse(loaded["promoted"])
        self.assertEqual(loaded["supervision_progress"], empty_supervision_progress(self.config))
        self.assertNotIn("implementation", self.frozen)
        self.assertEqual(state["frozen"]["config_sha256"], config_hash(self.model.config))

    def test_checkpoint_binds_all_v1_and_v2_dependencies_with_relative_paths(self):
        expected = {"python/nosl/" + name for name in V2_TRAINING_SOURCES} | set(EXTRA_SOURCE_FILES)
        identity = implementation_fingerprint()
        self.assertEqual(set(identity["source_sha256"]), expected)
        self.assertEqual(identity, json.loads(json.dumps(identity)))
        self.assertNotIn(str(ROOT), json.dumps(identity))
        self.assertIn("python/nosl/train.py", expected)
        self.assertIn("python/nosl/public_identity_v2.py", expected)
        self.assertIn("python/nosl/schema_regen.py", expected)
        self.assertIn("tools/prepare_dataset.py", expected)

    def test_checkpoint_rejects_drift_in_every_bound_source(self):
        self.save_zero()
        original = Path.read_bytes
        for relative in implementation_fingerprint()["source_sha256"]:
            def read_bytes(path, relative=relative):
                data = original(path)
                return data + b"\n# simulated drift" if path == ROOT / relative else data
            with self.subTest(source=relative), patch.object(Path, "read_bytes", read_bytes):
                with self.assertRaisesRegex(SchemaError, "implementation.*changed"):
                    restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)

    def test_checkpoint_rejects_runtime_config_budget_input_and_missing_identity_changes(self):
        state = self.save_zero()
        with patch("nosl.reproducibility.torch.__version__", "different"):
            with self.assertRaisesRegex(SchemaError, "runtime.*changed"):
                restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)
        with patch("nosl.reproducibility.torch.get_num_threads", return_value=999):
            with self.assertRaisesRegex(SchemaError, "CPU settings changed"):
                restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)
        for frozen, budget in (({**self.frozen, "changed_input": True}, self.budget),
                               (self.frozen, {**self.budget, "max_steps": 3})):
            with self.assertRaises(SchemaError):
                restore_checkpoint(self.path, self.model, self.optimizer, frozen, budget)
        self.model.config["plan_loss_weights"]["extra_net_hp_loss"] = .5
        with self.assertRaises(SchemaError):
            restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)
        del state["frozen"]["implementation"]
        atomic_save(state, self.path)
        with self.assertRaisesRegex(SchemaError, "lacks implementation"):
            restore_checkpoint(self.path, self.model, self.optimizer, self.frozen, self.budget)

    def test_checkpoint_refuses_nonzero_steps_without_real_admission_and_bad_progress(self):
        with self.assertRaisesRegex(SchemaError, "quality acceptance|committed supervision"):
            checkpoint_state(self.model, self.optimizer, self.frozen, self.budget, 0, 0, 1, [], [])
        for epoch, offset, steps, order in ((0, 0, 3, []), (2, 0, 0, []), (0, 1, 0, []), (0, 0, 0, [0, 0])):
            with self.assertRaises(SchemaError):
                checkpoint_state(self.model, self.optimizer, self.frozen, self.budget, epoch, offset, steps, order, [])

    def test_untrained_bundle_roundtrip_has_required_inference_implementation(self):
        manifest = export_bundle(self.model, self.temp.name, frozen=self.frozen, budget=self.budget)
        self.assertFalse(manifest["trained"])
        self.assertEqual(manifest["optimizer_steps"], 0)
        self.assertFalse(manifest["calibrated"])
        self.assertEqual(manifest["implementation"], inference_fingerprint())
        runner = InferenceV2.from_bundle(self.temp.name, allow_experimental=True)
        self.assertEqual(runner.predict(fixture("finite-hunt-record-v2.jsonl")["public_input"])["status"], "MODEL_UNTRAINED")
        self.assertTrue(all(torch.equal(value, runner.model.state_dict()[name]) for name, value in self.model.state_dict().items()))

    def test_bundle_rejects_nonzero_steps_without_admission_before_writing(self):
        with self.assertRaisesRegex(SchemaError, "accepted quality"):
            export_bundle(self.model, self.temp.name, frozen=self.frozen, budget=self.budget, optimizer_steps=1)
        self.assertFalse((Path(self.temp.name) / "weights.pt").exists())

    def test_plan_only_supervision_is_usable_and_unresolved_targets_are_unavailable(self):
        complete = fixture("finite-hunt-record-v2.jsonl")
        unresolved = fixture("finite-hunt-unresolved-v2.jsonl")
        self.assertTrue(has_supervision(complete, self.config))
        self.assertFalse(has_supervision(unresolved, self.config))
        before = {name: value.clone() for name, value in self.model.state_dict().items()}
        metrics = evaluate(self.model, SimpleNamespace(split="validation", records=[complete, unresolved]))
        self.assertEqual(metrics["plan_success_brier"]["count"], 1)
        self.assertEqual(metrics["plan_extra_net_hp_loss_mae"]["count"], 1)
        self.assertEqual(metrics["plan_extra_net_hp_loss_mse"]["count"], 1)
        self.assertEqual(metrics["unavailable_plan_roots"], 1)
        self.assertEqual(metrics["win_brier"], {"value": None, "count": 0})
        self.assertEqual(metrics["empirical_teacher_mean_regret"]["count"], 0)
        self.assertEqual(metrics["empirical_teacher_ranking_semantics"]["incomplete_value_roots_skipped"], 2)
        self.assertFalse(metrics["test_evaluated"])
        self.assertFalse(metrics["safe_learned_plan_execution_verified"])
        self.assertTrue(self.model.training)
        self.assertTrue(all(torch.equal(before[name], value) for name, value in self.model.state_dict().items()))
        self.assertTrue(all(value.grad is None for value in self.model.parameters()))

    def test_action_metrics_use_only_available_positive_weight_targets(self):
        record = fixture("real-teacher-v2.jsonl")
        record["public_input"]["schema_version"] = "nosl.student.public.v2"
        record["targets"].update(pairwise=[], equivalent_action_set=[])
        for row in record["targets"]["actions"][1:]:
            row["quality"] = "unresolved"
            row["masks"] = {name: False for name in HEADS}
            row.update({name: None for name in HEADS})
        metrics = evaluate(self.model, SimpleNamespace(split="validation", records=[record]))
        for name in ("win_brier", "death_brier", "hp_mae", "value_mae", "value_mse", "potion_net_change_mae", "hp_distribution_cross_entropy"):
            self.assertEqual(metrics[name]["count"], 1, name)
        self.assertEqual(metrics["plan_success_brier"], {"value": None, "count": 0})
        self.assertEqual(metrics["empirical_teacher_mean_regret"]["count"], 0)
        record["targets"]["actions"][0]["sample_weight"] = 0
        metrics = evaluate(self.model, SimpleNamespace(split="validation", records=[record]))
        self.assertEqual(metrics["value_mae"], {"value": None, "count": 0})

    def test_test_records_are_never_accessed_even_for_accidental_evaluation(self):
        class SealedTest:
            split = "test"
            @property
            def records(self):
                raise AssertionError("test labels must never be read")
        with self.assertRaisesRegex(SchemaError, "test labels are sealed"):
            evaluate(self.model, SealedTest())

    def action_record(self):
        record = fixture("real-teacher-v2.jsonl")
        record["public_input"]["schema_version"] = "nosl.student.public.v2"
        return record

    def synthetic_progress_manifest(self, corpus_records, consumed_records, config=None):
        """In-memory boundary fixture only: does not fit or export learned weights."""
        config = config or self.config
        corpus = supervision_coverage(corpus_records, config)
        progress = empty_supervision_progress(config)
        _record_committed_supervision(progress, consumed_records, config)
        frozen = {"training_supervision": corpus, "config_sha256": config_hash(config),
                  "splits": {"train": {"records_sha256": config_hash(corpus_records)}}}
        evidence = {"config_sha256": config_hash(config), "train_records_sha256": config_hash(corpus_records),
                    "corpus": corpus, "progress": progress}
        return {"trained": True, "optimizer_steps": 1, "status": "EXPERIMENTAL_UNPROMOTED", "calibrated": False,
                "config_sha256": config_hash(config), "frozen_inputs": frozen, "training_supervision": corpus,
                "supervision_progress": progress, "supervision_sha256": config_hash(evidence)}

    def test_actual_committed_policy_supervision_is_required_beyond_corpus_masks(self):
        plan, action = fixture("finite-hunt-record-v2.jsonl"), self.action_record()
        for corpus in ([plan], [plan, action]):
            manifest = self.synthetic_progress_manifest(corpus, [plan])
            self.assertGreater(manifest["supervision_progress"]["consumed"]["plan_heads"]["specified_success_probability"]["loss_roots"], 0)
            self.assertFalse(action_policy_training_verified(manifest, self.config))
            with patch.object(self.model, "forward", side_effect=AssertionError("must abstain before forward")):
                result = InferenceV2(self.config, self.model, manifest, allow_experimental=True).predict(action["public_input"])
            self.assertEqual(result["status"], "ACTION_POLICY_UNVALIDATED")
            self.assertIsNone(result["selected_action"])

    def test_auxiliary_only_and_disabled_objective_losses_cannot_enable_action_policy(self):
        action, config = self.action_record(), deepcopy(self.config)
        for name in ("value", "pairwise", "equivalent"): config["base_config"]["loss_weights"][name] = 0
        manifest = self.synthetic_progress_manifest([action], [action], config)
        heads = manifest["training_supervision"]["action_heads"]
        self.assertGreater(heads["value"]["masked_rows"], 0)
        self.assertEqual(heads["value"]["loss_rows"], 0)
        self.assertGreater(heads["win_probability"]["loss_rows"], 0)
        self.assertFalse(action_policy_training_verified(manifest, config))
        self.assertEqual(InferenceV2(config, self.model, manifest, allow_experimental=True).predict(action["public_input"])["status"],
                         "ACTION_POLICY_UNVALIDATED")

    def test_policy_coverage_requires_meaningful_positive_legal_value_or_ranking_losses(self):
        action, config = self.action_record(), deepcopy(self.config)
        action["targets"].update(pairwise=[], equivalent_action_set=[])
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["value_roots"], 1)
        action["targets"]["actions"][0]["sample_weight"] = 0
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["eligible_roots"], 0)
        action["targets"]["actions"][0]["sample_weight"] = 1
        config["base_config"]["loss_weights"]["value"] = 0
        action["targets"]["equivalent_action_set"] = list(range(len(action["public_input"]["candidate_actions"])))
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["eligible_roots"], 0)
        action["targets"]["pairwise"] = [{"preferred": 0, "other": 1, "weight": 0}]
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["eligible_roots"], 0)
        action["targets"]["pairwise"][0]["weight"] = 1
        coverage = supervision_coverage([action], config)
        self.assertEqual(coverage["action_policy"]["pairwise_roots"], 1)
        self.assertEqual(coverage["action_policy"]["pairwise_pairs"], 1)
        self.assertEqual(coverage["policy_action_kinds"]["end_turn"], 0)
        action["targets"]["actions"][1]["sample_weight"] = 0
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["pairwise_roots"], 0)

    def test_all_legal_equivalence_alone_is_not_usable_training_supervision(self):
        action, config = self.action_record(), deepcopy(self.config)
        config["base_config"]["loss_weights"] = {name: float(name == "equivalent")
                                                 for name in config["base_config"]["loss_weights"]}
        action["targets"]["equivalent_action_set"] = list(range(len(action["public_input"]["candidate_actions"])))
        self.assertFalse(has_supervision(action, config))
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["eligible_roots"], 0)
        action["targets"]["equivalent_action_set"] = [0]
        self.assertTrue(has_supervision(action, config))
        self.assertEqual(supervision_coverage([action], config)["action_policy"]["equivalent_roots"], 1)

    def test_missing_mismatched_or_zero_step_coverage_fails_closed(self):
        action = self.action_record()
        manifest = self.synthetic_progress_manifest([action], [action])
        self.assertTrue(action_policy_training_verified(manifest, self.config, action["public_input"]))
        for key in ("training_supervision", "supervision_progress", "supervision_sha256", "frozen_inputs"):
            changed = deepcopy(manifest); del changed[key]
            self.assertFalse(action_policy_training_verified(changed, self.config))
            self.assertEqual(InferenceV2(self.config, self.model, changed, allow_experimental=True).predict(action["public_input"])["status"],
                             "ACTION_POLICY_UNVALIDATED")
        changed = deepcopy(manifest); changed["supervision_progress"]["optimizer_steps"] = 2
        self.assertFalse(action_policy_training_verified(changed, self.config))
        changed = deepcopy(manifest); del changed["supervision_progress"]["consumed"]["policy_action_kinds"]
        self.assertFalse(action_policy_training_verified(changed, self.config))
        changed = deepcopy(manifest); changed["optimizer_steps"] = 0
        self.assertEqual(InferenceV2(self.config, self.model, changed, allow_experimental=True).predict(action["public_input"])["status"], "MODEL_UNTRAINED")

    def test_verified_action_coverage_never_enables_finite_plan_execution(self):
        action = self.action_record()
        manifest = self.synthetic_progress_manifest([action], [action])
        runner = InferenceV2(self.config, self.model, manifest, allow_experimental=True)
        self.assertEqual(runner.predict(action["public_input"])["status"], "EXPERIMENTAL_UNCALIBRATED")
        result = InferenceV2(self.config, self.model, manifest, allow_experimental=True).predict(fixture("finite-hunt-record-v2.jsonl")["public_input"])
        self.assertEqual(result["status"], "PLAN_POLICY_UNVALIDATED")
        self.assertIsNone(result["selected_action"])

    def test_unseen_legal_resource_kind_abstains_the_complete_decision(self):
        action = self.action_record()
        manifest = self.synthetic_progress_manifest([action], [action])
        for kind in ("potion", "discard_potion"):
            public = deepcopy(action["public_input"])
            public["observation"]["potions"][0] = "FirePotion"
            public["candidate_actions"].append({"revision": public["candidate_actions"][0]["revision"], "kind": kind,
                                                 "slot": 0, "target": 0 if kind == "potion" else -1, "selection": None})
            public["legal_mask"].append(True)
            before = deepcopy(public)
            with patch.object(self.model, "forward", side_effect=AssertionError("must abstain without filtering candidates")):
                result = InferenceV2(self.config, self.model, manifest, allow_experimental=True).predict(public)
            self.assertEqual(result["status"], "ACTION_POLICY_UNVALIDATED", result)
            self.assertIsNone(result["selected_action"])
            self.assertEqual(public, before)

    def gate_inputs(self):
        return {"format": "nosl.training.inputs.v2", "dataset_manifest": {"lock": {"mode": "pilot"}, "isolation_passed": True},
                "splits": {split: {"roots": 3, "supervised_roots": 2, "engineering_roots": 0, "nontrainable_roots": 0}
                           for split in ("train", "validation")}, "frozen_test_shards": [{"sha256": "0" * 64, "rows": 1}],
                "test_labels_read": False, "test_evaluated": False, "test_used_for_model_selection": False}

    def gate_records(self, frozen):
        quality = {"format": QUALITY_FORMAT, "accepted": True, "frozen_inputs_sha256": config_hash(frozen),
                   "reviewed_by": "synthetic gate unit test", "assessment": "schema-only test; no fit authorized"}
        authorization = {"format": AUTHORIZATION_FORMAT, "authorized": True, "purpose": "bounded_nonformal_pilot",
                         "frozen_inputs_sha256": config_hash(frozen), "quality_acceptance_sha256": config_hash(quality),
                         "authorization_id": "synthetic schema test only", "budget": self.budget}
        return quality, authorization

    def test_quality_acceptance_and_bounded_authorization_are_both_required_and_exact(self):
        frozen = self.gate_inputs()
        quality, authorization = self.gate_records(frozen)
        self.assertFalse(quality_gate(frozen, self.budget)["accepted"])
        self.assertFalse(quality_gate(frozen, self.budget, quality_record=quality)["accepted"])
        self.assertFalse(quality_gate(frozen, self.budget, authorization=authorization)["accepted"])
        self.assertTrue(quality_gate(frozen, self.budget, quality_record=quality, authorization=authorization)["accepted"])
        for changed in ({**frozen, "changed_source_or_input": True},
                        {**frozen, "test_evaluated": True},
                        {**frozen, "dataset_manifest": {"lock": {"mode": "engineering-smoke"}, "isolation_passed": True}}):
            self.assertFalse(quality_gate(changed, self.budget, quality_record=quality, authorization=authorization)["accepted"])
        self.assertFalse(quality_gate(frozen, {**self.budget, "max_steps": 3}, quality_record=quality, authorization=authorization)["accepted"])

    def test_even_fresh_acceptance_cannot_override_engineering_or_nontrainable_records(self):
        for field in ("engineering_roots", "nontrainable_roots"):
            frozen = self.gate_inputs()
            frozen["splits"]["train"][field] = 1
            quality, authorization = self.gate_records(frozen)
            self.assertFalse(quality_gate(frozen, self.budget, quality_record=quality, authorization=authorization)["accepted"])

    def test_unauthorized_pilot_stops_before_model_or_optimizer_creation(self):
        with patch("nosl.train_v2.freeze_inputs", return_value=self.gate_inputs()), \
             patch("nosl.train_v2.StudentV2", side_effect=AssertionError("should stop before model construction")):
            with self.assertRaisesRegex(SchemaError, "fit blocked"):
                pilot_train({}, self.config, Path(self.temp.name) / "no-fit", budget=self.budget)
        self.assertFalse((Path(self.temp.name) / "no-fit").exists())

    def test_formal_cli_is_disabled_before_reading_data(self):
        result = subprocess.run([sys.executable, "-m", "nosl.train_v2", "--config", "missing", "--prepared", "missing",
                                 "--mode", "formal"], cwd=ROOT, capture_output=True, text=True,
                                env={**__import__("os").environ, "PYTHONPATH": str(ROOT / "python")})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("formal v2 training is disabled", result.stderr)


if __name__ == "__main__": unittest.main()
