"""Exact retained constructed native reports, quarantined and forward-only.

No producer run occurs here. A missing explicitly retained native fixture skips
integration checks; no synthetic source hashes or historical labels substitute.
Adversarial mutations test validation, never claim new native executions.
"""
from copy import deepcopy
import hashlib
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
from nosl.constructed_native_policy_v5 import (EVALUATOR_SOURCE_FILES, EXECUTION_FIELDS, OBJECTIVE_SPEC_FILES,
    RAW_SOURCE_KIND, REPORT_SCHEMA, SOURCE_KIND, _fragments, _source_aliases, adapt_report, all_attempt_metadata, validate_raw_report)
from nosl.data import REGISTRY_VERSION
from nosl.data_policy_v5 import PolicyDatasetV5, supervision_coverage, validate_isolation, validate_record
from nosl.data_v5 import validate_production_record
from nosl.model_v5 import StudentV5
from nosl.native_policy_v5 import evaluate_candidate_outcome
from nosl.native_v5 import validate_candidate
from nosl.policy_v5 import INFERENCE_SOURCES, digest, state_digest
from nosl.prepare_v5 import public_metadata
from nosl.protection_v5 import component_id
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME as LEGACY_IDENTITY
from nosl.public_identity_v5 import public_input_digest
from nosl.schema import HEADS
from nosl.schema_v5 import load_config, loads
from nosl.train_policy_v5 import TRAINING_SOURCES, batch_loss, execution_gate

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURES = Path(os.environ.get("NOSL_CONSTRUCTED_TAPE_FIXTURES", ROOT / "artifacts/reports/constructed-tape-v1/action-boundary/response.jsonl"))


def encode(value):
    return (json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")



def reencode_report(report):
    """Rebind only public wire hashes changed by JSON transport in test doubles.

    Public values and exact embedded contract/build/generation strings stay as
    supplied. A baseline roundtrip is validated before any adversarial edit.
    This is never written back to a retained native artifact.
    """
    value = deepcopy(report)
    for record in [*value["records"], *(a["provenance"] for a in value["attempts"] if "public_input" in a["provenance"])]:
        text = encode(record).decode("utf-8")
        public_hash = hashlib.sha256(_fragments(text)["public_input"].encode("utf-8")).hexdigest()
        record["audit_only"]["public_state_digest"] = public_hash
        record["audit_only"]["public_root_alias"] = "constructed-native-tape-public-root-v1:" + public_hash
    return encode(value)

def protected(token):
    return {"schema_version": REGISTRY_VERSION, "public_identity_scheme": LEGACY_IDENTITY,
        "source": {"manifest_sha256": digest("synthetic-isolation-test-manifest-only"),
            "split_state_sha256": digest("synthetic-isolation-test-state-only"),
            "versions": {"synthetic_guard_contract": "v1"}, "frozen_test_shards": []},
        "components": {component_id([token]): {"split": "test", "tokens": [token]}}}


def replace_record(payload, transform):
    """Change only a raw record lexeme, preserving original public JSON bytes."""
    text = payload.decode("utf-8")
    records_text = _fragments(text)["records"]
    fragments = _fragments(records_text, array=True)
    replacement = transform(fragments[0])
    return text.replace(records_text, records_text.replace(fragments[0], replacement, 1), 1).encode("utf-8")


class ConstructedTapeLexemeTests(unittest.TestCase):
    def test_nested_exact_fragments_do_not_confuse_strings_or_whitespace(self):
        value = ' { "records" : [ {"text":"records: [\\\"x\\\"]", "value":1.00},\n{"x":2} ], "rest":null } '
        fields = _fragments(value)
        self.assertEqual('[ {"text":"records: [\\\"x\\\"]", "value":1.00},\n{"x":2} ]', fields["records"])
        self.assertEqual(['{"text":"records: [\\\"x\\\"]", "value":1.00}', '{"x":2}'], _fragments(fields["records"], array=True))

    def test_malformed_or_legacy_reports_fail_closed(self):
        for payload in (b"", b"{}", b'{"x":0,"x":1}', b'{"x":NaN}', b"\xff"):
            with self.subTest(payload=payload), self.assertRaises(ValueError): adapt_report(payload, CONFIG)


class ConstructedNativePolicyV5Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = [FIXTURES] if FIXTURES.is_file() else sorted({*FIXTURES.glob("*.json"), *FIXTURES.glob("*.jsonl")})
        cls.payloads = {}
        for path in paths:
            try:
                payload = path.read_bytes(); report = loads(payload.decode("utf-8"))
            except ValueError: continue
            if isinstance(report, dict) and report.get("schema_version") == REPORT_SCHEMA:
                cls.payloads[path.name] = payload
        if not cls.payloads:
            raise unittest.SkipTest("Retained new constructed native-tape reports missing; set NOSL_CONSTRUCTED_TAPE_FIXTURES. No native run started.")
        cls.paths = {path.name: path for path in paths}
        torch.set_num_threads(1)

    def setUp(self):
        self.calls = []
        def forbidden(*unused, **also_unused):
            self.calls.append(True)
            raise AssertionError("Constructed tape integration attempted learning")
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            guard = patch(target, side_effect=forbidden); guard.start(); self.addCleanup(guard.stop)
        context = torch.no_grad(); context.__enter__(); self.addCleanup(lambda: context.__exit__(None, None, None))
        self.addCleanup(lambda: self.assertEqual([], self.calls))

    def first(self, *, valued=False):
        for payload in self.payloads.values():
            rows = adapt_report(payload, CONFIG)
            if rows and (not valued or any(row["masks"]["value"] for row in rows[0]["targets"]["actions"])):
                return payload, rows[0]
        self.skipTest("Retained fixture has no " + ("scored" if valued else "recorded") + " action row")

    def test_exact_raw_records_values_masks_outcomes_and_declarations_are_bound(self):
        for name, payload in self.payloads.items():
            with self.subTest(name=name):
                report = validate_raw_report(payload, CONFIG); rows = adapt_report(payload, CONFIG)
                self.assertEqual(len(report["records"]), len(rows))
                fragments = _fragments(_fragments(payload.decode())["records"], array=True)
                for index, (raw, row) in enumerate(zip(report["records"], rows)):
                    projected = deepcopy(raw["targets"])
                    for action in projected["actions"]:
                        for field in EXECUTION_FIELDS: action.pop(field)
                    self.assertEqual(projected, row["targets"])
                    self.assertEqual(raw["public_input"], row["public_input"])
                    audit = row["audit_only"]
                    self.assertEqual(SOURCE_KIND, audit["source_kind"])
                    self.assertEqual("engineering-fixture", audit["purpose"])
                    self.assertIs(audit["native_run"], False); self.assertIs(audit["trainable"], False)
                    self.assertIsNone(audit["producer_receipt_sha256"])
                    for field in ("actual_seed", "source_draw_seed", "native_source_run_identity"):
                        self.assertNotIn(field, audit); self.assertNotIn(field, row["public_input"])
                    evidence = audit["constructed_tape_evidence"]
                    self.assertEqual(payload, evidence["report_utf8"].encode("utf-8"))
                    self.assertEqual(index, evidence["raw_record_index"])
                    self.assertEqual(hashlib.sha256(payload).hexdigest(), audit["source_artifact_sha256"])
                    self.assertEqual(hashlib.sha256(fragments[index].encode()).hexdigest(), audit["source_record_sha256"])
                    self.assertEqual(public_input_digest(row["public_input"]), audit["conditioned_public_input_digest"])
                    self.assertEqual(digest(row["targets"]), audit["targets_sha256"])
                    self.assertNotIn("plan", row["targets"])
                    self.assertEqual([], row["targets"]["pairwise"]); self.assertEqual([], row["targets"]["equivalent_action_set"])
                    self.assertIs(row, validate_record(row, CONFIG))

    def test_candidate_recomputation_and_incomplete_mass_stay_distinct(self):
        seen = set()
        for payload in self.payloads.values():
            report = loads(payload.decode()); rows = adapt_report(payload, CONFIG)
            for raw, row in zip(report["records"], rows):
                for sample, target, computed in zip(raw["audit_only"]["outcome_samples"], row["targets"]["actions"], row["audit_only"]["objective_evaluations"]):
                    values = [evaluate_candidate_outcome(outcome) for outcome in sample["outcomes"]]
                    self.assertEqual(values, computed["evaluations"])
                    for outcome in sample["outcomes"]: seen.add(outcome["terminalKind"])
                    self.assertEqual(len(sample["world_seeds"]), target["allocated_worlds"])
                    self.assertEqual(target["allocated_worlds"], sum(target[key] for key in ("completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")))
                    if target["completed_worlds"] < target["allocated_worlds"]:
                        self.assertTrue(all(value is None for key, value in target.items() if key in HEADS))
                        self.assertFalse(any(target["masks"].values()))
                    if target["masks"]["value"]:
                        self.assertAlmostEqual(-sum(v["cost"] for v in values) / len(values), target["value"])
        self.assertTrue(seen, "Fixture must contain actual raw outcome slots")

    def test_false_source_value_mask_is_preserved_even_when_value_is_known(self):
        payload, row = self.first(valued=True)
        def transform(text):
            target_text = _fragments(text)["targets"]
            action_text = _fragments(target_text)["actions"]
            actions = _fragments(action_text, array=True)
            for original in actions:
                value = loads(original)
                if value["masks"]["value"]:
                    value["masks"]["value"] = False; value["value"] = None
                    replacement = encode(value).decode().strip()
                    return text.replace(target_text, target_text.replace(action_text, action_text.replace(original, replacement, 1), 1), 1)
            raise AssertionError("No valued action")
        changed = adapt_report(replace_record(payload, transform), CONFIG)[0]
        index = next(i for i, action in enumerate(row["targets"]["actions"]) if action["masks"]["value"])
        self.assertFalse(changed["targets"]["actions"][index]["masks"]["value"])
        self.assertIsNone(changed["targets"]["actions"][index]["value"])
        self.assertIsNotNone(changed["audit_only"]["objective_evaluations"][index]["empirical_value"])

    def test_primitive_family_cannot_change_with_root_rule_or_json_formatting(self):
        payload, _ = self.first()
        report = loads(payload.decode()); recipe = report["attempts"][0]["recipe"]
        original = _source_aliases(report, recipe)[0]
        changed = deepcopy(report)
        changed["options"]["prior"].update(rootSelection="first_player_turn_3", sourceDecisionHorizon=999)
        self.assertEqual(original, _source_aliases(changed, recipe)[0])
        recipe = deepcopy(recipe); recipe["proposalSeed"] ^= 1; recipe["decisionIndex"] += 1
        self.assertEqual(original, _source_aliases(changed, recipe)[0])
        changed["source_generation_json"] = json.dumps(loads(changed["source_generation_json"]), sort_keys=True, indent=2)
        changed["source_generation_identity"] = hashlib.sha256(changed["source_generation_json"].encode()).hexdigest()
        self.assertEqual(original, _source_aliases(changed, recipe)[0])

    def test_objective_identity_hashes_actual_checked_out_sources_and_spec(self):
        _, row = self.first()
        for field, files in (("objective_spec_sha256", OBJECTIVE_SPEC_FILES), ("evaluator_source_sha256", EVALUATOR_SOURCE_FILES)):
            self.assertEqual(digest({path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in files}), row["objective"][field])
        self.assertEqual("candidate", row["objective"]["objective_profile_status"])
        self.assertIsNone(row["objective"]["calibration_evidence_sha256"])
        self.assertIs(row["objective"]["objective_calibrated"], False)
        self.assertIn("constructed_native_policy_v5.py", TRAINING_SOURCES)
        self.assertNotIn("constructed_native_policy_v5.py", INFERENCE_SOURCES)

    def test_all_attempt_metadata_has_no_receipt_authority_and_reaches_isolation(self):
        payload, row = self.first()
        report = loads(payload.decode()); expected = []
        for attempt in report["attempts"]:
            meta = public_metadata(attempt["provenance"])
            meta["audit_only"]["source_draw_seed"] = attempt["source_draw_seed"]
            expected.append(meta)
        self.assertEqual(expected, all_attempt_metadata(row, CONFIG))
        dataset = PolicyDatasetV5([row], CONFIG, split="train", purpose="engineering-fixture")
        protection = protected("source_run_group:" + expected[-1]["audit_only"]["source_run_group"])
        self.assertEqual({}, dataset.producer_metadata(protection))
        self.assertEqual(expected, dataset.engineering_source_metadata())
        other = PolicyDatasetV5([row], CONFIG, split="validation", purpose="engineering-fixture")
        with self.assertRaisesRegex(ValueError, "previously_observed_source_or_public_alias"):
            validate_isolation({"train": dataset, "validation": other}, protection)
        with self.assertRaisesRegex(ValueError, "train_validation_source_or_public_alias_overlap"):
            validate_isolation({"train": dataset, "validation": other}, protected("source_run_group:unrelated-contract-only"))

    def test_actual_batch_loss_uses_only_public_input_and_changes_no_parameters(self):
        _, row = self.first(valued=True)
        dataset = PolicyDatasetV5([row], CONFIG, split="train", purpose="engineering-fixture")
        with torch.random.fork_rng():
            torch.manual_seed(1729); model = StudentV5(CONFIG)
        model.eval(); before = state_digest(model.state_dict()); seen = []
        hook = model.register_forward_pre_hook(lambda unused, args: seen.append(deepcopy(args[0])))
        try: loss, terms = batch_loss(model, dataset.records, CONFIG)
        finally: hook.remove()
        self.assertEqual([row["public_input"]], seen)
        self.assertTrue(torch.isfinite(loss)); self.assertFalse(loss.requires_grad); self.assertIsNone(loss.grad_fn)
        self.assertEqual(before, state_digest(model.state_dict()))
        self.assertTrue(all(parameter.grad is None for parameter in model.parameters()))
        self.assertGreater(terms[0]["value"], 0)
        self.assertEqual(0, terms[0]["pairwise"]); self.assertEqual(0, terms[0]["equivalent"])
        self.assertTrue(all(not key.startswith("plan.") or value == 0 for key, value in terms[0].items()))

    def test_outer_resealing_cannot_admit_changed_source_targets_or_authority(self):
        _, original = self.first()
        mutations = [
            lambda r: r["audit_only"].update(purpose="bounded-objective-pilot", trainable=True),
            lambda r: r["audit_only"].update(source_kind="native_empirical_objective_candidate"),
            lambda r: r["audit_only"].update(native_run=True),
            lambda r: r["audit_only"].update(trainable=0),
            lambda r: r["audit_only"].update(actual_seed="invented"),
            lambda r: r["audit_only"].update(producer_receipt_sha256="a" * 64),
            lambda r: r["audit_only"]["collection_summary"].update(protection_status="COMPLETE", admitted=True),
            lambda r: r["audit_only"].update(all_attempts_metadata_sha256="b" * 64),
            lambda r: r["objective"].update(objective_profile_status="calibrated", objective_calibrated=True, calibration_evidence_sha256="c" * 64),
            lambda r: r["objective"].update(evaluation_design_sha256="d" * 64),
            lambda r: r["audit_only"]["constructed_tape_evidence"].update(raw_record_index=True),
            lambda r: r["audit_only"]["constructed_tape_evidence"].update(report_utf8=r["audit_only"]["constructed_tape_evidence"]["report_utf8"] + " "),
        ]
        for index, mutate in enumerate(mutations):
            row = deepcopy(original); mutate(row)
            with self.subTest(index=index), self.assertRaises(ValueError): validate_record(row, CONFIG)
        row = deepcopy(original)
        row["targets"]["actions"][0]["allocated_worlds"] += 1
        row["audit_only"]["targets_sha256"] = digest(row["targets"])
        with self.assertRaises(ValueError): validate_record(row, CONFIG)

    def test_raw_frozen_declaration_denominator_runtime_and_outcome_tampering_rejected(self):
        payload, _ = self.first()
        original = loads(payload.decode())
        roundtrip = adapt_report(reencode_report(original), CONFIG)
        self.assertEqual(adapt_report(payload, CONFIG)[0]["targets"], roundtrip[0]["targets"])
        mutations = [
            (lambda r: r["attempts"].pop(), "all_attempts_required"),
            (lambda r: r["options"]["sourceDrawSeeds"].append(999999), "frozen_collection_contract"),
            (lambda r: r["options"]["prior"]["setup"].update(actual_seed="private"), "constructed setup"),
            (lambda r: r.update(source_kind="natural_under_explicit_label_tape_prior"), "report_identity"),
            (lambda r: r.update(build_receipt_json=r["build_receipt_json"] + " "), "exact_build_receipt_hash"),
            (lambda r: r.update(source_generation_json=r["source_generation_json"] + " "), "frozen_collection_contract"),
            (lambda r: r["runtime_dependencies"].update(runtime_version="changed"), "runtime_dependencies"),
            (lambda r: r["records"][0]["targets"]["actions"][0].update(executed_worlds=99999), "action_execution_count:executed_worlds"),
            (lambda r: r["records"][0]["audit_only"]["outcome_samples"][0]["outcomes"].pop(), "all_outcome_mass_required"),
            (lambda r: r["records"][0]["audit_only"]["outcome_samples"][0]["outcomes"][0].update(hpAtCombatStart=1), "outcome_fixed_hp"),
            (lambda r: r["attempts"][0]["provenance"]["audit_only"].update(source_run_group="root-selected-family"), "source_run_group"),
            (lambda r: r["records"][0]["audit_only"].update(native_default_start=True), "raw_flag:native_default_start"),
            (lambda r: r["records"][0]["audit_only"]["costs"].update(rollout_decisions=999), "successful_rollout_decisions"),
            (lambda r: r["records"][0]["audit_only"]["execution_accounting"]["samples"][0]["branches"][0].update(stepCalls=999), "step_calls"),
            (lambda r: next(a for a in r["records"][0]["targets"]["actions"] if a["masks"]["value"]).update(value=-1), "empirical_target:value"),
            (lambda r: r["attempts"][0]["posterior_proposals"].clear(), "missing_entered_sampler_proposals"),
        ]
        for index, (mutate, reason) in enumerate(mutations):
            report = deepcopy(original); mutate(report)
            with self.subTest(index=index), self.assertRaisesRegex(ValueError, reason):
                adapt_report(reencode_report(report), CONFIG)

    def test_exact_transport_dataset_integrity_and_closed_fit_native_production_gates(self):
        payload, row = self.first()
        padded = adapt_report(payload + b" \n", CONFIG)[0]
        self.assertEqual(row["targets"], padded["targets"])
        self.assertNotEqual(row["audit_only"]["source_artifact_sha256"], padded["audit_only"]["source_artifact_sha256"])
        self.assertEqual(row["audit_only"]["source_record_sha256"], padded["audit_only"]["source_record_sha256"])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "engineering.jsonl"; path.write_bytes(encode(row))
            dataset = PolicyDatasetV5.from_jsonl(path, CONFIG, split="train", purpose="engineering-fixture")
            self.assertEqual([row], dataset.records)
            path.write_bytes(path.read_bytes() + b"\n")
            with self.assertRaisesRegex(ValueError, "dataset_file_changed"): dataset.verify_integrity()
        for purpose in ("bounded-objective-pilot", "native-objective-candidate"):
            with self.subTest(purpose=purpose), self.assertRaisesRegex(ValueError, "mixed_record_purposes"):
                PolicyDatasetV5([row], CONFIG, split="train", purpose=purpose)
        with self.assertRaises(ValueError): validate_candidate(row, CONFIG)
        with self.assertRaises(ValueError): validate_production_record(row, CONFIG)
        gate = execution_gate({"purpose": "engineering-fixture", "training_supervision": supervision_coverage(row, CONFIG)})
        self.assertFalse(gate["accepted"]); self.assertFalse(gate["policy_promoted"])
        self.assertIn("unadmitted_or_engineering_records_cannot_fit", gate["reasons"])

    def test_guarded_cli_loads_normalized_output_and_calls_real_batch_loss(self):
        payload, _ = self.first(valued=True)
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "report.raw.json"; source.write_bytes(payload)
            output = Path(directory) / "engineering.jsonl"
            result = subprocess.run([sys.executable, "-B", str(ROOT / "tools/check_constructed_native_policy_v5_forward.py"),
                str(source), "--normalized-output", str(output)], cwd=ROOT, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads(result.stdout)
            self.assertEqual("passed", report["status"])
            self.assertTrue(report["engineering_dataset_accepted"]); self.assertTrue(report["parameters_unchanged"])
            self.assertFalse(report["fit_gate"]["accepted"]); self.assertFalse(report["natural_native_admission"])
            self.assertFalse(report["weights_saved"]); self.assertFalse(report["gradients_created"])
            self.assertEqual(0, report["optimizer_constructions"]); self.assertEqual(0, report["backward_calls"])
            self.assertTrue(output.is_file())


if __name__ == "__main__": unittest.main()
