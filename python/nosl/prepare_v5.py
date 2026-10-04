"""Immutable, bounded full-context v5 cohort snapshots. No training imports.

One predeclared cohort per output directory; no append or in-place admission.
A subsequent cohort must create a new snapshot and protect all earlier metadata.
The engineering student config stays quarantined for learned deployment; this
separate profile admits audited auxiliary labels only, never a formal fit.
"""
from __future__ import annotations
from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path

from .data import canonical_object_digest, _hash_file, validate_components
from .native_v5 import (ADMISSION_PROFILE, ADMISSION_SCHEMA, PURPOSES, candidate_digest, has_usable_targets,
                        require, validate_admission, validate_candidate, validate_versions)
from .protection_v5 import (PROTECTION_SCHEMA, PROVENANCE_FIELDS, extend_protection, group_records,
                            record_tokens, validate_protection, validate_metadata)
from .public_identity_v5 import PUBLIC_IDENTITY_SCHEME, public_input_digest
from .schema_v5 import loads, validate_config as validate_student_config

PIPELINE_VERSION = "nosl.dataset.prepare.complete-map.v1"
MANIFEST_SCHEMA = "nosl.dataset.complete-map.manifest.v1"
CONFIG_SCHEMA = "nosl.dataset.config.v5"
SPLITS = ("train", "validation", "test")
FILES = {"candidates": "candidates.json", "attempts": "attempts.json", "admission": "admission.json",
         "protection": "protection.json", "metadata": "metadata.json", "state": "split-state.json",
         "train": "train.jsonl", "validation": "validation.jsonl", "test": "test.jsonl", "quarantine": "quarantine.jsonl"}
SOURCE_FILES = ("native_v5.py", "protection_v5.py", "prepare_v5.py", "data_v5.py", "schema_v5.py",
                "schema_v4.py", "schema_v3.py", "schema_v2.py", "schema_regen.py", "schema.py", "evidence_v4.py",
                "public_identity_v5.py", "public_identity_v4.py", "public_identity_v3.py", "public_identity_v2.py",
                "public_identity.py", "native_pilot.py", "data.py")


def canonical_bytes(value):
    def normalize(item):
        if isinstance(item, float) and math.isfinite(item) and item.is_integer(): return int(item)
        if isinstance(item, list): return [normalize(x) for x in item]
        if isinstance(item, dict): return {k: normalize(v) for k, v in item.items()}
        return item
    return json.dumps(normalize(value), sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()


def implementation_fingerprint():
    package = Path(__file__).resolve().parent
    names = {"python/nosl/" + name: _hash_file(package / name) for name in SOURCE_FILES}
    for name in ("prepare_dataset_v5.py", "native_protection_v5.py", "adapt_native_v5.py"):
        names["tools/" + name] = _hash_file(package.parents[1] / "tools" / name)
    return {"format": "nosl.dataset.complete-map.implementation.v1", "source_sha256": names}


def validate_pipeline_config(config):
    require(isinstance(config, dict) and set(config) == {"schema_version", "admission_profile", "public_identity_scheme", "split_seed", "split_ratios", "split_unit", "formal_labels_enabled"}, "pipeline_config_fields")
    require(config["schema_version"] == CONFIG_SCHEMA and config["admission_profile"] == ADMISSION_PROFILE
            and config["public_identity_scheme"] == PUBLIC_IDENTITY_SCHEME and config["formal_labels_enabled"] is False
            and config["split_unit"] == "transitive_provenance_connected_component", "pipeline_profile_invalid")
    ratios = config["split_ratios"]
    require(isinstance(ratios, dict) and set(ratios) == set(SPLITS)
            and all(type(v) in (float, int) and math.isfinite(v) and 0 < v < 1 for v in ratios.values())
            and math.isclose(sum(ratios.values()), 1, abs_tol=1e-9), "split_ratios_invalid")
    require(isinstance(config["split_seed"], str) and bool(config["split_seed"]), "split_seed_invalid")
    return config


def choose_split(group, config):
    fraction = int.from_bytes(hashlib.sha256((config["split_seed"] + ":" + group).encode()).digest()[:8], "big") / 2**64
    cumulative = 0
    for split in SPLITS:
        cumulative += config["split_ratios"][split]
        if fraction < cumulative: return split
    return "test"


def public_metadata(row):
    audit = row.get("audit_only", {})
    allowed = {*PROVENANCE_FIELDS, "actual_seed", "act", "floor", "encounter", "source_kind", "native_run", "source_draw_seed"}
    result = {"audit_only": {k: deepcopy(v) for k, v in audit.items() if k in allowed}}
    if "public_input" in row: result["public_input"] = deepcopy(row["public_input"])
    return result



def candidate_projection(row):
    """Metadata projection committed by the reviewed receipt, safe for test rows."""
    return {"candidate_digest": candidate_digest(row), "public_digest": public_input_digest(row["public_input"]),
            "provenance": public_metadata(row),
            "accounting": {"draw_id": row["audit_only"]["draw_id"], "source_draw_seed": row["audit_only"]["source_draw_seed"],
                "selected_combat_index": row["audit_only"]["selected_combat_index"], "selected_decision_index": row["audit_only"]["selected_decision_index"],
                "complete": all(a["completed_worlds"] == a["allocated_worlds"] and a["allocated_worlds"] > 0 for a in row["targets"]["actions"]),
                "engine_errors": row["audit_only"]["n_error"], "usable": has_usable_targets(row)}}


def prepare(records, attempts, receipt, protection, pipeline_config, student_config):
    validate_pipeline_config(pipeline_config); validate_student_config(student_config); validate_protection(protection)
    # Full closure happens first, including unavailable attempts and malformed metadata.
    provenance = []
    for attempt in attempts:
        if isinstance(attempt, dict):
            value = deepcopy(attempt.get("provenance", {}))
            value.setdefault("audit_only", {})["source_draw_seed"] = attempt.get("source_draw_seed")
            provenance.append(value)
    groups, tokens, historic = group_records([*records, *provenance], protection["components"])
    stats = validate_admission(receipt, records, attempts, canonical_object_digest(protection), student_config)
    protected = {t for c in protection["components"].values() for t in c["tokens"]}
    conflicts = sorted(g for g, owners in historic.items() if len(owners) != 1)
    if receipt["purpose"] == "bounded-pilot":
        require(not conflicts and all(not tokens[g] & protected for g in groups[:len(records)]), "pilot_cohort_contains_protected_source")
    owners = {g: next(iter(historic[g])) if g in historic and len(historic[g]) == 1 else choose_split(g, pipeline_config) for g in tokens}
    state = {"components": {g: {"split": owners[g], "tokens": sorted(t)} for g, t in tokens.items()}, "cross_split_conflicts": conflicts}
    split_rows = {s: [] for s in SPLITS}; quarantined, metadata, seen = [], [], set()
    for index, row in enumerate(records):
        group, digest = groups[index], public_input_digest(row["public_input"])
        reason = ("historical_cross_split_bridge" if group in conflicts else
                  "previously_observed_source_or_alias" if tokens[group] & protected else
                  "duplicate_public_input" if digest in seen else
                  "no_usable_auxiliary_targets" if not has_usable_targets(row) else None)
        seen.add(digest)
        item = {**candidate_projection(row), "component": group, "split": owners[group], "exclusion_reason": reason}
        metadata.append(item)
        if reason is None: split_rows[owners[group]].append(deepcopy(row))
        else: quarantined.append({"reason": reason, "candidate": deepcopy(row)})
    # Failed/absent attempts remain in both the ledger and future protection.
    for offset, row in enumerate(provenance):
        metadata.append({"candidate_digest": None, "public_digest": None, "component": groups[len(records) + offset],
                         "split": owners[groups[len(records) + offset]], "exclusion_reason": "attempt_provenance",
                         "provenance": deepcopy(row), "accounting": None})
    report = {**stats, "usable_roots": sum(map(len, split_rows.values())), "quarantined_roots": len(quarantined),
              "split_counts": {s: len(v) for s, v in split_rows.items()}, "isolation_passed": not conflicts,
              "purpose": receipt["purpose"], "formal_training_ready": False, "fit_authorized": False,
              "policy_supervision_roots": 0, "qualification": "synthetic_contract_only" if receipt["purpose"] == "engineering-fixture" else "reviewed_bounded_auxiliary_pilot"}
    return split_rows, quarantined, metadata, state, report


def persist_snapshot(output, records, attempts, receipt, protection, pipeline_config, student_config):
    output = Path(output)
    split_rows, quarantined, metadata, state, report = prepare(records, attempts, receipt, protection, pipeline_config, student_config)
    output.mkdir(parents=True, exist_ok=False)
    payloads = {"candidates": records, "attempts": attempts, "admission": receipt, "protection": protection,
                "metadata": metadata, "state": state, **split_rows, "quarantine": quarantined}
    descriptors = {}
    for kind, value in payloads.items():
        encoded = b"".join(canonical_bytes(row) + b"\n" for row in value) if kind in (*SPLITS, "quarantine") else canonical_bytes(value)
        path = output / FILES[kind]
        with path.open("xb") as handle: handle.write(encoded)
        descriptors[kind] = {"path": FILES[kind], "bytes": len(encoded), "sha256": hashlib.sha256(encoded).hexdigest(),
                             "rows": len(value) if isinstance(value, list) else None}
    manifest = {"schema_version": MANIFEST_SCHEMA, "pipeline_version": PIPELINE_VERSION,
                "public_identity_scheme": PUBLIC_IDENTITY_SCHEME, "purpose": receipt["purpose"],
                "student_config_sha256": canonical_object_digest(student_config), "pipeline_config": deepcopy(pipeline_config),
                "admission_sha256": canonical_object_digest(receipt), "protection_sha256": canonical_object_digest(protection),
                "versions": deepcopy(receipt["versions"]), "implementation": implementation_fingerprint(),
                "files": descriptors, "report": report}
    # The manifest is the final commit marker; incomplete directories never load.
    with (output / "manifest.json").open("xb") as handle: handle.write(canonical_bytes(manifest))
    return manifest


def verify_snapshot(root, student_config=None, *, expected_purpose=None):
    root = Path(root).resolve()
    manifest_path = root / "manifest.json"
    before = _hash_file(manifest_path)
    manifest = loads(manifest_path.read_text(encoding="utf-8"))
    require(isinstance(manifest, dict) and manifest.get("schema_version") == MANIFEST_SCHEMA
            and manifest.get("pipeline_version") == PIPELINE_VERSION and manifest.get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME, "snapshot_version")
    require(manifest.get("purpose") in PURPOSES and (expected_purpose is None or manifest["purpose"] == expected_purpose), "snapshot_purpose_mismatch")
    require(manifest.get("implementation") == implementation_fingerprint(), "snapshot_implementation_changed")
    validate_pipeline_config(manifest["pipeline_config"]); validate_versions(manifest["versions"])
    if student_config is not None:
        validate_student_config(student_config)
        require(manifest["student_config_sha256"] == canonical_object_digest(student_config), "snapshot_student_config_changed")
    descriptors = manifest.get("files")
    require(isinstance(descriptors, dict) and set(descriptors) == set(FILES), "snapshot_files_invalid")
    paths = {}
    for kind, item in descriptors.items():
        require(isinstance(item, dict) and set(item) == {"path", "bytes", "sha256", "rows"} and item["path"] == FILES[kind], "snapshot_path_invalid")
        path = (root / item["path"]).resolve()
        require(path.is_relative_to(root) and path.is_file(), "snapshot_path_escape")
        require(type(item["bytes"]) is int and path.stat().st_size == item["bytes"] and _hash_file(path) == item["sha256"], "snapshot_file_checksum")
        require(item["rows"] is None or type(item["rows"]) is int and item["rows"] >= 0, "snapshot_row_count_invalid")
        paths[kind] = path
    # Candidate/test/quarantine target bytes are hashed above, never decoded here.
    admission = loads(paths["admission"].read_text(encoding="utf-8"))
    protection = validate_protection(loads(paths["protection"].read_text(encoding="utf-8")))
    attempts = loads(paths["attempts"].read_text(encoding="utf-8"))
    metadata = loads(paths["metadata"].read_text(encoding="utf-8"))
    state = loads(paths["state"].read_text(encoding="utf-8"))
    require(manifest["admission_sha256"] == canonical_object_digest(admission)
            and manifest["protection_sha256"] == canonical_object_digest(protection)
            and admission["protection_sha256"] == manifest["protection_sha256"]
            and admission["student_config_sha256"] == manifest["student_config_sha256"]
            and admission["records_sha256"] == descriptors["candidates"]["sha256"]
            and admission["attempts_sha256"] == canonical_object_digest(attempts)
            and admission["versions"] == manifest["versions"] and admission["purpose"] == manifest["purpose"], "snapshot_admission_binding")
    require(admission.get("schema_version") == ADMISSION_SCHEMA and admission.get("profile") == ADMISSION_PROFILE
            and admission.get("selection") == "predeclared_all_attempts_no_replacement"
            and admission.get("review", {}).get("decision") == "accepted"
            and admission["review"].get("evidence_kind") == ("synthetic_contract_only" if manifest["purpose"] == "engineering-fixture" else "predeclared_fresh_cohort_quality"), "snapshot_review_invalid")
    require(hashlib.sha256(admission["build_receipt_json"].encode()).hexdigest() == admission["build_receipt_sha256"]
            and canonical_object_digest(loads(admission["build_receipt_json"])) == admission["versions"]["runtime_source_sha256"], "snapshot_build_receipt_mismatch")
    require(isinstance(metadata, list) and isinstance(state, dict) and set(state) == {"components", "cross_split_conflicts"}, "snapshot_metadata_invalid")
    validate_components(state["components"])
    require(state["cross_split_conflicts"] == [] and manifest["report"]["isolation_passed"] is True, "snapshot_isolation_blocked")
    sources = [validate_metadata(m["provenance"]) for m in metadata]
    groups, tokens, historic = group_records(sources, protection["components"])
    require(all(len(s) == 1 for s in historic.values()), "snapshot_cross_split_bridge")
    expected_components = {g: {"split": next(iter(historic[g])) if g in historic else choose_split(g, manifest["pipeline_config"]), "tokens": sorted(t)} for g, t in tokens.items()}
    require(state["components"] == expected_components, "snapshot_provenance_closure_changed")
    candidate_meta = [m for m in metadata if m["candidate_digest"] is not None]
    projected = [{k: m[k] for k in ("candidate_digest", "public_digest", "provenance", "accounting")} for m in candidate_meta]
    require(canonical_object_digest(projected) == admission["candidate_metadata_sha256"], "snapshot_reviewed_metadata_changed")
    require([m["candidate_digest"] for m in candidate_meta] == admission["candidate_digests"]
            and len(candidate_meta) == descriptors["candidates"]["rows"]
            and len(metadata) == descriptors["metadata"]["rows"] and len(attempts) == descriptors["attempts"]["rows"], "snapshot_candidate_metadata_mismatch")
    require(len(metadata) == len(candidate_meta) + len(attempts), "snapshot_attempt_metadata_missing")
    planned = admission["predeclared_source_draw_seeds"]
    require([a["source_draw_seed"] for a in attempts] == planned and len(set(planned)) == len(planned), "snapshot_attempt_denominator_changed")
    by_digest = {m["candidate_digest"]: m for m in candidate_meta}
    require(len(by_digest) == len(candidate_meta), "snapshot_duplicate_candidate")
    referenced = []
    for attempt, item in zip(attempts, metadata[len(candidate_meta):]):
        provenance = deepcopy(attempt["provenance"])
        provenance.setdefault("audit_only", {})["source_draw_seed"] = attempt["source_draw_seed"]
        require(item["provenance"] == provenance and item["accounting"] is None, "snapshot_attempt_provenance_changed")
        if attempt["status"] == "recorded":
            require(attempt["record_digest"] in by_digest, "snapshot_attempt_candidate_missing")
            accounting = by_digest[attempt["record_digest"]]["accounting"]
            require(accounting["draw_id"] == attempt["draw_id"] and accounting["source_draw_seed"] == attempt["source_draw_seed"], "snapshot_attempt_candidate_changed")
            referenced.append(attempt["record_digest"])
        else: require(attempt["record_digest"] is None and attempt["status"] in ("absent", "failed", "not_executed"), "snapshot_attempt_status_invalid")
    require(len(referenced) == len(candidate_meta) and set(referenced) == set(by_digest), "snapshot_unaccounted_candidate")
    complete = [m["accounting"] for m in candidate_meta if m["accounting"]["complete"] is True]
    require(all(type(m["accounting"]["complete"]) is bool and type(m["accounting"]["usable"]) is bool for m in candidate_meta), "snapshot_accounting_invalid")
    observed_counts = {"minimum_complete_roots": len(complete), "minimum_positive_decision_roots": sum(a["selected_decision_index"] > 0 for a in complete),
                       "minimum_later_combat_roots": sum(a["selected_combat_index"] >= 2 and a["selected_decision_index"] > 0 for a in complete)}
    require(all(type(admission[k]) is int and admission[k] >= 0 and observed_counts[k] >= admission[k] for k in observed_counts), "snapshot_cohort_gate_failed")
    if manifest["purpose"] == "bounded-pilot":
        require(all(admission[k] > 0 for k in observed_counts) and admission["budget_expired"] is False
                and all(a["status"] in ("recorded", "absent") for a in attempts) and len(complete) == len(candidate_meta)
                and all(m["accounting"]["usable"] and m["accounting"]["engine_errors"] == 0 for m in candidate_meta), "snapshot_pilot_quality_invalid")
    require(manifest["report"].get("fit_authorized") is False and manifest["report"].get("formal_training_ready") is False
            and manifest["report"].get("policy_supervision_roots") == 0, "snapshot_training_claim_invalid")
    protected = {t for c in protection["components"].values() for t in c["tokens"]}
    counts, seen = {s: 0 for s in SPLITS}, set()
    for index, item in enumerate(metadata):
        group = groups[index]
        require(item["component"] == group and item["split"] == state["components"][group]["split"], "snapshot_split_changed")
        if item["candidate_digest"] is None: continue
        digest = public_input_digest(item["provenance"]["public_input"])
        require(item["public_digest"] == digest, "snapshot_public_digest_changed")
        expected_reason = ("previously_observed_source_or_alias" if tokens[group] & protected else
                           "duplicate_public_input" if digest in seen else
                           "no_usable_auxiliary_targets" if not item["accounting"]["usable"] else None)
        require(item["exclusion_reason"] == expected_reason, "snapshot_exclusion_reason_changed")
        require(manifest["purpose"] != "bounded-pilot" or expected_reason is None, "snapshot_pilot_candidate_excluded")
        if expected_reason is None: counts[item["split"]] += 1
        seen.add(digest)
    require(counts == manifest["report"]["split_counts"] and all(descriptors[s]["rows"] == counts[s] for s in SPLITS)
            and manifest["report"]["usable_roots"] == sum(counts.values())
            and manifest["report"]["quarantined_roots"] == descriptors["quarantine"]["rows"] == len(candidate_meta) - sum(counts.values()), "snapshot_split_counts_changed")
    require(_hash_file(manifest_path) == before, "manifest_changed_during_read")
    return manifest, paths, metadata, before


class PreparedDatasetV5:
    """Verified auxiliary-only train/validation reader; no fit authorization."""
    def __init__(self, root, split, config, *, purpose="bounded-pilot"):
        require(split in ("train", "validation"), "fitting_never_loads_test_targets")
        require(purpose in PURPOSES, "purpose_invalid")
        self.root, self.split, self.config, self.purpose = Path(root), split, deepcopy(config), purpose
        manifest, paths, metadata, digest = verify_snapshot(root, config, expected_purpose=purpose)
        self.manifest, self.manifest_sha256 = manifest, digest
        expected = {m["candidate_digest"]: m for m in metadata if m["exclusion_reason"] is None and m["split"] == split}
        records = []
        with paths[split].open(encoding="utf-8") as handle:
            for line in handle:
                require(bool(line.strip()), "empty_shard_row")
                row = validate_candidate(loads(line), config)
                key = candidate_digest(row)
                require(key in expected and row["audit_only"]["versions"] == manifest["versions"]
                        and row["audit_only"]["purpose"] == purpose
                        and candidate_projection(row) == {k: expected[key][k] for k in ("candidate_digest", "public_digest", "provenance", "accounting")} and has_usable_targets(row), "prepared_candidate_changed")
                records.append(row); expected.pop(key)
        require(not expected and bool(records) and len(records) == manifest["files"][split]["rows"], "prepared_split_empty_or_incomplete")
        require(_hash_file(paths[split]) == manifest["files"][split]["sha256"]
                and _hash_file(self.root / "manifest.json") == digest, "prepared_inputs_changed_during_read")
        self.records = records
        self.records_sha256 = canonical_object_digest(records)
        self.sha256 = digest + ":" + split

    def verify_integrity(self):
        current = type(self)(self.root, self.split, self.config, purpose=self.purpose)
        require(current.manifest_sha256 == self.manifest_sha256 and current.records_sha256 == self.records_sha256
                and canonical_object_digest(self.records) == self.records_sha256, "prepared_inputs_changed_after_loading")
        return self.manifest_sha256

    def __len__(self): return len(self.records)
    def __getitem__(self, index): return self.records[index]


def export_protection(root):
    """Protect every observed root/attempt without reading any target shard."""
    manifest, paths, metadata, _ = verify_snapshot(root)
    base = loads(paths["protection"].read_text(encoding="utf-8"))
    values = [m["provenance"] for m in metadata if record_tokens(m["provenance"])]
    state = loads(paths["state"].read_text(encoding="utf-8"))
    _, tokens, historic = group_records(values, base["components"])
    new_owners = {g: state["components"][g]["split"] for g in set(tokens) - set(historic)}
    return extend_protection(base, values, new_component_owners=new_owners)
