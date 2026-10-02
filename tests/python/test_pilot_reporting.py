"""Read-only report tests. Synthetic bundles serialize initialization, never fit."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tools")]
from nosl.data import canonical_object_digest
from nosl.model import Student
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME, public_input_digest
from nosl.reproducibility import implementation_fingerprint
from nosl.schema import HEADS, SchemaError, load_config
from nosl.train import config_hash, evaluate
from test_student import fixture
import report_pilot_learning as report


def record(identity="one", block=1, means=(0., -5., -10.)):
    item = fixture()
    item["public_input"]["observation"]["block"] = block
    item["targets"]["pairwise"] = []
    item["targets"]["equivalent_action_set"] = []
    for row, value in zip(item["targets"]["actions"], means):
        row["value"] = value
    audit = item["audit_only"]
    audit.update(source_run_group="run-" + identity, source_combat_id="battle-" + identity,
                 branch_family="branch-" + identity, public_state_digest=public_input_digest(item["public_input"]),
                 simulator_commit="sim-test", rules_version="rules-test", label_endpoint="terminal-test",
                 continuation_version="test-policy:tree-" + identity, source_category="potion",
                 generation_source_phase="early", posterior_profile="P1")
    audit["versions"] = {"teacher": audit["teacher_version"], "continuation": "test-policy",
                         "objective": audit["objective_version"], "simulator": audit["simulator_commit"],
                         "public_schema": item["public_input"]["schema_version"], "observation_schema": "nosl.public.v1"}
    return item


def mask_value(item, index):
    row = item["targets"]["actions"][index]
    row.update(value=None, quality="objective_value_unresolved")
    row["masks"]["value"] = False


def mask_all(item, index):
    row = item["targets"]["actions"][index]
    row.update({head: None for head in HEADS})
    row.update(quality="unresolved", masks={head: False for head in HEADS})


def prediction(scores, selected):
    return {"selected_index": selected,
            "predictions": [{"action_index": i, "legal": score is not None, "score": score} for i, score in enumerate(scores)]}


def write_json(path, data):
    path.write_text(json.dumps(data, sort_keys=True, allow_nan=False) + "\n")


def prepared_fixture(root, config, train, validation):
    """Minimum valid immutable M5 chain, with deliberately unparsable test bytes."""
    root.mkdir()
    lock = {"public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
            "student_config_sha256": canonical_object_digest(config), "mode": "pilot"}
    versions = train[0]["audit_only"]["versions"]
    files = []
    for split, records in (("train", train), ("validation", validation), ("test", None)):
        path = root / (split + ".jsonl")
        path.write_text("TEST OUTCOMES MUST NEVER BE PARSED\n" if records is None else
                        "".join(json.dumps(row) + "\n" for row in records))
        files.append({"path": path.name, "kind": split, "rows": 1 if records is None else len(records),
                      "bytes": path.stat().st_size, "sha256": report.file_hash(path)})
    state = {"schema_version": "nosl.dataset.split-state.v2", "lock": lock, "versions": versions,
             "stage_count": 1, "observation_schema": "nosl.public.v1", "cross_split_conflicts": []}
    write_json(root / "state.json", state)
    state_descriptor = {"path": "state.json", "kind": "split_state", "rows": None,
                        "bytes": (root / "state.json").stat().st_size, "sha256": report.file_hash(root / "state.json")}
    files.append(state_descriptor)
    stage = {"schema_version": "nosl.dataset.stage.v2", "lock": lock, "versions": versions,
             "parent_stage_sha256": None, "observation_schema": "nosl.public.v1", "files": files}
    write_json(root / "stage.json", stage)
    manifest = {"schema_version": "nosl.dataset.manifest.v2", "pipeline_version": "nosl.dataset.prepare.v3",
                "public_identity_scheme": PUBLIC_IDENTITY_SCHEME, "isolation_passed": True,
                "lock": lock, "versions": versions, "observation_schema": "nosl.public.v1",
                "stages": [{"manifest": "stage.json", "sha256": report.file_hash(root / "stage.json")}],
                "frozen_test_shards": [files[2]], "latest_state": state_descriptor}
    write_json(root / "manifest.json", manifest)


def synthetic_bundle(root, prepared, config, train, validation):
    root.mkdir()
    # Marked synthetic only within this test harness; no optimizer is constructed.
    model = Student(config).eval()
    torch.save(model.state_dict(), root / "weights.pt")
    write_json(root / "config.json", config)
    metrics = evaluate(model, SimpleNamespace(records=validation))
    audit = train[0]["audit_only"]
    frozen = {"config_sha256": config_hash(config), "observation_schema": "nosl.public.v1",
              "stable_versions": audit["versions"], "test_used_for_model_selection": False,
              "implementation": implementation_fingerprint(),
              "splits": {name: {"sha256": report.file_hash(prepared / "manifest.json") + ":" + name, "roots": len(rows)}
                         for name, rows in (("train", train), ("validation", validation))},
              "versions": {key: audit[key] for key in ("simulator_commit", "rules_version", "label_endpoint", "teacher_version", "objective_version")}}
    frozen["versions"]["continuation_version"] = "test-policy"
    manifest = {"format": "nosl.student.bundle.v1", "public_schema": config["schema_version"],
                "config_sha256": config_hash(config), "weights_sha256": report.file_hash(root / "weights.pt"),
                "status": "EXPERIMENTAL_UNPROMOTED", "trained": True, "calibrated": False,
                "pilot_trial": True, "formal_training_run": False, "test_evaluated": False,
                "frozen_inputs": frozen, "validation_before_fit": metrics, "validation": metrics,
                "optimizer_steps": 1, "budget": {"max_steps": 1, "max_epochs": 1, "max_roots": 10}}
    write_json(root / "manifest.json", manifest)
    return manifest


class PilotReportingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        torch.set_num_threads(1)

    def test_constant_respects_root_candidate_masks_and_zero_sample_weights(self):
        first = record(means=(0., 10., 1000000.))
        mask_value(first, 2)
        first["targets"]["actions"][0]["sample_weight"] = 1.
        first["targets"]["actions"][1]["sample_weight"] = .5
        second = record("two", 2, (20., 1000000., 1000000.))
        mask_value(second, 2)
        second["targets"]["actions"][1]["sample_weight"] = 0.
        result = report.train_constant([first, second])
        self.assertAlmostEqual(result["value"], (0 * .5 + 10 * .25 + 20 * .5) / 1.25)
        self.assertEqual(result["masked_value_candidates"], 4)
        self.assertEqual(result["positive_weight_value_candidates"], 3)
        self.assertEqual(result["effective_coefficient_sum"], 1.25)

    def test_constant_no_value_or_positive_weight_is_rejected(self):
        item = record()
        for row in item["targets"]["actions"]:
            row["sample_weight"] = 0.
        with self.assertRaisesRegex(SchemaError, "positive-weight"):
            report.train_constant([item])

    def test_constant_gives_equal_root_mass_despite_unequal_candidate_counts(self):
        first = record(means=(0., 0., 0.))
        second = record("two", 2, (12., 99999., 99999.))
        mask_value(second, 1)
        mask_value(second, 2)
        self.assertEqual(report.train_constant([first, second])["value"], 6.)

    def test_four_availability_classes_and_strata_preserve_incomplete_potion_roots(self):
        items = [record(str(i), i) for i in range(4)]
        mask_value(items[1], 2)
        for i in range(3):
            mask_value(items[2], i)
            mask_all(items[3], i)
        items[1]["audit_only"]["source_combat_id"] = items[0]["audit_only"]["source_combat_id"]
        items[1]["audit_only"]["generation_source_phase"] = "late"
        items[1]["public_input"]["observation"]["turn"] = 7
        supports = [report.support_row(item) for item in items]
        result = report.stratified(items, supports, [prediction([-2, 0, -3], 1)] * 4, -5.)
        overall = result["overall"]
        for name in report.AVAILABILITY:
            self.assertEqual(overall[name + "_roots"], 1)
        self.assertEqual(overall["distinct_source_battles"], 3)
        self.assertEqual(overall["incomplete_value_roots_skipped"], 3)
        self.assertEqual(overall["empirical_teacher_mean_regret"], {"value": 5., "count": 1})
        self.assertEqual(overall["selected_action_empirical_mean_rank"], {"value": 2., "count": 1})
        self.assertEqual(overall["positive_weight_pairwise_labels"], 0)
        self.assertEqual(result["strata"]["source_category"]["potion"]["incomplete_value_roots_skipped"], 3)
        self.assertEqual(result["strata"]["declared_phase"]["late"]["roots"], 1)
        self.assertEqual(result["strata"]["actual_turn"]["7"]["roots"], 1)
        self.assertEqual(result["strata"]["posterior_profile"]["P1"]["roots"], 4)
        late_potion = result["strata"]["source_category_and_phase"]['["potion","late"]']
        self.assertEqual(late_potion["roots"], 1)
        self.assertEqual(late_potion["incomplete_value_roots_skipped"], 1)
        self.assertIsNone(late_potion["empirical_teacher_mean_regret"]["value"])

    def test_rank_uses_actual_selected_action_and_all_legal_values(self):
        item = record(means=(10., 10., 0.))
        support = report.support_row(item)
        result = report.summary([item], [support], [prediction([3., 2., 1.], 2)], 0.)
        self.assertEqual(result["empirical_teacher_mean_regret"]["value"], 10.)
        self.assertEqual(result["selected_action_empirical_mean_rank"]["value"], 3.)
        result = report.summary([item], [support], [prediction([3., 2., 1.], 1)], 0.)
        self.assertEqual(result["selected_action_empirical_mean_rank"]["value"], 1.)
        mask_value(item, 0)
        result = report.summary([item], [report.support_row(item)], [prediction([3., 2., 1.], 2)], 0.)
        self.assertEqual(result["empirical_teacher_mean_regret"], {"value": None, "count": 0})

    def test_illegal_and_single_action_candidates_do_not_inflate_ranking(self):
        item = record()
        for index in (1, 2):
            mask_all(item, index)
            item["public_input"]["legal_mask"][index] = False
        result = report.summary([item], [report.support_row(item)], [prediction([0., None, None], 0)], 0.)
        self.assertEqual(result["full_utility_roots"], 1)
        self.assertEqual(result["single_action_roots_skipped"], 1)
        self.assertEqual(result["empirical_teacher_best_action_agreement"]["count"], 0)
        self.assertEqual(result["learned"]["value_mae"]["count"], 1)

    def test_constant_and_model_evaluate_identical_validation_support(self):
        item = record(means=(0., 10., 20.))
        mask_value(item, 2)
        item["targets"]["actions"][1]["sample_weight"] = .5
        result = report.summary([item], [report.support_row(item)], [prediction([2., 12., 99999.], 2)], 5.)
        self.assertEqual(result["learned"]["value_mse"], {"value": 4., "count": 2})
        self.assertEqual(result["train_constant"]["value_mse"], {"value": 25., "count": 2})
        self.assertEqual(result["learned"]["root_mean_weighted_value_mse"], 3.)
        self.assertEqual(result["train_constant"]["normalized_root_weighted_value_mse"], 25.)

    def test_shared_integrity_loader_hashes_but_never_parses_test_outcomes(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "prepared"
            train, validation = [record("train", 1)], [record("val", 2)]
            prepared_fixture(path, self.config, train, validation)
            with patch.object(report, "prepared_paths", wraps=report.prepared_paths) as loader:
                data, audit = report.load_inputs(path, self.config)
            self.assertEqual([call.args[1] for call in loader.call_args_list], ["train", "validation"])
            self.assertEqual(set(data), {"train", "validation"})
            self.assertEqual(audit["parsed_records"], 2)
            self.assertNotIn("test", [item["split"] for item in audit["parsed_shards"]])
            (path / "test.jsonl").write_text("tampered integrity bytes")
            with self.assertRaisesRegex(SchemaError, "checksum"):
                report.load_inputs(path, self.config)

    def test_limits_reject_whole_report_without_silent_truncation(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "prepared"
            prepared_fixture(path, self.config, [record("train", 1)], [record("val", 2)])
            for limits in ({"max_records": 1}, {"max_loaded_bytes": 10}, {"max_line_bytes": 10}):
                with self.subTest(limits=limits), self.assertRaisesRegex(SchemaError, "cap"):
                    report.load_inputs(path, self.config, **limits)
            with self.assertRaises(SchemaError):
                report.load_inputs(path, self.config, max_records=10001)

    def test_overlap_is_rejected_even_when_manifest_claims_isolated(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "prepared"
            prepared_fixture(path, self.config, [record("train", 1)], [record("train", 2)])
            with self.assertRaisesRegex(SchemaError, "leakage"):
                report.load_inputs(path, self.config)

    def test_same_support_comparison_rejects_counts_and_changed_final(self):
        final = {"value_mse": {"value": 4., "count": 2}, "evaluation_roots": 1, "unresolved_actions": 0,
                 "empirical_teacher_ranking_semantics": {"incomplete_value_roots_skipped": 1}}
        before = deepcopy(final)
        before["value_mse"]["value"] = 9.
        self.assertEqual(report.compare_metrics(before, final, final)["value_mse"]["final_minus_before_fit"], -5.)
        before["value_mse"]["count"] = 1
        with self.assertRaisesRegex(SchemaError, "support mismatch"):
            report.compare_metrics(before, final, final)
        with self.assertRaisesRegex(SchemaError, "disagrees"):
            report.compare_metrics(final, final, {**final, "value_mse": {"value": 5., "count": 2}})

    def test_end_to_end_report_is_read_only_bound_and_rejects_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            prepared, bundle = Path(temp) / "prepared", Path(temp) / "bundle"
            train, validation = [record("train", 1)], [record("val", 2)]
            mask_value(validation[0], 2)
            prepared_fixture(prepared, self.config, train, validation)
            manifest = synthetic_bundle(bundle, prepared, self.config, train, validation)
            snapshots = {path: path.read_bytes() for path in Path(temp).rglob("*") if path.is_file()}
            with patch.object(report, "train_constant", wraps=report.train_constant) as constant_fit:
                result = report.build_report(prepared, bundle)
            constant_fit.assert_called_once_with(train)
            self.assertEqual(result["limits"]["optimizer_steps_performed"], 0)
            self.assertEqual(result["limits"]["test_target_records_parsed"], 0)
            self.assertEqual(result["train_only_constant"]["value"], -5.)
            self.assertEqual(result["validation"]["overall"]["partial_utility_roots"], 1)
            self.assertEqual(result["validation"]["overall"]["empirical_teacher_mean_regret"]["count"], 0)
            self.assertEqual(result["before_fit_vs_final_same_validation_support"]["value_mse"]["count"], 2)
            self.assertEqual(result["audit"]["weights_sha256"], manifest["weights_sha256"])
            self.assertEqual(snapshots, {path: path.read_bytes() for path in Path(temp).rglob("*") if path.is_file()})
            manifest["frozen_inputs"]["versions"]["objective_version"] = "wrong"
            write_json(bundle / "manifest.json", manifest)
            with self.assertRaisesRegex(SchemaError, "provenance"):
                report.build_report(prepared, bundle)
            manifest["frozen_inputs"]["versions"]["objective_version"] = train[0]["audit_only"]["objective_version"]
            manifest["frozen_inputs"]["implementation"]["source_sha256"]["python/nosl/train.py"] = "tampered"
            write_json(bundle / "manifest.json", manifest)
            with self.assertRaisesRegex(SchemaError, "implementation changed"):
                report.build_report(prepared, bundle)
            manifest["frozen_inputs"]["implementation"] = implementation_fingerprint()
            write_json(bundle / "manifest.json", manifest)
            with (bundle / "weights.pt").open("ab") as stream:
                stream.write(b"tampered")
            with self.assertRaisesRegex(SchemaError, "weight checksum"):
                report.build_report(prepared, bundle)


if __name__ == "__main__":
    unittest.main()
