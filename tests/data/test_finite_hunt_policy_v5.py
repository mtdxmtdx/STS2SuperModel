"""Fresh C# plan evidence in the full-policy engineering seam, with no learning.

The artifacts must come from FiniteHuntV5Tests in this checkout. Missing sources
explicitly skip these integration tests; no old labels or fabricated training,
protection, admission or optimization receipts are substituted.
"""
from copy import deepcopy
from contextlib import redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data_policy_v5 import (PolicyDatasetV5, RECORD_FORMAT, supervision_coverage,
                                 validate_record)
from nosl.data_v5 import validate_production_record
from nosl.finite_hunt_policy_v5 import (EVALUATOR_SOURCE_FILES, EVIDENCE_FORMAT, OBJECTIVE_SPEC_FILES,
    SOURCE_KIND, adapt_jsonl, adapt_record, validate_record as validate_constructed_record)
from nosl.inference_policy_v5 import InferencePolicyV5
from nosl.model_v5 import StudentV5
from nosl.native_v5 import validate_candidate
from nosl.policy_v5 import INFERENCE_SOURCES, digest, state_digest
from nosl.public_identity_v5 import public_input_digest
from nosl.schema import HEADS
from nosl.schema_v5 import load_config, loads
from nosl.train_policy_v5 import TRAINING_SOURCES, batch_loss, execution_gate, main as training_main

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURES = Path(os.environ.get("NOSL_FRESH_HUNT_V5_FIXTURES",
                              os.environ.get("NOSL_FRESH_HUNT_V5_EXPORT",
                                             ROOT / "artifacts/fresh-finite-v5")))


def encode(value):
    return (json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


class ConstructedFullPolicyV5Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = {kind: FIXTURES / f"fresh-finite-hunt-v5-{kind}.raw.json"
                 for kind in ("success", "truncated", "loss")}
        if not all(path.is_file() for path in paths.values()):
            raise unittest.SkipTest("Fresh C# Hunt-v5 artifacts missing. Run: "
                "NOSL_FRESH_HUNT_V5_EXPORT=artifacts/fresh-finite-v5 dotnet test tests/Nosl.Tests/Nosl.Tests.csproj "
                "--filter FullyQualifiedName~FiniteHuntV5Tests")
        cls.payloads = {kind: path.read_bytes() for kind, path in paths.items()}
        torch.set_num_threads(1)

    def setUp(self):
        self.calls = []

        def forbidden(*unused, **also_unused):
            self.calls.append(True)
            raise AssertionError("Constructed policy test attempted learning")

        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            guard = patch(target, side_effect=forbidden)
            guard.start(); self.addCleanup(guard.stop)
        context = torch.no_grad()
        context.__enter__(); self.addCleanup(lambda: context.__exit__(None, None, None))
        self.addCleanup(lambda: self.assertEqual([], self.calls))

    def row(self, kind="success"):
        payload = self.payloads[kind]
        return adapt_record(loads(payload.decode("utf-8")), CONFIG, source_raw_bytes=payload)

    def test_exact_fresh_evidence_has_only_constructed_engineering_claims(self):
        for kind in self.payloads:
            with self.subTest(kind=kind):
                row = self.row(kind); raw = loads(self.payloads[kind].decode("utf-8"))
                self.assertEqual(RECORD_FORMAT, row["format"])
                self.assertEqual(raw["public_input"], row["public_input"])
                self.assertEqual(raw["targets"], row["targets"])
                audit = row["audit_only"]
                self.assertEqual("engineering-fixture", audit["purpose"])
                self.assertEqual(SOURCE_KIND, audit["source_kind"])
                self.assertIs(audit["trainable"], False); self.assertIs(audit["native_run"], False)
                self.assertIsNone(audit["producer_receipt_sha256"])
                for key in ("actual_seed", "source_draw_seed", "native_source_run_identity"):
                    self.assertNotIn(key, audit)
                for key in ("source_run_group", "source_combat_id", "branch_family"):
                    self.assertEqual(raw["audit_only"][key], audit[key])
                self.assertEqual(EVIDENCE_FORMAT, audit["constructed_evidence"]["format"])
                self.assertEqual(self.payloads[kind], audit["constructed_evidence"]["raw_utf8"].encode("utf-8"))
                source_hash = hashlib.sha256(self.payloads[kind]).hexdigest()
                self.assertEqual(source_hash, audit["source_artifact_sha256"])
                self.assertEqual(source_hash, audit["source_record_sha256"])
                self.assertEqual(public_input_digest(row["public_input"]), audit["public_state_digest"])
                self.assertEqual(audit["public_state_digest"], audit["conditioned_public_input_digest"])
                self.assertEqual(digest(row["targets"]), audit["targets_sha256"])
                self.assertIs(row, validate_record(row, CONFIG))
                self.assertIs(row, validate_constructed_record(row, CONFIG))

    def test_real_success_loss_and_truncation_keep_masks_and_denominators(self):
        for kind, value in (("success", 1), ("loss", 0), ("truncated", None)):
            with self.subTest(kind=kind):
                row = self.row(kind); plan = row["targets"]["plan"]
                self.assertEqual(value, plan["specified_success_probability"])
                self.assertEqual(2, plan["allocated_worlds"])
                self.assertEqual(0 if value is None else 2, plan["success_completed_worlds"])
                self.assertEqual(0 if value is None else 2, plan["paired_completed_worlds"])
                self.assertEqual(None if value is None else 0, plan["extra_net_hp_loss"])
                self.assertEqual({head: value is not None for head in plan["masks"]}, plan["masks"])
                self.assertTrue(all(not any(r["masks"].values()) for r in row["targets"]["actions"]))
                self.assertEqual([], row["targets"]["pairwise"])
                self.assertEqual([], row["targets"]["equivalent_action_set"])

    def test_candidate_identity_uses_actual_spec_and_source_bytes(self):
        row = self.row(); objective = row["objective"]
        for key, paths in (("objective_spec_sha256", OBJECTIVE_SPEC_FILES),
                           ("evaluator_source_sha256", EVALUATOR_SOURCE_FILES)):
            actual = {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in paths}
            self.assertEqual(digest(actual), objective[key])
        self.assertEqual("candidate", objective["objective_profile_status"])
        self.assertIs(objective["objective_calibrated"], False)
        self.assertIsNone(objective["calibration_evidence_sha256"])
        self.assertEqual(row["public_input"]["controller_context"]["templateId"], objective["continuation_policy_id"])
        self.assertEqual(3, len({self.row(kind)["objective"]["evaluation_design_sha256"] for kind in self.payloads}))

    def test_engineering_dataset_and_actual_batch_loss_without_gradients(self):
        row = self.row()
        dataset = PolicyDatasetV5([row], CONFIG, split="train", purpose="engineering-fixture")
        before_row = deepcopy(row)
        with torch.random.fork_rng():
            torch.manual_seed(1729)
            model = StudentV5(CONFIG)
        model.eval(); before = state_digest(model.state_dict())
        loss, terms = batch_loss(model, dataset.records, CONFIG)
        self.assertTrue(torch.isfinite(loss)); self.assertFalse(loss.requires_grad)
        self.assertIsNone(loss.grad_fn)
        self.assertEqual(before, state_digest(model.state_dict()))
        self.assertTrue(all(parameter.grad is None for parameter in model.parameters()))
        self.assertEqual(before_row, row); self.assertEqual([row], dataset.records)
        self.assertTrue(all(terms[0][head] == 0 for head in HEADS))
        self.assertEqual(0, terms[0]["pairwise"]); self.assertEqual(0, terms[0]["equivalent"])
        self.assertGreater(terms[0]["plan.specified_success_probability"], 0)
        self.assertGreaterEqual(terms[0]["plan.extra_net_hp_loss"], 0)
        coverage = supervision_coverage(row, CONFIG)
        self.assertEqual(0, coverage["action_policy"]["eligible_roots"])
        self.assertTrue(all(head["loss_roots"] == 1 for head in coverage["plan_heads"].values()))
        prediction = InferencePolicyV5(CONFIG, model, allow_experimental=True).predict(row["public_input"])
        self.assertEqual("MODEL_UNTRAINED", prediction["status"])
        self.assertIsNone(prediction["selected_action"])

    def test_malicious_audit_and_objective_claims_cannot_enter_dataset(self):
        mutations = [
            lambda r: r["audit_only"].update(purpose="bounded-objective-pilot", trainable=True),
            lambda r: r["audit_only"].update(purpose="native-objective-candidate"),
            lambda r: r["audit_only"].update(source_kind="native_empirical_objective_candidate"),
            lambda r: r["audit_only"].update(source_kind="synthetic_contract_fixture"),
            lambda r: r["audit_only"].update(source_kind="externally_reviewed_objective_outcomes"),
            lambda r: r["audit_only"].update(native_run=True),
            lambda r: r["audit_only"].update(trainable=0),
            lambda r: r["audit_only"].update(producer_receipt_sha256="a" * 64),
            lambda r: r["audit_only"].update(actual_seed="invented-native-seed"),
            lambda r: r["audit_only"].update(source_draw_seed=7),
            lambda r: r["audit_only"].update(native_source_run_identity="b" * 64),
            lambda r: r["audit_only"].update(source_run_group="replacement"),
            lambda r: r["audit_only"].update(public_state_digest="c" * 64),
            lambda r: r["audit_only"].update(source_record_sha256="d" * 64),
            lambda r: r["audit_only"].update(source_artifact_sha256="e" * 64),
            lambda r: r["audit_only"]["constructed_evidence"].update(format="unreviewed-format"),
            lambda r: r["audit_only"]["constructed_evidence"].update(trusted=True),
            lambda r: r["audit_only"]["constructed_evidence"].update(raw_utf8=r["audit_only"]["constructed_evidence"]["raw_utf8"] + " "),
            lambda r: r["objective"].update(objective_profile_status="calibrated", objective_calibrated=True,
                                           calibration_evidence_sha256="f" * 64),
            lambda r: r["objective"].update(evaluation_design_sha256="a" * 64),
            lambda r: r["objective"].update(evaluator_source_sha256="b" * 64),
            lambda r: r["objective"].update(objective_spec_sha256="c" * 64),
            lambda r: r["objective"].update(continuation_policy_id="replacement"),
            lambda r: r["objective"].update(independent_final_evaluation=False),
        ]
        for index, mutate in enumerate(mutations):
            row = self.row(); mutate(row)
            with self.subTest(mutation=index), self.assertRaises(ValueError):
                PolicyDatasetV5([row], CONFIG, split="train", purpose="engineering-fixture")

    def test_resealed_outer_hashes_cannot_hide_target_or_raw_mutation(self):
        row = self.row(); row["targets"]["plan"]["specified_success_probability"] = .5
        row["audit_only"]["targets_sha256"] = digest(row["targets"])
        with self.assertRaises(ValueError): validate_record(row, CONFIG)
        for mutate in (
            lambda r: r["targets"]["plan"].update(specified_success_probability=.5),
            lambda r: r["audit_only"].update(native_run=True),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"].pop(),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(settlementComplete=False),
            lambda r: r["audit_only"]["sampled_public_roots"][0]["publicRoot"]["observation"].update(hp=1),
        ):
            row = self.row(); raw = loads(row["audit_only"]["constructed_evidence"]["raw_utf8"])
            mutate(raw); payload = encode(raw)
            row["audit_only"]["constructed_evidence"]["raw_utf8"] = payload.decode("utf-8")
            row["audit_only"]["source_artifact_sha256"] = hashlib.sha256(payload).hexdigest()
            row["audit_only"]["source_record_sha256"] = hashlib.sha256(payload).hexdigest()
            row["targets"] = deepcopy(raw["targets"]); row["audit_only"]["targets_sha256"] = digest(row["targets"])
            with self.subTest(mutate=mutate), self.assertRaises(ValueError): validate_record(row, CONFIG)

    def test_exact_transport_and_dataset_integrity(self):
        payload = self.payloads["success"]
        raw = loads(payload.decode("utf-8")); original = deepcopy(raw)
        first = adapt_record(raw, CONFIG, source_raw_bytes=payload)
        padded = adapt_record(raw, CONFIG, source_raw_bytes=payload + b" ")
        self.assertEqual(raw, original)
        self.assertNotEqual(first["audit_only"]["source_record_sha256"], padded["audit_only"]["source_record_sha256"])
        self.assertEqual(first["targets"], padded["targets"])
        validate_record(padded, CONFIG)  # Byte hashes are bindings, not authentication.
        for invalid in (b"", b"{}", b'{"x":0,"x":1}', b'{"x":NaN}', b"\xff"):
            with self.subTest(payload=invalid), self.assertRaises(ValueError): adapt_jsonl(invalid, CONFIG)
        with self.assertRaises(ValueError): adapt_record(raw, CONFIG, source_raw_bytes=b"{}")
        rows = adapt_jsonl(b"\n" + payload + self.payloads["loss"], CONFIG)
        self.assertEqual(2, len(rows))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "engineering.jsonl"; path.write_bytes(encode(first))
            dataset = PolicyDatasetV5.from_jsonl(path, CONFIG, split="train", purpose="engineering-fixture")
            self.assertEqual([first], dataset.records)
            path.write_bytes(path.read_bytes() + b"\n")
            with self.assertRaisesRegex(ValueError, "dataset_file_changed"): dataset.verify_integrity()

    def test_production_native_and_fit_admission_remain_closed(self):
        row = self.row(); coverage = supervision_coverage(row, CONFIG)
        for purpose in ("bounded-objective-pilot", "native-objective-candidate"):
            with self.subTest(purpose=purpose), self.assertRaisesRegex(ValueError, "mixed_record_purposes"):
                PolicyDatasetV5([row], CONFIG, split="train", purpose=purpose)
        with self.assertRaises(ValueError): validate_production_record(row, CONFIG)
        with self.assertRaises(ValueError): validate_candidate(row, CONFIG)
        gate = execution_gate({"purpose": row["audit_only"]["purpose"], "training_supervision": coverage})
        self.assertFalse(gate["accepted"])
        self.assertIn("unadmitted_or_engineering_records_cannot_fit", gate["reasons"])
        self.assertIn("enabled_objective_policy_supervision_required", gate["reasons"])
        self.assertFalse(gate["policy_promoted"])
        for name in ("finite_hunt_v5.py", "finite_hunt_policy_v5.py"):
            self.assertIn(name, TRAINING_SOURCES)
            self.assertNotIn(name, INFERENCE_SOURCES)

    def test_guarded_cli_calls_real_policy_batch_loss(self):
        command = [sys.executable, "-B", str(ROOT / "tools/check_finite_hunt_policy_v5_forward.py"),
                   str(FIXTURES / "fresh-finite-hunt-v5-success.raw.json")]
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual("passed", report["status"])
        self.assertTrue(report["engineering_dataset_accepted"]); self.assertTrue(report["parameters_unchanged"])
        self.assertFalse(report["fit_gate"]["accepted"]); self.assertFalse(report["natural_native_admission"])
        self.assertFalse(report["weights_saved"]); self.assertFalse(report["gradients_created"])
        self.assertEqual(0, report["optimizer_constructions"]); self.assertEqual(0, report["backward_calls"])

    def test_execute_cli_rejects_engineering_before_prepared_files_or_session(self):
        capture = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "must-not-exist"
            with patch("nosl.train_policy_v5.PolicyDatasetV5.from_jsonl", side_effect=AssertionError("read prepared data")), \
                 patch("nosl.train_policy_v5.PolicyTrainingSessionV5", side_effect=AssertionError("created session")), \
                 patch("nosl.train_policy_v5.StudentV5", side_effect=AssertionError("created model")), \
                 patch("nosl.train_policy_v5.train_bounded", side_effect=AssertionError("entered training")), \
                 redirect_stdout(capture):
                status = training_main(["--student-config", str(ROOT / "configs/student.v5.engineering.json"),
                    "--training-config", str(ROOT / "configs/student.v5.full-policy.json"),
                    "--engineering-fixture", "--execute-bounded-policy-pilot",
                    "--prepared", str(Path(directory) / "unopened"), "--output", str(output)])
            self.assertEqual(2, status)
            self.assertFalse(output.exists())
        result = json.loads(capture.getvalue())
        self.assertEqual("FIT_BLOCKED", result["status"])
        self.assertIn("engineering_fixtures_cannot_fit", result["reason"])


if __name__ == "__main__": unittest.main()
