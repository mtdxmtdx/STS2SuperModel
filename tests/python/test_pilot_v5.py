"""Engineering contract tests only: backward and optimizer steps are forbidden."""
from contextlib import ExitStack, redirect_stdout
from copy import deepcopy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/python"), str(ROOT / "tests/data")]
from test_prepare_v5 import STUDENT, PIPELINE, cohort, fixture, legacy_protection
from nosl.data import canonical_object_digest
from nosl.inference_v5 import InferenceV5
from nosl.pilot_v5 import (AUTHORIZATION_FORMAT, checkpoint_state, evaluate_auxiliary, execution_gate,
    freeze_inputs, main, new_model_optimizer, pilot_train, restore_checkpoint, validate_pilot_config,
    _validate_optimizer_snapshot)
from nosl.prepare_v5 import PreparedDatasetV5, choose_split, persist_snapshot
from nosl.protection_v5 import group_records
from nosl.schema import SchemaError
from nosl.train import seed_everything

PILOT = json.loads((ROOT / "configs/student.v5.bounded-pilot.json").read_text())


def forbidden_learning(*args, **kwargs):
    raise AssertionError("No backward or optimizer step is authorized in v5 interface tests")


class PilotV5Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name) / "fixture-snapshot"
        representatives = {}
        for index in range(70):
            row = fixture(index)
            group = group_records([row], legacy_protection()["components"])[0][0]
            representatives.setdefault(choose_split(group, PIPELINE), row)
            if len(representatives) == 3:
                break
        assert set(representatives) == {"train", "validation", "test"}
        rows = list(representatives.values())
        attempts, receipt = cohort(rows)
        persist_snapshot(cls.root, rows, attempts, receipt, legacy_protection(), PIPELINE, STUDENT)

    def setUp(self):
        self.guards = ExitStack()
        self.addCleanup(self.guards.close)
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.optim.Optimizer.step", "torch.optim.AdamW.step"):
            self.guards.enter_context(patch(target, new=forbidden_learning))
        seed_everything(PILOT["seed"], PILOT["torch_threads"])
        self.datasets = {s: PreparedDatasetV5(self.root, s, STUDENT, purpose="engineering-fixture")
                         for s in ("train", "validation")}
        self.frozen = freeze_inputs(self.datasets, STUDENT, PILOT)
        self.model, self.optimizer = new_model_optimizer(STUDENT, PILOT)

    def state(self):
        return checkpoint_state(self.model, self.optimizer, self.frozen, PILOT)

    def restore(self, state, *, frozen=None, pilot=None, authorization=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "checkpoint.pt"
            torch.save(state, path)
            return restore_checkpoint(path, self.model, self.optimizer,
                                      self.frozen if frozen is None else frozen,
                                      PILOT if pilot is None else pilot, authorization=authorization)

    def test_zero_step_roundtrip_reproduces_predictions_and_rng(self):
        public = self.datasets["validation"][0]["public_input"]
        self.model.eval()
        with torch.no_grad():
            before = self.model(public)
        state = self.state()
        expected_rng = torch.rand(4)
        with torch.no_grad():
            next(self.model.parameters()).add_(1)
        restored = self.restore(state)
        self.assertEqual(restored["optimizer_steps"], 0)
        self.assertEqual(restored["cursor"], 0)
        self.assertFalse(restored["auxiliary_trained"])
        self.assertFalse(restored["policy_ready"])
        self.assertEqual(self.optimizer.state_dict()["state"], {})
        self.assertTrue(torch.equal(torch.rand(4), expected_rng))
        with torch.no_grad():
            after = self.model(public)
        for head in PILOT["auxiliary_heads"]:
            self.assertTrue(torch.equal(before[head], after[head]), head)

    def test_fixed_config_rejects_schema_model_and_budget_expansion(self):
        for name, value in (("format", "legacy"), ("public_schema", "nosl.student.public.v4"),
            ("model_version", "nosl.student.model.v4"), ("student_config_sha256", "0" * 64),
            ("max_epochs", 2), ("max_optimizer_steps", 701), ("max_optimizer_steps", True),
            ("max_optimizer_steps", 0), ("batch_size", 0), ("max_training_roots", 5001),
            ("formal_training", True), ("policy_promotion", True), ("learning_rate", float("nan"))):
            with self.subTest(name=name, value=value):
                config = deepcopy(PILOT); config[name] = value
                with self.assertRaises((SchemaError, ValueError)):
                    validate_pilot_config(config, STUDENT)

    def test_only_prepared_train_validation_acceptance(self):
        for datasets in ({"train": self.datasets["train"]}, {**self.datasets, "test": self.datasets["validation"]},
                         {"train": [], "validation": []}):
            with self.assertRaises(SchemaError):
                freeze_inputs(datasets, STUDENT, PILOT)
        with self.assertRaisesRegex(SchemaError, "never_loads_test"):
            PreparedDatasetV5(self.root, "test", STUDENT, purpose="engineering-fixture")

    def test_in_memory_target_and_public_graph_mutations_rejected(self):
        for mutate in (lambda r: r["targets"]["actions"][0].update(expected_final_hp=1),
                       lambda r: r["public_input"]["public_evidence"]["events"][2]["payload"]["currentMap"]["nodes"][-2].update(nodeType="event")):
            with self.subTest(mutate=mutate):
                data = {s: PreparedDatasetV5(self.root, s, STUDENT, purpose="engineering-fixture")
                        for s in ("train", "validation")}
                mutate(data["train"].records[0])
                with self.assertRaisesRegex(SchemaError, "changed_after_loading"):
                    freeze_inputs(data, STUDENT, PILOT)

    def test_resume_rejects_changed_data_receipt_protection_config_or_source(self):
        state = self.state()
        for key in ("manifest_sha256", "admission_sha256", "protection_sha256", "pilot_config_sha256",
                    "student_config_sha256", "public_schema", "model_version"):
            changed = deepcopy(self.frozen); changed[key] = "changed"
            with self.subTest(key=key), self.assertRaises(SchemaError):
                self.restore(state, frozen=changed)
        changed = deepcopy(self.frozen)
        changed["implementation"]["source_sha256"]["python/nosl/model_v5.py"] = "0" * 64
        with self.assertRaisesRegex(SchemaError, "implementation"):
            self.restore(state, frozen=changed)
        changed = deepcopy(self.frozen)
        changed["splits"]["train"]["records_sha256"] = "0" * 64
        with self.assertRaisesRegex(SchemaError, "identity"):
            self.restore(state, frozen=changed)
        changed = deepcopy(PILOT); changed["max_optimizer_steps"] = 699
        with self.assertRaises(SchemaError):
            self.restore(state, pilot=changed)

    def test_resume_rejects_forged_schema_progress_budget_and_promotion(self):
        state = self.state()
        for key, value in (("format", "nosl.experimental.checkpoint.v2"), ("public_schema", "legacy"),
            ("model_version", "legacy"), ("cursor", 1), ("optimizer_steps", 1), ("optimizer_steps", 701),
            ("epochs_completed", 1), ("order", []), ("auxiliary_trained", True),
            ("policy_ready", True), ("formal_training_run", True), ("promoted", True)):
            changed = deepcopy(state); changed[key] = value
            with self.subTest(key=key), self.assertRaises(SchemaError):
                self.restore(changed)
        changed = deepcopy(state); changed["pilot_config"]["max_epochs"] = 2
        with self.assertRaisesRegex(SchemaError, "lifetime_budget"):
            self.restore(changed)
        changed = deepcopy(state)
        key = next(iter(changed["model"]))
        changed["model"][key] = changed["model"][key].to(torch.float64)
        with self.assertRaisesRegex(SchemaError, "tensor_schema"):
            self.restore(changed)

    def test_resume_rejects_optimizer_settings_and_nonfinite_state_before_mutation(self):
        before = deepcopy(self.model.state_dict())
        for mutate in (lambda s: s["optimizer"]["param_groups"][0].update(lr=.005),
                       lambda s: s["optimizer"]["state"].update({0: {"step": torch.tensor(1.)}}),
                       lambda s: next(iter(s["model"].values())).fill_(float("nan"))):
            changed = self.state(); mutate(changed)
            with self.assertRaises(SchemaError):
                self.restore(changed)
            self.assertTrue(all(torch.equal(before[k], v) for k, v in self.model.state_dict().items()))

    def test_raw_moment_schema_checked_before_deserializer_casts(self):
        # Handcrafted optimizer metadata only. No optimizer step was performed,
        # and this is deliberately not a valid learned or zero-step checkpoint.
        state = self.state()
        parameter = self.optimizer.param_groups[0]["params"][0]
        parameter_id = state["optimizer"]["param_groups"][0]["params"][0]
        state["optimizer"]["state"][parameter_id] = {
            "step": torch.tensor(1.), "exp_avg": torch.zeros_like(parameter),
            "exp_avg_sq": torch.zeros_like(parameter)}
        _validate_optimizer_snapshot(state["optimizer"], self.optimizer)
        before = deepcopy(self.model.state_dict())
        for field in ("exp_avg", "exp_avg_sq"):
            for malformed in (torch.full(parameter.shape, 1e300, dtype=torch.float64),
                              torch.zeros(parameter.shape, dtype=torch.float64),
                              torch.zeros((parameter.numel() + 1,), dtype=parameter.dtype)):
                changed = deepcopy(state)
                changed["optimizer"]["state"][parameter_id][field] = malformed
                with self.subTest(field=field, dtype=malformed.dtype, shape=malformed.shape), \
                        patch.object(torch.optim.AdamW, "load_state_dict", side_effect=AssertionError("raw validation must run first")), \
                        self.assertRaisesRegex(SchemaError, "optimizer_moment_schema"):
                    self.restore(changed)
        self.assertEqual(self.optimizer.state_dict()["state"], {})
        self.assertTrue(all(torch.equal(before[k], v) for k, v in self.model.state_dict().items()))

    def test_raw_parameter_id_order_is_checked_before_deserializer_remaps(self):
        state = self.state()
        parameters = self.optimizer.param_groups[0]["params"]
        left, right = next((i, j) for i, a in enumerate(parameters)
                           for j, b in enumerate(parameters) if i < j and a.shape == b.shape)
        changed = deepcopy(state)
        ids = changed["optimizer"]["param_groups"][0]["params"]
        ids[left], ids[right] = ids[right], ids[left]
        with patch.object(torch.optim.AdamW, "load_state_dict", side_effect=AssertionError("raw validation must run first")), \
                self.assertRaisesRegex(SchemaError, "optimizer_parameter_ids"):
            self.restore(changed)
        self.assertEqual(self.optimizer.state_dict()["state"], {})

    def test_deserialized_nonfinite_moments_rejected_before_caller_mutation(self):
        state = self.state()
        before = deepcopy(self.model.state_dict())
        load = torch.optim.AdamW.load_state_dict

        def corrupt_disposable_state(optimizer, saved):
            load(optimizer, saved)
            parameter = optimizer.param_groups[0]["params"][0]
            optimizer.state[parameter] = {"step": torch.tensor(1.),
                "exp_avg": torch.full_like(parameter, float("inf")), "exp_avg_sq": torch.zeros_like(parameter)}

        with patch.object(torch.optim.AdamW, "load_state_dict", new=corrupt_disposable_state), \
                self.assertRaisesRegex(SchemaError, "optimizer_contains_nonfinite"):
            self.restore(state)
        self.assertEqual(self.optimizer.state_dict()["state"], {})
        self.assertTrue(all(torch.equal(before[k], v) for k, v in self.model.state_dict().items()))

    def test_engineering_fixture_and_missing_authorization_never_execute(self):
        gate = execution_gate(self.frozen, PILOT)
        self.assertFalse(gate["accepted"])
        self.assertIn("real_native_cohort_required", gate["reasons"])
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "not-created"
            with self.assertRaisesRegex(SchemaError, "explicit_bounded"):
                pilot_train(self.datasets, STUDENT, PILOT, output)
            with self.assertRaisesRegex(SchemaError, "cohort"):
                pilot_train(self.datasets, STUDENT, PILOT, output, execute=True)
            self.assertFalse(output.exists())

    def test_authorization_bindings_only_with_synthetic_lock_objects_no_fit(self):
        # Validator-only shape exercise. This is not a real cohort or permission,
        # and this synthetic object never enters pilot_train or a checkpoint.
        frozen = deepcopy(self.frozen)
        frozen.update(purpose="bounded-pilot", synthetic_runtime=False)
        frozen["review"]["evidence_kind"] = "predeclared_fresh_cohort_quality"
        authorization = {"format": AUTHORIZATION_FORMAT, "authorized": True, "purpose": "bounded-pilot",
            "authorization_id": "synthetic-validator-test-only", "reviewed_by": "synthetic-test",
            "approval_kind": "reviewed_real_cohort", "frozen_inputs_sha256": canonical_object_digest(frozen),
            "admission_sha256": frozen["admission_sha256"], "protection_sha256": frozen["protection_sha256"],
            "review_evidence_sha256": frozen["review"]["evidence_sha256"],
            "pilot_config_sha256": canonical_object_digest(PILOT), "max_epochs": 1, "max_optimizer_steps": 700}
        self.assertTrue(execution_gate(frozen, PILOT, authorization)["accepted"])
        for key in authorization:
            changed = deepcopy(authorization)
            changed[key] = False if type(changed[key]) is bool else (701 if type(changed[key]) is int else "")
            with self.subTest(key=key):
                self.assertFalse(execution_gate(frozen, PILOT, changed)["accepted"])
        self.assertFalse(execution_gate(self.frozen, PILOT, authorization)["accepted"])

    def test_auxiliary_validation_no_policy_or_plan_predictions(self):
        report = evaluate_auxiliary(self.model, self.datasets["validation"], include_predictions=True)
        self.assertEqual(report["status"], "EXPERIMENTAL_AUXILIARY_ONLY")
        self.assertIsNone(report["selected_action"])
        self.assertFalse(report["policy_ready"])
        self.assertTrue(report["predictions"])
        for prediction in report["predictions"]:
            self.assertEqual(set(prediction["auxiliary"]), set(PILOT["auxiliary_heads"]))
            self.assertNotIn("value", prediction["auxiliary"])
            self.assertNotIn("plan", prediction)
        with self.assertRaisesRegex(SchemaError, "validation"):
            evaluate_auxiliary(self.model, self.datasets["train"])
        self.assertTrue(all(parameter.grad is None for parameter in self.model.parameters()))

    def test_unavailable_head_parameters_excluded_from_AdamW_decay(self):
        optimizer_parameters = {id(p) for p in self.optimizer.param_groups[0]["params"]}
        masked = []
        for name, parameter in self.model.named_parameters():
            if ".heads.value." in "." + name or ".plan_heads." in "." + name:
                masked.append(name)
                self.assertFalse(parameter.requires_grad)
                self.assertNotIn(id(parameter), optimizer_parameters)
        self.assertTrue(masked)

    def test_original_inference_abstention_survives_claimed_training(self):
        public = self.datasets["validation"][0]["public_input"]
        for manifest, expected in (({}, "MODEL_UNTRAINED"), ({"trained": True, "optimizer_steps": 3}, "MODEL_UNVALIDATED")):
            result = InferenceV5(STUDENT, self.model, manifest).predict(public)
            self.assertEqual(result["status"], expected)
            self.assertIsNone(result["selected_action"])
            self.assertEqual(result["predictions"], [])

    def test_cli_defaults_to_readiness_and_forward_check_has_no_learning(self):
        for arguments in ([], ["--prepared", str(self.root), "--engineering-fixture", "--forward-check"]):
            output = io.StringIO()
            with redirect_stdout(output):
                code = main(arguments)
            report = json.loads(output.getvalue())
            self.assertEqual(code, 0)
            self.assertEqual(report["status"], "FIT_BLOCKED")
            self.assertEqual(report["mode"], "dry-run")
            self.assertEqual(report["optimizer_steps"], 0)
            self.assertEqual(report["backward_calls"], 0)
            self.assertFalse(report["weights_written"])
        for arguments in (["--execute-bounded-pilot", "--engineering-fixture"], ["--resume"]):
            with redirect_stdout(io.StringIO()):
                self.assertEqual(main(arguments), 2)


if __name__ == "__main__":
    unittest.main()
