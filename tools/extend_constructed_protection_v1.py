#!/usr/bin/env python3
"""Protect the 13 retained constructed diagnostics using metadata only.

Only the named current reports and the v8 protection registry are read. Target,
outcome, proposal, and execution-accounting values are skipped lexically. This
does not run the full action-label adapter or establish historical completeness.
Writes are exclusive; --verify-only reproduces and compares every output byte.
"""
from __future__ import annotations

import argparse
from collections import Counter
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))

from native_protection import _value_end
from nosl.constructed_native_policy_v5 import _contract, _public, _source_aliases
from nosl.data import canonical_object_digest, validate_components, validate_registry
from nosl.native_policy_v5 import _closure
from nosl.prepare_v5 import public_metadata
from nosl.protection_v5 import extend_protection, record_tokens, validate_metadata, validate_protection
from nosl.public_identity_v5 import public_input_digest
from nosl.schema_v5 import load_config, loads

ARTIFACT_ROOT = Path("artifacts/recovery-protection-metadata")
BASE = ARTIFACT_ROOT / "INCOMPLETE-current-with-v8-protection-v5.json"
BASE_SHA256 = "04966ec89198eb9c13bc15eed804b7eef6478f69c1934f8d758f101ece12d7ea"
BASE_CANONICAL = "39d2518c73796d27cf9ff9b90ce26222e4c50e1405e8af1bb27bd410eaaac55f"
SUCCESSOR = ARTIFACT_ROOT / "INCOMPLETE-current-with-constructed-v1-protection-v5.json"
METADATA = ARTIFACT_ROOT / "constructed-v1-source-metadata.jsonl"
LEDGER = ARTIFACT_ROOT / "constructed-v1-attempt-identity-status.jsonl"
REPORT = ARTIFACT_ROOT / "constructed-v1-extension-report.json"
RECEIPT = Path("configs/protection_metadata_constructed_v1_extension.json")
REPORT_ROOT = Path("artifacts/reports/constructed-tape-v1")
RESPONSE_HASHES = (
    "94e234274ed02b012aed99d291eb2fc66e59da5fbeadaeed744785c5672522da",
    "2165f6ac262d03dec8387e82a03add3cd5f294adc302e47849a62f3b21aca09d",
    "2bca92497661026ec096e51477d886a19074131f06f75b727b6fa0ab96829755",
    "196b0a7ab71efe216a7edc92398b3934846f15682c1f24923c9a60f5a4d19d3f",
    "bc6359acea710fce925d0f07733a08a2531276bd4b66159a80cdfe0f209bf3ea",
    "ee6edd092e44b32490d1eb65f1d4173aeec720ec5af2a01cebe7ced0472b8d27",
    "bdab6dedd05da51b4da0ede06d5168ffe746ae6530c1ccd1d15fd58511a32119",
    "c7b90564a5887546b27feccb0b765701176607a36a4ebf78953d08e6684b8779",
    "7886e4dc36000120685d6236b86ae6ff4f9bc6af84146f41b810a72a39ee5ec4",
    "0e6e727d2b486a44fad855c6ad39048201a79485d603366dd5b330fb3dae8ad9",
    "dac0e93b88f9eb53a972b80f3d9b0c02b6337e9cc6b24a47969cb4e11c8551e9",
    "4b5c16a88f9634dfbfa656740cde83205da436e17db88e6889cab766f89ecdb5",
    "fca3311232c5d91f216af100b85f9b27401520bb414f541d3eff8304042be8c9",
)
CONTRACT_FIELDS = ("options", "teacher_options", "build_receipt_json", "build_receipt_sha256",
    "runtime_dependencies", "collection_contract_json", "collection_contract_sha256",
    "prior_identity", "source_generation_json", "source_generation_identity")
ATTEMPT_FIELDS = ("draw_id", "source_draw_seed", "status", "source_status", "stage", "detail",
    "recipe", "raw_record_index", "source_opened", "root_observed", "source_disposed", "provenance")
SOURCE_FILES = ("tools/extend_constructed_protection_v1.py", "tools/native_protection.py",
    "python/nosl/constructed_native_policy_v5.py", "python/nosl/prepare_v5.py",
    "python/nosl/protection_v5.py", "python/nosl/native_pilot.py", "python/nosl/native_policy_v5.py",
    "python/nosl/data.py", "python/nosl/public_identity_v5.py", "python/nosl/public_identity_v4.py",
    "python/nosl/public_identity_v3.py", "python/nosl/public_identity.py", "configs/student.v5.engineering.json")


def sha(payload):
    return hashlib.sha256(payload).hexdigest()


def canonical(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n").encode()


def check(condition, message):
    if not condition:
        raise ValueError(message)


def object_fields(text):
    """Decode object keys only; nested values remain exact unparsed slices."""
    decoder, index, fields = json.JSONDecoder(), 0, {}
    def whitespace(offset):
        while offset < len(text) and text[offset] in " \t\r\n":
            offset += 1
        return offset
    index = whitespace(index)
    check(text[index:index + 1] == "{", "object required")
    index = whitespace(index + 1)
    while text[index:index + 1] != "}":
        key, index = decoder.raw_decode(text, index)
        check(isinstance(key, str) and key not in fields, "invalid or duplicate object key")
        index = whitespace(index)
        check(text[index:index + 1] == ":", "object colon required")
        index = whitespace(index + 1)
        end = _value_end(text, index)
        fields[key] = text[index:end]
        index = whitespace(end)
        if text[index:index + 1] == "}":
            break
        check(text[index:index + 1] == ",", "object delimiter required")
        index = whitespace(index + 1)
        check(text[index:index + 1] != "}", "trailing object comma")
    check(whitespace(index + 1) == len(text), "trailing object content")
    return fields


def array_items(text):
    index = 0
    def whitespace(offset):
        while offset < len(text) and text[offset] in " \t\r\n":
            offset += 1
        return offset
    index = whitespace(index)
    check(text[index:index + 1] == "[", "array required")
    index = whitespace(index + 1)
    while text[index:index + 1] != "]":
        end = _value_end(text, index)
        yield text[index:end]
        index = whitespace(end)
        if text[index:index + 1] == "]":
            break
        check(text[index:index + 1] == ",", "array delimiter required")
        index = whitespace(index + 1)
        check(text[index:index + 1] != "]", "trailing array comma")
    check(whitespace(index + 1) == len(text), "trailing array content")


def supplemental_rows(audit):
    """Explicit aliases require this same projection in a future consumer.

    Standard family/public digests already enforce current exclusion. These
    linked rows preserve additional exporter spellings without changing helpers.
    Generation/prior identities are retained in the ledger, not treated as
    independent source identities shared by all draws of the same configuration.
    """
    family, battle = audit["source_run_group"], audit["underlying_battle_alias"]
    check(battle.endswith("/combat:0"), "configured combat alias required")
    rows = [{"audit_only": {"source_run_group": family, "source_combat_id": battle}},
            {"audit_only": {"source_run_group": battle.removesuffix("/combat:0"), "source_combat_id": battle}}]
    if "public_root_alias" in audit:
        rows.append({"audit_only": {"source_run_group": family, "public_state_digest": audit["public_root_alias"]}})
    return rows


def project_report(path, expected_hash, config):
    payload = path.read_bytes()
    check(sha(payload) == expected_hash, "retained response changed: " + str(path))
    fields = object_fields(payload.decode("utf-8"))
    report = {key: loads(fields[key]) for key in CONTRACT_FIELDS}
    options, _ = _contract(report)
    for key, expected in (("source_kind", "constructed_under_explicit_label_tape_prior"),
                          ("trainable", False), ("formal_labels", False)):
        check(loads(fields[key]) == expected, "report guard: " + key)
    raw_attempts, raw_records = list(array_items(fields["attempts"])), list(array_items(fields["records"]))
    check(len(raw_attempts) == len(raw_records) == 1, "named retained report must contain one attempt and record")
    attempt_fields = object_fields(raw_attempts[0])
    attempt = {key: loads(attempt_fields[key]) for key in ATTEMPT_FIELDS}
    seed = attempt["source_draw_seed"]
    check(options["sourceDrawSeeds"] == [seed] and attempt["draw_id"] == options["collectionId"] + "/draw:0", "all attempted draws required")
    check(attempt["raw_record_index"] == 0 and attempt["source_opened"] is True
          and attempt["root_observed"] is True and attempt["source_disposed"] is True, "retained observed root ownership")
    provenance, audit = attempt["provenance"], attempt["provenance"]["audit_only"]
    family, battle = _source_aliases(report, attempt["recipe"])
    check(audit["source_run_group"] == audit["source_random_family_alias"] == family
          and audit["underlying_battle_alias"] == battle, "derived source aliases disagree")
    check(audit["source_generation_identity"] == report["source_generation_identity"]
          and audit["source_prior_identity"] == report["prior_identity"], "source contract identity disagrees")
    check(audit["actual_seed"] == f"NOSL-NATIVE-TAPE-V1:{attempt['recipe']['runSeed']:016X}:{attempt['recipe']['tapeSeed']:016X}"
          and audit["native_run"] is True, "raw source recipe identity disagrees")
    _, entry = _public(provenance["public_input"], config, options["prior"])
    public_text = object_fields(attempt_fields["provenance"])["public_input"]
    check(audit["public_state_digest"] == sha(public_text.encode())
          and audit["public_root_alias"] == "constructed-native-tape-public-root-v1:" + audit["public_state_digest"], "public root alias disagrees")
    check(audit["source_combat_id"] == family + "/public-entry:" + sha(entry.encode())
          and audit["branch_family"] == audit["source_combat_id"] + "/tape-root-family", "combat/branch alias disagrees")
    # This is exactly _inspect's all-attempt metadata projection, without its
    # target/outcome inspection. Full adapter validation is a separate task.
    standard = public_metadata(provenance)
    standard["audit_only"]["source_draw_seed"] = seed
    validate_metadata(standard)
    raw_fields = object_fields(raw_records[0])
    raw_audit = object_fields(raw_fields["audit_only"])
    check(loads(raw_audit["draw_id"]) == attempt["draw_id"]
          and loads(raw_audit["source_draw_seed"]) == seed, "record/attempt draw binding")
    check(all(loads(raw_audit[key]) == value for key, value in audit.items()), "record/attempt aliases disagree")
    check(loads(raw_fields["public_input"]) == provenance["public_input"], "record/attempt public binding")
    record_projection = public_metadata({"public_input": loads(raw_fields["public_input"]),
        "audit_only": {key: loads(raw_audit[key]) for key in standard["audit_only"]}})
    check(record_projection == standard, "record metadata differs from all-attempt metadata")
    extra = supplemental_rows(audit)
    for row in extra:
        validate_metadata(row)
    ledger = {key: deepcopy(value) for key, value in attempt.items() if key != "provenance"}
    ledger.update(report_path=str(path.relative_to(ROOT)), report_sha256=sha(payload), audit_only=deepcopy(audit),
        full_v5_public_digest=public_input_digest(provenance["public_input"]), standard_metadata_sha256=sha(canonical(standard)),
        supplemental_linked_rows=extra, supplemental_tokens=sorted(set().union(*(record_tokens(row) for row in extra))))
    descriptor = {"path": str(path.relative_to(ROOT)), "bytes": len(payload), "sha256": sha(payload),
        "records_projected": 1, "attempts_projected": 1, "source_draw_seed": seed, "attempt_status": attempt["status"],
        "prior_identity": report["prior_identity"], "build_receipt_sha256": report["build_receipt_sha256"]}
    check(path.read_bytes() == payload, "response changed during projection")
    return standard, extra, ledger, descriptor


def original(registry):
    while "base" in registry:
        registry = registry["base"]
    return registry


def build_outputs():
    base_bytes = (ROOT / BASE).read_bytes()
    check(sha(base_bytes) == BASE_SHA256, "v8 base file hash changed")
    base = validate_protection(loads(base_bytes.decode()))
    check(canonical_object_digest(base) == BASE_CANONICAL and len(base["components"]) == 2672, "v8 base binding changed")
    config = load_config(ROOT / "configs/student.v5.engineering.json")
    paths = [REPORT_ROOT / "action-boundary/response.jsonl", *[
        REPORT_ROOT / f"breadth/case-{index:02d}/response.jsonl" for index in range(12)]]
    standard, supplemental, ledger, files = [], [], [], []
    for path, expected_hash in zip(paths, RESPONSE_HASHES):
        row, extra, entry, descriptor = project_report(ROOT / path, expected_hash, config)
        standard.append(row); supplemental.extend(extra); ledger.append(entry); files.append(descriptor)
    check([item["source_draw_seed"] for item in ledger] == [24401, *[seed for seed in range(44101, 44107) for _ in range(2)]], "retained seed inventory changed")
    status_counts = dict(sorted(Counter(item["status"] for item in ledger).items()))
    check(status_counts == {"computation_truncated": 1, "posterior_exhausted": 6, "recorded_complete": 6}, "retained attempt statuses changed")
    metadata = list({canonical(row): row for row in [*standard, *supplemental]}.values())
    check(len(metadata) == 40, "expected 13 public rows and 27 distinct supplemental rows")
    successor = extend_protection(base, metadata)
    validate_protection(successor)
    old_tokens, new_tokens = validate_components(base["components"]), validate_components(successor["components"])
    check(len(successor["components"]) == 2679 and successor["base"] == base, "expected seven additive families")
    check(all(successor["components"].get(key) == value for key, value in base["components"].items()), "old component tokens or owners changed")
    check(all(record_tokens(row) <= new_tokens.keys() for row in metadata), "metadata token missing")
    groups = [new_tokens["source_run_group:" + row["audit_only"]["source_run_group"]] for row in standard]
    check(len(set(groups)) == 7 and len(set(groups[1:])) == 6, "wrong independent family count")
    pairs = []
    for index in range(1, 13, 2):
        left, right = ledger[index:index + 2]
        check(groups[index] == groups[index + 1]
              and left["audit_only"]["underlying_battle_alias"] == right["audit_only"]["underlying_battle_alias"]
              and left["audit_only"]["source_prior_identity"] != right["audit_only"]["source_prior_identity"], "paired source closure failed")
        pairs.append({"source_draw_seed": left["source_draw_seed"], "attempt_indices": [index, index + 1],
                      "component": groups[index], "shared_random_family": left["audit_only"]["source_run_group"],
                      "shared_configured_battle": left["audit_only"]["underlying_battle_alias"]})
    _, closure = _closure(standard, [], successor)
    check(closure["protected_candidate_indices"] == list(range(13)) and not closure["cross_split_conflicts"], "normal source closure failed")
    # Test current helper consumption of each ordinary source/public alias alone,
    # not merely a family overlap that could hide an omitted public digest.
    normal_tokens = set().union(*(record_tokens(row) for row in standard))
    check(normal_tokens <= new_tokens.keys(), "normal all-attempt tokens missing")
    test_components = {key: value for key, value in base["components"].items() if value["split"] == "test"}
    test_aliases = {token for info in test_components.values() for token in info["tokens"] if token.startswith("prepared_public_input_digest:")}
    check(len(test_components) == 244 and len(test_aliases) == 524, "historical test protection changed")
    seal = original(base)["source"]["frozen_test_shards"]
    check(seal == [{"bytes": 10704382, "rows": 494, "sha256": "8c684b1c0bba7be4e97d8e5ce5151a835404b2d7d18cc84f71d7af1a2590de72"}]
          and original(successor) == original(base), "original seal or owners changed")
    try:
        validate_registry(successor)
    except ValueError as error:
        legacy_rejection = str(error)
    else:
        raise ValueError("legacy importer accepted conservative v5 wrapper")
    check((ROOT / BASE).read_bytes() == base_bytes, "base changed during extension")
    for entry, group in zip(ledger, groups):
        entry["protected_component"] = group
    outputs = {SUCCESSOR: canonical(successor), METADATA: b"".join(canonical(row) for row in metadata),
               LEDGER: b"".join(canonical(row) for row in ledger)}
    report = {"format": "nosl.protection-metadata-constructed-v1-extension-receipt.v1",
        "base_path": str(BASE), "base_file_sha256": BASE_SHA256, "base_canonical_object_sha256": BASE_CANONICAL,
        "base_components": 2672, "base_tokens": len(old_tokens), "base_unmodified": True,
        "successor_path": str(SUCCESSOR), "successor_file_sha256": sha(outputs[SUCCESSOR]),
        "successor_canonical_object_sha256": canonical_object_digest(successor), "successor_components": 2679,
        "successor_tokens": len(new_tokens), "new_tokens": len(set(new_tokens) - set(old_tokens)),
        "metadata_rows": len(metadata), "standard_all_attempt_rows": 13, "supplemental_alias_rows": 27,
        "records_projected": 13, "attempts_projected": 13, "attempt_status_counts": status_counts,
        "source_draw_seeds": [entry["source_draw_seed"] for entry in ledger], "distinct_primitive_rng_families": 7,
        "paired_breadth_families": pairs, "fixture_family_count": 1,
        "all_standard_source_and_public_tokens_excluded": True, "normal_token_count": len(normal_tokens),
        "normal_public_alias_count": sum(token.startswith("prepared_public_input_digest:") for token in normal_tokens),
        "all_13_attempts_excluded_by_current_native_policy_closure": True, "cross_split_conflicts": [],
        "all_base_components_tokens_and_owners_unchanged": True, "original_test_components": 244,
        "original_test_public_aliases": 524, "original_frozen_test_shards": seal,
        "generic_legacy_import_rejected": legacy_rejection, "files": files,
        "metadata_path": str(METADATA), "metadata_file_sha256": sha(outputs[METADATA]),
        "attempt_ledger_path": str(LEDGER), "attempt_ledger_file_sha256": sha(outputs[LEDGER]),
        "historical_protection_complete": False, "protection_status": "INCOMPLETE", "can_admit_or_train": False,
        "observed_diagnostics_are_fresh": False, "observed_diagnostics_quarantined": True,
        "owner_semantics": "New train owner tags are observed-source exclusions only. Every v5 role is protected; no training assignment or reuse is authorized. Never unwrap for legacy generic relabeling.",
        "missing_history": "All missing later cohorts in protection_metadata_recovery_20261003.json remain missing. These 13 attempts add seven observed constructed families and do not recover historical identities.",
        "alias_semantics": {"current_enforced": "Unmodified record_tokens/public_metadata and native_policy_v5._closure consume canonical source run/combat/branch, raw source draw, native source, full-v5 public and recoverable legacy public tokens.",
            "supplemental_projection": "underlying_battle_alias maps to source_combat_id linked to the canonical source_run_group; its configured-source prefix maps to source_run_group linked by that battle; public_root_alias maps to public_state_digest linked to the canonical family.",
            "supplemental_limit": "Exporter-only alias spellings are preserved in explicit linked rows and the full identity/status ledger. Future importers must apply the same projection to consume those spellings; arbitrary raw fields are not automatically recognized.",
            "source_generation_identity": "Preserved verbatim with source_prior_identity in every ledger entry; a generation configuration alone is not a per-draw source alias."},
        "guards": {"historical_target_payloads_opened": False, "old_library_files_opened": False,
            "current_response_targets_decoded": False, "current_response_outcomes_decoded": False,
            "current_proposals_or_execution_accounting_decoded": False, "full_action_adapter_run": False,
            "data_generation": False, "admission": False, "simulation": False, "fit": False,
            "model_instantiated": False, "backward_calls": 0, "optimizer_steps": 0,
            "old_registry_or_report_modified": False},
        "validation_scope": "Pinned current-report metadata, strict contract/public/source identities, immutable registry preservation, per-token alias coverage and current metadata closure only. Not a label audit or historical-completeness assertion.",
        "source_sha256": {name: sha((ROOT / name).read_bytes()) for name in SOURCE_FILES},
        "reproduce": "python tools/extend_constructed_protection_v1.py --verify-only (in the existing torch 2.6 environment; no model or learning calls)"}
    outputs[REPORT] = (json.dumps(report, sort_keys=True, indent=2) + "\n").encode()
    receipt = {**report, "verification_report_path": str(REPORT), "verification_report_sha256": sha(outputs[REPORT])}
    outputs[RECEIPT] = (json.dumps(receipt, sort_keys=True, indent=2) + "\n").encode()
    return outputs, receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-only", action="store_true", help="Reproduce without writing and compare existing outputs")
    args = parser.parse_args()
    outputs, receipt = build_outputs()
    for path, payload in outputs.items():
        if args.verify_only:
            check((ROOT / path).read_bytes() == payload, "output reproduction mismatch: " + str(path))
        else:
            check(not (ROOT / path).exists(), "refusing to replace existing output: " + str(path))
    if not args.verify_only:
        for path, payload in outputs.items():
            (ROOT / path).parent.mkdir(parents=True, exist_ok=True)
            with (ROOT / path).open("xb") as handle:
                handle.write(payload)
    print(json.dumps({key: receipt[key] for key in ("successor_components", "successor_tokens",
        "successor_file_sha256", "successor_canonical_object_sha256", "attempts_projected",
        "distinct_primitive_rng_families", "attempt_status_counts", "historical_protection_complete", "can_admit_or_train")}))


if __name__ == "__main__":
    main()
