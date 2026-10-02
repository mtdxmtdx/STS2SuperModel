"""Pure admission tests; integration collection is opt-in through NOSL_NATIVE_WORKER.

Fixtures for protection are synthetic metadata. No historical target corpus, old
native archive, optimizer, model fit, or backward pass is opened or executed.
"""
from copy import deepcopy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "python"))
import prepare_dataset as prep
from native_protection import project_native_source_metadata
from test_prepare_dataset import fixture as legacy_fixture
from nosl.data_v2 import PreparedDatasetV2, validate_production_record
from nosl.native_pilot import is_native_pilot, native_source_tokens, source_run_identity, source_battle_identity
from nosl.schema_v2 import load_config


def historic_metadata():
    return {"audit_only": {"source_kind": "natural", "native_run": True,
        "actual_seed": "nosl-m5-natural-proof-20261001:0", "act": 1, "floor": 1, "encounter": "ToadpolesWeak",
        "source_run_group": "inspected-fixed200", "source_combat_id": "inspected-fixed200/battle-1"}}


def protection_at(directory):
    config = json.loads((ROOT / "configs/data_pipeline.v1.json").read_text())
    prep.persist_batch(directory, [legacy_fixture(777)], [{}], config, "engineering-smoke", [{}], False)
    target_paths = set(path.resolve() for split in prep.SPLITS for path in prep.iter_dataset_paths(directory, split))
    original = Path.open
    def opaque_only(path, mode="r", *args, **kwargs):
        if path.resolve() in target_paths and "b" not in mode: raise AssertionError("old targets decoded")
        return original(path, mode, *args, **kwargs)
    with patch.object(Path, "open", opaque_only):
        base = prep.export_split_protection(directory)
        return prep.extend_native_protection(base, [historic_metadata()])


class NativePilotMetadataTests(unittest.TestCase):
    def test_inspected_source_prefix_changes_cannot_change_source_or_battle_tokens(self):
        before = historic_metadata()
        after = deepcopy(before)
        after["audit_only"].update(source_run_group="renamed", source_combat_id="renamed-combat", branch_family="renamed-family")
        self.assertEqual(native_source_tokens(before["audit_only"]), native_source_tokens(after["audit_only"]))
        self.assertTrue(native_source_tokens(before["audit_only"]) <= prep.record_tokens(after, prep.V2_IDENTITY))
        after["audit_only"]["actual_seed"] += "changed"
        self.assertNotEqual(native_source_tokens(before["audit_only"]), native_source_tokens(after["audit_only"]))

    def test_native_protection_is_metadata_only_and_binds_base_without_old_targets(self):
        with tempfile.TemporaryDirectory() as d:
            registry = protection_at(Path(d))
            self.assertEqual(1, registry["native_history"]["rows"])
            self.assertTrue(any(native_source_tokens(historic_metadata()["audit_only"]) <= set(c["tokens"])
                                for c in registry["components"].values()))
            with self.assertRaisesRegex(ValueError, "metadata_only"):
                prep.extend_native_protection(registry, [{**historic_metadata(), "targets": {}}])

    def test_native_archive_projection_never_decodes_target_or_outcome_values(self):
        row = historic_metadata()
        row["public_input"] = {"public": ["test"]}
        row["targets"] = {"marker": "NEVER_DECODE_TARGET"}
        row["audit_only"]["outcome_samples"] = [{"marker": "NEVER_DECODE_TARGET"}]
        encoded = json.dumps(row)
        decode = json.loads
        def public_only(value, *args, **kwargs):
            self.assertNotIn("NEVER_DECODE_TARGET", value)
            return decode(value, *args, **kwargs)
        with patch("native_protection.json.loads", public_only):
            projected = project_native_source_metadata(encoded)
        self.assertNotIn("targets", projected)
        self.assertNotIn("outcome_samples", projected["audit_only"])
        self.assertEqual(native_source_tokens(row["audit_only"]), native_source_tokens(projected["audit_only"]))

    def test_native_registry_cannot_drop_original_or_native_alias_closure(self):
        with tempfile.TemporaryDirectory() as d:
            registry = protection_at(Path(d))
            for native in (False, True):
                bad = deepcopy(registry)
                for group, info in list(bad["components"].items()):
                    is_native = any(t.startswith("source_run_group:native-source-v1:") for t in info["tokens"])
                    if is_native is native: bad["components"].pop(group)
                with self.assertRaises(ValueError): prep.validate_registry(bad)
            bad = deepcopy(registry); bad["native_history"]["base_registry_sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "base_checksum"): prep.validate_registry(bad)

    def test_explicit_native_mode_requires_both_prepared_and_native_history_protection(self):
        config = json.loads((ROOT / "configs/data_pipeline.native-pilot.v1.json").read_text())
        with self.assertRaisesRegex(ValueError, "requires_historical_protection"):
            prep.prepare([], config)


@unittest.skipUnless(os.environ.get("NOSL_NATIVE_WORKER"), "set NOSL_NATIVE_WORKER for bounded live native collection integration")
class NativePilotLiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory()
        cls.root = Path(cls.directory.name)
        cls.protection = protection_at(cls.root / "old")
        cls.config = json.loads((ROOT / "configs/data_pipeline.native-pilot.v1.json").read_text())
        cls.student = load_config(ROOT / "configs/student.v2.engineering.json")
        request = {"op": "native_pilot_collect",
            "options": {"runs": 4, "maxFloors": 8, "maxRoots": 12, "maxRootsPerCombat": 1,
                        "seedPrefix": "nosl-native-pilot/fresh-admission-regression", "sourceRunPrefix": "fresh-admission-regression"},
            "teacherOptions": {"evaluationSeeds": [8801], "maxDecisions": 200},
            "collection": {"collectionId": "fresh-admission-regression", "protectionRegistrySha256": prep.registry_hash(cls.protection)}}
        run = subprocess.run([os.environ.get("DOTNET_ROOT", "") + "/dotnet", os.environ["NOSL_NATIVE_WORKER"]],
            input=json.dumps(request) + "\n", text=True, capture_output=True, timeout=120, check=True)
        cls.report = json.loads(run.stdout.splitlines()[-1])
        if os.environ.get("NOSL_NATIVE_REPORT"):
            Path(os.environ["NOSL_NATIVE_REPORT"]).write_text(json.dumps(cls.report))
        if "records" not in cls.report: raise AssertionError(cls.report)
        cls.rows = [r for r in cls.report["records"] if is_native_pilot(r)]
        if not cls.rows: raise AssertionError("The declared bounded source cohort has no certified root: " + json.dumps(cls.report.get("unsupportedPosteriorReasons")))
        cls.row = next((r for r in cls.rows if prep.has_usable_targets(r)), None)
        if cls.row is None: raise AssertionError("No usable root in the declared bounded source cohort")
        if any(run["error"] for run in cls.report["runs"]): raise AssertionError(cls.report["runs"])

    @classmethod
    def tearDownClass(cls): cls.directory.cleanup()

    def prepare(self, rows, **kwargs):
        return prep.prepare(rows, self.config, student_config=self.student, protection=self.protection, **kwargs)

    def test_live_source_teacher_export_protected_prepare_and_load_without_learning(self):
        for row in self.rows: validate_production_record(row, self.student, require_usable=False)
        splits, report, rejected = self.prepare(self.report["records"])
        self.assertGreater(report["accepted_roots"], 0)
        self.assertEqual(sum(len(s) for s in splits.values()), report["accepted_roots"])
        for row in self.rows:
            self.assertNotIn(row["audit_only"]["actual_seed"], json.dumps(row["public_input"]))
            self.assertEqual([], row["targets"]["pairwise"])
        output = self.root / "fresh"
        prep.persist_batch(output, self.report["records"], [{} for _ in self.report["records"]], self.config,
            "engineering-smoke", [{"live_native": True}], False, protection=self.protection)
        loaded = 0
        for split in ("train", "validation"):
            if prep.iter_dataset_paths(output, split):
                dataset = PreparedDatasetV2(output, split, self.student)
                loaded += len(dataset); dataset.verify_integrity()
        self.assertGreater(loaded, 0)
        with self.assertRaises(ValueError): PreparedDatasetV2(output, "test", self.student)
        self.assertFalse(prep.verify_manifest(output)["lock"].get("training_authorized", False))

    def test_old_envelopes_flags_profile_diagnostics_and_fresh_namespace_fail_closed(self):
        mutations = [lambda r: r.update(schema_version="nosl.native-belief-prototype.v4", record_kind="natural_development_teacher_candidate"),
            lambda r: r["audit_only"].update(trainable=False),
            lambda r: r["audit_only"].update(posterior_profile="unreviewed"),
            lambda r: r["audit_only"].update(actual_seed="nosl-m5-natural-proof-20261001:0"),
            lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"][0].update(hpEventDiagnosticsComplete=False),
            lambda r: r["audit_only"].update(native_public_history=[]),
            lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"][0].update(healingReceived=None),
            lambda r: r["targets"]["actions"][0].update(expected_final_hp=-1),
            lambda r: r["audit_only"].update(objective_calibrated=True),
            lambda r: r["audit_only"].update(combat_policy="unknown-policy"),
            lambda r: r["audit_only"].update(continuation_version="unknown-policy"),
            lambda r: r["audit_only"]["versions"].update(objective="unknown-objective")]
        for mutate in mutations:
            row = deepcopy(self.row); mutate(row)
            with self.assertRaises(ValueError): validate_production_record(row, self.student)
        default = json.loads((ROOT / "configs/data_pipeline.v2.json").read_text())
        self.assertIn("native_pilot_explicit_admission_required", prep.prepare([self.row], default, student_config=self.student)[1]["filter_reasons"])
        row = deepcopy(self.row); row["audit_only"]["native_collection"]["protection_registry_sha256"] = "0" * 64
        self.assertIn("native_pilot_protection_registry_mismatch", self.prepare([row])[1]["filter_reasons"])

    def test_malformed_diagnostics_quarantine_without_aborting_source_graph(self):
        for mutate in (lambda r: r["audit_only"].update(versions=[]),
                       lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"].__setitem__(0, 42),
                       lambda r: r["audit_only"].update(source_kind="constructed")):
            row = deepcopy(self.row); mutate(row)
            _, report, rejected = self.prepare([row])
            self.assertEqual(1, len(rejected))
            self.assertTrue(report["split_state"]["components"])

    def test_registered_source_and_public_aliases_are_excluded_even_if_renamed(self):
        metadata = {key: deepcopy(self.row[key]) for key in ("public_input", "audit_only")}
        registry = prep.extend_native_protection(self.protection, [metadata])
        row = deepcopy(self.row)
        row["audit_only"]["native_collection"]["protection_registry_sha256"] = prep.registry_hash(registry)
        for key in prep.PROVENANCE_FIELDS: row["audit_only"][key] = "renamed-" + key
        result = prep.prepare([row], self.config, student_config=self.student, protection=registry)
        self.assertEqual(0, result[1]["accepted_roots"])
        self.assertIn("native_pilot_previously_registered_source_or_alias", result[1]["filter_reasons"])
        # Public identity alone protects the root even under a genuinely different source identity.
        alias_only = deepcopy(row)
        audit = alias_only["audit_only"]
        audit["actual_seed"] = audit["native_collection"]["seed_namespace"] + ":999"
        audit["native_source_run_identity"] = source_run_identity(audit["actual_seed"])
        audit["native_source_battle_identity"] = source_battle_identity(audit["actual_seed"], audit["act"], audit["floor"], audit["encounter"])
        result = prep.prepare([alias_only], self.config, student_config=self.student, protection=registry)
        self.assertIn("native_pilot_previously_registered_source_or_alias", result[1]["filter_reasons"])
        # Source identity alone protects changed public roots from the same original run.
        source_only = deepcopy(row)
        source_only["public_input"]["observation"]["enemies"][0]["hp"] += 1
        result = prep.prepare([source_only], self.config, student_config=self.student, protection=registry)
        self.assertIn("native_pilot_previously_registered_source_or_alias", result[1]["filter_reasons"])
        # Invalid rows carry transitive edges before filtering; they cannot hide a public/source bridge.
        bridge = {"audit_only": {"source_kind": "natural", "actual_seed": self.row["audit_only"]["actual_seed"],
                                  "source_combat_id": "inspected-fixed200/battle-1"}}
        result = self.prepare([self.row, bridge])
        self.assertEqual(0, result[1]["accepted_roots"])
        self.assertIn("native_pilot_previously_registered_source_or_alias", result[1]["filter_reasons"])

    def test_late_invalid_bridge_durably_blocks_prepared_loader(self):
        output = self.root / "late-bridge"
        prep.persist_batch(output, [self.row], [{}], self.config, "engineering-smoke", [{"first": True}], False, protection=self.protection)
        bridge = {"audit_only": {"source_kind": "natural", "actual_seed": self.row["audit_only"]["actual_seed"],
                                 "source_combat_id": "inspected-fixed200/battle-1"}}
        prep.persist_batch(output, [bridge], [{}], self.config, "engineering-smoke", [{"bridge": True}], True)
        self.assertFalse(prep.verify_manifest(output)["isolation_passed"])
        for split in ("train", "validation"):
            with self.assertRaises(ValueError): PreparedDatasetV2(output, split, self.student)

    def test_posterior_versions_keep_old_profiles_and_reject_cross_version_or_encounter_claims(self):
        from nosl.native_pilot import LEGACY_PROFILES, ENCOUNTER_PROFILES, REVIEWED_ENCOUNTERS
        original = next(row for row in self.rows if row["audit_only"]["posterior_profile"] in LEGACY_PROFILES)
        for implementation in ("nosl-belief-dispatch-v6", "nosl-belief-dispatch-v7"):
            row = deepcopy(original)
            row["audit_only"].update(posterior_implementation=implementation, sampler_version=implementation)
            row["audit_only"]["versions"].update(sampler=implementation, posterior_implementation=implementation)
            validate_production_record(row, self.student, require_usable=False)
            for profile in ENCOUNTER_PROFILES:
                invalid = deepcopy(row); invalid["audit_only"]["posterior_profile"] = profile
                with self.assertRaisesRegex(ValueError, "certificate_metadata_invalid|encounter_profile_mismatch"):
                    validate_production_record(invalid, self.student, require_usable=False)
        for encounter in REVIEWED_ENCOUNTERS:
            row = deepcopy(original)
            audit = row["audit_only"]
            audit["encounter"] = encounter
            audit["native_source_battle_identity"] = source_battle_identity(audit["actual_seed"], audit["act"], audit["floor"], encounter)
            with self.assertRaisesRegex(ValueError, "encounter_profile_mismatch"):
                validate_production_record(row, self.student, require_usable=False)
        for original in self.rows:
            if original["audit_only"]["posterior_profile"] not in ENCOUNTER_PROFILES:
                continue
            validate_production_record(original, self.student, require_usable=False)
            invalid = deepcopy(original)
            invalid["audit_only"].update(posterior_implementation="nosl-belief-dispatch-v6", sampler_version="nosl-belief-dispatch-v6")
            invalid["audit_only"]["versions"].update(sampler="nosl-belief-dispatch-v6", posterior_implementation="nosl-belief-dispatch-v6")
            with self.assertRaisesRegex(ValueError, "certificate_metadata_invalid"):
                validate_production_record(invalid, self.student, require_usable=False)
        # Metadata-only branch probes. These do not certify a synthetic encounter,
        # validate a public choice envelope, persist rows, or create pilot data.
        from nosl.native_pilot import PROFILES_BY_IMPLEMENTATION, CHOICE_PROFILES, validate_native_pilot
        for implementation, profiles in PROFILES_BY_IMPLEMENTATION.items():
            for profile in profiles:
                row = deepcopy(self.row); audit = row["audit_only"]
                audit.update(posterior_implementation=implementation, sampler_version=implementation, posterior_profile=profile)
                audit["versions"].update(sampler=implementation, posterior_implementation=implementation)
                if profile in ENCOUNTER_PROFILES: audit["encounter"] = "CorpseSlugsWeak"
                audit["native_source_battle_identity"] = source_battle_identity(audit["actual_seed"], audit["act"], audit["floor"], audit["encounter"])
                choice = profile in CHOICE_PROFILES
                row["public_input"]["observation"]["choice"] = {"source": "Survivor"} if choice else None
                audit["native_import"] = "certified_owned_stable_origin_choice_replay_v1" if choice else "certified_detached_boundary_clone_v2"
                validate_native_pilot(row)
                audit["native_import"] = "certified_detached_boundary_clone_v2" if choice else "certified_owned_stable_origin_choice_replay_v1"
                with self.assertRaisesRegex(ValueError, "import_profile_mismatch"):
                    validate_native_pilot(row)
        for invalid_version in ("nosl-belief-dispatch-v8", [], None):
            row = deepcopy(original)
            row["audit_only"].update(posterior_implementation=invalid_version, sampler_version=invalid_version)
            with self.assertRaisesRegex(ValueError, "certificate_metadata_invalid"):
                validate_production_record(row, self.student, require_usable=False)

    def test_incomplete_candidate_mass_is_retained_without_point_targets(self):
        row = deepcopy(self.row)
        target = row["targets"]["actions"][0]
        allocated = target["allocated_worlds"]
        for key in target["masks"]: target["masks"][key] = False; target[key] = None
        target.update(quality="unresolved", completed_worlds=0, truncated_worlds=allocated, error_worlds=0, other_worlds=0)
        audit = row["audit_only"]
        audit["outcome_samples"][0]["outcomes"] = [{"terminalKind": "ComputeTruncated"} for _ in range(allocated)]
        audit["n_unresolved"] += allocated
        audit["costs"]["worlds_completed"] -= allocated
        audit["native_diagnostics"]["settled_worlds"] -= allocated
        audit["native_diagnostics"]["unresolved_worlds"] += allocated
        validate_production_record(row, self.student)
        splits, report, _ = self.prepare([row])
        self.assertEqual(1, report["accepted_roots"])
        retained = next(s[0] for s in splits.values() if s)
        self.assertEqual(target, retained["targets"]["actions"][0])
        bad = deepcopy(row); bad["targets"]["actions"][0]["masks"]["win_probability"] = True
        bad["targets"]["actions"][0]["win_probability"] = 0
        with self.assertRaises(ValueError): validate_production_record(bad, self.student)


if __name__ == "__main__": unittest.main()
