"""Explicit bounded v5 auxiliary-label admission, independent of fit permission.

The caller supplies a reviewed, exact-bound cohort receipt. Hashes establish
integrity, not authenticity or user authorization. Current development/raw
records have different envelopes and are never upgraded by this module.
"""
from __future__ import annotations

import math
import re
from .data import canonical_object_digest, is_sha256
from .native_pilot import native_source_tokens, source_run_identity
from .public_identity_v5 import PUBLIC_IDENTITY_SCHEME, public_input_digest
from .schema import integer, number, object_keys, reject
from .schema_v5 import EVIDENCE_SCHEMA, MAP_PROFILE, PUBLIC_SCHEMA

CANDIDATE_SCHEMA = "nosl.dataset.native-complete-map.candidate.v1"
CANDIDATE_KIND = "native_complete_map_auxiliary_candidate"
ADMISSION_SCHEMA = "nosl.dataset.native-complete-map.admission.v1"
ADMISSION_PROFILE = "complete-map-auxiliary-pilot-v1"
PURPOSES = ("engineering-fixture", "bounded-pilot")
PRIOR_SCHEMA = "nosl.native-map-rewards-state-tape-prior.v1"
SAMPLER = "nosl-native-map-rewards-tape-marginalized-v1-public-evidence-v2"
POSTERIOR = "owned-native-map-rewards-tape-marginalized-v1-public-evidence-v2"
SUPPORT = "native-standard-act0-complete-map-producer-v1"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
COUNTS = ("allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")
AUXILIARY_HEADS = ("win_probability", "death_probability", "expected_final_hp", "hp_distribution", "potion_net_change")
PINNED_VERSIONS = {
    "public_schema": PUBLIC_SCHEMA, "observation_schema": "nosl.public.v3", "public_evidence": EVIDENCE_SCHEMA,
    "map_profile": MAP_PROFILE, "map_support": SUPPORT,
    "teacher": "nosl-full-combat-teacher-v1:T0", "continuation": "nosl-public-rules-v2",
    "objective": "nosl_silent_a10_terminal_v4_candidate", "controller": "nosl.controller.inactive.v1",
    "endpoint": ENDPOINT, "rules": "0.111.0", "simulator": "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0",
    "source_script": "nosl-natural-public-script-v3", "dataset": CANDIDATE_SCHEMA,
}


def require(condition, reason):
    if not condition: reject("native_v5:" + reason)


def _text(value, name):
    require(isinstance(value, str) and bool(value.strip()), name + "_missing")
    return value


def _sha(value, name):
    require(is_sha256(value), name + "_invalid")
    return value


def _seeds(values, name, *, nonempty=False):
    require(isinstance(values, list) and (not nonempty or bool(values)), name + "_invalid")
    for seed in values: integer(seed, name, 0, 2**64 - 1)
    require(len(set(values)) == len(values), name + "_duplicates")
    return values


def candidate_digest(record):
    """Bind original candidate bytes semantically; prepared decorations are separate."""
    return canonical_object_digest(record)


def validate_versions(versions):
    object_keys(versions, [*PINNED_VERSIONS, "source_prior", "runtime_source_sha256", "sampler", "posterior_profile"], "v5 versions")
    require(all(versions[k] == v for k, v in PINNED_VERSIONS.items()), "unreviewed_version")
    require(isinstance(versions["sampler"], str) and re.fullmatch(r"nosl-native-map-rewards-tape-marginalized-v[1-9][0-9]*-public-evidence-v2", versions["sampler"]) is not None
            and isinstance(versions["posterior_profile"], str) and re.fullmatch(r"owned-native-map-rewards-tape-marginalized-v[1-9][0-9]*-public-evidence-v2", versions["posterior_profile"]) is not None, "sampler_profile_mismatch")
    _sha(versions["source_prior"], "source_prior_identity")
    _sha(versions["runtime_source_sha256"], "runtime_source_sha256")
    return versions


def validate_candidate(record, config):
    # Import lazily so data_v5 can dispatch here without a module cycle.
    from .data_v5 import validate_targets
    object_keys(record, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "v5 candidate")
    require(record["schema_version"] == CANDIDATE_SCHEMA and record["record_kind"] == CANDIDATE_KIND,
            "explicit_new_candidate_envelope_required")
    public, targets, audit = record["public_input"], record["targets"], record["audit_only"]
    validate_targets(targets, public, config)
    require(isinstance(audit, dict), "audit_missing")
    require(public["controller_context"] == {"status": "inactive"} and "plan" not in targets,
            "auxiliary_profile_requires_inactive_controller")
    require(public["history_complete"] is True and public["public_evidence"]["completeFromRunStart"] is True
            and public["observation"]["runContext"]["completeFromRunStart"] is True, "complete_context_required")
    events = public["public_evidence"]["events"]
    initial_owner = next((e for e in events if e["payload"]["kind"] == "owner_started"
                          and e["payload"]["ownerKind"] == "map"), None)
    initial_map = next((e for e in events if e["payload"]["kind"] == "map"), None)
    require(initial_owner is not None and initial_owner["payload"]["actIndex"] == 0
            and initial_map is not None and initial_map["ownerOrdinal"] == initial_owner["ownerOrdinal"]
            and initial_map["payload"]["currentMap"]["status"] == "complete", "initial_act0_complete_map_required")
    # The reviewed producer explicitly emits Missing outside act zero. Full run
    # evidence still conditions those exact captures and their visible slices;
    # only the initial certified graph is required for Map marginalization.
    # validate_targets/schema_v5 already reject absent, null or partial captures.
    require(all(public["legal_mask"]), "native_all_candidate_coverage_required")
    for field, value in (("trainable", False), ("formal_labels", False), ("objective_calibrated", False),
                         ("source_seed_conditioning", False), ("independent_final_evaluation", True), ("native_run", True), ("native_default_start", True)):
        require(audit.get(field) is value, "flag_invalid:" + field)
    require(audit.get("purpose") in PURPOSES, "purpose_invalid")
    expected_source = "synthetic_engineering_fixture" if audit["purpose"] == "engineering-fixture" else "natural_under_explicit_label_tape_prior"
    require(audit.get("source_kind") == expected_source, "source_kind_mismatch")
    for field in ("collection_id", "draw_id", "source_run_group", "source_combat_id", "branch_family", "actual_seed"):
        _text(audit.get(field), field)
    for field in ("source_draw_seed", "selected_combat_index", "selected_decision_index"):
        integer(audit.get(field), field, 0, 2**64 - 1)
    require(audit["selected_combat_index"] == public["observation"]["runContext"]["combatEntryIndex"]
            and all(a["revision"] == audit["selected_decision_index"] for a in public["candidate_actions"])
            and sum(e["kind"] == "action" for e in public["observation"]["history"]) == audit["selected_decision_index"], "public_root_coordinate_mismatch")
    from .schema_v5 import loads
    entries = [loads(e["detail"]) for e in public["observation"]["history"] if e["kind"] == "native_entry_assets"]
    require(len(entries) == 1, "public_entry_anchor_required")
    entry = entries[0]
    require(entry["hp"] == public["observation"]["startHp"], "public_entry_hp_mismatch")
    require(audit.get("native_source_run_identity") == source_run_identity(audit["actual_seed"]), "source_identity_mismatch")
    require(audit.get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME
            and audit.get("conditioned_public_input_digest") == public_input_digest(public), "full_conditioning_digest_mismatch")
    _sha(audit.get("public_state_digest"), "source_public_state_digest")
    require(audit.get("adapter_version") == ADAPTER_VERSION, "candidate_adapter_required")
    for field in ("source_artifact_sha256", "source_record_sha256", "source_target_sha256", "build_receipt_sha256"):
        _sha(audit.get(field), field)
    versions = validate_versions(audit.get("versions"))
    require(audit.get("public_map_observation_profile") == MAP_PROFILE
            and audit.get("map_marginalization_contract") == SUPPORT, "producer_contract_mismatch")
    dependencies = object_keys(audit.get("runtime_dependencies"), ("upstream_commit", "wrapper_source_sha256", "vendor_source_sha256", "worker_assembly_sha256", "core_assembly_sha256", "runtime_version"), "runtime dependencies")
    require(dependencies["upstream_commit"] == versions["simulator"], "runtime_upstream_mismatch")
    for field in ("wrapper_source_sha256", "vendor_source_sha256", "worker_assembly_sha256", "core_assembly_sha256"):
        _sha(dependencies[field], field)
    _text(dependencies["runtime_version"], "runtime_version")
    require(versions["runtime_source_sha256"] == canonical_object_digest(dependencies), "runtime_dependency_digest_mismatch")
    prior = audit.get("declared_prior")
    require(isinstance(prior, dict) and prior.get("schemaVersion") == PRIOR_SCHEMA, "new_map_prior_required")
    integer(prior.get("eligibleCombats"), "eligibleCombats", 1)
    integer(prior.get("eligibleDecisionsPerCombat"), "eligibleDecisionsPerCombat", 1)
    require(audit["selected_combat_index"] < prior["eligibleCombats"] and audit["selected_decision_index"] < prior["eligibleDecisionsPerCombat"], "public_coordinate_outside_prior")
    execution = prior.get("execution")
    require(isinstance(execution, dict) and execution.get("publicContextProfile") == "nosl.public-run-context.v1"
            and execution.get("publicEvidenceProfile") == EVIDENCE_SCHEMA
            and execution.get("publicMapObservationProfile") == MAP_PROFILE
            and execution.get("sourcePolicyId") == versions["continuation"]
            and execution.get("outsideCombatScript") == versions["source_script"], "prior_execution_mismatch")
    # C# records its exact serialized declaration hash. Also bind a portable
    # canonical digest so Python never guesses C# property order or omission.
    _sha(audit.get("source_prior_identity"), "source_prior_identity")
    require(versions["source_prior"] == audit["source_prior_identity"]
            and audit.get("declared_prior_sha256") == canonical_object_digest(prior), "prior_digest_mismatch")
    eval_seeds = _seeds(audit.get("sampler_seeds"), "sampler_seeds", nonempty=True)
    explore = _seeds(audit.get("exploration_seeds"), "exploration_seeds")
    require(not explore and audit.get("n_exploration") == 0, "T0_only")
    require(audit["source_draw_seed"] not in eval_seeds and not set(eval_seeds) & set(explore), "seed_overlap")
    integer(audit.get("n_independent_eval"), "n_independent_eval", 1)
    require(audit["n_independent_eval"] == len(eval_seeds), "seed_count_mismatch")
    require(not targets["pairwise"] and not targets["equivalent_action_set"]
            and audit.get("ranking_evidence") == "MASKED_NO_CERTIFIED_UTILITY_SUPPORT", "uncertified_ranking")
    samples = audit.get("outcome_samples")
    require(isinstance(samples, list) and len(samples) == len(targets["actions"]), "outcome_samples_missing")
    totals = {k: 0 for k in COUNTS}
    for index, (sample, target) in enumerate(zip(samples, targets["actions"])):
        require(target["action_index"] == index and isinstance(sample, dict) and sample.get("action_index") == index,
                "candidate_alignment")
        require(not target["masks"]["value"] and target["value"] is None, "auxiliary_profile_has_no_utility_labels")
        require(sample.get("world_seeds") == eval_seeds
                and sample.get("conditioned_public_input_digest") == audit["conditioned_public_input_digest"], "outcome_world_or_root_binding_mismatch")
        outcomes = sample.get("outcomes")
        require(isinstance(outcomes, list) and len(outcomes) == len(eval_seeds), "allocated_outcome_ledger_mismatch")
        counts = {k: 0 for k in COUNTS}; counts["allocated_worlds"] = len(outcomes)
        for outcome in outcomes:
            require(isinstance(outcome, dict), "outcome_invalid")
            kind = outcome.get("terminalKind")
            require(outcome.get("continuationPolicyId") == versions["continuation"], "outcome_continuation_mismatch")
            require(outcome.get("hpAtCombatStart") == public["observation"]["startHp"], "outcome_fixed_anchor_mismatch")
            if kind in ("Win", "Loss"):
                require(all(outcome.get(k) is True for k in ("settlementComplete", "hpEventDiagnosticsComplete",
                    "resourceProvenanceComplete", "inventorySnapshotsComplete", "permanentChangesComplete")), "incomplete_terminal_diagnostics")
                require(outcome.get("maxHpStart") == entry["maxHp"], "outcome_initial_max_hp_mismatch")
                hp = integer(outcome.get("hpAfterSettlement"), "final_hp")
                maximum = integer(outcome.get("maxHpAfterSettlement"), "final_max_hp", 1)
                damage = number(outcome.get("cumulativeHpDamage"), "damage", 0, float("inf"))
                healing = number(outcome.get("healingReceived"), "healing", 0, float("inf"))
                other = number(outcome.get("otherHpAdjustment"), "other_hp")
                require(hp <= maximum and math.isclose(hp, outcome["hpAtCombatStart"] - damage + healing + other, abs_tol=1e-7)
                        and outcome.get("playerAlive") is (hp > 0) and (kind != "Win" or hp > 0)
                        and outcome.get("settlementProfileId") == ENDPOINT, "terminal_facts_inconsistent")
                for key in ("inventoryStart", "inventoryEnd"):
                    items = outcome.get(key)
                    require(isinstance(items, list), "inventory_missing")
                    for item in items:
                        require(isinstance(item, dict), "inventory_invalid")
                        _text(item.get("resourceId"), "resource_id"); integer(item.get("count"), "inventory_count")
                require(isinstance(outcome.get("resourceEvents"), list) and isinstance(outcome.get("permanentChanges"), list), "resource_ledger_missing")
                def quantities(items):
                    result = {}
                    for item in items: result[item["resourceId"]] = result.get(item["resourceId"], 0) + item["count"]
                    return {k: v for k, v in result.items() if v}
                expected_start = {p: entry["potions"].count(p) for p in entry["potions"] if p is not None}
                require(quantities(outcome["inventoryStart"]) == expected_start, "outcome_inventory_anchor_mismatch")
                expected_end = dict(expected_start)
                for event in outcome["resourceEvents"]:
                    require(isinstance(event, dict) and event.get("kind") in ("consumed", "generated", "discarded"), "resource_event_invalid")
                    _text(event.get("resourceId"), "resource_id"); _text(event.get("publicSource"), "resource_source")
                    quantity = integer(event.get("quantity"), "resource_quantity")
                    key = event["resourceId"]
                    expected_end[key] = expected_end.get(key, 0) + (quantity if event["kind"] == "generated" else -quantity)
                require(all(v >= 0 for v in expected_end.values()) and {k: v for k, v in expected_end.items() if v} == quantities(outcome["inventoryEnd"]), "resource_ledger_does_not_close")
                max_hp_change = 0
                for change in outcome["permanentChanges"]:
                    require(isinstance(change, dict), "permanent_change_invalid")
                    _text(change.get("kind"), "permanent_change_kind")
                    amount = number(change.get("amount"), "permanent_change_amount")
                    if change["kind"] == "max_hp": max_hp_change += amount
                require(max_hp_change == maximum - entry["maxHp"], "permanent_max_hp_ledger_mismatch")
                counts["completed_worlds"] += 1
            else:
                require(kind in ("ComputeTruncated", "EngineError", "PolicyNonterminating"), "unknown_outcome")
                counts[{"ComputeTruncated": "truncated_worlds", "EngineError": "error_worlds", "PolicyNonterminating": "other_worlds"}[kind]] += 1
        for key in COUNTS:
            integer(target.get(key), key)
            require(target[key] == counts[key], "outcome_count_mismatch:" + key)
            totals[key] += counts[key]
        if counts["completed_worlds"] != len(outcomes):
            require(target["quality"] == "unresolved" and not any(target["masks"].values()), "incomplete_mass_has_targets")
            continue
        empirical = {
            "win_probability": sum(o["terminalKind"] == "Win" for o in outcomes) / len(outcomes),
            "death_probability": sum(o["playerAlive"] is False for o in outcomes) / len(outcomes),
            "expected_final_hp": sum(o["hpAfterSettlement"] for o in outcomes) / len(outcomes),
            "potion_net_change": sum(sum(x["count"] for x in o["inventoryEnd"]) - sum(x["count"] for x in o["inventoryStart"]) for o in outcomes) / len(outcomes),
        }
        for head, value in empirical.items():
            if target["masks"][head]: require(math.isclose(target[head], value, rel_tol=1e-7, abs_tol=1e-7), "empirical_target_mismatch:" + head)
        distribution = [{"hp": hp, "probability": sum(o["hpAfterSettlement"] == hp for o in outcomes) / len(outcomes)}
                        for hp in sorted({o["hpAfterSettlement"] for o in outcomes})]
        if target["masks"]["hp_distribution"]: require(target["hp_distribution"] == distribution, "empirical_target_mismatch:hp_distribution")
    require(audit.get("n_error") == totals["error_worlds"]
            and audit.get("n_unresolved") == totals["truncated_worlds"] + totals["other_worlds"], "audit_count_mismatch")
    costs = audit.get("costs")
    require(isinstance(costs, dict), "costs_missing")
    for key in ("root_candidates", "worlds_allocated", "worlds_completed", "rollout_decisions", "elapsed_seconds", "clone_seconds", "settlement_seconds", "peak_worker_memory_bytes"):
        number(costs.get(key), "cost." + key, 0, float("inf"))
    require(costs["root_candidates"] == len(samples) and costs["worlds_allocated"] == totals["allocated_worlds"]
            and costs["worlds_completed"] == totals["completed_worlds"], "cost_count_mismatch")
    return record


def has_usable_targets(record):
    return any(row.get("sample_weight", 1) > 0 and any(row["masks"][h] for h in AUXILIARY_HEADS)
               for row in record["targets"]["actions"])


def cohort_statistics(records, attempts):
    complete = [r for r in records if all(a["allocated_worlds"] > 0 and a["completed_worlds"] == a["allocated_worlds"]
                                         for a in r["targets"]["actions"])]
    return {"requested_draws": len(attempts), "recorded_roots": len(records), "complete_roots": len(complete),
            "positive_decision_complete_roots": sum(r["audit_only"]["selected_decision_index"] > 0 for r in complete),
            "later_combat_complete_roots": sum(r["audit_only"]["selected_combat_index"] >= 2 and r["audit_only"]["selected_decision_index"] > 0 for r in complete),
            "engine_errors": sum(a["error_worlds"] for r in records for a in r["targets"]["actions"])}


def validate_admission(receipt, records, attempts, protection_sha256, config):
    object_keys(receipt, ("schema_version", "profile", "purpose", "collection_id", "records_sha256", "attempts_sha256",
        "protection_sha256", "student_config_sha256", "candidate_digests", "candidate_metadata_sha256", "versions", "predeclared_source_draw_seeds", "selection", "budget_expired",
        "review", "build_receipt_json", "build_receipt_sha256", "minimum_complete_roots", "minimum_positive_decision_roots", "minimum_later_combat_roots"), "v5 admission")
    require(receipt["schema_version"] == ADMISSION_SCHEMA and receipt["profile"] == ADMISSION_PROFILE, "admission_profile_unknown")
    require(receipt["purpose"] in PURPOSES, "purpose_invalid")
    require(receipt["records_sha256"] == canonical_object_digest(records)
            and receipt["attempts_sha256"] == canonical_object_digest(attempts)
            and receipt["protection_sha256"] == protection_sha256
            and receipt["student_config_sha256"] == canonical_object_digest(config), "admission_binding_mismatch")
    require(receipt["candidate_digests"] == [candidate_digest(r) for r in records], "candidate_digest_list_mismatch")
    from .prepare_v5 import candidate_projection
    require(receipt["candidate_metadata_sha256"] == canonical_object_digest([candidate_projection(r) for r in records]), "reviewed_candidate_metadata_mismatch")
    validate_versions(receipt["versions"])
    import hashlib
    from .schema_v5 import loads
    require(isinstance(receipt["build_receipt_json"], str) and hashlib.sha256(receipt["build_receipt_json"].encode()).hexdigest() == receipt["build_receipt_sha256"], "build_receipt_artifact_mismatch")
    build = loads(receipt["build_receipt_json"])
    require(canonical_object_digest(build) == receipt["versions"]["runtime_source_sha256"], "build_receipt_dependency_mismatch")
    require(receipt["selection"] == "predeclared_all_attempts_no_replacement", "selection_contract_invalid")
    require(type(receipt["budget_expired"]) is bool, "budget_flag_invalid")
    planned = _seeds(receipt["predeclared_source_draw_seeds"], "predeclared_sources", nonempty=True)
    require(isinstance(attempts, list) and len(attempts) == len(planned), "attempt_denominator_incomplete")
    require(all(not set(planned) & set(r["audit_only"].get("sampler_seeds", []) + r["audit_only"].get("exploration_seeds", [])) for r in records), "cohort_source_evaluation_seed_overlap")
    seen, referenced = set(), []
    by_digest = {candidate_digest(r): r for r in records}
    require(len(by_digest) == len(records), "duplicate_candidates")
    for seed, attempt in zip(planned, attempts):
        object_keys(attempt, ("draw_id", "source_draw_seed", "status", "record_digest", "detail", "provenance"), "v5 attempt")
        _text(attempt["draw_id"], "draw_id")
        require(attempt["draw_id"] not in seen and attempt["source_draw_seed"] == seed, "attempt_identity_mismatch")
        seen.add(attempt["draw_id"])
        from .protection_v5 import validate_metadata
        validate_metadata(attempt["provenance"])
        integer(attempt["source_draw_seed"], "source_draw_seed", 0, 2**64 - 1)
        provenance_audit = attempt["provenance"].get("audit_only", {})
        require("source_draw_seed" not in provenance_audit
                or provenance_audit["source_draw_seed"] == attempt["source_draw_seed"], "attempt_source_draw_seed_mismatch")
        require(attempt["status"] in ("recorded", "absent", "failed", "not_executed"), "attempt_status_invalid")
        require(native_source_tokens(attempt["provenance"].get("audit_only")), "attempt_source_provenance_required")
        require(attempt["detail"] is None or isinstance(attempt["detail"], str), "attempt_detail_invalid")
        if attempt["status"] == "recorded":
            row = by_digest.get(attempt["record_digest"])
            require(row is not None, "attempt_candidate_missing")
            audit = row["audit_only"]
            require(audit["draw_id"] == attempt["draw_id"] and audit["source_draw_seed"] == seed
                    and native_source_tokens(attempt["provenance"]["audit_only"]) == native_source_tokens(audit), "attempt_candidate_mismatch")
            referenced.append(attempt["record_digest"])
        else: require(attempt["record_digest"] is None, "absent_attempt_has_labels")
    require(len(referenced) == len(records) and set(referenced) == set(by_digest), "unaccounted_candidate")
    for row in records:
        validate_candidate(row, config)
        require(row["audit_only"]["runtime_dependencies"] == build and row["audit_only"]["build_receipt_sha256"] == receipt["build_receipt_sha256"], "cohort_build_receipt_mismatch")
        require(row["audit_only"]["collection_id"] == receipt["collection_id"] and row["audit_only"]["purpose"] == receipt["purpose"]
                and row["audit_only"]["versions"] == receipt["versions"], "cohort_versions_or_purpose_mismatch")
    review = object_keys(receipt["review"], ("decision", "evidence_kind", "evidence_sha256"), "v5 review")
    _sha(review["evidence_sha256"], "review_evidence")
    expected = "synthetic_contract_only" if receipt["purpose"] == "engineering-fixture" else "predeclared_fresh_cohort_quality"
    require(review["decision"] == "accepted" and review["evidence_kind"] == expected, "quality_review_not_accepted")
    stats = cohort_statistics(records, attempts)
    for key, count in (("minimum_complete_roots", "complete_roots"), ("minimum_positive_decision_roots", "positive_decision_complete_roots"), ("minimum_later_combat_roots", "later_combat_complete_roots")):
        integer(receipt[key], key, 0)
        require(stats[count] >= receipt[key], "cohort_gate_failed:" + count)
    require(bool(records), "empty_cohort")
    if receipt["purpose"] == "bounded-pilot":
        require(len({public_input_digest(r["public_input"]) for r in records}) == len(records), "pilot_duplicate_public_roots")
        require(receipt["minimum_complete_roots"] > 0 and receipt["minimum_positive_decision_roots"] > 0
                and receipt["minimum_later_combat_roots"] > 0, "pilot_breadth_gates_required")
        require(not receipt["budget_expired"] and all(a["status"] in ("recorded", "absent") for a in attempts)
                and stats["engine_errors"] == 0 and stats["complete_roots"] == len(records), "pilot_incomplete_or_time_selected_cohort")
        require(all(has_usable_targets(r) for r in records), "pilot_no_usable_targets")
    return stats


RAW_CANDIDATE_SCHEMA = "nosl.native-complete-map.raw-candidate.v1"
RAW_CANDIDATE_KIND = "native_complete_map_raw_candidate"
ADAPTER_VERSION = "nosl.native-complete-map.candidate-adapter.v1"


def adapt_native_candidates(raw_jsonl_bytes, config):
    """New native export -> auxiliary candidates; old diagnostic envelopes reject.

    The original bytes remain immutable outside this pure adapter. It never
    asserts cohort quality, changes outcome facts, or supplies missing worlds.
    Only unavailable utility/ranking heads are explicitly masked by this profile.
    """
    from copy import deepcopy
    import hashlib
    from .schema_v5 import loads
    require(isinstance(raw_jsonl_bytes, bytes), "raw_export_bytes_required")
    artifact_hash = hashlib.sha256(raw_jsonl_bytes).hexdigest()
    result = []
    for line in raw_jsonl_bytes.decode("utf-8").splitlines():
        if not line.strip(): continue
        source = loads(line)
        object_keys(source, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "raw v5 candidate")
        require(source["schema_version"] == RAW_CANDIDATE_SCHEMA and source["record_kind"] == RAW_CANDIDATE_KIND,
                "new_native_export_required_no_diagnostic_promotion")
        row = deepcopy(source)
        row["schema_version"], row["record_kind"] = CANDIDATE_SCHEMA, CANDIDATE_KIND
        audit = row["audit_only"]
        require(isinstance(audit, dict) and isinstance(audit.get("versions"), dict), "raw_audit_missing")
        digest = public_input_digest(row["public_input"])
        if "conditioned_public_input_digest" in audit:
            require(audit["conditioned_public_input_digest"] == digest, "raw_conditioning_digest_mismatch")
        for sample in audit.get("outcome_samples", []):
            if "conditioned_public_input_digest" in sample:
                require(sample["conditioned_public_input_digest"] == digest, "raw_outcome_root_mismatch")
            sample["conditioned_public_input_digest"] = digest
        audit.update(public_identity_scheme=PUBLIC_IDENTITY_SCHEME, conditioned_public_input_digest=digest,
                     declared_prior_sha256=canonical_object_digest(audit.get("declared_prior")),
                     source_artifact_sha256=artifact_hash, source_record_sha256=candidate_digest(source),
                     adapter_version=ADAPTER_VERSION, source_target_sha256=canonical_object_digest(source["targets"]))
        audit["versions"]["dataset"] = CANDIDATE_SCHEMA
        audit["versions"]["runtime_source_sha256"] = canonical_object_digest(audit.get("runtime_dependencies"))
        for target in row["targets"]["actions"]:
            target["value"], target["masks"]["value"] = None, False
            if target["quality"] == "complete": target["quality"] = "objective_value_unresolved"
        row["targets"]["pairwise"], row["targets"]["equivalent_action_set"] = [], []
        audit["ranking_evidence"] = "MASKED_NO_CERTIFIED_UTILITY_SUPPORT"
        validate_candidate(row, config)
        result.append(row)
    require(bool(result), "raw_export_empty")
    return result


def adapt_native_attempts(raw_attempts_json_bytes, records):
    """Bind every fresh producer attempt to adapted rows; never replace draws."""
    from copy import deepcopy
    from .protection_v5 import validate_metadata
    from .schema_v5 import loads
    require(isinstance(raw_attempts_json_bytes, bytes), "raw_attempt_bytes_required")
    raw = loads(raw_attempts_json_bytes.decode("utf-8"))
    require(isinstance(raw, list), "raw_attempt_list_required")
    result, referenced, draws, seeds = [], [], set(), set()
    for item in raw:
        object_keys(item, ("draw_id", "source_draw_seed", "status", "raw_record_index", "detail", "provenance"), "native raw attempt")
        _text(item["draw_id"], "draw_id"); integer(item["source_draw_seed"], "source_draw_seed", 0, 2**64 - 1)
        require(item["draw_id"] not in draws and item["source_draw_seed"] not in seeds, "duplicate_raw_attempt")
        draws.add(item["draw_id"]); seeds.add(item["source_draw_seed"])
        validate_metadata(item["provenance"])
        provenance_audit = item["provenance"].get("audit_only", {})
        require("source_draw_seed" not in provenance_audit
                or provenance_audit["source_draw_seed"] == item["source_draw_seed"], "attempt_source_draw_seed_mismatch")
        require(native_source_tokens(item["provenance"].get("audit_only")), "attempt_source_provenance_required")
        require(item["detail"] is None or isinstance(item["detail"], str), "attempt_detail_invalid")
        value = deepcopy(item); index = value.pop("raw_record_index")
        if item["status"] == "recorded":
            integer(index, "raw_record_index", 0, len(records) - 1)
            row = records[index]; audit = row["audit_only"]
            require(audit["draw_id"] == item["draw_id"] and audit["source_draw_seed"] == item["source_draw_seed"]
                    and native_source_tokens(audit) == native_source_tokens(item["provenance"]["audit_only"]), "raw_attempt_candidate_mismatch")
            require(index not in referenced, "duplicate_attempt_candidate")
            referenced.append(index); value["record_digest"] = candidate_digest(row)
        else:
            require(item["status"] in ("absent", "failed", "not_executed") and index is None, "raw_attempt_status_invalid")
            value["record_digest"] = None
        result.append(value)
    require(set(referenced) == set(range(len(records))), "unaccounted_raw_candidate")
    return result
