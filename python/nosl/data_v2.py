"""Versioned nullable whole-plan supervision, with all assigned mass retained."""
import json
from pathlib import Path

from .data import (validate_targets as validate_legacy_targets, prepared_paths,
                   canonical_object_digest, _hash_file)
from .schema import boolean, integer, number, object_keys, reject
from .schema_v2 import PLAN_HEADS, legacy_input, validate_public
from .public_identity_v2 import PUBLIC_IDENTITY_SCHEME, public_input_digest, legacy_alias_digests

COUNT_FIELDS = ("allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")
PROVENANCE_FIELDS = ("source_run_group", "source_combat_id", "branch_family", "public_state_digest")
REQUIRED_VERSIONS = {"teacher", "continuation", "objective", "public_schema", "observation_schema",
                     "simulator", "rules", "endpoint", "controller", "sampler", "dataset"}


def validate_targets(targets, public, config):
    # Inactive native records may have only ordinary action supervision.
    keys = ["actions", "pairwise", "equivalent_action_set"]
    active_contract = public["controller_context"]["status"] != "inactive"
    if active_contract or "plan" in targets: keys.append("plan")
    object_keys(targets, keys, "v2 targets")
    validate_legacy_targets({key: targets[key] for key in keys if key != "plan"}, legacy_input(public), config["base_config"])
    if "plan" not in targets: return targets
    plan = object_keys(targets["plan"], ["label_scope", *PLAN_HEADS, "masks", "allocated_worlds",
                                          "success_completed_worlds", "paired_completed_worlds"], "plan targets")
    object_keys(plan["masks"], PLAN_HEADS, "plan masks")
    allocated = integer(plan["allocated_worlds"], "allocated_worlds")
    success_count = integer(plan["success_completed_worlds"], "success_completed_worlds", 0, allocated)
    pair_count = integer(plan["paired_completed_worlds"], "paired_completed_worlds", 0, success_count)
    if plan["label_scope"] not in ("whole_plan_from_anchor", "unavailable"): reject("unknown plan label scope")
    at_anchor = active_contract and legacy_input(public) == public["controller_context"]["anchor"] and public["controller_context"]["status"] == "active"
    if plan["label_scope"] == "whole_plan_from_anchor" and not at_anchor:
        reject("whole-plan labels only describe the exact original active anchor")
    for head, completed in zip(PLAN_HEADS, (success_count, pair_count)):
        mask = boolean(plan["masks"][head], "plan mask." + head)
        value = plan[head]
        if not mask:
            if value is not None: reject("missing plan targets must be null, not zero")
        else:
            if not at_anchor or plan["label_scope"] != "whole_plan_from_anchor" or allocated == 0 or completed != allocated:
                reject("unresolved or off-anchor mass cannot produce normalized plan labels")
            if head == "specified_success_probability": number(value, head, 0, 1)
            else:
                hp = public["controller_context"]["anchor"]["observation"]["hp"]
                number(value, head, -hp, hp)
    return targets


def validate_record(record, config):
    keys = ["public_input", "targets", "audit_only"]
    native = "schema_version" in record or "record_kind" in record
    if native: keys += ["schema_version", "record_kind"]
    object_keys(record, keys, "DecisionRecord v2")
    if native and (record["schema_version"] != "nosl.native-belief-prototype.v2"
            or record["record_kind"] != "natural_development_teacher_candidate"
            or not isinstance(record["audit_only"], dict) or record["audit_only"].get("trainable") is not False):
        reject("only explicitly non-trainable native development teacher records are supported")
    validate_public(record["public_input"], config)
    validate_targets(record["targets"], record["public_input"], config)
    if not isinstance(record["audit_only"], dict): reject("audit_only must be an object")
    return record


class DecisionDatasetV2:
    """Bounded engineering reader, NOT a split-protected or fit-ready corpus.

    Use PreparedDatasetV2 for production preparation/training admission.
    This compatibility reader alone grants no provenance or fit authorization.
    """
    def __init__(self, path: str | Path, config: dict):
        self.records = []
        with Path(path).open() as handle:
            for line in handle:
                if not line.strip(): continue
                if len(self.records) == config["base_config"]["batch_size"]:
                    reject("v2 engineering reader is limited to one batch; no production corpus admission")
                self.records.append(validate_record(json.loads(line), config))
        if not self.records: reject("dataset is empty")


def has_usable_targets(record):
    return (any(row.get("sample_weight", 1) > 0 and any(row["masks"].values())
                for row in record["targets"]["actions"])
            or any(record["targets"].get("plan", {}).get("masks", {}).values()))


def _counts(value, name):
    object_keys(value, COUNT_FIELDS, name)
    for key in COUNT_FIELDS: integer(value[key], name + "." + key)
    if value["allocated_worlds"] != sum(value[key] for key in COUNT_FIELDS[1:]):
        reject(name + " world count conservation failed")
    return value


def validate_production_record(record, config, *, require_usable=True):
    """Production admission adds provenance; engineering reader stays compatible.

    A development envelope or trainable:false is never converted into admitted
    data. Diagnostic/failed rows still contribute aliases in preparation first.
    """
    validate_record(record, config)
    audit, targets, public = record["audit_only"], record["targets"], record["public_input"]
    native_context = any(event.get("kind") == "native_entry_assets" for event in public["observation"]["history"])
    if ("record_kind" in record or audit.get("trainable") is False or native_context
            or audit.get("native_import") is not None
            or audit.get("label_status") in ("raw_unlabeled", "development_teacher_prototype")):
        reject("native_development_not_admitted")
    if "trainable" in audit and audit["trainable"] is not True:
        reject("trainable flag must be an explicit boolean")
    for field in PROVENANCE_FIELDS:
        if not isinstance(audit.get(field), str) or not audit[field].strip(): reject("provenance_missing:" + field)
    if audit.get("source_kind") not in ("constructed", "natural", "stress_test"): reject("source_kind_undeclared")
    if audit["source_kind"] == "natural" and (not isinstance(audit.get("natural_reachability_evidence"), str) or not audit["natural_reachability_evidence"].strip()):
        reject("natural_source_requires_reachability_evidence")
    versions = audit.get("versions")
    if (not isinstance(versions, dict) or not REQUIRED_VERSIONS <= set(versions)
            or any(not isinstance(v, str) or not v.strip() for v in versions.values())):
        reject("versions_missing")
    if versions["public_schema"] != public["schema_version"] or versions["observation_schema"] != public["observation"]["schema"]:
        reject("public_schema_version_mismatch")
    for field, stable in (("continuation_version", "continuation"), ("teacher_version", "teacher"), ("objective_version", "objective"),
                          ("simulator_commit", "simulator"), ("rules_version", "rules"), ("label_endpoint", "endpoint"),
                          ("controller_version", "controller"), ("sampler_version", "sampler"), ("dataset_version", "dataset")):
        full = audit.get(field)
        if not isinstance(full, str) or not full or (full.split(":", 1)[0] if stable == "continuation" else full) != versions[stable]:
            reject("audit_version_inconsistent:" + field)
    if audit.get("independent_final_evaluation") is not True: reject("independent_eval_unverified")
    for field in ("n_exploration", "n_independent_eval", "n_unresolved", "n_error"):
        integer(audit.get(field), field)
    for field, count in (("sampler_seeds", "n_independent_eval"), ("exploration_seeds", "n_exploration")):
        values = audit.get(field)
        if not isinstance(values, list): reject("complete_seed_accounting_required")
        for value in values: integer(value, field, 0, 2**64 - 1)
        if len(values) != audit[count] or len(set(values)) != len(values): reject("sampler_seed_count_or_uniqueness_invalid")
    if set(audit["sampler_seeds"]) & set(audit["exploration_seeds"]): reject("exploration_evaluation_seed_overlap")
    counts = []
    for index, row in enumerate(targets["actions"]):
        if row["action_index"] != index: reject("action_index_alignment_invalid")
        counts.append(_counts({key: row.get(key) for key in COUNT_FIELDS}, "action"))
        complete = counts[-1]["allocated_worlds"] > 0 and counts[-1]["allocated_worlds"] == counts[-1]["completed_worlds"]
        if any(row["masks"].values()) and not complete: reject("incomplete_worlds_have_point_targets")
        if row["masks"]["win_probability"] and row["masks"]["death_probability"] and row["win_probability"] + row["death_probability"] > 1 + 1e-7:
            reject("win_and_death_probability_inconsistent")
    if (targets["pairwise"] or targets["equivalent_action_set"]) and any(c["allocated_worlds"] == 0 or c["completed_worlds"] != c["allocated_worlds"] for c in counts):
        reject("incomplete_root_has_strong_ranking")
    if any(pair["weight"] <= 0 for pair in targets["pairwise"]): reject("pairwise_target_invalid")
    if "plan" in targets:
        if audit.get("candidate_evaluation_performed") is not False or any(any(c.values()) for c in counts):
            reject("whole_plan_labels_do_not_evaluate_candidates")
        plans = object_keys(audit.get("plan_counts"), ("baseline", "plan"), "plan_counts")
        counts = [_counts(plans[key], key) for key in ("baseline", "plan")]
        plan = targets["plan"]
        if any(c["allocated_worlds"] != audit["n_independent_eval"] for c in counts) or plan["allocated_worlds"] != audit["n_independent_eval"]:
            reject("plan_independent_world_count_inconsistent")
        if plan["success_completed_worlds"] != plans["plan"]["completed_worlds"]:
            reject("plan_success_completion_inconsistent")
        paired = plan["paired_completed_worlds"]
        if paired > min(c["completed_worlds"] for c in counts) or paired < max(0, sum(c["completed_worlds"] for c in counts) - plan["allocated_worlds"]):
            reject("plan_pair_completion_inconsistent")
        cost_unit = "policy_world_execution"
    else:
        if max(c["allocated_worlds"] for c in counts) != audit["n_independent_eval"]:
            reject("root_independent_world_count_inconsistent")
        cost_unit = "candidate_action_world_execution"
    if audit["n_error"] != sum(c["error_worlds"] for c in counts): reject("audit_error_count_inconsistent")
    if audit["n_unresolved"] != sum(c["truncated_worlds"] + c["other_worlds"] for c in counts): reject("audit_unresolved_count_inconsistent")
    costs = audit.get("costs")
    if not isinstance(costs, dict): reject("costs_missing")
    if costs.get("cost_unit", cost_unit) != cost_unit: reject("cost_unit_invalid")
    for field in ("root_candidates", "worlds_allocated", "worlds_completed", "rollout_decisions", "elapsed_seconds", "clone_seconds", "settlement_seconds", "peak_worker_memory_bytes"):
        if field not in costs: reject("cost_missing:" + field)
        if costs[field] is not None: number(costs[field], "cost." + field, 0, float("inf"))
    if costs["root_candidates"] != len(targets["actions"]): reject("root_candidates_cost_inconsistent")
    for key, count in (("worlds_allocated", "allocated_worlds"), ("worlds_completed", "completed_worlds")):
        if costs[key] != sum(c[count] for c in counts): reject("cost_world_count_inconsistent:" + key)
    if require_usable and not has_usable_targets(record): reject("root_has_no_usable_targets")
    return record


class PreparedDatasetV2:
    """Verified immutable v2 training/validation shards; test targets stay opaque."""
    def __init__(self, root, split, config):
        if split not in ("train", "validation"):
            reject("v2 fitting never loads test targets; use frozen_test_shards metadata")
        self.root, self.split, self.config = Path(root), split, config
        self.path = self.root / "manifest.json"
        self.config_sha256 = canonical_object_digest(config)
        paths, self.manifest_sha256 = prepared_paths(self.root, split, self.config_sha256,
            expected_identity=PUBLIC_IDENTITY_SCHEME, expected_pipeline="nosl.dataset.prepare.v4")
        self.manifest = json.loads(self.path.read_text())
        state = json.loads((self.root / self.manifest["latest_state"]["path"]).read_text())
        owners = {token: info for info in state["components"].values() for token in info["tokens"]}
        historic_components = {}
        for reference in self.manifest["stages"]:
            stage = json.loads((self.root / reference["manifest"]).read_text())
            for descriptor in stage["files"]:
                if descriptor["kind"] == "split_state":
                    historic_components.update(json.loads((self.root / descriptor["path"]).read_text())["components"])
        self.records = []
        for path in paths:
            with path.open(encoding="utf-8") as handle:
                for line in handle:
                    if not line.strip(): continue
                    row = validate_production_record(json.loads(line), config)
                    audit = row["audit_only"]
                    digest = public_input_digest(row["public_input"])
                    component = owners.get("prepared_public_input_digest:" + digest)
                    original = historic_components.get(audit.get("provenance_component"))
                    if (audit.get("dataset_split") != split or audit.get("prepared_public_input_digest") != digest
                            or audit.get("versions") != self.manifest["versions"] or component is None or component["split"] != split
                            or original is None or original["split"] != split
                            or "prepared_public_input_digest:" + digest not in original["tokens"]
                            or "prepared_public_input_digest:" + digest not in component["tokens"]
                            or not {key + ":" + audit[key] for key in PROVENANCE_FIELDS} <= set(component["tokens"])
                            or not {"prepared_public_input_digest:" + d for d in legacy_alias_digests(row["public_input"])} <= set(component["tokens"])):
                        reject("prepared v2 row identity/provenance mismatch")
                    self.records.append(row)
        if not self.records: reject("prepared split is empty; independent groups required")
        if len({public_input_digest(row["public_input"]) for row in self.records}) != len(self.records):
            reject("duplicate prepared public roots")
        self.sha256 = self.manifest_sha256 + ":" + split
        self.records_sha256 = canonical_object_digest(self.records)
        if _hash_file(self.path) != self.manifest_sha256: reject("manifest changed during read")

    def verify_integrity(self):
        current = type(self)(self.root, self.split, self.config)
        if (current.sha256 != self.sha256 or current.records_sha256 != self.records_sha256
                or canonical_object_digest(self.records) != self.records_sha256):
            reject("prepared inputs changed after loading")
        return self.manifest_sha256

    def __len__(self): return len(self.records)
    def __getitem__(self, index): return self.records[index]
