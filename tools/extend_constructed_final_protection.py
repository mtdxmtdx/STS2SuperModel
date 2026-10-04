#!/usr/bin/env python3
"""Add five retained report attempts without reopening historical payloads.

The immutable v1 projector skips targets, outcomes and execution values lexically.
Current contract/public/source validators and strict-adapter metadata hashes must
agree before extending the registry. Counts are measured, never synthesized.
Existing registries, scripts, ledgers and reports are never replaced.
"""
from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path
import json

from extend_constructed_protection_v1 import (
    ROOT, ARTIFACT_ROOT, canonical, check, original, project_report, sha,
)
from nosl.data import canonical_object_digest, validate_components, validate_registry
from nosl.native_policy_v5 import _closure
from nosl.policy_v5 import digest
from nosl.protection_v5 import extend_protection, record_tokens
from nosl.schema_v5 import load_config, loads

BASE = ARTIFACT_ROOT / "INCOMPLETE-current-with-constructed-v1-protection-v5.json"
BASE_SHA256 = "4feefc12e17f90bb1ff5d4b95a1e1f260405c18f560d4ea8cf951caa2820ad34"
BASE_CANONICAL = "298ec0fee8bbe2d6729e7d162bcc2243193baf9eb01f1065cd70b99736a24731"
BASE_METADATA = ARTIFACT_ROOT / "constructed-v1-source-metadata.jsonl"
BASE_METADATA_SHA256 = "f0ef4e12c57fca16dffc8d8272840f2340625617014a46b4836fd0f028fc213a"
BASE_LEDGER = ARTIFACT_ROOT / "constructed-v1-attempt-identity-status.jsonl"
BASE_LEDGER_SHA256 = "9ee09955b154e929253de6ca6a356ce84a327700c0eb4cee40742491d36f629a"
BASE_RECEIPT = Path("configs/protection_metadata_constructed_v1_extension.json")
BASE_RECEIPT_SHA256 = "bbab846d3c49bd65165d66b79ff5810e616dbb26ed571d3174bed1a730fb8a85"
SUCCESSOR = ARTIFACT_ROOT / "INCOMPLETE-current-with-constructed-final-protection-v5.json"
METADATA = ARTIFACT_ROOT / "constructed-final-source-metadata.jsonl"
LEDGER = ARTIFACT_ROOT / "constructed-final-new-attempt-identity-status.jsonl"
CUMULATIVE_LEDGER = ARTIFACT_ROOT / "constructed-final-all-attempt-identity-status.jsonl"
REPORT = ARTIFACT_ROOT / "constructed-final-extension-report.json"
RECEIPT = Path("configs/protection_metadata_constructed_final_extension.json")
LOGS = {"focused_checks": str(ARTIFACT_ROOT / "constructed-final-focused-checks.log"),
        "build": str(ARTIFACT_ROOT / "constructed-final-extension-build.log"),
        "verify_only": str(ARTIFACT_ROOT / "constructed-final-extension-verify-only.log")}

# Exact report bytes plus independently supplied strict-adapter metadata hashes.
# These pin all-attempt projection only; no target or label admission is implied.
INPUTS = (
    ("artifacts/reports/constructed-owner-ruby-v1/lantern-boundary/response.jsonl",
     "94c459ce681491067a714b8fb306c24f122577d39536edbdac6cc275824d5193",
     "af45ca869c2810818510bab554268c32170ec697c5db8d3261f40cba23fabe81"),
    ("artifacts/ruby-proof/case-00/response.jsonl",
     "f76e34fe8e16108ccc5c38940873f84168bd81d6d27f6d799c25f627c4c8264e",
     "e33f786574927b9f50dcd61a0c4c3e4cb6a4576c8b1532cb53c0f21d06cf1db3"),
    ("artifacts/ruby-proof/case-01/response.jsonl",
     "2adb3a50b8bca9bab171c2aeef894c0c0faceeaa0e18030c0597f6068b99daa3",
     "2f424b0b68149e003dc46126cce0acd5544a2321e486a399db87b2a6cec92386"),
    ("artifacts/reports/constructed-owner-ruby-v3/ruby-turn2/response.jsonl",
     "d0821c436b9577d90f8bcb805854d4580a5b784a4fc0807e88e4aeb7030b947b",
     "2f424b0b68149e003dc46126cce0acd5544a2321e486a399db87b2a6cec92386"),
    ("artifacts/reports/constructed-final-v2/lantern-mixed/response.jsonl",
     "7636e9d2c7c801395c31e90bf4b70c385673e1f28e186f6af6ad6669cc8d84ce",
     "bb3376aec2ae0b241fb79a483a5b35a5a8ad44d0ff3257516c26967e0738cb57"),
)
SOURCE_FILES = ("tools/extend_constructed_final_protection.py", "tools/extend_constructed_protection_v1.py",
    "tools/native_protection.py", "tests/data/test_constructed_final_protection_metadata.py",
    "tests/data/test_constructed_protection_metadata.py", "python/nosl/constructed_native_policy_v5.py",
    "python/nosl/constructed_native_event_v5.py", "python/nosl/prepare_v5.py", "python/nosl/protection_v5.py",
    "python/nosl/native_pilot.py", "python/nosl/native_policy_v5.py", "python/nosl/data.py",
    "python/nosl/public_identity.py", "python/nosl/public_identity_v2.py", "python/nosl/public_identity_v3.py",
    "python/nosl/public_identity_v4.py", "python/nosl/public_identity_v5.py", "configs/student.v5.engineering.json")


def pinned_bytes(path, expected_sha):
    payload = (ROOT / path).read_bytes()
    check(sha(payload) == expected_sha, "immutable input changed: " + str(path))
    return payload


def verify_alias_extension(base, successor, rows):
    """Verify literal preservation and every token, not just any group overlap."""
    old, new = validate_components(base["components"]), validate_components(successor["components"])
    check(all(successor["components"].get(key) == value for key, value in base["components"].items()),
          "old component tokens or owners changed")
    normal = set().union(*(record_tokens(row) for row in rows))
    check(normal <= new.keys(), "normal source/public token missing")
    _, closure = _closure(rows, [], successor)
    check(closure["protected_candidate_indices"] == list(range(len(rows)))
          and not closure["cross_split_conflicts"], "normal metadata closure failed")
    return {"base_components": len(base["components"]), "successor_components": len(successor["components"]),
        "new_components": len(successor["components"]) - len(base["components"]),
        "base_tokens": len(old), "successor_tokens": len(new), "new_tokens": len(set(new) - set(old)),
        "checked_report_attempts": len(rows), "normal_token_count": len(normal),
        "normal_public_alias_count": sum(token.startswith("prepared_public_input_digest:") for token in normal),
        "all_base_components_tokens_and_owners_unchanged": True,
        "all_standard_source_and_public_tokens_excluded": True,
        "all_report_attempts_excluded_by_current_native_policy_closure": True,
        "cross_split_conflicts": []}


def standard_rows_for_ledger(metadata, ledger):
    """Preserve per-report attempt multiplicity even when projections coincide."""
    by_hash = {sha(canonical(row)): row for row in metadata}
    return [by_hash[entry["standard_metadata_sha256"]] for entry in ledger]


def build_outputs():
    base_bytes = pinned_bytes(BASE, BASE_SHA256)
    base_metadata_bytes = pinned_bytes(BASE_METADATA, BASE_METADATA_SHA256)
    base_ledger_bytes = pinned_bytes(BASE_LEDGER, BASE_LEDGER_SHA256)
    base_receipt_bytes = pinned_bytes(BASE_RECEIPT, BASE_RECEIPT_SHA256)
    base = loads(base_bytes.decode())
    base_metadata = [loads(line) for line in base_metadata_bytes.decode().splitlines()]
    base_ledger = [loads(line) for line in base_ledger_bytes.decode().splitlines()]
    base_receipt = loads(base_receipt_bytes.decode())
    check(canonical_object_digest(base) == BASE_CANONICAL, "base canonical binding changed")
    check(len(base["components"]) == 2679 and len(base_ledger) == 13, "base inventory changed")
    old_standard = standard_rows_for_ledger(base_metadata, base_ledger)
    config = load_config(ROOT / "configs/student.v5.engineering.json")
    standard, extras, ledger, files = [], [], [], []
    print("Projecting five pinned reports; targets/outcomes remain unparsed", flush=True)
    for path, raw_hash, expected_metadata_hash in INPUTS:
        row, supplemental, entry, descriptor = project_report(ROOT / path, raw_hash, config)
        actual_metadata_hash = digest([row])
        check(actual_metadata_hash == expected_metadata_hash, "strict adapter metadata mismatch: " + path)
        entry["strict_adapter_all_attempts_metadata_sha256"] = actual_metadata_hash
        descriptor["strict_adapter_all_attempts_metadata_sha256"] = actual_metadata_hash
        descriptor["metadata_matches_strict_adapter"] = True
        standard.append(row); extras.extend(supplemental); ledger.append(entry); files.append(descriptor)
    check([entry["source_draw_seed"] for entry in ledger] == [24501, 44101, 44101, 44101, 44121],
          "new report-attempt inventory changed")
    # Ruby producer versions preserve both earlier public roots. Success in a
    # newer report cannot overwrite or reclassify the earlier exhausted report.
    ruby_matches = []
    for new_index, old_index in ((1, 1), (2, 2), (3, 2)):
        check(standard[new_index] == old_standard[old_index], "Ruby metadata changed across producer versions")
        check(ledger[new_index]["audit_only"] == base_ledger[old_index]["audit_only"], "Ruby exporter aliases changed")
        ruby_matches.append({"new_report_path": ledger[new_index]["report_path"],
            "old_report_path": base_ledger[old_index]["report_path"],
            "strict_adapter_all_attempts_metadata_sha256": digest([standard[new_index]]),
            "same_standard_metadata": True, "same_exporter_aliases": True,
            "old_attempt_status_preserved": base_ledger[old_index]["status"],
            "new_attempt_status": ledger[new_index]["status"]})
    metadata = list({canonical(row): row for row in [*standard, *extras]}.values())
    print("Extending immutable registry through existing recursive metadata validators", flush=True)
    # extend_protection validates the complete input and complete successor.
    # No additional redundant whole-history validation pass is necessary.
    successor = extend_protection(base, metadata)
    check(successor["base"] == base, "nested base changed")
    all_standard = [*old_standard, *standard]
    stats = verify_alias_extension(base, successor, all_standard)
    new_tokens = validate_components(successor["components"])
    check(all(record_tokens(row) <= new_tokens.keys() for row in metadata), "supplemental alias token missing")
    for entry in ledger:
        entry["protected_component"] = new_tokens["source_run_group:" + entry["audit_only"]["source_run_group"]]
    cumulative = [*base_ledger, *ledger]
    old_families = {entry["audit_only"]["source_run_group"] for entry in base_ledger}
    families = {entry["audit_only"]["source_run_group"] for entry in cumulative}
    added_families = families - old_families
    lantern_families = {ledger[index]["audit_only"]["source_run_group"] for index in (0, 4)}
    check(added_families == lantern_families and len(added_families) == 2,
          "expected exactly the two observed Lantern families")
    check(len(cumulative) == 18 and len(families) == 9 and stats["successor_components"] == 2681,
          "measured continuation inventory differs from reviewed expectation")
    check(len({(entry["report_sha256"], entry["draw_id"]) for entry in cumulative}) == len(cumulative),
          "duplicate report-attempt entry")
    test = {key: value for key, value in base["components"].items() if value["split"] == "test"}
    test_aliases = {token for info in test.values() for token in info["tokens"] if token.startswith("prepared_public_input_digest:")}
    seal = original(base)["source"]["frozen_test_shards"]
    check(len(test) == 244 and len(test_aliases) == 524 and original(successor) == original(base), "old test protection changed")
    check(seal == [{"bytes": 10704382, "rows": 494,
        "sha256": "8c684b1c0bba7be4e97d8e5ce5151a835404b2d7d18cc84f71d7af1a2590de72"}], "old test seal changed")
    try:
        validate_registry(successor)
    except ValueError as error:
        legacy_rejection = str(error)
    else:
        raise ValueError("legacy importer accepted conservative v5 wrapper")
    for path, expected in ((BASE, base_bytes), (BASE_METADATA, base_metadata_bytes),
                           (BASE_LEDGER, base_ledger_bytes), (BASE_RECEIPT, base_receipt_bytes)):
        check((ROOT / path).read_bytes() == expected, "immutable base artifact changed: " + str(path))
    outputs = {SUCCESSOR: canonical(successor), METADATA: b"".join(canonical(row) for row in metadata),
        LEDGER: b"".join(canonical(row) for row in ledger),
        CUMULATIVE_LEDGER: b"".join(canonical(row) for row in cumulative)}
    report = {"format": "nosl.protection-metadata-constructed-final-extension-receipt.v1", **stats,
        "base_path": str(BASE), "base_file_sha256": BASE_SHA256, "base_canonical_object_sha256": BASE_CANONICAL,
        "base_metadata_file_sha256": BASE_METADATA_SHA256, "base_attempt_ledger_file_sha256": BASE_LEDGER_SHA256,
        "base_receipt_file_sha256": BASE_RECEIPT_SHA256, "base_unmodified": True,
        "successor_path": str(SUCCESSOR), "successor_file_sha256": sha(outputs[SUCCESSOR]),
        "successor_canonical_object_sha256": canonical_object_digest(successor),
        "new_report_attempts": len(ledger), "retained_report_attempts_total": len(cumulative),
        "prior_report_attempts_preserved": len(base_ledger), "new_metadata_rows": len(metadata),
        "new_unique_standard_metadata_rows": len({canonical(row) for row in standard}),
        "new_unique_supplemental_rows": len({canonical(row) for row in extras}),
        "distinct_primitive_rng_families_total": len(families), "new_primitive_rng_families": sorted(added_families),
        "new_family_count": len(added_families), "new_source_draw_seeds": [entry["source_draw_seed"] for entry in ledger],
        "new_attempt_status_counts": dict(sorted(Counter(entry["status"] for entry in ledger).items())),
        "all_attempt_status_counts": dict(sorted(Counter(entry["status"] for entry in cumulative).items())),
        "ruby_version_identity_checks": ruby_matches, "files": files,
        "original_test_components": len(test), "original_test_public_aliases": len(test_aliases),
        "original_frozen_test_shards": seal, "generic_legacy_import_rejected": legacy_rejection,
        "metadata_path": str(METADATA), "metadata_file_sha256": sha(outputs[METADATA]),
        "new_attempt_ledger_path": str(LEDGER), "new_attempt_ledger_file_sha256": sha(outputs[LEDGER]),
        "all_attempt_ledger_path": str(CUMULATIVE_LEDGER), "all_attempt_ledger_file_sha256": sha(outputs[CUMULATIVE_LEDGER]),
        "all_five_adapter_metadata_hashes_matched": True,
        "historical_protection_complete": False, "protection_status": "INCOMPLETE", "can_admit_or_train": False,
        "observed_diagnostics_are_fresh": False, "observed_diagnostics_quarantined": True,
        "owner_semantics": base_receipt["owner_semantics"], "alias_semantics": base_receipt["alias_semantics"],
        "missing_history": "All missing later historical cohorts remain missing; five additional inspected report attempts do not restore those identities.",
        "denominator_semantics": "18 retained report attempts cover nine primitive RNG families. Three new Ruby report attempts reuse one earlier family and two earlier roots; versioned successes do not replace earlier exhaustion.",
        "guards": {"historical_target_payloads_opened": False, "old_library_files_opened": False,
            "current_response_targets_decoded": False, "current_response_outcomes_decoded": False,
            "current_proposals_or_execution_accounting_decoded": False, "full_action_adapter_run": False,
            "data_generation": False, "admission": False, "simulation": False, "fit": False,
            "model_instantiated": False, "backward_calls": 0, "optimizer_steps": 0,
            "old_registry_script_or_report_modified": False},
        "validation_scope": "Current report identity/public metadata and strict-adapter metadata hashes; complete supplied registry preservation and per-token closure. This is not label validation or historical completeness.",
        "source_sha256": {path: sha((ROOT / path).read_bytes()) for path in SOURCE_FILES},
        "console_logs": LOGS,
        "reproduce": "python tools/extend_constructed_final_protection.py --verify-only (existing torch 2.6 environment; no model or learning calls)"}
    outputs[REPORT] = (json.dumps(report, sort_keys=True, indent=2) + "\n").encode()
    receipt = {**report, "verification_report_path": str(REPORT), "verification_report_sha256": sha(outputs[REPORT])}
    outputs[RECEIPT] = (json.dumps(receipt, sort_keys=True, indent=2) + "\n").encode()
    return outputs, receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-only", action="store_true", help="Reproduce all outputs in memory and compare exact bytes")
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
    print(json.dumps({key: receipt[key] for key in ("successor_components", "successor_tokens", "new_tokens",
        "successor_file_sha256", "successor_canonical_object_sha256", "new_report_attempts",
        "retained_report_attempts_total", "distinct_primitive_rng_families_total", "new_attempt_status_counts",
        "all_attempt_status_counts", "all_five_adapter_metadata_hashes_matched", "historical_protection_complete",
        "can_admit_or_train")}), flush=True)


if __name__ == "__main__":
    main()
