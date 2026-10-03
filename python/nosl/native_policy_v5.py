"""Lossless native empirical-value bridge; admission and learning are separate.

Only the new complete-map native export is accepted. Its exact bytes, full
outcome ledger and every attempted source remain in a sealed producer receipt.
The existing auxiliary adapter validates owned copies; its deliberate value
masking never touches the source or the policy targets produced here.

Hashes bind evidence, not authenticity or permission. The native candidate
objective remains uncalibrated, and no resource prices or ranking certificates
are introduced by this adapter.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import math
from pathlib import Path

from .data_v5 import validate_targets
from .native_v5 import (ENDPOINT, PINNED_VERSIONS, RAW_CANDIDATE_SCHEMA, adapt_native_attempts,
                        adapt_native_candidates, candidate_digest)
from .prepare_v5 import public_metadata
from .protection_v5 import group_records, validate_protection
from .public_identity_v5 import public_input_digest
from .schema import integer, number, object_keys, reject
from .schema_v5 import loads, validate_config

ADAPTER_VERSION = "nosl.native-full-policy.adapter.v5.1"
RECEIPT_FORMAT = "nosl.native-full-policy.producer-receipt.v5.1"
REVIEW_FORMAT = "nosl.native-full-policy.cohort-review.v5.1"
RAW_REPORT_SCHEMA = "nosl.native-complete-map.raw-report.v1"
NO_RANKING = "MASKED_NO_CERTIFIED_UTILITY_SUPPORT"
SOURCE_HASHES = {
    "src/Nosl.Objectives/ObjectiveEvaluator.cs": "a5c386188f00d096fc5c037881c4643e52cd180632e14da0a94b810cee63c494",
    "src/Nosl.Objectives/RolloutOutcome.cs": "492d066f1e0b2fddfd111c68c844c6edee5782982a7149641dfec2e03c1d2c04",
    "src/Nosl.Worker/TeacherDataset.cs": "3c81734b9a840e267edf1731e403100356dbb9f47d9a78569cb6cffcf617c037",
}
CANDIDATE_PROFILE = {
    "id": "nosl_silent_a10_terminal_v4_candidate", "defeat_cost": 1000,
    "downside_coefficient": .2, "calibrated": False, "calibration_evidence": None,
    "value_table_version": "unresolved-v1", "maximum_absolute_unit_value": 100,
    "maximum_absolute_inventory_adjustment": 100, "maximum_absolute_permanent_adjustment": 100,
    "inventory_values": {}, "permanent_future_values": {},
    "reference_hp": "fixed_combat_start", "loss_retained_inventory": 0,
    "loss_permanent_future_value": 0, "time_discount": 1,
}


def require(condition, reason):
    if not condition: reject("native_policy_v5:" + reason)


def _digest(value):
    # Use exactly the full-policy identity representation (not the legacy
    # integer-normalizing canonical identity) without importing any trainer.
    import json
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                    allow_nan=False).encode()).hexdigest()


def _sha(value, name):
    require(isinstance(value, str) and len(value) == 64
            and all(c in "0123456789abcdef" for c in value), name + "_invalid_sha256")
    return value


def _text(value, name):
    require(isinstance(value, str) and bool(value.strip()), name + "_missing")
    return value


def objective_contract():
    return {"format": "nosl.native-candidate-objective-contract.v1",
            "profile": deepcopy(CANDIDATE_PROFILE), "source_sha256": dict(SOURCE_HASHES)}


def implementation_fingerprint():
    # Include the complete auxiliary validation/isolation closure. A changed
    # adapter cannot silently reinterpret an already frozen producer receipt.
    from .prepare_v5 import implementation_fingerprint as auxiliary_fingerprint
    return {"adapter_version": ADAPTER_VERSION,
            "adapter_source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "auxiliary": auxiliary_fingerprint()}


def _quantities(items):
    result = {}
    for item in items:
        result[item["resourceId"]] = result.get(item["resourceId"], 0) + item["count"]
    return result


def evaluate_candidate_outcome(outcome):
    """Recompute the pinned C# Candidate profile, never price unknown resources.

    The raw adapter first performs the stricter native terminal/resource-ledger
    checks. This evaluator also rejects the invalid native diagnostics relevant
    to objective arithmetic. Incomplete outcomes never become losses.
    """
    require(isinstance(outcome, dict), "outcome_object_required")
    kind = outcome.get("terminalKind")
    require(kind in ("Win", "Loss", "ComputeTruncated", "EngineError", "PolicyNonterminating"),
            "unknown_terminal_kind")
    if kind not in ("Win", "Loss"):
        return {"status": "Incomplete", "cost": None, "reasons": [kind]}
    require(outcome.get("settlementComplete") is True
            and bool(_text(outcome.get("settlementProfileId"), "settlement_profile"))
            and bool(_text(outcome.get("continuationPolicyId"), "continuation_policy")),
            "actual_terminal_settlement_required")
    start = integer(outcome.get("hpAtCombatStart"), "start_hp", 1)
    initial_max = integer(outcome.get("maxHpStart"), "start_max_hp", start)
    final_max = integer(outcome.get("maxHpAfterSettlement"), "final_max_hp", 1)
    final = integer(outcome.get("hpAfterSettlement"), "final_hp", 0, final_max)
    require(outcome.get("playerAlive") is (final > 0) and (kind != "Win" or final > 0),
            "invalid_alive_facts")
    integer(outcome.get("playerTurnsElapsed"), "player_turns_elapsed", 0, 2**31 - 1)
    integer(outcome.get("atomicActionsExecuted"), "atomic_actions_executed", 0, 2**63 - 1)
    for key in ("cumulativeHpDamage", "healingReceived"):
        if outcome.get(key) is not None: number(outcome[key], key, 0, float("inf"))
    if outcome.get("otherHpAdjustment") is not None: number(outcome["otherHpAdjustment"], "other_hp_adjustment")
    if outcome.get("hpEventDiagnosticsComplete") is True:
        require(all(outcome.get(k) is not None for k in ("cumulativeHpDamage", "healingReceived", "otherHpAdjustment"))
                and start - outcome["cumulativeHpDamage"] + outcome["healingReceived"] + outcome["otherHpAdjustment"] == final,
                "invalid_hp_diagnostics")
    for key in ("inventoryStart", "inventoryEnd"):
        require(isinstance(outcome.get(key), list), "missing_inventory_ledger")
        for item in outcome[key]:
            require(isinstance(item, dict), "invalid_inventory_item")
            _text(item.get("resourceId"), "resource_id"); integer(item.get("count"), "inventory_count")
    require(isinstance(outcome.get("resourceEvents"), list)
            and isinstance(outcome.get("permanentChanges"), list), "missing_resource_ledger")
    for event in outcome["resourceEvents"]:
        require(isinstance(event, dict), "invalid_resource_event")
        _text(event.get("resourceId"), "resource_id"); integer(event.get("quantity"), "resource_quantity")
    changes = {}
    for change in outcome["permanentChanges"]:
        require(isinstance(change, dict), "invalid_permanent_change")
        key = _text(change.get("kind"), "permanent_change_kind")
        changes[key] = changes.get(key, 0) + number(change.get("amount"), "permanent_change_amount")
    reasons = []
    if outcome.get("inventorySnapshotsComplete") is not True: reasons.append("inventory_snapshots_incomplete")
    before, after = _quantities(outcome["inventoryStart"]), _quantities(outcome["inventoryEnd"])
    for key in sorted(set(before) | set(after)):
        retained = after.get(key, 0) if kind == "Win" else 0
        if before.get(key, 0) != retained: reasons.append("inventory_value_unresolved:" + key)
    if outcome.get("permanentChangesComplete") is not True: reasons.append("permanent_change_ledger_incomplete")
    if changes.get("max_hp", 0) != final_max - initial_max: reasons.append("permanent_change_not_recorded:max_hp")
    if kind == "Win":
        reasons.extend("permanent_future_value_unresolved:" + key for key in sorted(changes) if changes[key] != 0)
    if reasons: return {"status": "ObjectiveValueUnresolved", "cost": None, "reasons": reasons}
    loss = start - final
    cost = (1000 if kind == "Loss" else 0) + loss + .2 * math.pow(max(loss, 0), 2) / max(1, start)
    require(math.isfinite(cost), "nonfinite_terminal_cost")
    return {"status": "Scored", "cost": cost, "reasons": []}


def _artifact(payload, name):
    require(isinstance(payload, bytes), name + "_exact_bytes_required")
    text = payload.decode("utf-8")
    return {"bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest(), "utf8": text}


def _read_artifact(value, name):
    object_keys(value, ("bytes", "sha256", "utf8"), name + " artifact")
    require(isinstance(value["utf8"], str), name + "_text_required")
    payload = value["utf8"].encode("utf-8")
    require(_digest(value) == _digest(_artifact(payload, name)), name + "_artifact_changed")
    return payload


def _parse_sources(raw_bytes, attempts_bytes, report_bytes, config):
    require(all(isinstance(payload, bytes) for payload in (raw_bytes, attempts_bytes, report_bytes)),
            "exact_source_bytes_required")
    sources = [loads(line) for line in raw_bytes.decode("utf-8").splitlines() if line.strip()]
    # Owned copies only: legacy adapter is deliberately auxiliary-only.
    auxiliary = adapt_native_candidates(raw_bytes, config) if sources else []
    attempts = adapt_native_attempts(attempts_bytes, auxiliary)
    raw_attempts, report = loads(attempts_bytes.decode("utf-8")), loads(report_bytes.decode("utf-8"))
    require(isinstance(report, dict) and report.get("schema_version") == RAW_REPORT_SCHEMA
            and report.get("status") == "bounded_fresh_candidates_require_separate_quality_admission"
            and report.get("purpose") == "bounded-pilot"
            and report.get("selection") == "predeclared_all_attempts_no_replacement", "new_native_report_required")
    require(_digest(report.get("records")) == _digest(sources) and _digest(report.get("attempts")) == _digest(raw_attempts),
            "raw_report_export_or_attempt_mismatch")
    require(all(report.get(key) is False for key in ("trainable", "formalTraining", "formal_labels"))
            and type(report.get("budgetExpired")) is bool, "raw_report_claim_invalid")
    options, teacher = report.get("options"), report.get("teacherOptions")
    require(isinstance(options, dict) and isinstance(teacher, dict), "report_source_plan_missing")
    planned = options.get("sourceDrawSeeds")
    require(isinstance(planned, list) and 1 <= len(planned) <= 16
            and all(type(seed) is int and 0 <= seed < 2**64 for seed in planned)
            and len(set(planned)) == len(planned), "predeclared_sources_invalid")
    require([attempt["source_draw_seed"] for attempt in raw_attempts] == planned
            and integer(report.get("sourceDrawsRequested"), "source_draws_requested", 1, 16) == len(planned)
            and integer(report.get("recordedRoots"), "recorded_roots", 0, 16) == len(sources), "attempt_denominator_incomplete")
    collection = _text(options.get("collectionId"), "collection_id")
    require([a["draw_id"] for a in raw_attempts] == [collection + "/draw:" + str(i) for i in range(len(planned))],
            "attempt_collection_identity_mismatch")
    require(teacher.get("mode") == "T0" and teacher.get("explorationSeeds") == []
            and teacher.get("formalLabels") is False, "native_T0_only")
    evaluation_seeds = teacher.get("evaluationSeeds")
    require(isinstance(evaluation_seeds, list) and 1 <= len(evaluation_seeds) <= 16
            and all(type(seed) is int and 0 <= seed < 2**64 for seed in evaluation_seeds)
            and len(set(evaluation_seeds)) == len(evaluation_seeds), "evaluation_seed_plan_invalid")
    require(not set(planned) & set(evaluation_seeds), "cohort_source_evaluation_seed_overlap")
    build_json = report.get("build_receipt_json")
    require(isinstance(build_json, str)
            and hashlib.sha256(build_json.encode()).hexdigest() == report.get("build_receipt_sha256"),
            "report_build_receipt_mismatch")
    build = loads(build_json)
    require(build == report.get("runtime_dependencies"), "report_runtime_dependencies_mismatch")
    values = []
    for index, source in enumerate(sources):
        public, targets, audit = source["public_input"], source["targets"], source["audit_only"]
        validate_targets(targets, public, config)
        object_keys(audit.get("versions"), (*PINNED_VERSIONS, "source_prior", "sampler", "posterior_profile"), "raw native versions")
        require(audit["versions"]["dataset"] == RAW_CANDIDATE_SCHEMA,
                "raw_dataset_version_required")
        require(audit.get("purpose") == "bounded-pilot" and audit.get("collection_id") == collection
                and audit.get("sampler_seeds") == teacher.get("evaluationSeeds")
                and audit.get("versions", {}).get("continuation") == teacher.get("continuationPolicyId")
                and audit.get("declared_prior") == report.get("prior") == options.get("prior")
                and audit.get("source_prior_identity") == report.get("priorIdentity"), "raw_report_record_identity_mismatch")
        require(audit.get("runtime_dependencies") == build
                and audit.get("build_receipt_sha256") == report["build_receipt_sha256"], "record_runtime_receipt_mismatch")
        require(targets["pairwise"] == [] and targets["equivalent_action_set"] == []
                and audit.get("ranking_evidence") == NO_RANKING
                and audit.get("ranking_intervals") == [], "native_ranking_has_no_certificate")
        action_values = []
        for target, sample in zip(targets["actions"], audit["outcome_samples"]):
            evaluations = [evaluate_candidate_outcome(outcome) for outcome in sample["outcomes"]]
            costs = [value["cost"] for value in evaluations]
            expected = -sum(costs) / len(costs) if costs and all(cost is not None for cost in costs) else None
            if target["masks"]["value"]:
                require(expected is not None and math.isclose(target["value"], expected, rel_tol=1e-12, abs_tol=1e-9),
                        "empirical_native_value_mismatch")
            # A false source mask stays false even when arithmetic is known.
            action_values.append({"action_index": target["action_index"], "evaluations": evaluations,
                                  "empirical_value": expected, "source_value_mask": target["masks"]["value"]})
        values.append(action_values)
    for attempt, raw in zip(attempts, raw_attempts):
        attempt["record_digest"] = candidate_digest(sources[raw["raw_record_index"]]) if raw["status"] == "recorded" else None
    return sources, attempts, report, values


def _closure(sources, attempts, protection):
    # Add every attempted draw, including failed/unexecuted aliases, before any
    # eligibility decision. Preserve source-draw aliases in metadata explicitly.
    metadata = [public_metadata(source) for source in sources]
    for attempt in attempts:
        value = deepcopy(attempt["provenance"])
        value.setdefault("audit_only", {})["source_draw_seed"] = attempt["source_draw_seed"]
        metadata.append(value)
    groups, tokens, historic = group_records(metadata, protection["components"])
    protected = {token for component in protection["components"].values() for token in component["tokens"]}
    closure = {"groups": groups, "components": {key: sorted(value) for key, value in tokens.items()},
               "historical_owners": {key: sorted(value) for key, value in historic.items()},
               "cross_split_conflicts": sorted(key for key, value in historic.items() if len(value) > 1),
               "protected_metadata_indices": [i for i, key in enumerate(groups) if tokens[key] & protected],
               "protected_candidate_indices": [i for i, key in enumerate(groups[:len(sources)]) if tokens[key] & protected]}
    return metadata, closure


def _review(receipt, sources, attempts, report, config):
    review = receipt["review"]
    if review is None: return
    object_keys(review, ("format", "decision", "producer_receipt_sha256", "reviewed_by", "evidence_sha256",
        "objective_supervision_reviewed", "full_public_conditioning_reviewed", "source_isolation_reviewed",
        "selection", "minimum_complete_roots", "minimum_positive_decision_roots", "minimum_later_combat_roots"), "native policy cohort review")
    base = deepcopy(receipt); base["review"] = None
    require(review["format"] == REVIEW_FORMAT and review["decision"] == "accepted"
            and review["producer_receipt_sha256"] == _digest(base), "exact_candidate_cohort_review_required")
    _text(review["reviewed_by"], "reviewer"); _sha(review["evidence_sha256"], "review_evidence")
    require(all(review[key] is True for key in ("objective_supervision_reviewed", "full_public_conditioning_reviewed", "source_isolation_reviewed"))
            and review["selection"] == "predeclared_all_attempts_no_replacement", "cohort_review_scope_invalid")
    complete = [source for source in sources if all(row["allocated_worlds"] > 0 and row["completed_worlds"] == row["allocated_worlds"] for row in source["targets"]["actions"])]
    counts = {"minimum_complete_roots": len(complete),
              "minimum_positive_decision_roots": sum(row["audit_only"]["selected_decision_index"] > 0 for row in complete),
              "minimum_later_combat_roots": sum(row["audit_only"]["selected_combat_index"] >= 2 and row["audit_only"]["selected_decision_index"] > 0 for row in complete)}
    for key, count in counts.items(): require(count >= integer(review[key], key, 1), "cohort_gate_failed:" + key)
    require(not report["budgetExpired"] and all(row["status"] in ("recorded", "absent") for row in attempts)
            and len(complete) == len(sources), "incomplete_or_time_selected_cohort")
    require(not receipt["closure"]["cross_split_conflicts"] and not receipt["closure"]["protected_metadata_indices"],
            "protected_source_or_historical_bridge")
    require(len({public_input_digest(row["public_input"]) for row in sources}) == len(sources), "duplicate_public_roots")
    require(any(len(row["targets"]["actions"]) >= 2 and all(action["masks"]["value"] and action.get("sample_weight", 1) > 0
                for action in row["targets"]["actions"]) for row in sources), "no_complete_policy_value_root")


def _make_receipt(raw_bytes, attempts_bytes, report_bytes, config, protection):
    validate_config(config); validate_protection(protection)
    sources, attempts, report, values = _parse_sources(raw_bytes, attempts_bytes, report_bytes, config)
    metadata, closure = _closure(sources, attempts, protection)
    lines = [line for line in raw_bytes.decode("utf-8").splitlines() if line.strip()]
    source_records = [{"record_index": index, "source_record_sha256": hashlib.sha256(line.encode("utf-8")).hexdigest(),
                       "source_record_canonical_sha256": candidate_digest(source), "source_targets_sha256": _digest(source["targets"]),
                       "conditioned_public_input_digest": public_input_digest(source["public_input"])}
                      for index, (line, source) in enumerate(zip(lines, sources))]
    return {"format": RECEIPT_FORMAT, "adapter_version": ADAPTER_VERSION,
            "source_artifacts": {"candidates": _artifact(raw_bytes, "candidates"), "attempts": _artifact(attempts_bytes, "attempts"), "report": _artifact(report_bytes, "report")},
            "student_config_sha256": _digest(config), "objective_contract": objective_contract(),
            "implementation": implementation_fingerprint(), "protection": deepcopy(protection), "protection_sha256": _digest(protection),
            "source_records": source_records, "attempts": attempts, "metadata": metadata, "closure": closure,
            "value_evaluations": values, "review": None, "fit_authorized": False, "formal_labels": False}


def validate_policy_producer_receipt(receipt, config, protection=None):
    """Rebuild all evidence and its alias closure; a checksum alone is insufficient."""
    require(isinstance(receipt, dict), "producer_receipt_required")
    object_keys(receipt, ("format", "adapter_version", "source_artifacts", "student_config_sha256", "objective_contract",
        "implementation", "protection", "protection_sha256", "source_records", "attempts", "metadata", "closure", "value_evaluations", "review", "fit_authorized", "formal_labels"), "native policy producer receipt")
    require(protection is None or _digest(receipt["protection"]) == _digest(protection), "imported_protection_changed")
    artifacts = object_keys(receipt["source_artifacts"], ("candidates", "attempts", "report"), "native source artifacts")
    raw, attempts, report = (_read_artifact(artifacts[key], key) for key in ("candidates", "attempts", "report"))
    expected = _make_receipt(raw, attempts, report, config, receipt["protection"])
    require(_digest({**receipt, "review": None}) == _digest(expected), "producer_receipt_changed")
    sources, parsed_attempts, parsed_report, _ = _parse_sources(raw, attempts, report, config)
    _review(receipt, sources, parsed_attempts, parsed_report, config)
    return receipt


def policy_producer_metadata(receipt, config):
    validate_policy_producer_receipt(receipt, config)
    return deepcopy(receipt["metadata"])


def _records(receipt, config):
    from .data_policy_v5 import OBJECTIVE_FORMAT, RECORD_FORMAT, SOURCE_FIELDS, validate_record
    artifacts = receipt["source_artifacts"]
    sources = [loads(line) for line in artifacts["candidates"]["utf8"].splitlines() if line.strip()]
    admitted = receipt["review"] is not None
    records = []
    for source, source_identity in zip(sources, receipt["source_records"]):
        audit, targets, public = source["audit_only"], source["targets"], source["public_input"]
        normalized_audit = {key: deepcopy(audit[key]) for key in (*SOURCE_FIELDS, "native_run", "actual_seed", "source_draw_seed", "native_source_run_identity")}
        normalized_audit.update(purpose="bounded-objective-pilot" if admitted else "native-objective-candidate",
            trainable=admitted, source_kind="externally_reviewed_objective_outcomes" if admitted else "native_empirical_objective_candidate",
            source_artifact_sha256=artifacts["candidates"]["sha256"], source_record_sha256=source_identity["source_record_sha256"],
            conditioned_public_input_digest=public_input_digest(public), targets_sha256=_digest(targets),
            producer_receipt_sha256=_digest(receipt))
        objective = {"format": OBJECTIVE_FORMAT, "objective_spec_sha256": _digest(CANDIDATE_PROFILE),
            "evaluator_source_sha256": _digest(SOURCE_HASHES), "calibration_evidence_sha256": None,
            "evaluation_design_sha256": _digest({"outcome_samples": audit["outcome_samples"], "versions": audit["versions"],
                "runtime_dependencies": audit["runtime_dependencies"], "declared_prior": audit["declared_prior"],
                "sampler_seeds": audit["sampler_seeds"], "exploration_seeds": audit["exploration_seeds"]}),
            "endpoint": ENDPOINT, "continuation_policy_id": audit["versions"]["continuation"],
            "independent_final_evaluation": True, "public_conditioning_scope": "full_v5",
            "objective_profile_status": "candidate", "objective_calibrated": False}
        row = {"format": RECORD_FORMAT, "public_input": deepcopy(public), "targets": deepcopy(targets),
               "audit_only": normalized_audit, "objective": objective}
        validate_record(row, config); records.append(row)
    return records


def adapt_native_policy_candidates(raw_jsonl_bytes, raw_attempts_json_bytes, raw_report_json_bytes, config, protection):
    """Return (new full-policy candidate records, unadmitted producer receipt)."""
    receipt = _make_receipt(raw_jsonl_bytes, raw_attempts_json_bytes, raw_report_json_bytes, config, protection)
    return _records(receipt, config), receipt


def admit_native_policy_cohort(receipt, review, config):
    """External exact-cohort review makes NEW pilot records, never authorizes fit."""
    validate_policy_producer_receipt(receipt, config)
    require(receipt["review"] is None, "candidate_receipt_required_for_admission")
    admitted = deepcopy(receipt); admitted["review"] = deepcopy(review)
    require(review is not None, "external_exact_cohort_review_required")
    validate_policy_producer_receipt(admitted, config)
    return _records(admitted, config), admitted


def validate_policy_record_producer(record, receipt, config, protection=None):
    validate_policy_producer_receipt(receipt, config, protection)
    require(_digest(record) in {_digest(expected) for expected in _records(receipt, config)}, "record_not_exact_producer_output")
    return record


def validate_policy_record_source(record, receipt, config, protection):
    """One caller boundary: exact record, full receipt and imported protection."""
    validate_policy_record_producer(record, receipt, config, protection)
    return deepcopy(receipt["metadata"])
