"""Declared synthetic contract roots, never evidence of natural reachability."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "python"))
import prepare_dataset as prep
from test_prepare_dataset import fixture as legacy_fixture
from nosl.data import prepared_paths
from nosl.data_v2 import PreparedDatasetV2, validate_production_record
from nosl.schema_v2 import load_config, validate_config
from nosl.public_identity_v2 import PUBLIC_IDENTITY_SCHEME, public_input_digest, legacy_alias_digests


def fixture(number=0):
    row = legacy_fixture(number)
    public, audit = row["public_input"], row["audit_only"]
    public["schema_version"] = "nosl.student.public.v2"
    public["observation"]["history"].append({"kind": "player_turn", "detail": "1"})
    audit["versions"].update(public_schema=public["schema_version"], observation_schema="nosl.public.v1",
        rules="synthetic-rules", endpoint="synthetic-endpoint", controller="inactive", sampler="synthetic-sampler", dataset="synthetic-v2")
    audit.update(sampler_seeds=list(range(8)), exploration_seeds=[10, 11],
        continuation_version="test-policy:synthetic", teacher_version="T0-test", objective_version="synthetic-contract",
        simulator_commit="test-simulator", rules_version="synthetic-rules", label_endpoint="synthetic-endpoint",
        controller_version="inactive", sampler_version="synthetic-sampler", dataset_version="synthetic-v2")
    return row


class PrepareV2Tests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((ROOT / "configs/data_pipeline.v2.json").read_text())
        self.student = load_config(ROOT / "configs/student.v2.engineering.json")
        self.legacy_config = json.loads((ROOT / "configs/data_pipeline.v1.json").read_text())

    def representatives(self):
        found = {}
        for i in range(100):
            row = fixture(i)
            group = prep.provenance_components([row], identity_scheme=PUBLIC_IDENTITY_SCHEME)[0][0]
            found.setdefault(prep.choose_split(group, self.config), row)
            if len(found) == 3: return found
        self.fail("three fixture groups not found")

    def persist(self, root, rows, *, resume=False, protection=None):
        return prep.persist_batch(root, rows, [{"fixture": i} for i in range(len(rows))], self.config,
            "engineering-smoke", [{"synthetic_contract_sha256": prep.object_digest(rows)}], resume,
            protection=protection)

    def test_training_config_rejects_nonprogress_and_nonfinite_weights(self):
        for key, value in (("batch_size", 0), ("batch_size", -1), ("batch_size", True), ("learning_rate", 0), ("learning_rate", float("nan"))):
            config = deepcopy(self.student); config["base_config"][key] = value
            with self.assertRaises(ValueError): validate_config(config)
        for value in (-1, float("inf"), float("nan")):
            config = deepcopy(self.student); config["base_config"]["loss_weights"]["value"] = value
            with self.assertRaises(ValueError): validate_config(config)

    def test_real_plan_only_and_diagnostic_masks(self):
        root = json.loads((ROOT / "tests/python/fixtures/finite-hunt-record-v2.jsonl").read_text())
        splits, report, rejected = prep.prepare([root], self.config, student_config=self.student)
        self.assertEqual([], rejected)
        self.assertEqual(1, report["accepted_roots"])
        self.assertEqual(0, report["throughput"]["allocated_action_worlds"])
        self.assertEqual(2 * root["targets"]["plan"]["allocated_worlds"], report["throughput"]["paired_policy_worlds"]["allocated_worlds"])
        self.assertTrue(all(not any(row["masks"].values()) for row in root["targets"]["actions"]))
        bad = deepcopy(root); bad["targets"]["plan"]["specified_success_probability"] = None
        with self.assertRaises(ValueError): validate_production_record(bad, self.student)
        bad = deepcopy(root); bad["audit_only"]["sampler_seeds"].pop()
        with self.assertRaises(ValueError): validate_production_record(bad, self.student)
        bad = deepcopy(root); bad["audit_only"]["candidate_evaluation_performed"] = True
        with self.assertRaises(ValueError): validate_production_record(bad, self.student)
        for file in ("finite-hunt-unresolved-v2.jsonl", "finite-hunt-continuation-v2.jsonl"):
            row = json.loads((ROOT / "tests/python/fixtures" / file).read_text())
            validate_production_record(row, self.student, require_usable=False)
            self.assertEqual(0, prep.prepare([row], self.config, student_config=self.student)[1]["accepted_roots"])

    def test_incomplete_plan_costs_retain_all_supplied_mass(self):
        rows = [json.loads((ROOT / "tests/python/fixtures" / file).read_text()) for file in
                ("finite-hunt-record-v2.jsonl", "finite-hunt-unresolved-v2.jsonl")]
        report = prep.prepare(rows, self.config, student_config=self.student)[1]
        self.assertEqual(2, report["throughput"]["decision_roots"])
        self.assertEqual(2, report["valid_attempts"])
        self.assertEqual(4, report["throughput"]["independent_eval_world_draws"])
        self.assertEqual(8, report["throughput"]["paired_policy_worlds"]["allocated_worlds"])
        self.assertEqual(4, report["throughput"]["paired_policy_worlds"]["truncated_worlds"])
        self.assertEqual(.5, report["throughput"]["paired_policy_effective_sample_rate"])
        self.assertEqual(1, report["accepted_roots"])

    def test_real_forced_event_audit_and_large_seed_memory(self):
        for filename in ("forced-event-t0-v2.jsonl", "forced-event-t1-v2.jsonl"):
            row = json.loads((ROOT / "tests/python/fixtures" / filename).read_text())
            validate_production_record(row, self.student)
            row["audit_only"]["sampler_seeds"][0] = 2**64 - 1
            validate_production_record(row, self.student)
            row["audit_only"]["rules_version"] = "mismatched"
            with self.assertRaises(ValueError): validate_production_record(row, self.student)

    def test_prepare_append_load_and_tamper(self):
        rows = self.representatives()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.persist(root, list(rows.values()))
            original = prep.verify_manifest(root)["frozen_test_shards"]
            train = PreparedDatasetV2(root, "train", self.student)
            self.assertEqual(1, len(train)); train.verify_integrity()
            with self.assertRaises(ValueError): PreparedDatasetV2(root, "test", self.student)
            with self.assertRaises(ValueError): prepared_paths(root, "train")
            added = deepcopy(rows["train"]); added["public_input"]["observation"]["enemies"][0]["hp"] += 500
            self.persist(root, [added], resume=True)
            self.assertEqual(original, prep.verify_manifest(root)["frozen_test_shards"])
            with self.assertRaises(ValueError): train.verify_integrity()
            train = PreparedDatasetV2(root, "train", self.student)
            self.assertEqual(2, len(train))
            train.records[0]["targets"]["actions"][0]["value"] = 999
            with self.assertRaises(ValueError): train.verify_integrity()
            path = prep.iter_dataset_paths(root, "train")[0]
            path.write_text(path.read_text() + " ")
            with self.assertRaises(ValueError): PreparedDatasetV2(root, "train", self.student)

    def test_legacy_test_aliases_and_invalid_bridges(self):
        old = []
        for i in range(100):
            row = fixture(i)
            row["public_input"]["schema_version"] = "nosl.student.public.v1"
            row["audit_only"]["versions"]["public_schema"] = "nosl.student.public.v1"
            group = prep.provenance_components([row])[0][0]
            if prep.choose_split(group, self.legacy_config) == "test": old = [row]; break
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "old", Path(directory) / "new"
            prep.persist_batch(source, old, [{}], self.legacy_config, "engineering-smoke", [{"fixture": "old"}], False)
            protection = prep.export_split_protection(source)
            changed = deepcopy(old[0]); changed["public_input"]["schema_version"] = "nosl.student.public.v2"
            changed["audit_only"]["versions"]["public_schema"] = "nosl.student.public.v2"
            for key in prep.PROVENANCE_FIELDS: changed["audit_only"][key] = "changed-" + key
            self.persist(target, [changed], protection=protection)
            manifest = prep.verify_manifest(target)
            self.assertEqual([], prep.iter_dataset_paths(target, "train"))
            self.assertEqual([], prep.iter_dataset_paths(target, "test"))
            self.assertNotEqual(manifest["public_identity_scheme"], protection["public_identity_scheme"])
            state = prep.read_json(target / manifest["latest_state"]["path"])
            self.assertTrue(all(x["held_out_excluded"] for x in state["seen_public_digests"].values()))
            retained = fixture(200)
            retained["audit_only"]["source_run_group"] = "new-independent-root"
            self.persist(target, [retained], resume=True)
            # Failed metadata joins both aliases before filtering and blocks the corpus permanently.
            bridge = {"audit_only": {"source_run_group": retained["audit_only"]["source_run_group"],
                "source_combat_id": old[0]["audit_only"]["source_combat_id"]}}
            self.persist(target, [bridge], resume=True)
            self.assertFalse(prep.verify_manifest(target)["isolation_passed"])
            with self.assertRaises(ValueError): PreparedDatasetV2(target, "train", self.student)

    def test_unprotected_v2_conflict_is_committed_despite_version_mismatch(self):
        rows = self.representatives()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.persist(root, list(rows.values()))
            bridge = {"audit_only": {"source_run_group": rows["train"]["audit_only"]["source_run_group"],
                                    "source_combat_id": rows["test"]["audit_only"]["source_combat_id"]}}
            mixed = fixture(666)
            mixed["audit_only"]["versions"]["teacher"] = "different"
            mixed["audit_only"]["teacher_version"] = "different"
            self.persist(root, [bridge, mixed], resume=True)
            self.assertFalse(prep.verify_manifest(root)["isolation_passed"])
            with self.assertRaises(ValueError): PreparedDatasetV2(root, "train", self.student)
            with self.assertRaises(ValueError): self.persist(root, [fixture(667)], resume=True)

    def test_invalid_current_identity_cannot_hide_valid_anchor_alias(self):
        anchor = legacy_fixture(5)["public_input"]
        record = fixture(9)
        record["public_input"]["observation"]["unknownDraw"] = [{}]
        record["public_input"]["controller_context"] = {"anchor": anchor}
        expected = "prepared_public_input_digest:" + prep.public_digest(anchor)
        self.assertIn(expected, prep.record_tokens(record, PUBLIC_IDENTITY_SCHEME))
        protection = {"components": {"old": {"split": "test", "tokens": [expected]}}}
        groups, tokens, history = prep.provenance_components([record], protection, PUBLIC_IDENTITY_SCHEME)
        self.assertEqual({"old"}, history[groups[0]])
        self.assertIn(expected, tokens[groups[0]])

    def test_native_never_admitted_malformed_and_mixed_versions(self):
        row = fixture(); row["audit_only"]["trainable"] = False
        report = prep.prepare([row], self.config, student_config=self.student)[1]
        self.assertIn("native_development_not_admitted", report["filter_reasons"])
        self.assertTrue(report["split_state"]["components"])
        row["audit_only"]["trainable"] = True
        row["audit_only"]["native_import"] = "certified_detached_boundary_clone"
        self.assertIn("native_development_not_admitted", prep.prepare([row], self.config, student_config=self.student)[1]["filter_reasons"])
        row = fixture(); row["audit_only"]["versions"].pop("sampler")
        self.assertIn("versions_missing", prep.prepare([row], self.config, student_config=self.student)[1]["filter_reasons"])
        rows = [fixture(1), fixture(2)]; rows[1]["audit_only"]["versions"]["teacher"] = "different"
        rows[1]["audit_only"]["teacher_version"] = "different"
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "versions_mismatch"): self.persist(Path(directory), rows)
        old = legacy_fixture()
        self.assertEqual(0, prep.prepare([old], self.config, student_config=self.student)[1]["accepted_roots"])

    def test_recursive_identity_numeric_json_decks_revisions_order(self):
        public = json.loads((ROOT / "tests/python/fixtures/finite-hunt-record-v2.jsonl").read_text())["public_input"]
        changed = deepcopy(public)
        changed["controller_context"]["anchor"]["candidate_actions"][0]["revision"] += 33
        changed["controller_context"]["anchor"]["observation"]["hp"] = float(changed["controller_context"]["anchor"]["observation"]["hp"])
        self.assertEqual(public_input_digest(public), public_input_digest(changed))
        self.assertIn(prep.public_digest(public["controller_context"]["anchor"]), legacy_alias_digests(public))
        native = json.loads((ROOT / "tests/python/fixtures/native-entry-public-v2.json").read_text())
        changed = deepcopy(native); detail = json.loads(changed["observation"]["history"][1]["detail"])
        detail["deck"].reverse(); detail["hp"] = float(detail["hp"])
        changed["observation"]["history"][1]["detail"] = json.dumps(detail, indent=3)
        self.assertEqual(public_input_digest(native), public_input_digest(changed))
        changed["observation"]["history"].reverse()
        self.assertNotEqual(public_input_digest(native), public_input_digest(changed))

    def test_v2_journal_aliases_union_before_filtering(self):
        row = fixture()
        journal = {"source_run_group": "failed-run", "public_input": row["public_input"], "legacy_public_input_digests": ["a" * 64]}
        records = prep.journal_provenance([journal], identity_scheme=PUBLIC_IDENTITY_SCHEME)
        tokens = prep.record_tokens(records[0], PUBLIC_IDENTITY_SCHEME)
        self.assertIn("prepared_public_input_digest:" + "a" * 64, tokens)
        self.assertIn("prepared_public_input_digest:" + public_input_digest(row["public_input"]), tokens)
        journal["legacy_public_input_digests"] = ["malformed"]
        with self.assertRaises(ValueError): prep.journal_provenance([journal], identity_scheme=PUBLIC_IDENTITY_SCHEME)


if __name__ == "__main__": unittest.main()
