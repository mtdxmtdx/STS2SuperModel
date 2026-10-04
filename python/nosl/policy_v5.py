"""Inference-safe contracts for the separately versioned full v5 policy path.

Integrity receipts are not authentication, calibration, execution permission or
promotion. This module never imports datasets, a trainer, or a rules engine.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json

import torch

from .inference_v2 import (POLICY_ACTION_KINDS, POLICY_COVERAGE_KEYS,
                           valid_supervision_coverage as valid_v2_coverage)
from .inference_v5 import V5_INFERENCE_SOURCES
from .policy_applicability_v5 import APPLICABILITY_PROFILE
from .reproducibility import runtime_identity, source_hashes
from .schema import HEADS, SchemaError, integer, number, object_keys
from .schema_v2 import PLAN_HEADS
from .schema_v5 import MODEL_VERSION, PUBLIC_SCHEMA, validate_config

POLICY_VERSION = "nosl.full-policy.v5.1"
BUNDLE_FORMAT = "nosl.student.full-policy.bundle.v5.1"
INPUTS_FORMAT = "nosl.student.full-policy.inputs.v5.1"
SUPERVISION_FORMAT = "nosl.training.full-policy.supervision.v5.1"
PROGRESS_FORMAT = "nosl.training.full-policy.progress.v5.1"
REVIEW_FORMAT = "nosl.training.full-policy.review.v5.1"
AUTHORIZATION_FORMAT = "nosl.training.full-policy.authorization.v5.1"
TRAINING_CONFIG_FORMAT = "nosl.training.full-policy.config.v5.1"
PURPOSES = ("engineering-fixture", "bounded-objective-pilot", "native-objective-candidate")
OBJECTIVE_IDENTITY_FIELDS = ("format", "objective_spec_sha256", "evaluator_source_sha256", "calibration_evidence_sha256",
    "endpoint", "continuation_policy_id", "independent_final_evaluation", "public_conditioning_scope",
    "objective_profile_status", "objective_calibrated")
INFERENCE_SOURCES = tuple(dict.fromkeys((*V5_INFERENCE_SOURCES,
    "policy_v5.py", "policy_applicability_v5.py", "inference_policy_v5.py")))


def require(condition, reason):
    if not condition:
        raise SchemaError("full_policy_v5:" + reason)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                    allow_nan=False).encode()).hexdigest()


def sha(value, where):
    require(isinstance(value, str) and len(value) == 64
            and all(c in "0123456789abcdef" for c in value), where + "_invalid_sha256")
    return value


def validate_objective_identity(objective):
    object_keys(objective, OBJECTIVE_IDENTITY_FIELDS, "run objective identity")
    require(objective["format"] == "nosl.dataset.full-policy.objective-attestation.v5.1"
            and objective["endpoint"] == "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
            and objective["independent_final_evaluation"] is True and objective["public_conditioning_scope"] == "full_v5"
            and isinstance(objective["continuation_policy_id"], str) and bool(objective["continuation_policy_id"].strip()),
            "objective_attestation_scope_invalid")
    for key in ("objective_spec_sha256", "evaluator_source_sha256"):
        sha(objective[key], key)
    if objective["objective_profile_status"] == "candidate":
        require(objective["objective_calibrated"] is False and objective["calibration_evidence_sha256"] is None,
                "candidate_objective_cannot_claim_calibration")
    else:
        require(objective["objective_profile_status"] == "calibrated" and objective["objective_calibrated"] is True,
                "objective_calibration_status_invalid")
        sha(objective["calibration_evidence_sha256"], "calibration_evidence")
    return objective


def mechanics_config(config):
    """Configuration selection only; never strip public v5 conditioning."""
    return config["base_config"]["base_config"]["base_config"]


def implementation_fingerprint():
    return {"policy_version": POLICY_VERSION, "model_version": MODEL_VERSION,
            "source_sha256": source_hashes(INFERENCE_SOURCES), "runtime": runtime_identity()}


def validate_training_config(value, config):
    validate_config(config)
    object_keys(value, ("format", "student_config_sha256", "seed", "torch_threads", "batch_size",
        "learning_rate", "weight_decay", "gradient_clip_norm", "max_optimizer_steps",
        "max_training_roots", "max_validation_roots", "max_epochs", "formal_training", "promotion"),
        "full v5 training config")
    require(value["format"] == TRAINING_CONFIG_FORMAT and value["student_config_sha256"] == digest(config)
            and value["formal_training"] is False and value["promotion"] is False, "training_config_mismatch")
    for key, low, high in (("seed", 0, 2**32 - 1), ("torch_threads", 1, 8), ("batch_size", 1, 64),
                          ("max_optimizer_steps", 1, 700), ("max_training_roots", 1, 5000),
                          ("max_validation_roots", 1, 5000), ("max_epochs", 1, 1)):
        integer(value[key], key, low, high)
    number(value["learning_rate"], "learning_rate", 1e-12, .01)
    number(value["weight_decay"], "weight_decay", 0, 1)
    number(value["gradient_clip_norm"], "gradient_clip_norm", 1e-12, 10)
    return value


def review_verified(frozen):
    review = frozen.get("review")
    if not isinstance(review, dict): return False
    keys = {"format", "accepted", "inputs_sha256", "reviewed_by", "evidence_sha256",
            "objective_supervision_reviewed", "full_public_conditioning_reviewed", "source_isolation_reviewed"}
    if set(review) != keys: return False
    return (review["format"] == REVIEW_FORMAT and review["accepted"] is True
            and review["inputs_sha256"] == digest({k: v for k, v in frozen.items() if k != "review"})
            and isinstance(review["reviewed_by"], str) and bool(review["reviewed_by"].strip())
            and isinstance(review["evidence_sha256"], str) and len(review["evidence_sha256"]) == 64
            and all(c in "0123456789abcdef" for c in review["evidence_sha256"])
            and all(review[k] is True for k in ("objective_supervision_reviewed",
                    "full_public_conditioning_reviewed", "source_isolation_reviewed")))


def authorization_verified(value, frozen):
    if not isinstance(value, dict): return False
    keys = {"format", "authorized", "purpose", "authorization_id", "frozen_inputs_sha256", "review_sha256"}
    return (set(value) == keys and value["format"] == AUTHORIZATION_FORMAT and value["authorized"] is True
            and value["purpose"] == "bounded-objective-pilot" and review_verified(frozen)
            and frozen["purpose"] == value["purpose"]
            and value["frozen_inputs_sha256"] == digest(frozen) and value["review_sha256"] == digest(frozen["review"])
            and isinstance(value["authorization_id"], str) and bool(value["authorization_id"].strip()))


def state_digest(state):
    """Portable exact CPU tensor identity, independent of torch.save metadata."""
    require(isinstance(state, dict) and bool(state), "model_state_missing")
    result = hashlib.sha256()
    for name, value in sorted(state.items()):
        require(isinstance(name, str) and isinstance(value, torch.Tensor)
                and value.device.type == "cpu" and value.layout == torch.strided
                and bool(torch.isfinite(value).all()), "model_state_nonfinite_or_unsupported")
        value = value.detach().contiguous()
        result.update(json.dumps([name, str(value.dtype), list(value.shape)], separators=(",", ":")).encode())
        # No NumPy dependency. Index logical tensor bytes rather than a view's
        # potentially larger backing storage; tolist performs a bulk conversion.
        result.update(bytes(value.reshape(-1).view(torch.uint8).tolist()))
    return result.hexdigest()


def validate_output(output, public, config):
    """Validate every head, exact legal mask, and the only permitted -inf slots."""
    object_keys(output, (*HEADS, "legal_mask", "ranking_score", "plan"), "full v5 output")
    count = len(public["candidate_actions"])
    bins = mechanics_config(config)["base_config"]["hp_bins"]
    for name in HEADS:
        value = output[name]
        shape = (count, bins) if name == "hp_distribution" else (count,)
        require(isinstance(value, torch.Tensor) and tuple(value.shape) == shape
                and value.dtype == torch.float32 and value.device.type == "cpu"
                and bool(torch.isfinite(value).all()), "invalid_output:" + name)
    expected = torch.tensor(public["legal_mask"], dtype=torch.bool)
    mask, ranking = output["legal_mask"], output["ranking_score"]
    require(isinstance(mask, torch.Tensor) and mask.dtype == torch.bool
            and mask.device.type == "cpu" and torch.equal(mask, expected), "output_legal_mask_mismatch")
    require(isinstance(ranking, torch.Tensor) and ranking.device.type == "cpu"
            and ranking.dtype == torch.float32 and tuple(ranking.shape) == (count,)
            and torch.equal(ranking, output["value"].masked_fill(~expected, -torch.inf)),
            "output_ranking_mask_or_value_mismatch")
    object_keys(output["plan"], PLAN_HEADS, "full v5 plan output")
    for name, value in output["plan"].items():
        require(isinstance(value, torch.Tensor) and value.shape == torch.Size([])
                and value.dtype == torch.float32 and value.device.type == "cpu"
                and bool(torch.isfinite(value)), "invalid_plan_output:" + name)
    return output


def valid_coverage(value, config):
    if not isinstance(value, dict) or value.get("format") != SUPERVISION_FORMAT:
        return False
    if set(value) != {"format", "roots", "action_heads", "plan_heads", "action_policy", "policy_action_kinds"}:
        return False
    for section, keys in (("action_heads", {"masked_rows", "loss_rows", "loss_roots"}),
                          ("plan_heads", {"masked_roots", "loss_roots"})):
        counts = value.get(section)
        if not isinstance(counts, dict) or any(not isinstance(row, dict) or set(row) != keys for row in counts.values()):
            return False
    return valid_v2_coverage({**value, "format": "nosl.training.supervision.v2"}, mechanics_config(config))


def empty_coverage():
    return {"format": SUPERVISION_FORMAT, "roots": 0,
            "action_heads": {h: {"masked_rows": 0, "loss_rows": 0, "loss_roots": 0} for h in HEADS},
            "plan_heads": {h: {"masked_roots": 0, "loss_roots": 0} for h in PLAN_HEADS},
            "action_policy": {k: 0 for k in POLICY_COVERAGE_KEYS},
            "policy_action_kinds": {k: 0 for k in POLICY_ACTION_KINDS}}


def merge_coverage(values):
    result = empty_coverage()
    for value in values:
        result["roots"] += value["roots"]
        for section in ("action_heads", "plan_heads"):
            for head, row in value[section].items():
                for key, count in row.items(): result[section][head][key] += count
        for section in ("action_policy", "policy_action_kinds"):
            for key, count in value[section].items(): result[section][key] += count
    return result


def empty_progress():
    return {"format": PROGRESS_FORMAT, "optimizer_steps": 0, "action_policy_optimizer_steps": 0,
            "consumed": empty_coverage(), "committed_batches": []}


def validate_training_evidence(frozen, progress, config, *, final_state_sha256):
    """Recompute consumed coverage from exact committed record IDs, never totals."""
    object_keys(frozen, ("format", "policy_version", "public_schema", "model_version", "purpose",
        "config_sha256", "training_config", "implementation", "protection_sha256", "splits",
        "training_supervision", "objective_identity", "producer_receipts", "review", "test_labels_read"), "full v5 frozen inputs")
    require(frozen["format"] == INPUTS_FORMAT and frozen["policy_version"] == POLICY_VERSION
            and frozen["public_schema"] == PUBLIC_SCHEMA and frozen["model_version"] == MODEL_VERSION
            and frozen["purpose"] in PURPOSES and frozen["config_sha256"] == digest(config)
            and frozen["test_labels_read"] is False, "frozen_schema_config_or_claim_mismatch")
    sha(frozen["protection_sha256"], "protection")
    validate_training_config(frozen["training_config"], config)
    objective = validate_objective_identity(frozen["objective_identity"])
    producers = frozen["producer_receipts"]
    require(isinstance(producers, dict) and (not producers if frozen["purpose"] == "engineering-fixture" else bool(producers)),
            "native_producer_receipt_bindings_required")
    for key, value in producers.items():
        sha(key, "producer_receipt"); sha(value, "producer_metadata")
    implementation = frozen["implementation"]
    object_keys(implementation, ("format", "source_sha256", "runtime", "cpu_settings"), "training implementation")
    require(implementation["format"] == "nosl.training.full-policy.implementation.v5.1"
            and implementation["runtime"] == runtime_identity()
            and isinstance(implementation["source_sha256"], dict) and bool(implementation["source_sha256"]),
            "training_implementation_missing")
    for name, value in implementation["source_sha256"].items():
        require(isinstance(name, str) and name.startswith("python/nosl/") and ".." not in name,
                "training_source_path_invalid")
        sha(value, "training_source")
    require(all(implementation["source_sha256"].get(name) == value
                for name, value in source_hashes(INFERENCE_SOURCES).items()), "training_inference_sources_differ")
    object_keys(frozen["splits"], ("train", "validation"), "full v5 splits")
    descriptors = {}
    for split, rows in frozen["splits"].items():
        limit = frozen["training_config"]["max_" + ("training" if split == "train" else "validation") + "_roots"]
        require(isinstance(rows, list) and 1 <= len(rows) <= limit, "empty_or_oversized_split:" + split)
        for row in rows:
            object_keys(row, ("record_sha256", "public_input_sha256", "targets_sha256",
                             "objective_sha256", "objective_identity_sha256", "producer_receipt_sha256", "coverage"), "full v5 record descriptor")
            for key in ("record_sha256", "public_input_sha256", "targets_sha256", "objective_sha256"):
                sha(row[key], key)
            require(row["objective_identity_sha256"] == digest(objective), "mixed_objective_identity")
            require(row["producer_receipt_sha256"] is None if frozen["purpose"] == "engineering-fixture"
                    else row["producer_receipt_sha256"] in producers, "record_producer_receipt_unbound")
            require(row["record_sha256"] not in descriptors and valid_coverage(row["coverage"], config)
                    and row["coverage"]["roots"] == 1, "duplicate_record_or_invalid_coverage")
            descriptors[row["record_sha256"]] = row
    train = {r["record_sha256"]: r for r in frozen["splits"]["train"]}
    corpus = merge_coverage(r["coverage"] for r in train.values())
    require(frozen["training_supervision"] == corpus and valid_coverage(corpus, config), "corpus_coverage_mismatch")
    object_keys(progress, ("format", "optimizer_steps", "action_policy_optimizer_steps", "consumed",
                          "committed_batches"), "full v5 progress")
    require(progress["format"] == PROGRESS_FORMAT, "progress_version_mismatch")
    steps = integer(progress["optimizer_steps"], "optimizer_steps", 0, 700)
    require(steps <= frozen["training_config"]["max_optimizer_steps"], "lifetime_step_budget_exceeded")
    batches = progress["committed_batches"]
    require(isinstance(batches, list) and len(batches) == steps, "committed_batch_count_mismatch")
    consumed, used, prior, policy_steps = [], set(), None, 0
    batch_size = frozen["training_config"]["batch_size"]
    for step, batch in enumerate(batches, 1):
        object_keys(batch, ("step", "records", "before_state_sha256", "after_state_sha256"), "committed v5 batch")
        require(type(batch["step"]) is int and batch["step"] == step, "committed_step_order_invalid")
        ids = batch["records"]
        require(isinstance(ids, list) and 1 <= len(ids) <= batch_size
                and all(isinstance(key, str) and key in train and key not in used for key in ids)
                and len(ids) == len(set(ids)), "committed_records_invalid_or_replayed")
        sha(batch["before_state_sha256"], "before_state"); sha(batch["after_state_sha256"], "after_state")
        require(prior is None or batch["before_state_sha256"] == prior, "committed_model_chain_broken")
        prior = batch["after_state_sha256"]
        coverage = merge_coverage(train[key]["coverage"] for key in ids)
        policy_steps += coverage["action_policy"]["eligible_roots"] > 0
        consumed.append(coverage); used.update(ids)
    require(progress["consumed"] == merge_coverage(consumed) and valid_coverage(progress["consumed"], config)
            and type(progress["action_policy_optimizer_steps"]) is int
            and progress["action_policy_optimizer_steps"] == policy_steps, "consumed_supervision_mismatch")
    require(prior is None or prior == final_state_sha256, "learned_state_not_last_committed_batch")
    return bool(steps and policy_steps and frozen["purpose"] == "bounded-objective-pilot")


def validate_manifest(manifest, config, state):
    object_keys(manifest, ("format", "policy_version", "public_schema", "model_version", "config_sha256",
        "weights_sha256", "state_sha256", "implementation", "frozen_inputs", "supervision_progress",
        "supervision_sha256", "authorization", "status", "trained", "optimizer_steps", "calibrated",
        "promoted", "formal_training_run", "safe_learned_plan_execution_verified", "test_labels_read", "applicability_profile"),
        "full v5 bundle manifest")
    require(manifest["format"] == BUNDLE_FORMAT and manifest["policy_version"] == POLICY_VERSION
            and manifest["public_schema"] == PUBLIC_SCHEMA and manifest["model_version"] == MODEL_VERSION
            and manifest["config_sha256"] == digest(config) and manifest["applicability_profile"] == APPLICABILITY_PROFILE,
            "bundle_version_config_or_applicability_mismatch")
    require(manifest["implementation"] == implementation_fingerprint(), "inference_source_or_runtime_changed")
    sha(manifest["weights_sha256"], "weights")
    require(manifest["state_sha256"] == state_digest(state), "model_state_checksum_mismatch")
    frozen, progress = manifest["frozen_inputs"], manifest["supervision_progress"]
    validate_training_evidence(frozen, progress, config, final_state_sha256=manifest["state_sha256"])
    steps = progress["optimizer_steps"]
    require(type(manifest["optimizer_steps"]) is int and manifest["optimizer_steps"] == steps
            and manifest["trained"] is (steps > 0) and manifest["status"] == "EXPERIMENTAL_UNPROMOTED"
            and all(manifest[key] is False for key in ("calibrated", "promoted", "formal_training_run",
                "safe_learned_plan_execution_verified", "test_labels_read")), "unsupported_bundle_claim")
    require(manifest["supervision_sha256"] == digest({"frozen_inputs": frozen, "progress": progress}),
            "supervision_checksum_mismatch")
    require(not steps or authorization_verified(manifest["authorization"], frozen), "learned_bundle_missing_authorization")
    return manifest


def action_policy_training_verified(manifest, config, public):
    """Called only after full public validation and manifest/bundle validation."""
    if not manifest or manifest.get("trained") is not True:
        return False
    frozen, progress = manifest["frozen_inputs"], manifest["supervision_progress"]
    if not validate_training_evidence(frozen, progress, config, final_state_sha256=manifest["state_sha256"]):
        return False
    corpus, consumed = frozen["training_supervision"], progress["consumed"]
    kinds = {a["kind"] for a, legal in zip(public["candidate_actions"], public["legal_mask"]) if legal}
    return all(corpus["policy_action_kinds"][kind] > 0 and consumed["policy_action_kinds"][kind] > 0 for kind in kinds)
