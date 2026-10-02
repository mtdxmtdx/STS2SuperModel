#!/usr/bin/env python3
"""Validate whole decision records and split provenance components (stdlib only).

No labels are calculated, repaired, zero-filled, or inferred by this tool. Invalid
roots are quarantined whole. Even rejected roots participate in provenance union
so that filtering cannot cut a transitive bridge between accepted roots. Source
IDs and computed public-input digests remain strictly in audit metadata.

Example (the mode is deliberately mandatory):
  python -B tools/prepare_dataset.py artifacts/teacher.jsonl \
      --output-dir artifacts/prepared --mode engineering-smoke
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from copy import deepcopy
from contextlib import contextmanager
import hashlib
import json
import math
import os
from pathlib import Path
import sys
from typing import Any

REPO = Path(__file__).resolve().parents[1]
PIPELINE_VERSION = "nosl.dataset.prepare.v3"
sys.path.insert(0, str(REPO / "python"))
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME, public_input_digest
from nosl.data import (REGISTRY_VERSION, is_sha256, registry_bytes, registry_hash,
                                  validate_binding, validate_components, validate_registry,
                                  validate_state_protection)
SPLITS = ("train", "validation", "test")
HEADS = ("value", "win_probability", "death_probability", "expected_final_hp",
         "hp_distribution", "potion_net_change")
COUNT_FIELDS = ("allocated_worlds", "completed_worlds", "truncated_worlds",
                "error_worlds", "other_worlds")
PROVENANCE_FIELDS = ("source_run_group", "source_combat_id", "branch_family", "public_state_digest")
COST_FIELDS = ("root_candidates", "worlds_allocated", "worlds_completed",
               "rollout_decisions", "elapsed_seconds", "clone_seconds",
               "settlement_seconds", "peak_worker_memory_bytes")


class ValidationError(ValueError):
    """The entire root must be quarantined; never remove just an action."""


def canonical_json(value: Any) -> str:
    # JSON numbers 1 and 1.0 mean the same public numeric feature.
    def normalize(item: Any) -> Any:
        if isinstance(item, float) and math.isfinite(item) and item.is_integer():
            return int(item)
        if isinstance(item, list):
            return [normalize(x) for x in item]
        if isinstance(item, dict):
            return {k: normalize(v) for k, v in item.items()}
        return item
    return json.dumps(normalize(value), sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False, allow_nan=False)


def public_digest(public_input: dict) -> str:
    return public_input_digest(public_input)


def finite_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def nonnegative_integer(value: Any) -> bool:
    return type(value) is int and value >= 0


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise ValidationError(reason)


def validate_public(public: Any, student_config: dict | None = None) -> None:
    require(isinstance(public, dict), "public_input_not_object")
    required = {"schema_version", "observation", "history_complete", "controller_context",
                "candidate_actions", "legal_mask"}
    require(set(public) == required, "public_input_fields_not_whitelisted")
    require(public["history_complete"] is True, "history_incomplete")
    require(isinstance(public["observation"], dict), "observation_not_object")
    require(isinstance(public["controller_context"], dict), "controller_context_not_object")
    # The M6 validator is dependency-free; use its exhaustive nested whitelist.
    # A missing validator is a hard error, never a silent validation downgrade.
    python_root = str(REPO / "python")
    if python_root not in sys.path:
        sys.path.insert(0, python_root)
    try:
        from nosl.schema import validate_public as validate_student_public, load_config
        validate_student_public(public, student_config or load_config(REPO / "configs/student.pilot.json"))
    except ImportError as exc:
        raise ValidationError("student_public_validator_unavailable") from exc
    except (ValueError, TypeError, KeyError) as exc:
        raise ValidationError("public_input_invalid: " + str(exc)) from exc
    candidates = public["candidate_actions"]
    require(isinstance(candidates, list) and bool(candidates), "candidates_empty_or_invalid")
    require(isinstance(public["legal_mask"], list) and len(public["legal_mask"]) == len(candidates)
            and all(type(x) is bool for x in public["legal_mask"]), "legal_mask_invalid")
    require(any(public["legal_mask"]), "no_legal_candidates")
    require(len({canonical_json(c) for c in candidates}) == len(candidates), "duplicate_candidates")


def action_counts(record: dict, action: dict, index: int) -> dict:
    """Accept counters directly on actions or in a strictly aligned audit array."""
    if all(name in action for name in COUNT_FIELDS):
        return {name: action[name] for name in COUNT_FIELDS}
    audit_counts = record["audit_only"].get("action_counts")
    if isinstance(audit_counts, list) and index < len(audit_counts):
        counts = audit_counts[index]
        require(isinstance(counts, dict) and counts.get("action_index") == index,
                "audit_action_counts_alignment_invalid")
        require(all(name in counts for name in COUNT_FIELDS), "action_world_counts_missing")
        return {name: counts[name] for name in COUNT_FIELDS}
    raise ValidationError("action_world_counts_missing")


def has_usable_targets(record: dict) -> bool:
    """Whether an already validated whole root supplies positive-weight targets."""
    return any(action.get("sample_weight", 1) > 0 and any(action["masks"].values())
               for action in record["targets"]["actions"])


def validate_record(record: Any, config: dict, mode: str, student_config: dict | None = None,
                    *, require_usable: bool = True) -> None:
    """Check all evidence; optionally allow structurally valid diagnostic-only roots.

    Preparation keeps the default trainability requirement. Reporting can disable
    only that final requirement, never schema, provenance or outcome accounting.
    """
    require(type(require_usable) is bool, "require_usable_must_be_boolean")
    require(isinstance(record, dict) and set(record) == {"public_input", "targets", "audit_only"},
            "decision_record_fields_invalid")
    validate_public(record["public_input"], student_config)
    public, targets, audit = record["public_input"], record["targets"], record["audit_only"]
    require(isinstance(audit, dict), "audit_only_not_object")
    for field in PROVENANCE_FIELDS:
        require(isinstance(audit.get(field), str) and bool(audit[field].strip()),
                "provenance_missing:" + field)
    require(audit.get("source_kind") in config["source_kinds"], "source_kind_undeclared")
    if audit["source_kind"] == "natural":
        require(isinstance(audit.get("natural_reachability_evidence"), str)
                and bool(audit["natural_reachability_evidence"].strip()),
                "natural_source_requires_reachability_evidence")
    required_versions = {"teacher", "continuation", "objective", "public_schema", "simulator"}
    require(isinstance(audit.get("versions"), dict) and required_versions <= set(audit["versions"])
            and all(isinstance(v, str) and bool(v.strip()) for v in audit["versions"].values()), "versions_missing")
    require(audit["versions"]["public_schema"] == public["schema_version"], "public_schema_version_mismatch")
    require(audit.get("independent_final_evaluation") is True, "independent_eval_unverified")
    if "sampler_seeds" in audit and "exploration_seeds" in audit:
        require(isinstance(audit["sampler_seeds"], list) and isinstance(audit["exploration_seeds"], list)
                and all(nonnegative_integer(seed) for seed in audit["sampler_seeds"] + audit["exploration_seeds"]), "sampler_seed_audit_invalid")
        require(not set(audit["sampler_seeds"]) & set(audit["exploration_seeds"]), "exploration_evaluation_seed_overlap")
        for field, count_field in (("sampler_seeds", "n_independent_eval"), ("exploration_seeds", "n_exploration")):
            require(len(audit[field]) == audit.get(count_field) and len(set(audit[field])) == len(audit[field]), "sampler_seed_count_or_uniqueness_invalid")
    for field in ("n_exploration", "n_independent_eval", "n_unresolved", "n_error"):
        require(nonnegative_integer(audit.get(field)), "audit_count_invalid:" + field)
    if mode == "formal":
        require(audit.get("engineering_smoke") is False, "engineering_smoke_not_formal_data")
        require(audit.get("objective_calibrated") is True, "objective_uncalibrated")
        require(audit.get("independent_final_evaluation") is True, "independent_eval_unverified")
    require(isinstance(targets, dict) and set(targets) == {"actions", "pairwise", "equivalent_action_set"}, "target_fields_invalid")
    actions = targets.get("actions")
    require(isinstance(actions, list) and len(actions) == len(public["candidate_actions"]),
            "candidate_target_coverage_incomplete")
    all_complete = True
    for index, action in enumerate(actions):
        require(isinstance(action, dict) and type(action.get("action_index")) is int
                and action["action_index"] == index, "action_index_alignment_invalid")
        allowed_fields = {"action_index", "quality", "masks", *HEADS, *COUNT_FIELDS, "sample_weight"}
        require(set(action) <= allowed_fields, "action_target_fields_unknown")
        require(finite_number(action.get("sample_weight", 1)) and 0 <= action.get("sample_weight", 1) <= 1, "sample_weight_invalid")
        counts = action_counts(record, action, index)
        require(all(nonnegative_integer(v) for v in counts.values()), "action_world_counts_invalid")
        require(counts["allocated_worlds"] == sum(counts[name] for name in COUNT_FIELDS[1:]),
                "world_count_conservation_failed")
        complete = counts["allocated_worlds"] > 0 and counts["completed_worlds"] == counts["allocated_worlds"]
        all_complete &= complete
        quality = action.get("quality")
        require(quality in ("complete", "unresolved", "objective_value_unresolved"), "target_quality_unknown")
        require(quality == "unresolved" or complete, "incomplete_worlds_claim_complete")
        masks = action.get("masks")
        require(isinstance(masks, dict) and set(masks) == set(HEADS)
                and all(type(v) is bool for v in masks.values()), "target_masks_invalid")
        require(complete or not any(masks.values()), "incomplete_worlds_have_point_targets")
        require(quality != "unresolved" or not any(masks.values()), "unresolved_action_has_point_targets")
        require(quality != "objective_value_unresolved" or masks["value"] is False,
                "unresolved_objective_has_value_target")
        require(public["legal_mask"][index] or not any(masks.values()), "illegal_action_has_targets")
        for head in HEADS:
            require(head in action, "target_field_missing:" + head)
            value = action[head]
            if not masks[head]:
                require(value is None, "masked_target_must_be_null:" + head)
                continue
            if head == "hp_distribution":
                require(isinstance(value, list) and bool(value), "hp_distribution_invalid")
                require(all(isinstance(x, dict) and set(x) == {"hp", "probability"}
                            and finite_number(x["hp"]) and x["hp"] >= 0
                            and finite_number(x["probability"]) and 0 <= x["probability"] <= 1
                            for x in value), "hp_distribution_invalid")
                require(math.isclose(sum(x["probability"] for x in value), 1.0, abs_tol=1e-7),
                        "hp_distribution_probability_mass_invalid")
                if masks["expected_final_hp"]:
                    require(finite_number(action["expected_final_hp"]) and math.isclose(
                        sum(x["hp"] * x["probability"] for x in value),
                        action["expected_final_hp"], rel_tol=1e-7, abs_tol=1e-7),
                        "hp_distribution_mean_inconsistent")
            else:
                require(finite_number(value), "target_number_invalid:" + head)
                if head.endswith("probability"):
                    require(0 <= value <= 1, "target_probability_invalid:" + head)
                if head == "expected_final_hp":
                    require(value >= 0, "target_hp_negative")
        if masks["win_probability"] and masks["death_probability"]:
            require(action["win_probability"] + action["death_probability"] <= 1 + 1e-7,
                    "win_and_death_probability_inconsistent")
    pairwise, equivalent = targets.get("pairwise"), targets.get("equivalent_action_set")
    require(isinstance(pairwise, list) and isinstance(equivalent, list), "ranking_targets_missing")
    require((not pairwise and not equivalent) or all_complete, "incomplete_root_has_strong_ranking")
    def valid_value_index(index: Any) -> bool:
        return (type(index) is int and 0 <= index < len(actions)
                and public["legal_mask"][index] and actions[index]["masks"]["value"])
    for pair in pairwise:
        require(isinstance(pair, dict) and set(pair) == {"preferred", "other", "weight"}
                and valid_value_index(pair["preferred"]) and valid_value_index(pair["other"])
                and pair["preferred"] != pair["other"] and finite_number(pair["weight"])
                and 0 < pair["weight"] <= 1, "pairwise_target_invalid")
    require(all(valid_value_index(x) for x in equivalent) and len(set(equivalent)) == len(equivalent),
            "equivalent_action_set_invalid")
    counts = [action_counts(record, action, i) for i, action in enumerate(actions)]
    require(max(c["allocated_worlds"] for c in counts) == audit["n_independent_eval"], "root_independent_world_count_inconsistent")
    require(audit["n_error"] == sum(c["error_worlds"] for c in counts), "audit_error_count_inconsistent")
    require(audit["n_unresolved"] == sum(c["truncated_worlds"] + c["other_worlds"] for c in counts), "audit_unresolved_count_inconsistent")
    costs = audit.get("costs", {})
    require(isinstance(costs, dict), "costs_not_object")
    for key in COST_FIELDS:
        value = costs.get(key)
        require(value is None or finite_number(value) and value >= 0, "cost_invalid:" + key)
    if costs.get("root_candidates") is not None:
        require(costs["root_candidates"] == len(actions), "root_candidates_cost_inconsistent")
    for key, count_name in (("worlds_allocated", "allocated_worlds"), ("worlds_completed", "completed_worlds")):
        if costs.get(key) is not None:
            require(costs[key] == sum(action_counts(record, a, i)[count_name] for i, a in enumerate(actions)),
                    "cost_world_count_inconsistent:" + key)
    from nosl.data import validate_targets
    validate_targets(targets, public, student_config or load_student_config())
    require(not require_usable or has_usable_targets(record), "root_has_no_usable_targets")


class UnionFind:
    def __init__(self, size: int):
        self.parent = list(range(size))

    def find(self, node: int) -> int:
        while self.parent[node] != node:
            self.parent[node] = self.parent[self.parent[node]]
            node = self.parent[node]
        return node

    def union(self, a: int, b: int) -> None:
        a, b = self.find(a), self.find(b)
        if a != b:
            self.parent[max(a, b)] = min(a, b)


def record_tokens(record: Any) -> set[str]:
    """Recover even partial provenance before filtering, preserving bridge roots."""
    if not isinstance(record, dict):
        return set()
    tokens = set()
    audit = record.get("audit_only", {})
    if isinstance(audit, dict):
        for key in PROVENANCE_FIELDS:
            value = audit.get(key)
            if isinstance(value, str) and value.strip():
                tokens.add(key + ":" + value)
    if isinstance(record.get("public_input"), dict):
        try:
            tokens.add("prepared_public_input_digest:" + public_digest(record["public_input"]))
        except (ValueError, TypeError):
            pass
    # Metadata-only journals carry semantic identity without fabricating a
    # public input. This token is conservative provenance, never a target.
    if (record.get("public_digest_scheme") == PUBLIC_IDENTITY_SCHEME
            and is_sha256(record.get("public_input_digest"))):
        tokens.add("prepared_public_input_digest:" + record["public_input_digest"])
    return tokens


def provenance_components(records: list[Any], state: dict | None = None) -> tuple[list[str], dict[str, set[str]], dict[str, set[str]]]:
    uf, first = UnionFind(len(records)), {}
    tokens_by_row = [record_tokens(row) for row in records]
    previous = (state or {}).get("components", {})
    old_token_component = {token: group for group, info in previous.items() for token in info["tokens"]}
    historic_links = []
    for index, tokens in enumerate(tokens_by_row):
        old_groups = {old_token_component[t] for t in tokens if t in old_token_component}
        historic_links.append(old_groups)
        for token in tokens | {"historic_component:" + g for g in old_groups}:
            if token in first:
                uf.union(index, first[token])
            else:
                first[token] = index
    group_tokens: dict[int, set[str]] = defaultdict(set)
    group_history: dict[int, set[str]] = defaultdict(set)
    for index, tokens in enumerate(tokens_by_row):
        root = uf.find(index)
        group_tokens[root].update(tokens)
        group_history[root].update(historic_links[index])
    for root, old_groups in group_history.items():
        for group in old_groups:
            group_tokens[root].update(previous[group]["tokens"])
    group_ids = {root: hashlib.sha256(canonical_json(sorted(tokens)).encode()).hexdigest()
                 for root, tokens in group_tokens.items()}
    return ([group_ids[uf.find(i)] for i in range(len(records))],
            {group_ids[root]: tokens for root, tokens in group_tokens.items()},
            {group_ids[root]: group_history[root] for root in group_tokens})


def validate_config(config: dict) -> None:
    require(config.get("schema_version") == "nosl.dataset.config.v1", "dataset_config_schema_unknown")
    ratios = config.get("split_ratios", {})
    require(set(ratios) == set(SPLITS) and all(finite_number(x) and x > 0 for x in ratios.values())
            and math.isclose(sum(ratios.values()), 1.0, abs_tol=1e-9), "split_ratios_invalid")
    require(isinstance(config.get("split_seed"), str), "split_seed_invalid")
    require(config.get("split_unit") == "transitive_provenance_connected_component", "split_unit_invalid")
    require(config.get("source_kinds") == ["constructed", "natural", "stress_test"], "source_kinds_invalid")


def choose_split(group: str, config: dict) -> str:
    digest = hashlib.sha256((config["split_seed"] + ":" + group).encode()).digest()
    fraction = int.from_bytes(digest[:8], "big") / 2**64
    cumulative = 0.0
    for name in SPLITS:
        cumulative += config["split_ratios"][name]
        if fraction < cumulative:
            return name
    return SPLITS[-1]


def summarize_throughput(records: list[dict]) -> dict:
    """Missing timings remain null and are reported, never treated as zero cost."""
    totals = Counter()
    measured = Counter()
    per_action = []
    peak = []
    for row_index, record in enumerate(records):
        actions = record["targets"]["actions"]
        totals["root_candidates"] += len(actions)
        for index, action in enumerate(actions):
            counts = action_counts(record, action, index)
            for key, value in counts.items():
                totals[key] += value
            per_action.append({"validated_attempt_row": row_index, "action_index": index, **counts,
                               "effective_sample_rate": (counts["completed_worlds"] / counts["allocated_worlds"]
                                                         if counts["allocated_worlds"] else None)})
        costs = record["audit_only"].get("costs", {})
        for key in ("rollout_decisions", "elapsed_seconds", "clone_seconds", "settlement_seconds"):
            value = costs.get(key)
            if finite_number(value) and value >= 0:
                totals[key] += value
                measured[key] += 1
        if finite_number(costs.get("peak_worker_memory_bytes")):
            peak.append(costs["peak_worker_memory_bytes"])
    all_measured = lambda key: totals[key] if records and measured[key] == len(records) else None
    elapsed = all_measured("elapsed_seconds")
    allocated, completed = totals["allocated_worlds"], totals["completed_worlds"]
    return {
        "decision_roots": len(records), "root_candidates": totals["root_candidates"],
        "independent_eval_world_draws": sum(r["audit_only"]["n_independent_eval"] for r in records),
        "exploration_world_draws": sum(r["audit_only"]["n_exploration"] for r in records),
        "allocated_action_worlds": allocated, "completed_action_worlds": completed,
        "truncated_action_worlds": totals["truncated_worlds"], "error_action_worlds": totals["error_worlds"],
        "other_action_worlds": totals["other_worlds"],
        "effective_sample_rate": completed / allocated if allocated else None,
        "mean_candidates_per_root": totals["root_candidates"] / len(records) if records else None,
        "rollout_decisions": all_measured("rollout_decisions"),
        "elapsed_seconds_sum": elapsed,
        "clone_seconds_sum": all_measured("clone_seconds"),
        "settlement_seconds_sum": all_measured("settlement_seconds"),
        "seconds_per_allocated_action_world": elapsed / allocated if elapsed is not None and allocated else None,
        "seconds_per_completed_action_world": elapsed / completed if elapsed is not None and completed else None,
        "completed_action_worlds_per_second": completed / elapsed if elapsed else None,
        "peak_worker_memory_bytes": max(peak) if len(peak) == len(records) and records else None,
        "missing_cost_rows": {key: len(records) - measured[key] for key in
                              ("rollout_decisions", "elapsed_seconds", "clone_seconds", "settlement_seconds")}
                             | {"peak_worker_memory_bytes": len(records) - len(peak)},
        "per_action": per_action,
        "note": "Action-world rollouts are correlated copies of root worlds, not independent source games. Elapsed sum is serial worker service time; no parallel wall-clock speedup is inferred. Memory is the maximum reported worker high-water mark. Costs cover the supplied structurally valid attempt population; duplicate attempts remain real compute costs. Malformed-record costs are not silently estimated."
    }


def load_student_config() -> dict:
    python_root = str(REPO / "python")
    if python_root not in sys.path:
        sys.path.insert(0, python_root)
    from nosl.schema import load_config
    return load_config(REPO / "configs/student.pilot.json")


def object_digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value).encode()).hexdigest()


def prepare(records: list[Any], config: dict, mode: str = "engineering-smoke",
            state: dict | None = None, student_config: dict | None = None, *,
            protection: dict | None = None, provenance_records: list | None = None) -> tuple[dict, dict, list]:
    """Pure batch preparation. Persist report['split_state'] with the stage manifest.

    Existing test shards never change. A new row connected to test inherits the
    test split in state but is excluded from the frozen evaluation set. A merge
    of two historical splits is quarantined and permanently blocks readiness.
    """
    validate_config(config)
    require(mode in ("engineering-smoke", "pilot", "formal"), "dataset_mode_unknown")
    require(mode != "formal", "formal_data_blocked: formal production is not implemented or authorized")
    student_config = student_config or load_student_config()
    lock = {"pipeline_version": PIPELINE_VERSION, "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
            "config_sha256": object_digest(config),
            "student_config_sha256": object_digest(student_config), "mode": mode}
    previous = deepcopy(state) if state else None
    if protection is not None:
        validate_registry(protection)
        lock["split_protection"] = {"schema_version": REGISTRY_VERSION, "sha256": registry_hash(protection)}
    if previous:
        require(previous.get("schema_version") == "nosl.dataset.split-state.v2", "split_state_version_unknown")
        require(previous.get("lock") == lock, "append_configuration_or_mode_mismatch")
        if protection is not None:
            validate_state_protection(previous, protection)
            require(not previous["cross_split_conflicts"], "protected_corpus_cross_split_conflict_resume_blocked")
    prior_components = previous or ({"components": protection["components"]} if protection else None)
    provenance_records = provenance_records or []
    group_ids, tokens, history = provenance_components(records + provenance_records, prior_components)
    versions = previous.get("versions") if previous else None
    observation_schema = previous.get("observation_schema") if previous else None
    # Validate every row before choosing corpus versions; invalid rows never set the version lock.
    valid, errors = {}, {}
    for index, record in enumerate(records):
        try:
            validate_record(record, config, mode, student_config)
            valid[index] = True
            candidate_versions = record["audit_only"]["versions"]
            if versions is None:
                versions = deepcopy(candidate_versions)
            require(candidate_versions == versions, "append_record_versions_mismatch")
            candidate_schema = record["public_input"]["observation"]["schema"]
            if observation_schema is None:
                observation_schema = candidate_schema
            require(candidate_schema == observation_schema, "append_observation_schema_versions_mismatch")
            if "observation_schema" in candidate_versions:
                require(candidate_versions["observation_schema"] == candidate_schema, "audit_observation_schema_versions_mismatch")
        except (ValidationError, TypeError, KeyError, ValueError, OverflowError) as exc:
            valid.pop(index, None)
            errors[index] = str(exc)
    splits = {name: [] for name in SPLITS}
    rejected, reasons, quality = [], Counter(), Counter()
    source_all, source_accepted = Counter(), Counter()
    accepted = []
    next_state = previous or {"schema_version": "nosl.dataset.split-state.v2", "lock": lock,
                              "versions": versions, "components": deepcopy(protection["components"]) if protection else {}, "seen_public_digests": {},
                              "frozen_test_digests": [], "stage_count": 0, "cross_split_conflicts": [],
                              "cumulative_attempts": 0}
    old_seen = dict(next_state["seen_public_digests"])
    seen = set(old_seen)
    frozen = next_state["stage_count"] > 0
    protected_test_tokens = {token for info in (protection or {}).get("components", {}).values()
                             if info["split"] == "test" for token in info["tokens"]}
    retained_tokens = {"prepared_public_input_digest:" + digest for digest, info in old_seen.items()
                       if not info["held_out_excluded"]}
    group_splits, conflicts, conflict_reasons = {}, {}, {}
    for group, old_groups in history.items():
        old_splits = {next_state["components"][g]["split"] for g in old_groups}
        retroactive_test_link = bool(tokens[group] & protected_test_tokens and tokens[group] & retained_tokens)
        if len(old_splits) > 1 or retroactive_test_link:
            conflicts[group] = sorted(old_splits)
            conflict_reasons[group] = ("retained_target_connected_to_protected_test" if retroactive_test_link
                                       else "historical_cross_split_merge")
        else:
            group_splits[group] = next(iter(old_splits)) if old_splits else choose_split(group, config)
    # Lock all resolvable provenance, including invalid/duplicate rows. Such rows
    # may connect future roots, but never gain targets or counts as usable data.
    for group, split in group_splits.items():
        for old_group in history[group]:
            next_state["components"].pop(old_group, None)
        next_state["components"][group] = {"split": split, "tokens": sorted(tokens[group])}
    if conflicts:
        next_state["cross_split_conflicts"].extend({"component": k, "historical_splits": v,
                                                   **({"tokens": sorted(tokens[k]), "reason": conflict_reasons[k]} if protection else {})}
                                                  for k, v in sorted(conflicts.items()))
    protected_test_groups = {group for group, aliases in tokens.items() if aliases & protected_test_tokens}
    unique_valid_new = 0
    full_candidate_complete_new = 0
    trajectory_complete_new = 0
    auxiliary_only_new = 0
    duplicates, holdout_excluded, protected_excluded = 0, 0, 0
    for index, record in enumerate(records):
        audit = record.get("audit_only", {}) if isinstance(record, dict) else {}
        source = audit.get("source_kind", "undeclared") if isinstance(audit, dict) else "undeclared"
        source_all[str(source)] += 1
        group = group_ids[index]
        reason = conflict_reasons[group] if group in conflicts else errors.get(index)
        split = group_splits.get(group)
        digest = None
        if reason is None:
            digest = public_digest(record["public_input"])
            if digest in seen:
                reason = "exact_public_input_duplicate"
                duplicates += 1
            else:
                seen.add(digest)
                unique_valid_new += 1
                actions = record["targets"]["actions"]
                complete = all(a["masks"]["value"] and all(a["masks"].values()) for a in actions)
                trajectories = all(action_counts(record, a, i)["allocated_worlds"] > 0
                                   and action_counts(record, a, i)["allocated_worlds"] == action_counts(record, a, i)["completed_worlds"]
                                   for i, a in enumerate(actions))
                full_candidate_complete_new += complete
                trajectory_complete_new += trajectories
                auxiliary_only_new += not complete
                protected_test = group in protected_test_groups
                next_state["seen_public_digests"][digest] = {"split": split, "held_out_excluded": protected_test or frozen and split == "test",
                                                          "full_candidate_complete": complete, "all_candidate_trajectory_complete": trajectories}
                if protected_test:
                    reason = "protected_old_test_component_excluded"
                    protected_excluded += 1
                elif frozen and split == "test":
                    reason = "frozen_test_holdout_not_extended"
                    holdout_excluded += 1
        if reason is not None:
            reasons[reason] += 1
            rejected.append({"input_row": index, "reason": reason, "provenance_component": group,
                             "inherited_split": split, "record": record})
            continue
        row = deepcopy(record)
        row["audit_only"].update(prepared_public_input_digest=digest, provenance_component=group,
                                 dataset_split=split, dataset_mode=mode)
        splits[split].append(row)
        accepted.append(row)
        source_accepted[source] += 1
        quality.update(action["quality"] for action in row["targets"]["actions"])
        if not frozen and split == "test":
            next_state["frozen_test_digests"].append(digest)
    next_state["stage_count"] += 1
    next_state["cumulative_attempts"] = next_state.get("cumulative_attempts", 0) + len(records)
    next_state["versions"] = versions
    next_state["observation_schema"] = observation_schema
    overlap = bool(next_state["cross_split_conflicts"])
    component_counts = Counter(info["split"] for info in next_state["components"].values())
    unique_inputs = {public_digest(r["public_input"]) for r in records if isinstance(r, dict)
                     and isinstance(r.get("public_input"), dict) and _digestible(r["public_input"])}
    throughput_records = [records[i] for i in valid]
    throughput = summarize_throughput(throughput_records)
    cumulative_accepted = [info for info in next_state["seen_public_digests"].values() if not info["held_out_excluded"]]
    cumulative_splits = Counter(info["split"] for info in cumulative_accepted)
    report = {
        "schema_version": "nosl.dataset.report.v2", "mode": mode, "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
        "status": "ENGINEERING_SMOKE_ONLY" if mode == "engineering-smoke" else "PILOT_PREPARATION_ONLY",
        "formal_data_ready": False, "m7_training_started": False,
        "input_attempts": len(records), "input_unique_public_roots": len(unique_inputs),
        "valid_attempts": len(valid), "usable_new_roots_before_holdout_exclusion": unique_valid_new,
        "cumulative": {"input_attempts": next_state["cumulative_attempts"],
                       "unique_usable_roots_before_holdout_exclusion": len(next_state["seen_public_digests"]),
                       "effective_dataset_roots": len(cumulative_accepted),
                       "effective_roots_by_split": {name: cumulative_splits[name] for name in SPLITS},
                       "holdout_excluded_roots": len(next_state["seen_public_digests"]) - len(cumulative_accepted),
                       "full_candidate_complete_roots": sum(info["full_candidate_complete"] for info in cumulative_accepted),
                       "auxiliary_only_usable_roots": sum(not info["full_candidate_complete"] for info in cumulative_accepted)},
        "accepted_roots": len(accepted), "rejected_or_excluded_roots": len(rejected),
        "invalid_roots": sum(1 for x in rejected if x["reason"] not in ("exact_public_input_duplicate", "frozen_test_holdout_not_extended", "protected_old_test_component_excluded")),
        "duplicate_attempts": duplicates, "frozen_holdout_excluded_roots": holdout_excluded,
        "full_candidate_complete_new_roots": full_candidate_complete_new,
        "all_candidate_trajectory_complete_new_roots": trajectory_complete_new,
        "auxiliary_only_usable_new_roots": auxiliary_only_new,
        "root_quality_definitions": {
            "full_candidate_complete": "Every candidate has all six valid targets including calibrated-or-provisional value; no missing world mass. This does not imply validated ranking separation or objective calibration.",
            "auxiliary_only_usable": "Some usable supervision exists, but at least one candidate lacks at least one head. Whole candidate rows and masks are preserved.",
            "stage_target_count": "usable_new_roots_before_holdout_exclusion; excludes duplicate attempts and invalid roots"
        },
        "filter_reasons": dict(sorted(reasons.items())), "action_quality": dict(sorted(quality.items())),
        "source_distribution": {"input_attempts": dict(source_all), "accepted": dict(source_accepted), "natural_distribution_claimed": False},
        "split": {"unit": "transitive_provenance_connected_component",
                  "fields": list(PROVENANCE_FIELDS) + ["prepared_public_input_digest"],
                  "ratio_targets": config["split_ratios"], "new_roots": {k: len(v) for k, v in splits.items()},
                  "cumulative_components": dict(component_counts), "historical_cross_split_merges": conflicts,
                  "audit_passed": not overlap, "frozen_test_root_count": len(next_state["frozen_test_digests"]),
                  "note": "Provenance is unioned before filtering. Existing split assignments and initial test roots are immutable; correlated appends inherit split. Conflicts cannot be repaired by discarding a bridge and are a permanent readiness blocker."},
        "throughput": throughput,
        "throughput_population": "All structurally valid attempts, including duplicate attempts and holdout exclusions; never conflated with effective unique roots",
        "versions": versions, "observation_schema": observation_schema, "split_state": next_state,
        "formal_blockers": config.get("formal_blockers", []),
        "retained_unresolved_actions": sum(a["quality"] == "unresolved" for r in accepted for a in r["targets"]["actions"]),
        "note": "Preparation never runs generation or training. No targets are calculated, repaired, merged across retries, or zero-filled. Bounded engineering smoke is a separate corpus from staged pilot data."
    }
    required_measurements = all(value == 0 for value in throughput["missing_cost_rows"].values())
    report["quality_gate"] = {
        "isolation_passed": not overlap,
        "independent_three_way_split_available": all(cumulative_splits[name] > 0 for name in SPLITS),
        "all_versions_match": not any("versions_mismatch" in key for key in reasons),
        "no_invalid_roots": report["invalid_roots"] == 0,
        "throughput_measurements_complete": required_measurements,
        "nonzero_elapsed_measurement": bool(throughput["elapsed_seconds_sum"]),
        "all_candidate_trajectory_complete_fraction": trajectory_complete_new / unique_valid_new if unique_valid_new else None,
        "full_candidate_target_complete_fraction": full_candidate_complete_new / unique_valid_new if unique_valid_new else None,
        "effective_sample_rate": throughput["effective_sample_rate"],
        "automatic_generation_or_training_authorized": False,
        "stage_promotion": "REQUIRES_EXPLICIT_REVIEW_OF_QUALITY_THROUGHPUT_AND_SOURCE_COVERAGE"
    }
    if protection is not None:
        validate_state_protection(next_state, protection)
        protected_tokens = {token for info in protection["components"].values() for token in info["tokens"]}
        closure = {group: info for group, info in next_state["components"].items()
                   if protected_tokens.intersection(info["tokens"])}
        report["split_protection"] = {
            **lock["split_protection"], "source": protection["source"],
            "old_targets_read_or_copied": False, "old_seen_digests_imported": False,
            "provenance_only_rows": len(provenance_records),
            "version_mismatch_roots": sum("versions_mismatch" in error for error in errors.values()),
            "protected_test_excluded_roots": protected_excluded,
            "protected_alias_closure": closure,
            "new_component_aliases": {group: {"tokens": sorted(tokens[group]),
                                      "historical_components": sorted(history[group]),
                                      "split": group_splits.get(group)} for group in sorted(tokens)},
            "note": "All incoming decision and provenance-only rows participate before filtering. Old test-connected roots are excluded from every target split. Old train roots may receive deliberate new-version labels; old validation roots remain validation. Resplitting inspected development data does not create an unseen benchmark."
        }
    return splits, report, rejected


def _digestible(value: Any) -> bool:
    try:
        public_digest(value)
        return True
    except (ValueError, TypeError, OverflowError):
        return False


def read_jsonl(paths: list[Path]) -> tuple[list, list[dict]]:
    records, origins = [], []
    for path in paths:
        with path.open(encoding="utf-8") as stream:
            for line_number, line in enumerate(stream, 1):
                if not line.strip():
                    continue
                try:
                    record = json.loads(line, parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
                except (ValueError, json.JSONDecodeError) as exc:
                    raise ValidationError(f"invalid_json:{path}:{line_number}:{exc}") from exc
                records.append(record)
                origins.append({"path": str(path), "line": line_number})
    return records, origins


def safe_path(root: Path, relative: str) -> Path:
    candidate = (root / relative).resolve()
    require(isinstance(relative, str) and not Path(relative).is_absolute()
            and candidate.is_relative_to(root.resolve()), "manifest_path_outside_output")
    return candidate


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    require(isinstance(value, dict), "manifest_not_object")
    return value


def verify_manifest(output_dir: Path | str) -> dict:
    """Verify every immutable stage, shard, state and frozen-test reference."""
    root = Path(output_dir)
    manifest = read_json(root / "manifest.json")
    require(manifest.get("schema_version") == "nosl.dataset.manifest.v2"
            and manifest.get("pipeline_version") == PIPELINE_VERSION
            and manifest.get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME
            and manifest.get("lock", {}).get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME, "manifest_version_or_public_identity_unknown")
    seen_paths = set()
    descriptors = []
    parent_hash = None
    stage_ids = set()
    for entry in manifest["stages"]:
        require(entry["id"] not in stage_ids, "duplicate_manifest_stage")
        stage_ids.add(entry["id"])
        stage_path = safe_path(root, entry["manifest"])
        require(file_hash(stage_path) == entry["sha256"], "stage_manifest_checksum_mismatch")
        stage = read_json(stage_path)
        require(stage["schema_version"] == "nosl.dataset.stage.v2" and stage["lock"] == manifest["lock"]
                and stage["versions"] == manifest["versions"] and stage["observation_schema"] == manifest["observation_schema"], "stage_versions_or_config_mismatch")
        require(stage["parent_stage_sha256"] == parent_hash, "stage_chain_mismatch")
        parent_hash = entry["sha256"]
        for descriptor in stage["files"]:
            descriptors.append(descriptor)
            path = safe_path(root, descriptor["path"])
            require(descriptor["path"] not in seen_paths, "duplicate_manifest_file")
            seen_paths.add(descriptor["path"])
            require(path.stat().st_size == descriptor["bytes"] and file_hash(path) == descriptor["sha256"],
                    "shard_checksum_mismatch:" + descriptor["path"])
    require(bool(manifest["stages"]), "manifest_has_no_stages")
    state_path = safe_path(root, manifest["latest_state"]["path"])
    require(file_hash(state_path) == manifest["latest_state"]["sha256"], "split_state_checksum_mismatch")
    state = read_json(state_path)
    require(state["lock"] == manifest["lock"] and state["versions"] == manifest["versions"]
            and state["observation_schema"] == manifest["observation_schema"], "state_lock_mismatch")
    require(state["stage_count"] == len(manifest["stages"]), "state_stage_count_mismatch")
    require(manifest["isolation_passed"] == (not bool(state["cross_split_conflicts"])), "manifest_isolation_state_mismatch")
    require(manifest["latest_state"] in descriptors, "state_not_in_stage_manifest")
    first_stage = read_json(safe_path(root, manifest["stages"][0]["manifest"]))
    first_test = [item for item in first_stage["files"] if item["kind"] == "test"]
    require(manifest["frozen_test_shards"] == first_test, "frozen_test_manifest_changed")
    validate_binding(manifest["lock"], descriptors,
                     lambda path: read_json(safe_path(root, path)), state)
    return manifest


def export_split_protection(source_dir: Path | str) -> dict:
    """Verify old shards as opaque bytes and project only portable split metadata.

    No old JSONL shard is decoded. Every token, including invalid/diagnostic
    aliases absent from accepted records, comes from the verified split state.
    """
    root = Path(source_dir)
    manifest_hash = file_hash(root / "manifest.json")
    manifest = verify_manifest(root)
    require(manifest["isolation_passed"] is True, "protection_source_isolation_blocked")
    state_path = safe_path(root, manifest["latest_state"]["path"])
    state = read_json(state_path)
    require(state.get("schema_version") == "nosl.dataset.split-state.v2", "protection_source_state_version_unknown")
    require(state.get("observation_schema") in ("nosl.public.v1", "nosl.public.v2"), "protection_source_observation_schema_unknown")
    registry = {"schema_version": REGISTRY_VERSION, "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
                "source": {"manifest_sha256": manifest_hash,
                           "split_state_sha256": manifest["latest_state"]["sha256"],
                           "versions": deepcopy(manifest["versions"]),
                           "frozen_test_shards": [{k: d[k] for k in ("sha256", "bytes", "rows")}
                                                  for d in manifest["frozen_test_shards"]]},
                "components": deepcopy(state["components"])}
    validate_registry(registry)
    validate_state_protection(state, registry, enforce_exclusion=False)
    require(file_hash(root / "manifest.json") == manifest_hash
            and file_hash(state_path) == manifest["latest_state"]["sha256"], "protection_source_changed_during_read")
    return registry


def bound_split_protection(output_dir: Path, manifest: dict) -> dict | None:
    """Only call after verify_manifest; keep resumes independent of source paths."""
    if "split_protection" not in manifest["lock"]:
        return None
    first = read_json(safe_path(output_dir, manifest["stages"][0]["manifest"]))
    descriptor = next(item for item in first["files"] if item["kind"] == "split_protection")
    return read_json(safe_path(output_dir, descriptor["path"]))


def recover_initial_protection(output_dir: Path, protection: dict | None) -> dict | None:
    """Do not drop a first-stage registry if publishing the root was interrupted."""
    for request_path in sorted((output_dir / "stages").glob("*/request.json")):
        request = read_json(request_path)
        expected = request.get("split_protection_sha256")
        if expected is None:
            require(protection is None, "uncommitted_stage_protection_changed")
            continue
        if protection is None:
            path = request_path.parent / "split_protection.json"
            require(path.exists(), "uncommitted_protection_missing_supply_original_source")
            protection = read_json(path)
            validate_registry(protection)
        require(registry_hash(protection) == expected, "uncommitted_stage_protection_changed")
    return protection


def journal_provenance(rows: list, generation_config: dict | None = None) -> list[dict]:
    """Project generation journals to aliases, failing closed on bad metadata."""
    if generation_config is not None:
        # The pinned generator defines these aliases even for failed attempts
        # whose journal predates explicit alias fields. Import metadata helpers
        # only; never invoke a worker, reconcile, repair or generation routine.
        import generate_pilot_data as generator
        require(generation_config.get("generator_sha256") == file_hash(Path(generator.__file__))
                and generation_config.get("version") == generator.VERSION
                and generation_config.get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME,
                "journal_generator_or_identity_unsupported")
        label_hash = generator.sha(generator.generation_identity_config(generation_config))
    result = []
    for row in rows:
        require(isinstance(row, dict), "journal_row_not_object")
        audit = {key: row[key] for key in PROVENANCE_FIELDS if key in row}
        if generation_config is not None:
            require(row.get("generation_config_sha256") == label_hash
                    and row.get("generation_version") == generator.VERSION, "journal_generation_config_mismatch")
            index = generator.validate_journal(row, generation_config)
            battle = index // generation_config["roots_per_battle"]
            for key, suffix in (("source_run_group", "run"), ("source_combat_id", "combat"), ("branch_family", "family")):
                expected = f"{generation_config['seed_prefix']}:{suffix}:{battle}"
                require(key not in audit or audit[key] == expected, "journal_declared_alias_mismatch")
                audit[key] = expected
        require(bool(audit) and all(isinstance(value, str) and value.strip() for value in audit.values()),
                "journal_provenance_missing_or_invalid")
        record = {"audit_only": audit}
        if row.get("public_input_digest") is not None:
            require(row.get("public_digest_scheme") == PUBLIC_IDENTITY_SCHEME
                    and is_sha256(row["public_input_digest"]), "journal_public_digest_invalid")
            record.update(public_input_digest=row["public_input_digest"], public_digest_scheme=PUBLIC_IDENTITY_SCHEME)
        result.append(record)
    return result


def iter_dataset_paths(output_dir: Path | str, split: str) -> list[Path]:
    """Resolve only verified split shards. Test always means the initial holdout."""
    require(split in SPLITS, "unknown_split")
    root = Path(output_dir)
    manifest = verify_manifest(root)
    require(manifest["isolation_passed"] is True, "dataset_isolation_blocked_by_historical_split_conflict")
    if split == "test":
        return [safe_path(root, d["path"]) for d in manifest["frozen_test_shards"]]
    paths = []
    for entry in manifest["stages"]:
        stage = read_json(safe_path(root, entry["manifest"]))
        paths.extend(safe_path(root, d["path"]) for d in stage["files"] if d["kind"] == split)
    return paths


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def _persist_batch(output_dir: Path, records: list, origins: list, config: dict, mode: str,
                  inputs: list[dict], resume: bool, shard_size: int = 1000, *,
                  protection: dict | None = None, provenance_records: list | None = None) -> dict:
    require(type(shard_size) is int and shard_size > 0, "shard_size_invalid")
    output_dir.mkdir(parents=True, exist_ok=True)
    root_manifest = output_dir / "manifest.json"
    old = verify_manifest(output_dir) if root_manifest.exists() else None
    require(not old or resume, "output_exists_use_resume")
    state = read_json(safe_path(output_dir, old["latest_state"]["path"])) if old else None
    if old:
        bound = bound_split_protection(output_dir, old)
        require(protection is None or bound is not None and registry_hash(protection) == registry_hash(bound),
                "append_split_protection_changed")
        protection = bound
    else:
        protection = recover_initial_protection(output_dir, protection)
    if protection is not None:
        validate_registry(protection)
    student_config = load_student_config()
    request = {"inputs": inputs, "config_sha256": object_digest(config),
               "student_config_sha256": object_digest(student_config), "mode": mode,
               "shard_size": shard_size}
    if protection is not None:
        request["split_protection_sha256"] = registry_hash(protection)
    if provenance_records:
        request["provenance_records_sha256"] = object_digest(provenance_records)
    if old:
        require(old["lock"]["config_sha256"] == request["config_sha256"]
                and old["lock"]["student_config_sha256"] == request["student_config_sha256"]
                and old["lock"]["mode"] == mode, "append_configuration_or_mode_mismatch")
        last = read_json(safe_path(output_dir, old["stages"][-1]["manifest"]))
        if last["request"] == request:
            return {"status": "ALREADY_COMMITTED", "manifest": str(root_manifest), "stage_id": last["id"], "isolation_passed": old["isolation_passed"]}
    splits, report, rejected = prepare(records, config, mode, state, student_config,
                                       protection=protection, provenance_records=provenance_records)
    # Ordinary invalid/version-mismatch attempts cannot alter the corpus. A
    # protected isolation failure must durably block existing targets even if
    # an unrelated row also has bad versions. Incompatible labels remain
    # quarantined; a blocked first stage may have no label-version population.
    protected_conflict = protection is not None and not report["split"]["audit_passed"]
    require(report["versions"] is not None or protected_conflict, "no_valid_versioned_records")
    if any("versions_mismatch" in reason for reason in report["filter_reasons"]) and not protected_conflict:
        raise ValidationError("record_versions_mismatch_no_stage_committed")
    for row in rejected:
        row["origin"] = origins[row["input_row"]]
    next_state = report.pop("split_state")
    number = len(old["stages"]) + 1 if old else 1
    parent_hash = old["stages"][-1]["sha256"] if old else None
    batch_id = object_digest({"request": request, "parent": parent_hash})[:16]
    stage_id = f"stage-{number:06d}-{batch_id}"
    relative_dir = Path("stages") / stage_id
    destination = output_dir / relative_dir
    staging = output_dir / "stages" / ("." + stage_id + ".pending")
    if destination.exists():
        stage = read_json(destination / "stage_manifest.json")
        require(stage["request"] == request and stage["parent_stage_sha256"] == parent_hash,
                "uncommitted_stage_identity_mismatch")
        for descriptor in stage["files"]:
            path = safe_path(output_dir, descriptor["path"])
            require(path.stat().st_size == descriptor["bytes"] and file_hash(path) == descriptor["sha256"],
                    "uncommitted_stage_checksum_mismatch")
    else:
        staging.mkdir(parents=True, exist_ok=True)
        plan = staging / "request.json"
        if plan.exists():
            require(read_json(plan) == request, "pending_stage_identity_mismatch")
        else:
            write_json(plan, request)
        descriptors = []
        def remember(path: Path, kind: str, rows: int | None = None) -> None:
            descriptors.append({"path": str(relative_dir / path.name), "kind": kind,
                                "rows": rows, "bytes": path.stat().st_size, "sha256": file_hash(path)})
        for split, rows in list(splits.items()) + [("quarantine", rejected)]:
            for offset in range(0, len(rows), shard_size):
                path = staging / f"{split}-{offset // shard_size:05d}.jsonl"
                path.write_text("".join(canonical_json(row) + "\n" for row in rows[offset:offset + shard_size]), encoding="utf-8")
                remember(path, split, len(rows[offset:offset + shard_size]))
        write_json(staging / "split_state.json", next_state)
        remember(staging / "split_state.json", "split_state")
        write_json(staging / "quality_report.json", report)
        remember(staging / "quality_report.json", "quality_report")
        if protection is not None and old is None:
            path = staging / "split_protection.json"
            path.write_bytes(registry_bytes(protection))
            remember(path, "split_protection")
        stage = {"schema_version": "nosl.dataset.stage.v2", "id": stage_id,
                 "parent_stage_sha256": parent_hash, "request": request, "lock": next_state["lock"],
                 "versions": report["versions"], "observation_schema": report["observation_schema"], "files": descriptors}
        write_json(staging / "stage_manifest.json", stage)
        os.replace(staging, destination)
    stage_manifest_path = destination / "stage_manifest.json"
    state_descriptor = next(d for d in stage["files"] if d["kind"] == "split_state")
    new = deepcopy(old) if old else {"schema_version": "nosl.dataset.manifest.v2", "pipeline_version": PIPELINE_VERSION,
                                     "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
                                     "lock": stage["lock"], "versions": stage["versions"], "observation_schema": stage["observation_schema"], "stages": [],
                                     "frozen_test_shards": [d for d in stage["files"] if d["kind"] == "test"]}
    new["stages"].append({"id": stage_id, "manifest": str(relative_dir / "stage_manifest.json"), "sha256": file_hash(stage_manifest_path)})
    new["latest_state"] = state_descriptor
    new["isolation_passed"] = report["split"]["audit_passed"]
    new["formal_data_ready"] = False
    new["training_started"] = False
    temp = output_dir / ".manifest.pending.json"
    write_json(temp, new)
    os.replace(temp, root_manifest)
    verify_manifest(output_dir)
    return {"status": report["status"], "manifest": str(root_manifest), "stage_id": stage_id,
            "isolation_passed": report["split"]["audit_passed"],
            **{key: report[key] for key in ("input_attempts", "usable_new_roots_before_holdout_exclusion", "accepted_roots",
                                           "duplicate_attempts", "frozen_holdout_excluded_roots", "full_candidate_complete_new_roots",
                                           "auxiliary_only_usable_new_roots")}}


@contextmanager
def output_lock(output_dir: Path):
    """OS advisory lock releases on crash; the harmless lock file may remain."""
    output_dir.mkdir(parents=True, exist_ok=True)
    with (output_dir / ".prepare.lock").open("a+b") as handle:
        try:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                if not handle.read(1):
                    handle.write(b"0")
                    handle.flush()
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise ValidationError("dataset_output_locked_by_another_preparer") from exc
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def persist_batch(output_dir: Path, records: list, origins: list, config: dict, mode: str,
                  inputs: list[dict], resume: bool, shard_size: int = 1000, *,
                  protection: dict | None = None, provenance_records: list | None = None) -> dict:
    with output_lock(output_dir):
        return _persist_batch(output_dir, records, origins, config, mode, inputs, resume, shard_size,
                              protection=protection, provenance_records=provenance_records)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="*", type=Path)
    parser.add_argument("--config", type=Path, default=REPO / "configs/data_pipeline.v1.json")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--mode", choices=("engineering-smoke", "pilot", "formal"))
    parser.add_argument("--resume", action="store_true", help="Append compatible data or recover an interrupted identical stage")
    parser.add_argument("--verify-only", action="store_true", help="Verify all checksums, versions and frozen holdout without writing")
    parser.add_argument("--shard-size", type=int, default=1000)
    parser.add_argument("--protect-from-prepared", type=Path,
                        help="Seed a NEW version corpus with verified portable old split aliases; exclude old test-connected roots")
    parser.add_argument("--provenance-journals", nargs="+", type=Path, default=[],
                        help="Union all generation attempt metadata before filtering; never use journal targets")
    args = parser.parse_args(argv)
    try:
        if args.verify_only:
            result = verify_manifest(args.output_dir)
            print(json.dumps({"status": "VERIFIED", "stages": len(result["stages"]), "isolation_passed": result["isolation_passed"]}))
            return 0 if result["isolation_passed"] else 2
        require(bool(args.inputs) and args.mode is not None, "inputs_and_explicit_mode_required")
        inputs = [{"path": str(p.resolve()), "sha256": file_hash(p), "bytes": p.stat().st_size} for p in args.inputs]
        records, origins = read_jsonl(args.inputs)
        require(all(file_hash(path) == descriptor["sha256"] for path, descriptor in zip(args.inputs, inputs)), "input_changed_during_read")
        config = read_json(args.config)
        protection = export_split_protection(args.protect_from_prepared) if args.protect_from_prepared else None
        provenance = []
        if args.provenance_journals:
            journal_inputs = [{"path": str(p.resolve()), "sha256": file_hash(p), "bytes": p.stat().st_size,
                               "kind": "provenance_journal"} for p in args.provenance_journals]
            for path in args.provenance_journals:
                journal_rows, _ = read_jsonl([path])
                sidecar = path.parent / "generation_config.json"
                generation_config = None
                if sidecar.exists():
                    descriptor = {"path": str(sidecar.resolve()), "sha256": file_hash(sidecar),
                                  "bytes": sidecar.stat().st_size, "kind": "journal_generation_config"}
                    generation_config = read_json(sidecar)
                    require(file_hash(sidecar) == descriptor["sha256"], "journal_config_changed_during_read")
                    journal_inputs.append(descriptor)
                provenance.extend(journal_provenance(journal_rows, generation_config))
            require(all(file_hash(p) == d["sha256"] for p, d in zip(args.provenance_journals, journal_inputs)),
                    "journal_changed_during_read")
            inputs.extend(journal_inputs)
        result = persist_batch(args.output_dir, records, origins, config, args.mode, inputs, args.resume, args.shard_size,
                               protection=protection, provenance_records=provenance)
        print(json.dumps(result, sort_keys=True))
        return 0 if result.get("isolation_passed", True) else 2
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print(str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
