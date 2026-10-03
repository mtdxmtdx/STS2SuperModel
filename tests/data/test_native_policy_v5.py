"""Synthetic bridge contracts only: no native generation, fitting or backward."""
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/data"), str(ROOT / "tests/python")]
from test_prepare_v5 import raw_fixture, later_decision_engineering_fixture, legacy_protection
from nosl.data_policy_v5 import PolicyDatasetV5, validate_isolation, validate_record
from nosl.native_policy_v5 import (NO_RANKING, RAW_CANDIDATE_SCHEMA, REVIEW_FORMAT, SOURCE_HASHES,
    adapt_native_policy_candidates, admit_native_policy_cohort, evaluate_candidate_outcome,
    policy_producer_metadata, validate_policy_producer_receipt, validate_policy_record_source, _digest)
from nosl.prepare_v5 import public_metadata
from nosl.schema import HEADS
from nosl.schema_v5 import load_config

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")


def encode(value):
    return json.dumps(value, separators=(",", ":"), allow_nan=False).encode()


def source_fixture(number=0, *, later=False):
    """Declare a synthetic wire-shaped source, never claim native authenticity."""
    raw = later_decision_engineering_fixture(number, return_raw=True) if later else raw_fixture(number)
    audit = raw["audit_only"]
    audit.update(purpose="bounded-pilot", source_kind="natural_under_explicit_label_tape_prior",
                 collection_id="synthetic-native-policy-contract", draw_id="synthetic-native-policy-contract/draw:0",
                 ranking_evidence=NO_RANKING, ranking_intervals=[])
    audit["versions"]["dataset"] = RAW_CANDIDATE_SCHEMA
    for target, sample in zip(raw["targets"]["actions"], audit["outcome_samples"]):
        for outcome in sample["outcomes"]:
            outcome.update(playerTurnsElapsed=1, atomicActionsExecuted=2)
        target["value"] = -sum(evaluate_candidate_outcome(outcome)["cost"] for outcome in sample["outcomes"]) / len(sample["outcomes"])
    return raw


def inputs(raw=None, *, extras=None):
    raw = source_fixture() if raw is None else raw
    sources = [raw] if raw is not False else []
    audit = sources[0]["audit_only"] if sources else source_fixture()["audit_only"]
    attempts = [{"draw_id": audit["draw_id"], "source_draw_seed": audit["source_draw_seed"], "status": "recorded",
                 "raw_record_index": 0, "detail": "synthetic wire contract", "provenance": public_metadata(raw)}] if sources else []
    attempts.extend(deepcopy(extras or []))
    for i, attempt in enumerate(attempts): attempt["draw_id"] = audit["collection_id"] + "/draw:" + str(i)
    build_json = json.dumps(audit["runtime_dependencies"], sort_keys=True, separators=(",", ":"))
    audit["build_receipt_sha256"] = hashlib.sha256(build_json.encode()).hexdigest()
    report = {"schema_version": "nosl.native-complete-map.raw-report.v1",
        "status": "bounded_fresh_candidates_require_separate_quality_admission", "purpose": "bounded-pilot",
        "selection": "predeclared_all_attempts_no_replacement", "trainable": False, "formalTraining": False,
        "formal_labels": False, "budgetExpired": False, "sourceDrawsRequested": len(attempts), "recordedRoots": len(sources),
        "options": {"sourceDrawSeeds": [a["source_draw_seed"] for a in attempts], "collectionId": audit["collection_id"], "prior": audit["declared_prior"]},
        "teacherOptions": {"mode": "T0", "explorationSeeds": [], "formalLabels": False, "evaluationSeeds": audit["sampler_seeds"],
                           "continuationPolicyId": audit["versions"]["continuation"]},
        "prior": audit["declared_prior"], "priorIdentity": audit["source_prior_identity"],
        "build_receipt_json": build_json, "build_receipt_sha256": audit["build_receipt_sha256"], "runtime_dependencies": audit["runtime_dependencies"],
        "records": sources, "attempts": attempts}
    return b"\n" + b"\n".join(encode(row) for row in sources) + b"\n", encode(attempts), encode(report)


def failed_attempt(status="failed", *, provenance=None):
    return {"draw_id": "replaced-by-fixture", "source_draw_seed": 99001, "status": status, "raw_record_index": None,
            "detail": "synthetic failed or unexecuted draw", "provenance": provenance or {"audit_only": {
                "actual_seed": "synthetic-failed-native-source", "native_run": True}}}


def review_fixture(receipt):
    return {"format": REVIEW_FORMAT, "decision": "accepted", "producer_receipt_sha256": _digest(receipt),
        "reviewed_by": "synthetic-review-contract-only", "evidence_sha256": "b" * 64,
        "objective_supervision_reviewed": True, "full_public_conditioning_reviewed": True, "source_isolation_reviewed": True,
        "selection": "predeclared_all_attempts_no_replacement", "minimum_complete_roots": 1,
        "minimum_positive_decision_roots": 1, "minimum_later_combat_roots": 1}


class NativePolicyBridgeTests(unittest.TestCase):
    def setUp(self):
        # Test every adapter/admission/loader route without permitting learning.
        # The CLI subprocess has no training command or execution option.
        import torch
        from nosl.train_policy_v5 import PolicyTrainingSessionV5
        forbidden = AssertionError("native bridge tests must not execute learning")
        guards = [patch.object(torch.Tensor, "backward", side_effect=forbidden),
                  patch.object(torch.autograd, "backward", side_effect=forbidden),
                  patch.object(torch.optim.AdamW, "__init__", side_effect=forbidden),
                  patch.object(torch.optim.AdamW, "step", side_effect=forbidden),
                  patch.object(PolicyTrainingSessionV5, "train_next", side_effect=forbidden),
                  patch("nosl.train_policy_v5.train_bounded", side_effect=forbidden)]
        for guard in guards:
            guard.start(); self.addCleanup(guard.stop)

    def adapt(self, raw=None, **kwargs):
        return adapt_native_policy_candidates(*inputs(raw, **kwargs), CONFIG, legacy_protection())

    def test_source_pins_match_production_native_evaluator_profile_and_teacher(self):
        for path, expected in SOURCE_HASHES.items():
            with self.subTest(path=path): self.assertEqual(expected, hashlib.sha256((ROOT / path).read_bytes()).hexdigest())

    def test_lossless_full_input_native_value_masks_outcomes_and_exact_bytes(self):
        raw = source_fixture(); raw_before = deepcopy(raw); payloads = inputs(raw)
        records, receipt = adapt_native_policy_candidates(*payloads, CONFIG, legacy_protection())
        self.assertEqual(raw_before, raw)
        row = records[0]
        self.assertEqual(raw["public_input"], row["public_input"])
        self.assertEqual(raw["targets"], row["targets"])
        self.assertTrue(any(action["value"] < 0 for action in row["targets"]["actions"]))
        self.assertFalse(row["audit_only"]["trainable"])
        self.assertEqual("native-objective-candidate", row["audit_only"]["purpose"])
        self.assertFalse(row["objective"]["objective_calibrated"])
        self.assertIsNone(row["objective"]["calibration_evidence_sha256"])
        self.assertNotIn("plan", row["targets"])
        for key, payload in zip(("candidates", "attempts", "report"), payloads):
            self.assertEqual(payload, receipt["source_artifacts"][key]["utf8"].encode())
        self.assertEqual(hashlib.sha256(payloads[0].strip(b"\n")).hexdigest(), row["audit_only"]["source_record_sha256"])
        original = json.loads(receipt["source_artifacts"]["candidates"]["utf8"])
        self.assertEqual(raw["audit_only"], original["audit_only"])
        validate_record(row, CONFIG)
        metadata = validate_policy_record_source(row, receipt, CONFIG, legacy_protection())
        self.assertEqual(2, len(metadata))

    def test_false_masks_are_not_filled_even_when_value_is_computable(self):
        raw = source_fixture()
        for row in raw["targets"]["actions"]:
            row.update(value=None, quality="objective_value_unresolved"); row["masks"]["value"] = False
        rows, receipt = self.adapt(raw)
        self.assertEqual(raw["targets"], rows[0]["targets"])
        self.assertTrue(all(action["empirical_value"] is not None for action in receipt["value_evaluations"][0]))
        self.assertTrue(all(action["value"] is None for action in rows[0]["targets"]["actions"]))

    def test_arbitrary_scalar_and_synthetic_ranking_cannot_survive_auxiliary_masking(self):
        for mutate in (
            lambda r: r["targets"]["actions"][0].update(value=123),
            lambda r: r["targets"].update(pairwise=[{"preferred": 0, "other": 1, "weight": 1}]),
            lambda r: r["targets"].update(equivalent_action_set=[0]),
            lambda r: r["audit_only"].update(ranking_intervals=[{"first": 0, "second": 1}]),
            lambda r: r["audit_only"]["versions"].update(objective="arbitrary-objective"),
            lambda r: r.update(schema_version="nosl.dataset.public-run-context.v1"),
        ):
            raw = source_fixture(); mutate(raw)
            with self.subTest(mutate=mutate), self.assertRaises(ValueError): self.adapt(raw)

    def test_resource_unknowns_keep_native_masks_null(self):
        raw = source_fixture()
        target, sample = raw["targets"]["actions"][0], raw["audit_only"]["outcome_samples"][0]
        for outcome in sample["outcomes"]:
            outcome["inventoryEnd"] = [{"resourceId": "UnknownPotion", "count": 1}]
            outcome["resourceEvents"] = [{"kind": "generated", "resourceId": "UnknownPotion", "quantity": 1, "publicSource": "synthetic-contract"}]
        target.update(value=None, quality="objective_value_unresolved", potion_net_change=1); target["masks"]["value"] = False
        rows, receipt = self.adapt(raw)
        self.assertIsNone(rows[0]["targets"]["actions"][0]["value"])
        self.assertIsNone(receipt["value_evaluations"][0][0]["empirical_value"])
        target.update(value=0, quality="complete"); target["masks"]["value"] = True
        with self.assertRaisesRegex(ValueError, "empirical_native_value_mismatch"): self.adapt(raw)

    def test_incomplete_mass_retains_every_world_without_renormalization(self):
        for terminal, count in (("ComputeTruncated", "truncated_worlds"), ("EngineError", "error_worlds"), ("PolicyNonterminating", "other_worlds")):
            raw = source_fixture(); target = raw["targets"]["actions"][0]
            raw["audit_only"]["outcome_samples"][0]["outcomes"][1].update(terminalKind=terminal, settlementComplete=False)
            target.update({head: None for head in HEADS}); target.update(quality="unresolved", masks={head: False for head in HEADS}, completed_worlds=1)
            target[count] = 1
            raw["audit_only"]["n_error" if terminal == "EngineError" else "n_unresolved"] = 1
            raw["audit_only"]["costs"]["worlds_completed"] -= 1
            with self.subTest(kind=terminal):
                rows, receipt = self.adapt(raw)
                self.assertEqual(target, rows[0]["targets"]["actions"][0])
                self.assertIsNone(receipt["value_evaluations"][0][0]["empirical_value"])
                self.assertEqual(2, len(receipt["value_evaluations"][0][0]["evaluations"]))

    def test_complete_attempt_journal_binds_report_draws_order_and_absence(self):
        payloads = inputs(extras=[failed_attempt()])
        for payload_index, change in (
            (1, lambda value: value.pop()),
            (1, lambda value: value.reverse()),
            (2, lambda value: value["options"]["sourceDrawSeeds"].append(100001)),
            (2, lambda value: value.update(recordedRoots=2)),
            (2, lambda value: value.update(recordedRoots=True)),
        ):
            altered = list(payloads); value = json.loads(altered[payload_index]); change(value); altered[payload_index] = encode(value)
            with self.subTest(index=payload_index), self.assertRaises(ValueError):
                adapt_native_policy_candidates(*altered, CONFIG, legacy_protection())
        rows, receipt = adapt_native_policy_candidates(*payloads, CONFIG, legacy_protection())
        self.assertEqual(2, len(receipt["attempts"]))
        self.assertEqual(3, len(policy_producer_metadata(receipt, CONFIG)))

    def test_failed_unexecuted_aliases_join_historical_protection_before_admission(self):
        for status in ("failed", "not_executed", "absent"):
            raw = source_fixture(later=True)
            bridge = {"audit_only": {"actual_seed": raw["audit_only"]["actual_seed"], "native_run": True,
                                     "source_run_group": "old-engineering-fixture"}}
            rows, receipt = self.adapt(raw, extras=[failed_attempt(status, provenance=bridge)])
            self.assertEqual([0], receipt["closure"]["protected_candidate_indices"])
            self.assertTrue(any("source_run_group:native-tape-source-draw-v1:99001" in tokens for tokens in receipt["closure"]["components"].values()))
            with self.assertRaises(ValueError): admit_native_policy_cohort(receipt, review_fixture(receipt), CONFIG)

    def test_no_records_still_preserves_attempts_and_protection_metadata(self):
        rows, receipt = self.adapt(False, extras=[failed_attempt()])
        self.assertEqual([], rows)
        self.assertEqual(1, len(receipt["attempts"]))
        self.assertEqual(1, len(receipt["metadata"]))
        validate_policy_producer_receipt(receipt, CONFIG)

    def test_unrelated_protected_absent_attempt_blocks_admission_and_loader(self):
        raw = source_fixture(later=True)
        protected = {"audit_only": {"actual_seed": "unrelated-synthetic-native-source", "native_run": True,
                                    "source_run_group": "old-engineering-fixture"}}
        rows, receipt = self.adapt(raw, extras=[failed_attempt("absent", provenance=protected)])
        self.assertEqual([], receipt["closure"]["protected_candidate_indices"])
        self.assertEqual([2], receipt["closure"]["protected_metadata_indices"])
        with self.assertRaisesRegex(ValueError, "protected_source_or_historical_bridge"):
            admit_native_policy_cohort(receipt, review_fixture(receipt), CONFIG)

    def test_resealing_changed_derived_metadata_value_or_source_binding_fails(self):
        rows, receipt = self.adapt()
        for mutate in (
            lambda r: r.update(fit_authorized=True),
            lambda r: r["value_evaluations"][0][0].update(empirical_value=50),
            lambda r: r["metadata"].pop(),
            lambda r: r["attempts"][0].update(status="absent"),
            lambda r: r["closure"].update(groups=[]),
            lambda r: r["source_artifacts"]["report"].update(sha256="f" * 64),
            lambda r: r.update(protection_sha256="f" * 64),
        ):
            changed = deepcopy(receipt); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(ValueError): validate_policy_producer_receipt(changed, CONFIG)
        changed = deepcopy(rows[0]); changed["targets"]["actions"][0]["value"] += 1
        changed["audit_only"]["targets_sha256"] = _digest(changed["targets"])
        with self.assertRaises(ValueError): validate_policy_record_source(changed, receipt, CONFIG, legacy_protection())
        changed = deepcopy(rows[0]); changed["targets"]["actions"][0]["win_probability"] = True
        with self.assertRaises(ValueError): validate_policy_record_source(changed, receipt, CONFIG, legacy_protection())
        changed_protection = legacy_protection(); changed_protection["source"]["manifest_sha256"] = "f" * 64
        with self.assertRaisesRegex(ValueError, "imported_protection_changed"):
            validate_policy_record_source(rows[0], receipt, CONFIG, changed_protection)

    def test_separate_review_emits_new_records_and_receipt_without_learning(self):
        import torch
        with patch.object(torch.Tensor, "backward", side_effect=AssertionError("backward forbidden")), \
             patch.object(torch.autograd, "backward", side_effect=AssertionError("autograd forbidden")), \
             patch.object(torch.optim.AdamW, "__init__", side_effect=AssertionError("optimizer forbidden")):
            rows, receipt = self.adapt(source_fixture(later=True)); original = deepcopy(receipt)
            admitted, admitted_receipt = admit_native_policy_cohort(receipt, review_fixture(receipt), CONFIG)
            self.assertEqual(original, receipt)
            self.assertEqual(rows[0]["public_input"], admitted[0]["public_input"])
            self.assertEqual(rows[0]["targets"], admitted[0]["targets"])
            self.assertEqual("bounded-objective-pilot", admitted[0]["audit_only"]["purpose"])
            self.assertFalse(admitted_receipt["fit_authorized"])
            self.assertFalse(admitted[0]["objective"]["objective_calibrated"])
            validate_policy_record_source(admitted[0], admitted_receipt, CONFIG, legacy_protection())
            with self.assertRaises(ValueError): validate_policy_record_source(admitted[0], receipt, CONFIG, legacy_protection())

    def test_external_review_cannot_skip_breadth_or_change_exact_cohort(self):
        _, receipt = self.adapt()
        with self.assertRaisesRegex(ValueError, "cohort_gate_failed"): admit_native_policy_cohort(receipt, review_fixture(receipt), CONFIG)
        _, receipt = self.adapt(source_fixture(later=True))
        for key, value in (("producer_receipt_sha256", "f" * 64), ("minimum_later_combat_roots", 0),
                           ("source_isolation_reviewed", False), ("decision", "pending")):
            review = review_fixture(receipt); review[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): admit_native_policy_cohort(receipt, review, CONFIG)

    def test_real_policy_loader_requires_complete_receipts_and_blocks_unreviewed_execution(self):
        import torch
        from nosl.train_policy_v5 import execution_gate, freeze_inputs
        training = json.loads((ROOT / "configs/student.v5.full-policy.json").read_text())
        with patch.object(torch.Tensor, "backward", side_effect=AssertionError("backward forbidden")), \
             patch.object(torch.autograd, "backward", side_effect=AssertionError("autograd forbidden")), \
             patch.object(torch.optim.AdamW, "__init__", side_effect=AssertionError("optimizer forbidden")), \
             patch.object(torch, "get_num_threads", return_value=training["torch_threads"]):
            for admitted in (False, True):
                datasets, receipts = {}, {}
                for split, number in (("train", 10), ("validation", 20)):
                    rows, receipt = self.adapt(source_fixture(number, later=True))
                    if admitted: rows, receipt = admit_native_policy_cohort(receipt, review_fixture(receipt), CONFIG)
                    purpose = rows[0]["audit_only"]["purpose"]
                    with self.assertRaisesRegex(ValueError, "exact_native_producer_receipts_required"):
                        PolicyDatasetV5(rows, CONFIG, split=split, purpose=purpose)
                    receipts[_digest(receipt)] = receipt
                    datasets[split] = PolicyDatasetV5(rows, CONFIG, split=split, purpose=purpose,
                                                     producer_receipts={_digest(receipt): receipt})
                metadata_bindings = validate_isolation(datasets, legacy_protection())
                self.assertEqual(set(receipts), set(metadata_bindings))
                frozen = freeze_inputs(datasets, CONFIG, training, legacy_protection())
                self.assertGreater(frozen["training_supervision"]["action_policy"]["value_roots"], 0)
                self.assertEqual(metadata_bindings, frozen["producer_receipts"])
                self.assertFalse(execution_gate(frozen)["accepted"])

    def test_loader_rejects_source_attempt_bridge_even_if_rows_themselves_are_disjoint(self):
        datasets = {}
        for split, number in (("train", 10), ("validation", 20)):
            raw = source_fixture(number, later=True)
            extras = None
            if split == "validation":
                extras = [failed_attempt("absent", provenance={"audit_only": {
                    "actual_seed": "synthetic-v5-engineering:10", "native_run": True,
                    "source_run_group": raw["audit_only"]["source_run_group"]}})]
            rows, receipt = self.adapt(raw, extras=extras)
            datasets[split] = PolicyDatasetV5(rows, CONFIG, split=split, purpose="native-objective-candidate",
                                             producer_receipts={_digest(receipt): receipt})
        with self.assertRaisesRegex(ValueError, "train_validation_source_or_public_alias_overlap"):
            validate_isolation(datasets, legacy_protection())

    def test_cli_preserves_source_artifacts_and_exclusively_creates_a_sealed_export(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); payloads = inputs()
            for name, payload in zip(("raw.jsonl", "attempts.json", "report.json"), payloads): (root / name).write_bytes(payload)
            (root / "protection.json").write_bytes(encode(legacy_protection()))
            command = [sys.executable, "-B", str(ROOT / "tools/adapt_native_policy_v5.py"), str(root / "raw.jsonl"),
                       "--attempts", str(root / "attempts.json"), "--report", str(root / "report.json"),
                       "--protection", str(root / "protection.json"), "--output", str(root / "output")]
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertEqual(0, result.returncode, result.stderr)
            summary = json.loads((root / "output/summary.json").read_text())
            self.assertFalse(summary["fit_authorized"]); self.assertFalse(summary["cohort_admitted"])
            self.assertGreater(summary["source_value_rows"], 0)
            receipts = json.loads((root / "output/producer-receipts.json").read_text())
            dataset = PolicyDatasetV5.from_jsonl(root / "output/records.jsonl", CONFIG, split="train",
                purpose="native-objective-candidate", producer_receipts_path=root / "output/producer-receipts.json")
            self.assertEqual(1, len(dataset))
            self.assertEqual(set(receipts), set(dataset.producer_metadata(legacy_protection())))
            for name, payload in zip(("raw.jsonl", "attempts.json", "report.json"), payloads): self.assertEqual(payload, (root / name).read_bytes())
            before = (root / "output/summary.json").read_bytes()
            again = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertNotEqual(0, again.returncode)
            self.assertEqual(before, (root / "output/summary.json").read_bytes())
            path = root / "output/producer-receipts.json"; path.write_bytes(path.read_bytes() + b"\n")
            with self.assertRaisesRegex(ValueError, "producer_receipt_file_changed"): dataset.verify_integrity()

    def test_candidate_evaluator_matches_native_fixed_anchor_semantics(self):
        base = source_fixture()["audit_only"]["outcome_samples"][0]["outcomes"][0]
        base.update(hpAtCombatStart=60, maxHpStart=100, maxHpAfterSettlement=100, hpAfterSettlement=57,
                    cumulativeHpDamage=3)
        self.assertAlmostEqual(3.03, evaluate_candidate_outcome(base)["cost"])
        healed = {**base, "hpAfterSettlement": 65, "cumulativeHpDamage": 25, "healingReceived": 30}
        self.assertEqual(-5, evaluate_candidate_outcome(healed)["cost"])
        retained = {**base, "inventoryStart": [{"resourceId": "Unknown", "count": 1}], "inventoryEnd": [{"resourceId": "Unknown", "count": 1}]}
        self.assertAlmostEqual(3.03, evaluate_candidate_outcome(retained)["cost"])
        lost = {**retained, "terminalKind": "Loss", "hpAfterSettlement": 0, "playerAlive": False, "cumulativeHpDamage": 60}
        self.assertIsNone(evaluate_candidate_outcome(lost)["cost"])
        lost["inventoryStart"] = []; lost["inventoryEnd"] = []
        self.assertEqual(1072, evaluate_candidate_outcome(lost)["cost"])
        lost["permanentChanges"] = [{"kind": "unknown_future_benefit", "amount": 2}]
        self.assertEqual(1072, evaluate_candidate_outcome(lost)["cost"])
        winning = {**base, "permanentChanges": [{"kind": "unknown_future_benefit", "amount": 2}]}
        self.assertIsNone(evaluate_candidate_outcome(winning)["cost"])
        winning["permanentChanges"].append({"kind": "unknown_future_benefit", "amount": -2})
        self.assertAlmostEqual(3.03, evaluate_candidate_outcome(winning)["cost"])


if __name__ == "__main__": unittest.main()
