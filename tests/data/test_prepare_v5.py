"""Synthetic engineering fixtures only; no native generation, backward or fit."""
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
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/python")]
from test_public_map_v5 import public_v5, graph_payload
from nosl.data import canonical_object_digest, REGISTRY_VERSION
from nosl.data_v5 import PreparedDatasetV5, validate_production_record
from nosl.native_pilot import source_run_identity
from nosl.native_v5 import (ADMISSION_PROFILE, ADMISSION_SCHEMA, ADAPTER_VERSION, CANDIDATE_SCHEMA,
    PINNED_VERSIONS, RAW_CANDIDATE_SCHEMA, RAW_CANDIDATE_KIND, SAMPLER, POSTERIOR,
    adapt_native_candidates, adapt_native_attempts, candidate_digest, validate_candidate, validate_admission)
from nosl.prepare_v5 import (canonical_bytes, choose_split, export_protection, implementation_fingerprint,
    persist_snapshot, prepare, public_metadata, candidate_projection, verify_snapshot)
from nosl.protection_v5 import component_id, extend_protection, group_records, record_tokens, validate_protection
from nosl.public_identity_v5 import PUBLIC_IDENTITY_SCHEME, public_input_digest
from nosl.schema import HEADS
from nosl.schema_v5 import load_config, loads

STUDENT = load_config(ROOT / "configs/student.v5.engineering.json")
PIPELINE = loads((ROOT / "configs/data_pipeline.v5.json").read_text())


def legacy_protection():
    tokens = ["source_run_group:old-engineering-fixture"]
    return {"schema_version": REGISTRY_VERSION, "public_identity_scheme": "nosl.public-identity.v2",
            "source": {"manifest_sha256": "a" * 64, "split_state_sha256": "b" * 64,
                       "versions": {"fixture": "synthetic"}, "frozen_test_shards": [{"sha256": "c" * 64, "bytes": 0, "rows": 494}]},
            "components": {component_id(tokens): {"split": "test", "tokens": tokens}}}


def raw_fixture(number=0):
    public = public_v5()
    def shift(value):
        if isinstance(value, dict):
            if "enemies" in value:
                for enemy in value["enemies"]: enemy["hp"] += number; enemy["maxHp"] += number
            for child in value.values(): shift(child)
        elif isinstance(value, list):
            for child in value: shift(child)
    shift(public)
    start = public["observation"]["startHp"]
    rows, samples = [], []
    for index in range(len(public["candidate_actions"])):
        hp = start - index % 4
        outcome = {"terminalKind": "Win", "settlementComplete": True, "hpEventDiagnosticsComplete": True,
            "resourceProvenanceComplete": True, "inventorySnapshotsComplete": True, "permanentChangesComplete": True,
            "hpAtCombatStart": start, "maxHpStart": 70, "hpAfterSettlement": hp, "maxHpAfterSettlement": 70,
            "cumulativeHpDamage": start - hp, "healingReceived": 0, "otherHpAdjustment": 0, "playerAlive": True,
            "settlementProfileId": PINNED_VERSIONS["endpoint"], "continuationPolicyId": PINNED_VERSIONS["continuation"],
            "inventoryStart": [], "inventoryEnd": [], "resourceEvents": [], "permanentChanges": []}
        target = {"action_index": index, "quality": "complete", "masks": {h: True for h in HEADS},
                  "value": 0, "win_probability": 1, "death_probability": 0, "expected_final_hp": hp,
                  "hp_distribution": [{"hp": hp, "probability": 1}], "potion_net_change": 0,
                  "allocated_worlds": 2, "completed_worlds": 2, "truncated_worlds": 0, "error_worlds": 0, "other_worlds": 0}
        rows.append(target); samples.append({"action_index": index, "world_seeds": [501, 502], "outcomes": [deepcopy(outcome), deepcopy(outcome)]})
    prior = {"schemaVersion": "nosl.native-map-rewards-state-tape-prior.v1", "eligibleCombats": 3, "eligibleDecisionsPerCombat": 8,
             "execution": {"publicContextProfile": "nosl.public-run-context.v1", "publicEvidenceProfile": "nosl.public-run-evidence.v2",
                           "publicMapObservationProfile": "nosl.public-map-complete-graph.v1", "sourcePolicyId": "nosl-public-rules-v2",
                           "outsideCombatScript": "nosl-natural-public-script-v3", "maxFloors": 8, "sourceDecisionHorizon": 1024}}
    dependencies = {"upstream_commit": PINNED_VERSIONS["simulator"], "wrapper_source_sha256": "1" * 64,
                    "vendor_source_sha256": "2" * 64, "worker_assembly_sha256": "3" * 64, "core_assembly_sha256": "4" * 64,
                    "runtime_version": "synthetic-engineering-runtime-fixture"}
    seed = "synthetic-v5-engineering:" + str(number)
    versions = {**PINNED_VERSIONS, "sampler": SAMPLER, "posterior_profile": POSTERIOR,
                "source_prior": "5" * 64}
    audit = {"purpose": "engineering-fixture", "source_kind": "synthetic_engineering_fixture", "collection_id": "synthetic-engineering",
        "draw_id": "draw-" + str(number), "source_draw_seed": 9000 + number, "selected_combat_index": 1, "selected_decision_index": 0,
        "source_run_group": "synthetic-run-" + str(number), "source_combat_id": "synthetic-combat-" + str(number),
        "branch_family": "synthetic-family-" + str(number), "actual_seed": seed, "native_source_run_identity": source_run_identity(seed),
        "public_state_digest": canonical_object_digest(public), "native_run": True, "native_default_start": True,
        "public_map_observation_profile": PINNED_VERSIONS["map_profile"], "map_marginalization_contract": PINNED_VERSIONS["map_support"], "trainable": False, "formal_labels": False,
        "objective_calibrated": False, "source_seed_conditioning": False, "independent_final_evaluation": True,
        "declared_prior": prior, "source_prior_identity": "5" * 64, "runtime_dependencies": dependencies,
        "build_receipt_sha256": hashlib.sha256(canonical_bytes(dependencies)).hexdigest(), "versions": versions,
        "sampler_seeds": [501, 502], "exploration_seeds": [], "n_exploration": 0, "n_independent_eval": 2,
        "n_error": 0, "n_unresolved": 0, "outcome_samples": samples,
        "costs": {"root_candidates": len(rows), "worlds_allocated": 2 * len(rows), "worlds_completed": 2 * len(rows),
                  "rollout_decisions": 25, "elapsed_seconds": 1.0, "clone_seconds": .2, "settlement_seconds": .1, "peak_worker_memory_bytes": 1000}}
    return {"schema_version": RAW_CANDIDATE_SCHEMA, "record_kind": RAW_CANDIDATE_KIND, "public_input": public,
            "targets": {"actions": rows, "pairwise": [], "equivalent_action_set": []}, "audit_only": audit}


def fixture(number=0):
    return adapt_native_candidates(canonical_bytes(raw_fixture(number)) + b"\n", STUDENT)[0]


def cohort(rows, protection=None):
    protection = protection or legacy_protection()
    attempts = [{"draw_id": r["audit_only"]["draw_id"], "source_draw_seed": r["audit_only"]["source_draw_seed"],
                 "status": "recorded", "record_digest": candidate_digest(r), "detail": None, "provenance": public_metadata(r)} for r in rows]
    dependencies = rows[0]["audit_only"]["runtime_dependencies"]
    receipt = {"schema_version": ADMISSION_SCHEMA, "profile": ADMISSION_PROFILE, "purpose": rows[0]["audit_only"]["purpose"],
               "collection_id": rows[0]["audit_only"]["collection_id"], "records_sha256": canonical_object_digest(rows),
               "candidate_digests": [candidate_digest(r) for r in rows], "candidate_metadata_sha256": canonical_object_digest([candidate_projection(r) for r in rows]), "attempts_sha256": canonical_object_digest(attempts),
               "protection_sha256": canonical_object_digest(protection), "student_config_sha256": canonical_object_digest(STUDENT),
               "versions": deepcopy(rows[0]["audit_only"]["versions"]), "predeclared_source_draw_seeds": [a["source_draw_seed"] for a in attempts],
               "selection": "predeclared_all_attempts_no_replacement", "budget_expired": False,
               "review": {"decision": "accepted", "evidence_kind": "synthetic_contract_only" if rows[0]["audit_only"]["purpose"] == "engineering-fixture" else "predeclared_fresh_cohort_quality", "evidence_sha256": "7" * 64},
               "build_receipt_json": canonical_bytes(dependencies).decode(), "build_receipt_sha256": hashlib.sha256(canonical_bytes(dependencies)).hexdigest(),
               "minimum_complete_roots": 0, "minimum_positive_decision_roots": 0, "minimum_later_combat_roots": 0}
    return attempts, receipt


def later_decision_engineering_fixture(number=0, *, later_missing_map=False, return_raw=False):
    """Synthetic wire-contract exercise, never a qualifying real native cohort."""
    raw = raw_fixture(number); public = raw["public_input"]; events = public["public_evidence"]["events"]
    current_owner = events[-1]["ownerOrdinal"]
    start = next(i for i, e in enumerate(events) if e["ownerOrdinal"] == current_owner)
    first_owner = next(e["ownerOrdinal"] for e in events if e["payload"].get("ownerKind") == "combat")
    inserted = deepcopy([e for e in events if e["ownerOrdinal"] == first_owner])
    for i, event in enumerate(inserted):
        event["eventOrdinal"] = start + i; event["ownerOrdinal"] = current_owner
        if event["payload"]["kind"] == "owner_started": event["payload"]["floor"] = 2
    for event in events[start:]:
        event["eventOrdinal"] += len(inserted); event["ownerOrdinal"] += 1
        for field in ("historyThroughEventOrdinal", "decisionEventOrdinal"):
            if field in event["payload"]: event["payload"][field] += len(inserted)
    events[start:start] = inserted
    public["observation"]["runContext"]["combatEntryIndex"] = 2
    events[-1]["payload"]["observation"]["runContext"]["combatEntryIndex"] = 2
    offered = events[-1]; action = deepcopy(public["candidate_actions"][0])
    public["observation"]["history"].append({"kind": "action", "detail": json.dumps(action)})
    for candidate in public["candidate_actions"]: candidate["revision"] = 1
    events.append({"eventOrdinal": len(events), "ownerOrdinal": current_owner + 1,
                   "payload": {"kind": "combat_action", "decisionEventOrdinal": offered["eventOrdinal"], "action": action}})
    observation = deepcopy(public["observation"]); observation["history"] = []
    events.append({"eventOrdinal": len(events), "ownerOrdinal": current_owner + 1,
                   "payload": {"kind": "combat_decision", "status": "player_decision", "historyThroughEventOrdinal": len(events) - 1,
                               "historyCompleteFromCombatStart": True, "observation": observation, "actions": deepcopy(public["candidate_actions"])}})
    raw["audit_only"].update(selected_combat_index=2, selected_decision_index=1, purpose="bounded-pilot",
                            source_kind="natural_under_explicit_label_tape_prior", public_state_digest=canonical_object_digest(public))
    if later_missing_map:
        current_owner = events[-1]["ownerOrdinal"]
        start = next(i for i, e in enumerate(events) if e["ownerOrdinal"] == current_owner)
        payload = graph_payload("missing")
        inserted = [{"eventOrdinal": start, "ownerOrdinal": current_owner,
                     "payload": {"kind": "owner_started", "ownerKind": "map", "actIndex": 1, "floor": 17,
                                 "parentOwnerOrdinal": None, "completeFromOwnerStart": True}},
                    {"eventOrdinal": start + 1, "ownerOrdinal": current_owner, "payload": payload},
                    {"eventOrdinal": start + 2, "ownerOrdinal": current_owner,
                     "payload": {"kind": "map_chosen", "offerEventOrdinal": start + 1, "coordinate": payload["options"][0]["coordinate"]}},
                    {"eventOrdinal": start + 3, "ownerOrdinal": current_owner,
                     "payload": {"kind": "owner_ended", "outcome": "completed", "assets": None}}]
        for event in events[start:]:
            event["eventOrdinal"] += len(inserted); event["ownerOrdinal"] += 1
            for field in ("historyThroughEventOrdinal", "decisionEventOrdinal"):
                if field in event["payload"]: event["payload"][field] += len(inserted)
            if event["payload"]["kind"] == "owner_started": event["payload"].update(actIndex=1, floor=18)
            if event["payload"]["kind"] == "combat_decision":
                event["payload"]["observation"]["runContext"].update(actIndex=1, floor=18)
        events[start:start] = inserted
        public["observation"]["runContext"].update(actIndex=1, floor=18)
        raw["audit_only"]["public_state_digest"] = canonical_object_digest(public)
        raw["audit_only"]["declared_prior"]["execution"]["maxFloors"] = 30
    return raw if return_raw else adapt_native_candidates(canonical_bytes(raw), STUDENT)[0]


class PrepareV5Tests(unittest.TestCase):
    def test_attempt_draw_seed_contradictions_never_erase_observed_protection(self):
        row = fixture()
        for status in ("recorded", "absent", "failed", "not_executed"):
            with self.subTest(status=status):
                attempts, receipt = cohort([row])
                attempt = attempts[0] if status == "recorded" else {
                    "draw_id": "synthetic-" + status, "source_draw_seed": 99001, "status": status,
                    "record_digest": None, "detail": "synthetic", "provenance": public_metadata(row)}
                if status != "recorded": attempts.append(attempt)
                attempt["provenance"]["audit_only"]["source_draw_seed"] = 99999
                receipt.update(attempts_sha256=canonical_object_digest(attempts),
                               predeclared_source_draw_seeds=[a["source_draw_seed"] for a in attempts])
                original = deepcopy(attempts)
                with self.assertRaisesRegex(ValueError, "attempt_source_draw_seed_mismatch"):
                    validate_admission(receipt, [row], attempts, canonical_object_digest(legacy_protection()), STUDENT)
                with self.assertRaisesRegex(ValueError, "attempt_source_draw_seed_mismatch"):
                    prepare([row], attempts, receipt, legacy_protection(), PIPELINE, STUDENT)
                self.assertEqual(original, attempts)
                raw_attempts = [{**a, "raw_record_index": 0 if a["status"] == "recorded" else None}
                                for a in attempts]
                for a in raw_attempts: a.pop("record_digest")
                with self.assertRaisesRegex(ValueError, "attempt_source_draw_seed_mismatch"):
                    adapt_native_attempts(canonical_bytes(raw_attempts), [row])

    def test_admission_attempt_source_draw_seed_requires_integer_not_boolean(self):
        row = fixture(); attempts, receipt = cohort([row])
        attempts.append({"draw_id": "bool-seed", "source_draw_seed": True, "status": "absent",
                         "record_digest": None, "detail": None, "provenance": {"audit_only": {
                             "actual_seed": "synthetic-bool-seed", "native_run": True}}})
        receipt.update(attempts_sha256=canonical_object_digest(attempts),
                       predeclared_source_draw_seeds=[row["audit_only"]["source_draw_seed"], 1])
        with self.assertRaises(ValueError):
            validate_admission(receipt, [row], attempts, canonical_object_digest(legacy_protection()), STUDENT)

    def test_failed_attempt_source_draw_bridge_blocks_direct_dispatch(self):
        row = fixture()
        old_token = "source_run_group:native-tape-source-draw-v1:99999"
        protection = legacy_protection()
        protection["components"][component_id([old_token])] = {"split": "test", "tokens": [old_token]}
        attempts, receipt = cohort([row], protection)
        attempts.append({"draw_id": "failed-bridge", "source_draw_seed": 99999, "status": "failed", "record_digest": None,
                         "detail": "synthetic failed source", "provenance": {"audit_only": {
                             "actual_seed": "synthetic-failed-bridge", "native_run": True,
                             "branch_family": row["audit_only"]["branch_family"]}}})
        receipt.update(attempts_sha256=canonical_object_digest(attempts),
                       predeclared_source_draw_seeds=[row["audit_only"]["source_draw_seed"], 99999])
        original = deepcopy(attempts)
        self.assertEqual(0, prepare([row], attempts, receipt, protection, PIPELINE, STUDENT)[-1]["usable_roots"])
        with self.assertRaisesRegex(ValueError, "previously_observed"):
            validate_production_record(row, STUDENT, admission=receipt, cohort_records=[row],
                                       attempts=attempts, protection=protection)
        self.assertEqual(original, attempts)

    def test_absent_attempt_cross_split_bridge_blocks_entire_cohort_dispatch(self):
        row = later_decision_engineering_fixture()
        protection = legacy_protection()
        train_token = "branch_family:historical-train-branch"
        protection["components"][component_id([train_token])] = {"split": "train", "tokens": [train_token]}
        attempts, receipt = cohort([row], protection)
        attempts.append({"draw_id": "absent-cross-split-bridge", "source_draw_seed": 99999, "status": "absent",
                         "record_digest": None, "detail": "synthetic absent source", "provenance": {"audit_only": {
                             "actual_seed": "synthetic-absent-bridge", "native_run": True,
                             "source_run_group": "old-engineering-fixture", "branch_family": "historical-train-branch"}}})
        receipt.update(attempts_sha256=canonical_object_digest(attempts),
                       predeclared_source_draw_seeds=[row["audit_only"]["source_draw_seed"], 99999],
                       minimum_complete_roots=1, minimum_positive_decision_roots=1, minimum_later_combat_roots=1)
        validate_admission(receipt, [row], attempts, canonical_object_digest(protection), STUDENT)
        with self.assertRaisesRegex(ValueError, "pilot_cohort_contains_protected_source"):
            prepare([row], attempts, receipt, protection, PIPELINE, STUDENT)
        with self.assertRaisesRegex(ValueError, "cross_split_bridge"):
            validate_production_record(row, STUDENT, admission=receipt, cohort_records=[row],
                                       attempts=attempts, protection=protection)


    def representatives(self):
        found = {}
        for i in range(70):
            row = fixture(i)
            group = group_records([row], legacy_protection()["components"])[0][0]
            found.setdefault(choose_split(group, PIPELINE), row)
            if len(found) == 3: return found
        self.fail("missing synthetic split representatives")

    def persist(self, root, rows, protection=None):
        protection = protection or legacy_protection()
        attempts, receipt = cohort(rows, protection)
        return persist_snapshot(root, rows, attempts, receipt, protection, PIPELINE, STUDENT)


    def test_later_act_missing_map_preserves_complete_context_and_admission(self):
        raw = later_decision_engineering_fixture(91, later_missing_map=True, return_raw=True)
        row = adapt_native_candidates(canonical_bytes(raw), STUDENT)[0]
        self.assertEqual(raw["public_input"], row["public_input"])
        public = row["public_input"]
        maps = [e for e in public["public_evidence"]["events"] if e["payload"]["kind"] == "map"]
        self.assertEqual(["complete", "missing"], [e["payload"]["currentMap"]["status"] for e in maps])
        self.assertEqual({"status": "missing", "nodes": [], "edges": [], "startingNode": None, "bossNodes": []}, maps[-1]["payload"]["currentMap"])
        self.assertTrue(public["public_evidence"]["completeFromRunStart"])
        self.assertEqual(1, public["observation"]["runContext"]["actIndex"])
        attempts, receipt = cohort([row])
        receipt.update(minimum_complete_roots=1, minimum_positive_decision_roots=1, minimum_later_combat_roots=1)
        self.assertIs(validate_production_record(row, STUDENT, admission=receipt, cohort_records=[row], attempts=attempts, protection=legacy_protection()), row)
        splits, _, _, _, report = prepare([row], attempts, receipt, legacy_protection(), PIPELINE, STUDENT)
        self.assertEqual(1, report["usable_roots"])
        self.assertEqual(public, next(r["public_input"] for group in splits.values() for r in group))
        changed = deepcopy(row)
        changed_maps = [e for e in changed["public_input"]["public_evidence"]["events"] if e["payload"]["kind"] == "map"]
        changed_maps[-1]["payload"]["currentMap"] = graph_payload("complete")["currentMap"]
        self.assertNotEqual(public_input_digest(public), public_input_digest(changed["public_input"]))
        with self.assertRaisesRegex(ValueError, "conditioning_digest"):
            validate_candidate(changed, STUDENT)

    def test_later_missing_never_weakens_initial_map_or_evidence_boundary(self):
        raw = later_decision_engineering_fixture(92, later_missing_map=True, return_raw=True)
        def maps(value): return [e for e in value["public_input"]["public_evidence"]["events"] if e["payload"]["kind"] == "map"]
        mutations = [lambda r: maps(r)[0]["payload"].update(currentMap=graph_payload("missing")["currentMap"]),
                     lambda r: maps(r)[-1]["payload"].pop("currentMap"),
                     lambda r: maps(r)[-1]["payload"].update(currentMap=None),
                     lambda r: maps(r)[-1]["payload"]["currentMap"].update(nodes=graph_payload()["nodes"]),
                     lambda r: r["public_input"]["public_evidence"].update(completeFromRunStart=False),
                     lambda r: r["public_input"].update(history_complete=False)]
        for mutate in mutations:
            changed = deepcopy(raw); mutate(changed)
            with self.assertRaises(ValueError): adapt_native_candidates(canonical_bytes(changed), STUDENT)
        changed = deepcopy(raw)
        initial_owner = next(e for e in changed["public_input"]["public_evidence"]["events"]
                             if e["payload"].get("ownerKind") == "map")
        initial_owner["payload"]["actIndex"] = 1
        with self.assertRaisesRegex(ValueError, "initial_act0_complete_map"):
            adapt_native_candidates(canonical_bytes(changed), STUDENT)

    def test_synthetic_wire_fixture_exercises_future_pilot_positive_path(self):
        row = later_decision_engineering_fixture(85)
        attempts, receipt = cohort([row])
        receipt.update(minimum_complete_roots=1, minimum_positive_decision_roots=1, minimum_later_combat_roots=1)
        stats = validate_admission(receipt, [row], attempts, canonical_object_digest(legacy_protection()), STUDENT)
        self.assertEqual(1, stats["later_combat_complete_roots"])
        self.assertIs(validate_production_record(row, STUDENT, admission=receipt, cohort_records=[row], attempts=attempts, protection=legacy_protection()), row)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"
            manifest = persist_snapshot(root, [row], attempts, receipt, legacy_protection(), PIPELINE, STUDENT)
            self.assertFalse(manifest["report"]["fit_authorized"])
            self.assertFalse(manifest["report"]["formal_training_ready"])
        bad = deepcopy(row); bad["audit_only"]["selected_decision_index"] = 2
        with self.assertRaisesRegex(ValueError, "coordinate"): validate_candidate(bad, STUDENT)

    def test_provenance_audit_never_accepts_targets_or_outcomes(self):
        for key in ("targets", "outcome_samples"):
            metadata = public_metadata(fixture()); metadata["audit_only"][key] = []
            with self.assertRaisesRegex(ValueError, "metadata_only"):
                extend_protection(legacy_protection(), [metadata])
            rows = [fixture()]; attempts, receipt = cohort(rows)
            attempts[0]["provenance"] = metadata; receipt["attempts_sha256"] = canonical_object_digest(attempts)
            with self.assertRaisesRegex(ValueError, "metadata_only"):
                validate_admission(receipt, rows, attempts, canonical_object_digest(legacy_protection()), STUDENT)

    def test_snapshot_cannot_prune_reviewed_rows_with_new_exclusion_claim(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, list(self.representatives().values()))
            path = root / "metadata.json"; metadata = loads(path.read_text())
            metadata[0]["exclusion_reason"] = "caller_wants_to_drop_this_root"
            path.write_bytes(canonical_bytes(metadata))
            m = loads((root / "manifest.json").read_text())
            m["files"]["metadata"].update(sha256=hashlib.sha256(path.read_bytes()).hexdigest(), bytes=path.stat().st_size)
            (root / "manifest.json").write_bytes(canonical_bytes(m))
            with self.assertRaisesRegex(ValueError, "exclusion_reason"):
                verify_snapshot(root)

    def test_reviewed_projection_prevents_opaque_test_metadata_tampering(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, list(self.representatives().values()))
            path = root / "metadata.json"; metadata = loads(path.read_text())
            metadata[0]["accounting"]["complete"] = False
            path.write_bytes(canonical_bytes(metadata))
            m = loads((root / "manifest.json").read_text())
            m["files"]["metadata"].update(sha256=hashlib.sha256(path.read_bytes()).hexdigest(), bytes=path.stat().st_size)
            (root / "manifest.json").write_bytes(canonical_bytes(m))
            with self.assertRaisesRegex(ValueError, "reviewed_metadata"):
                verify_snapshot(root)

    def test_export_preserves_new_validation_and_test_split_owners(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, list(self.representatives().values()))
            _, _, metadata, _ = verify_snapshot(root); protected = export_protection(root)
            owners = {t: c["split"] for c in protected["components"].values() for t in c["tokens"]}
            for row in metadata:
                for token in record_tokens(row["provenance"]): self.assertEqual(row["split"], owners[token])

    def test_complete_native_attempt_adapter_and_absent_sources_stay_protected(self):
        row = fixture(); raw = [{"draw_id": row["audit_only"]["draw_id"], "source_draw_seed": row["audit_only"]["source_draw_seed"],
                "status": "recorded", "raw_record_index": 0, "detail": None, "provenance": public_metadata(row)},
               {"draw_id": "synthetic-absent", "source_draw_seed": 99999, "status": "absent", "raw_record_index": None,
                "detail": "synthetic absent root", "provenance": {"audit_only": {"actual_seed": "synthetic-absent-seed", "native_run": True, "source_kind": "synthetic_engineering_fixture"}}}]
        attempts = adapt_native_attempts(canonical_bytes(raw), [row])
        _, receipt = cohort([row]); receipt.update(attempts_sha256=canonical_object_digest(attempts), predeclared_source_draw_seeds=[row["audit_only"]["source_draw_seed"], 99999])
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; persist_snapshot(root, [row], attempts, receipt, legacy_protection(), PIPELINE, STUDENT)
            protection = export_protection(root)
            self.assertTrue(record_tokens(raw[1]["provenance"]) <= {t for c in protection["components"].values() for t in c["tokens"]})
        with self.assertRaisesRegex(ValueError, "unaccounted"):
            adapt_native_attempts(canonical_bytes(raw[1:]), [row])
        raw[1]["provenance"] = {}
        with self.assertRaisesRegex(ValueError, "source_provenance"):
            adapt_native_attempts(canonical_bytes(raw), [row])

    def test_cohort_wide_source_eval_seed_overlap_rejects(self):
        rows = [fixture(1), fixture(2)]
        rows[1]["audit_only"]["source_draw_seed"] = rows[0]["audit_only"]["sampler_seeds"][0]
        attempts, receipt = cohort(rows)
        with self.assertRaisesRegex(ValueError, "seed_overlap"):
            validate_admission(receipt, rows, attempts, canonical_object_digest(legacy_protection()), STUDENT)

    def test_adapter_preserves_full_context_and_outcomes_masks_only_utility(self):
        raw = raw_fixture(); encoded = canonical_bytes(raw) + b"\n"
        row = adapt_native_candidates(encoded, STUDENT)[0]
        self.assertEqual(raw["public_input"], row["public_input"])
        self.assertEqual(raw["audit_only"]["outcome_samples"][0]["outcomes"], row["audit_only"]["outcome_samples"][0]["outcomes"])
        self.assertFalse(row["targets"]["actions"][0]["masks"]["value"])
        self.assertEqual(hashlib.sha256(encoded).hexdigest(), row["audit_only"]["source_artifact_sha256"])
        self.assertIs(validate_candidate(row, STUDENT), row)

    def test_old_diagnostic_or_raw_source_envelopes_never_promote(self):
        for schema in ("nosl.native-map-rewards-tape-replay-development.v1", "nosl.natural-source.v5", "nosl.public-map-complete-graph.engineering.v1"):
            raw = raw_fixture(); raw["schema_version"] = schema
            with self.assertRaisesRegex(ValueError, "no_diagnostic_promotion"):
                adapt_native_candidates(canonical_bytes(raw), STUDENT)
        with self.assertRaisesRegex(ValueError, "explicit_cohort"):
            validate_production_record(fixture(), STUDENT)

    def test_duplicate_json_key_and_nonfinite_reject_at_adapter(self):
        raw = canonical_bytes(raw_fixture()).decode()
        with self.assertRaises(ValueError): adapt_native_candidates(raw.replace('"schema_version":', '"schema_version":"duplicate","schema_version":', 1).encode(), STUDENT)
        with self.assertRaises(ValueError): adapt_native_candidates(raw.replace('"hp":56', '"hp":NaN', 1).encode(), STUDENT)

    def test_graph_identity_stale_or_stripped_labels_reject(self):
        row = fixture(); graph = row["public_input"]["public_evidence"]["events"][2]["payload"]["currentMap"]
        graph["nodes"][3]["nodeType"] = "rest"
        with self.assertRaisesRegex(ValueError, "conditioning_digest"):
            validate_candidate(row, STUDENT)
        row = fixture(); row["public_input"].pop("public_evidence")
        with self.assertRaises(ValueError): validate_candidate(row, STUDENT)

    def test_outcome_seed_root_and_empirical_masks_are_checked(self):
        mutations = [lambda r: r["audit_only"]["outcome_samples"][0]["world_seeds"].reverse(),
                     lambda r: r["audit_only"]["outcome_samples"][0]["world_seeds"].__setitem__(1, 501),
                     lambda r: r["audit_only"]["outcome_samples"][0].update(conditioned_public_input_digest="0" * 64),
                     lambda r: r["targets"]["actions"][0].update(expected_final_hp=1),
                     lambda r: r["targets"]["actions"][0]["masks"].update(value=True),
                     lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"].pop(),
                     lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"][0].update(settlementComplete=False),
                     lambda r: r["audit_only"]["costs"].update(worlds_completed=1)]
        for mutate in mutations:
            row = fixture(); mutate(row)
            with self.assertRaises(ValueError): validate_candidate(row, STUDENT)

    def test_unresolved_allocated_mass_is_retained_and_never_normalized(self):
        row = fixture(); outcome = row["audit_only"]["outcome_samples"][0]["outcomes"][0]
        outcome.update(terminalKind="ComputeTruncated", settlementComplete=False)
        row["audit_only"].update(n_unresolved=1); row["audit_only"]["costs"]["worlds_completed"] -= 1
        target = row["targets"]["actions"][0]
        target.update(completed_worlds=1, truncated_worlds=1, quality="unresolved")
        for h in HEADS: target[h] = None; target["masks"][h] = False
        validate_candidate(row, STUDENT)
        attempts, receipt = cohort([row])
        result = prepare([row], attempts, receipt, legacy_protection(), PIPELINE, STUDENT)
        self.assertEqual(1, result[-1]["usable_roots"])
        target.update(expected_final_hp=56); target["masks"]["expected_final_hp"] = True
        with self.assertRaises(ValueError): validate_candidate(row, STUDENT)

    def test_admission_binds_all_inputs_attempts_and_build_receipt(self):
        rows = [fixture()]; attempts, receipt = cohort(rows)
        validate_admission(receipt, rows, attempts, canonical_object_digest(legacy_protection()), STUDENT)
        for key, value in (("records_sha256", "0" * 64), ("attempts_sha256", "0" * 64), ("student_config_sha256", "0" * 64),
                           ("protection_sha256", "0" * 64), ("minimum_complete_roots", 999), ("build_receipt_json", "{}")):
            bad = deepcopy(receipt); bad[key] = value
            with self.assertRaises(ValueError): validate_admission(bad, rows, attempts, canonical_object_digest(legacy_protection()), STUDENT)
        bad = deepcopy(receipt); bad["predeclared_source_draw_seeds"].append(99999)
        with self.assertRaisesRegex(ValueError, "denominator"):
            validate_admission(bad, rows, attempts, canonical_object_digest(legacy_protection()), STUDENT)

    def test_future_reviewed_sampler_version_is_not_permanently_rejected(self):
        raw = raw_fixture(); raw["audit_only"]["versions"].update(sampler=SAMPLER.replace("v1-public", "v2-public"), posterior_profile=POSTERIOR.replace("v1-public", "v2-public"))
        row = adapt_native_candidates(canonical_bytes(raw), STUDENT)[0]
        attempts, receipt = cohort([row])
        validate_admission(receipt, [row], attempts, canonical_object_digest(legacy_protection()), STUDENT)
        bad = deepcopy(receipt); bad["versions"]["sampler"] = SAMPLER
        with self.assertRaises(ValueError): validate_admission(bad, [row], attempts, canonical_object_digest(legacy_protection()), STUDENT)

    def test_synthetic_fixture_cannot_load_as_bounded_pilot(self):
        rows = self.representatives()
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, list(rows.values()))
            with self.assertRaisesRegex(ValueError, "purpose"):
                PreparedDatasetV5(root, "train", STUDENT)
            self.assertEqual(1, len(PreparedDatasetV5(root, "train", STUDENT, purpose="engineering-fixture")))

    def test_snapshot_preparation_loader_and_opaque_test_protection(self):
        rows = self.representatives()
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; manifest = self.persist(root, list(rows.values()))
            opaque = {(root / name).resolve() for name in ("test.jsonl", "candidates.json", "quarantine.jsonl")}
            original = Path.open
            def guard(path, mode="r", *args, **kwargs):
                if path.resolve() in opaque and "b" not in mode: raise AssertionError("test targets decoded")
                return original(path, mode, *args, **kwargs)
            with patch.object(Path, "open", guard):
                data = PreparedDatasetV5(root, "train", STUDENT, purpose="engineering-fixture")
                data.verify_integrity(); protection = export_protection(root)
            self.assertEqual(1, len(data)); validate_protection(protection)
            self.assertFalse(manifest["report"]["fit_authorized"])
            self.assertFalse(manifest["report"]["formal_training_ready"])
            with self.assertRaisesRegex(ValueError, "never_loads_test"):
                PreparedDatasetV5(root, "test", STUDENT, purpose="engineering-fixture")
            with self.assertRaises(FileExistsError): self.persist(root, list(rows.values()))
            data.records[0]["targets"]["actions"][0]["expected_final_hp"] = 0
            with self.assertRaises(ValueError): data.verify_integrity()

    def test_protected_context_aliases_and_malformed_bridge_exclude(self):
        old, fresh = fixture(70), fixture(71)
        protection = extend_protection(legacy_protection(), [public_metadata(old)])
        bad = public_metadata(old); bad["public_input"].pop("public_evidence")
        bad["audit_only"].update(public_metadata(fresh)["audit_only"])
        attempts, receipt = cohort([fresh], protection)
        attempts[0]["provenance"] = bad; receipt["attempts_sha256"] = canonical_object_digest(attempts)
        result = prepare([fresh], attempts, receipt, protection, PIPELINE, STUDENT)
        self.assertEqual(0, result[-1]["usable_roots"])
        self.assertEqual("previously_observed_source_or_alias", result[1][0]["reason"])

    def test_protection_rejects_target_metadata_and_removed_aliases(self):
        row = public_metadata(fixture())
        with self.assertRaises(ValueError): extend_protection(legacy_protection(), [{**row, "targets": {}}])
        protection = extend_protection(legacy_protection(), [row])
        protection["components"].pop(next(iter(protection["components"])))
        with self.assertRaises(ValueError): validate_protection(protection)

    def test_snapshot_detects_file_tamper_and_path_escape(self):
        rows = list(self.representatives().values())
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, rows)
            path = root / "train.jsonl"; path.write_bytes(path.read_bytes() + b" ")
            with self.assertRaisesRegex(ValueError, "checksum"): verify_snapshot(root)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, rows)
            m = loads((root / "manifest.json").read_text()); m["files"]["train"]["path"] = "../escape"
            (root / "manifest.json").write_bytes(canonical_bytes(m))
            with self.assertRaisesRegex(ValueError, "path"): verify_snapshot(root)

    def test_loader_positive_row_full_graph_no_grad_forward_and_loss(self):
        import torch
        from nosl.model_v5 import StudentV5
        from nosl.loss_v5 import decision_loss
        torch.set_num_threads(1)
        rows = self.representatives()
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "v5"; self.persist(root, list(rows.values()))
            row = PreparedDatasetV5(root, "train", STUDENT, purpose="engineering-fixture")[0]
            with patch("torch.Tensor.backward", side_effect=AssertionError("backward forbidden")), patch("torch.optim.Optimizer.step", side_effect=AssertionError("optimizer forbidden")), torch.no_grad():
                model = StudentV5(STUDENT).eval(); output = model(row["public_input"])
                loss, terms = decision_loss(output, row["targets"], row["public_input"], STUDENT)
            self.assertTrue(torch.isfinite(loss)); self.assertFalse(loss.requires_grad)
            self.assertIn("public_evidence", row["public_input"])
            self.assertEqual(0, terms["value"])


if __name__ == "__main__": unittest.main()
