"""Synthetic metadata/holdout safety tests; never train or inspect real targets."""
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import test_prepare_dataset as fixtures
import prepare_dataset as data
from nosl.data import prepared_paths

ROOT, fixture = fixtures.ROOT, fixtures.fixture


class SplitProtectionTests(unittest.TestCase):
    split_for = fixtures.DataPreparationTests.split_for
    representative_splits = fixtures.DataPreparationTests.representative_splits

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "old"
        self.output = self.root / "new"
        self.config = data.read_json(ROOT / "configs/data_pipeline.v1.json")
        self.roots = self.representative_splits()
        self.invalid_old = fixture(501)
        self.invalid_old["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
        self.invalid_old["targets"]["actions"].pop()
        self.persist(self.source, list(self.roots.values()))
        self.persist(self.source, [self.invalid_old], resume=True, tag="old-invalid-alias")
        self.protection = data.export_split_protection(self.source)

    def persist(self, output, rows, *, resume=False, protection=None, provenance=None, tag="input"):
        return data.persist_batch(output, rows, [{"path": tag, "line": i + 1} for i in range(len(rows))],
                                  self.config, "pilot", [{"path": tag, "sha256": data.object_digest(rows), "bytes": 0}],
                                  resume, protection=protection, provenance_records=provenance)

    def relabel(self, row):
        row = deepcopy(row)
        row["audit_only"]["versions"]["continuation"] = "new-policy-v2"
        row["audit_only"]["versions"]["sampler"] = "new-sampler-v3"
        return row

    def prepare(self, rows, state=None, provenance=None):
        return data.prepare(rows, self.config, "pilot", state, protection=self.protection,
                            provenance_records=provenance)

    def test_export_is_metadata_only_and_never_decodes_old_shards(self):
        original = Path.open
        def guarded(path, mode="r", *args, **kwargs):
            if path.suffix == ".jsonl" and path.is_relative_to(self.source):
                self.assertEqual("rb", mode, "Old targets must only be hashed as opaque bytes")
            return original(path, mode, *args, **kwargs)
        before = {p: data.file_hash(p) for p in self.source.rglob("*") if p.is_file()}
        with patch.object(Path, "open", guarded):
            exported = data.export_split_protection(self.source)
        self.assertEqual(before, {p: data.file_hash(p) for p in before})
        self.assertEqual({"schema_version", "public_identity_scheme", "source", "components"}, set(exported))
        self.assertNotIn("seen_public_digests", exported)
        self.assertTrue(any("source_run_group:run-501" in c["tokens"] for c in exported["components"].values()))

    def test_old_test_direct_and_semantic_identity_excluded_from_all_splits(self):
        direct = self.relabel(self.roots["test"])
        semantic = self.relabel(fixture(601))
        semantic["public_input"] = deepcopy(direct["public_input"])
        for action in semantic["public_input"]["candidate_actions"]:
            action["revision"] = 333
        for row in (direct, semantic):
            splits, report, rejected = self.prepare([row])
            self.assertEqual(0, sum(map(len, splits.values())))
            self.assertEqual("protected_old_test_component_excluded", rejected[0]["reason"])
            self.assertEqual([], report["split_state"]["frozen_test_digests"])
            self.assertEqual(1, report["split_protection"]["protected_test_excluded_roots"])

    def test_invalid_and_diagnostic_bridges_union_before_filtering(self):
        for diagnostic in (False, True):
            new = self.relabel(fixture(602))
            bridge = self.relabel(fixture(603))
            bridge["audit_only"]["source_run_group"] = new["audit_only"]["source_run_group"]
            bridge["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
            if diagnostic:
                for action in bridge["targets"]["actions"]:
                    action["sample_weight"] = 0
            else:
                bridge["targets"]["actions"].pop()
            splits, report, rejected = self.prepare([new, bridge])
            self.assertEqual(0, sum(map(len, splits.values())))
            self.assertIn("protected_old_test_component_excluded", [r["reason"] for r in rejected])
            self.assertTrue(any("source_run_group:run-602" in c["tokens"] for c in report["split_state"]["components"].values()))

    def test_rejected_historical_alias_is_protected(self):
        new = self.relabel(fixture(604))
        new["audit_only"]["source_run_group"] = self.invalid_old["audit_only"]["source_run_group"]
        splits, _, rejected = self.prepare([new])
        self.assertFalse(any(splits.values()))
        self.assertEqual("protected_old_test_component_excluded", rejected[0]["reason"])

    def test_old_train_relabels_and_old_validation_keep_assignments(self):
        rows = [self.relabel(self.roots[s]) for s in ("train", "validation")]
        splits, report, rejected = self.prepare(rows)
        self.assertFalse(rejected)
        self.assertEqual(1, len(splits["train"]))
        self.assertEqual(1, len(splits["validation"]))
        self.assertEqual(2, len(report["split_state"]["seen_public_digests"]))
        self.assertNotEqual(self.protection["source"]["versions"], report["versions"])

    def test_journal_bridge_and_semantic_digest_survive(self):
        row = self.relabel(fixture(605))
        journals = data.journal_provenance([{
            "source_run_group": row["audit_only"]["source_run_group"],
            "public_input_digest": data.public_digest(self.roots["test"]["public_input"]),
            "public_digest_scheme": data.PUBLIC_IDENTITY_SCHEME, "status": "failed_attempt"}])
        splits, report, _ = self.prepare([row], provenance=journals)
        self.assertFalse(any(splits.values()))
        self.assertEqual(1, report["split_protection"]["provenance_only_rows"])
        self.assertEqual(1, report["input_attempts"])
        with self.assertRaisesRegex(ValueError, "journal_public_digest"):
            data.journal_provenance([{**journals[0]["audit_only"], "public_input_digest": "0" * 64}])

    def test_cross_split_conflict_is_persistent_and_readiness_blocked(self):
        bridge = self.relabel(fixture(606))
        bridge["audit_only"]["source_run_group"] = self.roots["train"]["audit_only"]["source_run_group"]
        bridge["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
        result = self.persist(self.output, [bridge], protection=self.protection)
        self.assertFalse(result["isolation_passed"])
        manifest = data.verify_manifest(self.output)
        state = data.read_json(self.output / manifest["latest_state"]["path"])
        self.assertIn("source_combat_id:combat-606", state["cross_split_conflicts"][0]["tokens"])
        with self.assertRaisesRegex(ValueError, "isolation blocked"):
            prepared_paths(self.output, "train")
        with self.assertRaisesRegex(ValueError, "conflict_resume_blocked"):
            self.persist(self.output, [self.relabel(fixture(607))], resume=True)

    def test_append_omitted_option_preserves_new_alias_closure(self):
        bridge = self.relabel(fixture(608))
        bridge["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
        self.persist(self.output, [bridge], protection=self.protection)
        next_row = self.relabel(fixture(609))
        next_row["audit_only"]["source_run_group"] = bridge["audit_only"]["source_run_group"]
        result = self.persist(self.output, [next_row], resume=True)
        self.assertEqual(0, result["accepted_roots"])
        self.assertFalse(prepared_paths(self.output, "train")[0])
        self.assertFalse(prepared_paths(self.output, "test")[0])
        with self.assertRaisesRegex(ValueError, "configuration_or_mode"):
            manifest = data.verify_manifest(self.output)
            state = data.read_json(self.output / manifest["latest_state"]["path"])
            data.prepare([next_row], self.config, "pilot", state)

    def test_cannot_replace_registry_or_add_it_to_ordinary_resume(self):
        self.persist(self.output, [self.relabel(fixture(610))], protection=self.protection)
        changed = deepcopy(self.protection)
        changed["source"]["manifest_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "protection_changed"):
            self.persist(self.output, [self.relabel(fixture(611))], resume=True, protection=changed)
        with self.assertRaisesRegex(ValueError, "protection_changed"):
            self.persist(self.source, [fixture(611)], resume=True, protection=self.protection)

    def test_protected_new_corpus_and_resume_keep_strict_versions(self):
        rows = [self.relabel(fixture(612)), self.relabel(fixture(613))]
        rows[1]["audit_only"]["versions"]["continuation"] = "third-policy"
        with self.assertRaisesRegex(ValueError, "versions_mismatch"):
            self.persist(self.output, rows, protection=self.protection)
        self.assertFalse((self.output / "manifest.json").exists())
        self.persist(self.output, rows[:1], protection=self.protection)
        before = (self.output / "manifest.json").read_bytes()
        with self.assertRaisesRegex(ValueError, "versions_mismatch"):
            self.persist(self.output, rows[1:], resume=True)
        self.assertEqual(before, (self.output / "manifest.json").read_bytes())

    def test_tampered_source_metadata_and_registry_fail_without_commit(self):
        manifest = data.verify_manifest(self.source)
        state_path = self.source / manifest["latest_state"]["path"]
        state_path.write_text(state_path.read_text() + " ")
        with self.assertRaisesRegex(ValueError, "checksum_mismatch"):
            data.export_split_protection(self.source)
        self.persist(self.output, [self.relabel(fixture(614))], protection=self.protection)
        registry_path = next(self.output.glob("stages/*/split_protection.json"))
        registry_path.write_text(registry_path.read_text() + " ")
        with self.assertRaisesRegex(ValueError, "checksum_mismatch"):
            self.persist(self.output, [self.relabel(fixture(615))], resume=True)
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            prepared_paths(self.output, "train")

    def test_registry_schema_identity_and_alias_tampering_rejected(self):
        for key, value in (("schema_version", "future"), ("public_identity_scheme", "future")):
            changed = deepcopy(self.protection)
            changed[key] = value
            with self.assertRaises(ValueError):
                self.persist(self.output, [self.relabel(fixture(616))], protection=changed)
        changed = deepcopy(self.protection)
        next(iter(changed["components"].values()))["tokens"].append("source_run_group:tampered")
        with self.assertRaises(ValueError):
            data.validate_registry(changed)

    def test_all_source_bytes_unchanged_after_new_corpus_and_append(self):
        before = {p: p.read_bytes() for p in self.source.rglob("*") if p.is_file()}
        self.persist(self.output, [self.relabel(self.roots["train"])], protection=self.protection)
        self.persist(self.output, [self.relabel(self.roots["validation"])], resume=True)
        self.assertTrue(all(p.read_bytes() == value for p, value in before.items()))
        self.assertEqual(1, len(prepared_paths(self.source, "test")[0]))

    def test_cli_new_protection_and_idempotent_resume_without_source(self):
        path = self.root / "input.jsonl"
        path.write_text(json.dumps(self.relabel(self.roots["train"])) + "\n")
        journal = self.root / "journals.jsonl"
        journal.write_text(json.dumps({"source_run_group": "failed-only-alias"}) + "\n")
        command = [sys.executable, "-B", str(ROOT / "tools/prepare_dataset.py"), str(path),
                   "--provenance-journals", str(journal), "--output-dir", str(self.output), "--mode", "pilot"]
        result = subprocess.run(command + ["--protect-from-prepared", str(self.source)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        result = subprocess.run(command + ["--resume"], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("ALREADY_COMMITTED", json.loads(result.stdout)["status"])
        data.verify_manifest(self.output)
        prepared_paths(self.output, "train")

    def test_failed_journals_recover_pinned_generator_aliases(self):
        import generate_pilot_data as generator
        config = {"version": generator.VERSION, "generator_sha256": data.file_hash(Path(generator.__file__)),
                  "public_identity_scheme": data.PUBLIC_IDENTITY_SCHEME,
                  "roots_per_battle": 4, "seed_prefix": "synthetic-journal"}
        row = {"generation_version": generator.VERSION,
               "generation_config_sha256": generator.sha(generator.generation_identity_config(config)),
               "source_index": 9, "source_battle_index": 2, "status": "failed_attempt", "elapsed_seconds": 0.1}
        metadata = data.journal_provenance([row], config)[0]
        self.assertEqual({"source_run_group:synthetic-journal:run:2",
                          "source_combat_id:synthetic-journal:combat:2",
                          "branch_family:synthetic-journal:family:2"}, data.record_tokens(metadata))
        for changed in ({**row, "generation_config_sha256": "0" * 64},
                        {**row, "source_battle_index": 7},
                        {**row, "branch_family": "false-alias"}):
            with self.assertRaises(ValueError):
                data.journal_provenance([changed], config)

    def test_interrupted_first_publish_cannot_drop_protection(self):
        original = data.os.replace
        def interrupt(src, dst):
            if Path(dst).name == "manifest.json":
                raise OSError("synthetic interruption")
            return original(src, dst)
        row = self.relabel(self.roots["test"])
        with patch.object(data.os, "replace", side_effect=interrupt):
            with self.assertRaisesRegex(OSError, "synthetic interruption"):
                self.persist(self.output, [row], protection=self.protection)
        result = self.persist(self.output, [row], resume=True)
        self.assertEqual(0, result["accepted_roots"])
        self.assertIn("split_protection", data.verify_manifest(self.output)["lock"])
        self.assertFalse(prepared_paths(self.output, "test")[0])

    def rehash_single_stage(self, mutate):
        """Keep byte checks valid so tests reach semantic consumer checks."""
        manifest = data.read_json(self.output / "manifest.json")
        stage_path = self.output / manifest["stages"][0]["manifest"]
        stage = data.read_json(stage_path)
        state_path = self.output / manifest["latest_state"]["path"]
        state = data.read_json(state_path)
        registry_descriptor = next(d for d in stage["files"] if d["kind"] == "split_protection")
        registry_path = self.output / registry_descriptor["path"]
        registry = data.read_json(registry_path)
        mutate(manifest, stage, state, registry)
        registry_path.write_bytes(data.registry_bytes(registry))
        data.write_json(state_path, state)
        for descriptor in stage["files"]:
            path = self.output / descriptor["path"]
            descriptor.update(sha256=data.file_hash(path), bytes=path.stat().st_size)
        manifest["latest_state"] = next(d for d in stage["files"] if d["kind"] == "split_state")
        data.write_json(stage_path, stage)
        manifest["stages"][0]["sha256"] = data.file_hash(stage_path)
        data.write_json(self.output / "manifest.json", manifest)

    def test_loader_enforces_registry_binding_even_when_file_hashes_match(self):
        self.persist(self.output, [self.relabel(self.roots["train"])], protection=self.protection)
        def mutate(manifest, stage, state, registry):
            registry["source"]["manifest_sha256"] = "0" * 64
        self.rehash_single_stage(mutate)
        for verify in (data.verify_manifest, lambda p: prepared_paths(p, "train")):
            with self.assertRaisesRegex(ValueError, "registry_binding_mismatch"):
                verify(self.output)

    def test_loader_rejects_unknown_registry_version_and_removed_binding(self):
        for remove in (False, True):
            self.output = self.root / ("removed" if remove else "unknown")
            self.persist(self.output, [self.relabel(self.roots["train"])], protection=self.protection)
            def mutate(manifest, stage, state, registry):
                for value in (manifest, stage, state):
                    if remove:
                        value["lock"].pop("split_protection")
                    else:
                        value["lock"]["split_protection"]["schema_version"] = "future"
            self.rehash_single_stage(mutate)
            for verify in (data.verify_manifest, lambda p: prepared_paths(p, "train")):
                with self.assertRaisesRegex(ValueError, "unbound_registry|binding_version"):
                    verify(self.output)

    def test_loader_rejects_removed_protected_alias_in_hashed_state(self):
        self.persist(self.output, [self.relabel(self.roots["train"])], protection=self.protection)
        def mutate(manifest, stage, state, registry):
            old_group = next(g for g, c in state["components"].items() if c["split"] == "test")
            state["components"].pop(old_group)
        self.rehash_single_stage(mutate)
        for verify in (data.verify_manifest, lambda p: prepared_paths(p, "train")):
            with self.assertRaisesRegex(ValueError, "protected_alias_closure_removed"):
                verify(self.output)

    def test_exporting_protected_corpus_preserves_both_generations_of_test(self):
        fresh_test = next(fixture(n) for n in range(700, 1000) if self.split_for(fixture(n)) == "test")
        self.persist(self.output, [self.relabel(fresh_test)], protection=self.protection)
        self.assertEqual(1, len(prepared_paths(self.output, "test")[0]))
        exported = data.export_split_protection(self.output)
        fresh_test["audit_only"]["versions"]["continuation"] = "third-policy"
        splits, report, rejected = data.prepare([fresh_test], self.config, "pilot", protection=exported)
        self.assertFalse(any(splits.values()))
        self.assertEqual("protected_old_test_component_excluded", rejected[0]["reason"])
        all_tokens = {t for c in exported["components"].values() for t in c["tokens"]}
        self.assertTrue(all(t in all_tokens for c in self.protection["components"].values() for t in c["tokens"]))

    def test_missing_interrupted_registry_fails_closed_on_resume(self):
        original = data.os.replace
        def interrupt(src, dst):
            if Path(dst).name == "manifest.json":
                raise OSError("synthetic interruption")
            return original(src, dst)
        row = self.relabel(self.roots["train"])
        with patch.object(data.os, "replace", side_effect=interrupt):
            with self.assertRaisesRegex(OSError, "synthetic interruption"):
                self.persist(self.output, [row], protection=self.protection)
        next(self.output.glob("stages/*/split_protection.json")).unlink()
        with self.assertRaisesRegex(ValueError, "uncommitted_protection_missing"):
            self.persist(self.output, [row], resume=True)
        self.assertFalse((self.output / "manifest.json").exists())

    def test_later_same_split_bridge_to_protected_test_commits_permanent_block(self):
        fresh_test = next(fixture(n) for n in range(700, 1000) if self.split_for(fixture(n)) == "test")
        self.persist(self.output, [self.relabel(fresh_test)], protection=self.protection)
        original_test = {path: path.read_bytes() for path in prepared_paths(self.output, "test")[0]}
        bridge = self.relabel(fixture(1001))
        bridge["audit_only"]["source_run_group"] = fresh_test["audit_only"]["source_run_group"]
        bridge["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
        bridge["targets"]["actions"].pop()  # Even a rejected row establishes contamination.
        result = self.persist(self.output, [bridge], resume=True)
        self.assertFalse(result["isolation_passed"])
        manifest = data.verify_manifest(self.output)
        state = data.read_json(self.output / manifest["latest_state"]["path"])
        conflict = state["cross_split_conflicts"][0]
        self.assertEqual(["test"], conflict["historical_splits"])
        self.assertEqual("retained_target_connected_to_protected_test", conflict["reason"])
        self.assertTrue(data.record_tokens(bridge) <= set(conflict["tokens"]))
        self.assertTrue(all(path.read_bytes() == value for path, value in original_test.items()))
        with self.assertRaisesRegex(ValueError, "isolation blocked"):
            prepared_paths(self.output, "test")
        with self.assertRaisesRegex(ValueError, "conflict_resume_blocked"):
            self.persist(self.output, [self.relabel(fixture(1002))], resume=True)

    def test_conflict_cannot_be_erased_by_unrelated_version_mismatch(self):
        fresh_test = next(fixture(n) for n in range(700, 1000) if self.split_for(fixture(n)) == "test")
        self.persist(self.output, [self.relabel(fresh_test)], protection=self.protection)
        bridge = self.relabel(fixture(1001))
        bridge["audit_only"]["source_run_group"] = fresh_test["audit_only"]["source_run_group"]
        bridge["audit_only"]["branch_family"] = self.roots["test"]["audit_only"]["branch_family"]
        wrong_version = self.relabel(fixture(1002))
        wrong_version["audit_only"]["versions"]["continuation"] = "incompatible"
        result = self.persist(self.output, [bridge, wrong_version], resume=True)
        self.assertFalse(result["isolation_passed"])
        manifest = data.verify_manifest(self.output)
        self.assertEqual("new-policy-v2", manifest["versions"]["continuation"])
        stage = data.read_json(self.output / manifest["stages"][-1]["manifest"])
        self.assertFalse(any(d["kind"] in data.SPLITS for d in stage["files"]))
        with self.assertRaisesRegex(ValueError, "isolation blocked"):
            prepared_paths(self.output, "test")

    def test_invalid_only_initial_conflict_is_a_durable_blocked_corpus(self):
        bridge = {"audit_only": {
            "source_run_group": self.roots["train"]["audit_only"]["source_run_group"],
            "branch_family": self.roots["test"]["audit_only"]["branch_family"]}}
        result = self.persist(self.output, [bridge], protection=self.protection)
        self.assertFalse(result["isolation_passed"])
        manifest = data.verify_manifest(self.output)
        self.assertIsNone(manifest["versions"])
        with self.assertRaisesRegex(ValueError, "isolation blocked"):
            prepared_paths(self.output, "train")
        with self.assertRaisesRegex(ValueError, "conflict_resume_blocked"):
            self.persist(self.output, [self.relabel(fixture(1003))], resume=True)


if __name__ == "__main__":
    unittest.main()
