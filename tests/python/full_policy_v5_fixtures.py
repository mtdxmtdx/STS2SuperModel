"""Synthetic contract objects only; no authentic labels, review or authorization.

Values and receipts below exercise guards. They must never be mistaken for a
source corpus, a completed optimization step or approval to train real data.
"""
from copy import deepcopy
from pathlib import Path

from test_public_map_v5 import public_v5
from test_public_evidence_v4 import unavailable_targets, old_public
from nosl.data import REGISTRY_VERSION
from nosl.data_policy_v5 import RECORD_FORMAT, OBJECTIVE_FORMAT, ENDPOINT, PolicyDatasetV5
from nosl.native_pilot import source_run_identity
from nosl.policy_v5 import (TRAINING_CONFIG_FORMAT, REVIEW_FORMAT, AUTHORIZATION_FORMAT,
    BUNDLE_FORMAT, POLICY_VERSION, APPLICABILITY_PROFILE, digest, merge_coverage, state_digest,
    implementation_fingerprint as inference_fingerprint)
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME as OLD_IDENTITY
from nosl.public_identity_v5 import public_input_digest
from nosl.protection_v5 import component_id
from nosl.schema import HEADS
from nosl.schema_v2 import PLAN_HEADS
from nosl.schema_v5 import MODEL_VERSION, PUBLIC_SCHEMA, load_config
from nosl.train_policy_v5 import PolicyTrainingSessionV5

ROOT = Path(__file__).resolve().parents[2]


def config_fixture():
    config = load_config(ROOT / "configs/student.v5.engineering.json")
    config["base_config"]["base_config"]["base_config"]["base_config"]["hidden_dim"] = 16
    return config


def training_fixture(config):
    return {"format": TRAINING_CONFIG_FORMAT, "student_config_sha256": digest(config),
            "seed": 1729, "torch_threads": 1, "batch_size": 2, "learning_rate": .0003,
            "weight_decay": .01, "gradient_clip_norm": 1., "max_optimizer_steps": 3,
            "max_training_roots": 6, "max_validation_roots": 6, "max_epochs": 1,
            "formal_training": False, "promotion": False}


def public_fixture(tag=0, *, finite=False, illegal=False):
    public = public_v5(source=old_public("finite-hunt-record-v2.jsonl")["public_input"] if finite else None)
    if not finite:
        public["observation"]["block"] += tag
        public["public_evidence"]["events"][-1]["payload"]["observation"]["block"] += tag
    if illegal:
        public["legal_mask"][-1] = False
        public["public_evidence"]["events"][-1]["payload"]["actions"] = deepcopy(public["candidate_actions"][:-1])
    return public


def record_fixture(tag=0, *, purpose="engineering-fixture", finite=False, auxiliary=False, illegal=False):
    public = public_fixture(tag, finite=finite, illegal=illegal)
    targets = unavailable_targets(public)
    for i, row in enumerate(targets["actions"]):
        row.update(allocated_worlds=4, completed_worlds=4, truncated_worlds=0, error_worlds=0, other_worlds=0,
                   sample_weight=1.)
        if not public["legal_mask"][i]: continue
        row.update(quality="complete", value=float(-5 - i), win_probability=.75, death_probability=.25,
                   expected_final_hp=30., hp_distribution=[{"hp": 20., "probability": .5}, {"hp": 40., "probability": .5}],
                   potion_net_change=0., masks={head: True for head in HEADS})
        if auxiliary:
            row["value"] = None; row["masks"]["value"] = False; row["quality"] = "objective_value_unresolved"
    legal = [i for i, allowed in enumerate(public["legal_mask"]) if allowed]
    if len(legal) >= 2 and not auxiliary:
        targets["pairwise"] = [{"preferred": legal[0], "other": legal[-1], "weight": .75}]
        targets["equivalent_action_set"] = [legal[0]]
    if finite:
        targets["plan"] = {"label_scope": "whole_plan_from_anchor", "specified_success_probability": .9,
            "extra_net_hp_loss": 2., "masks": {h: True for h in PLAN_HEADS}, "allocated_worlds": 4,
            "success_completed_worlds": 4, "paired_completed_worlds": 4}
    seed = "synthetic-only-" + str(tag)
    audit = {"purpose": purpose, "trainable": purpose == "bounded-objective-pilot",
        "source_kind": {"engineering-fixture": "synthetic_contract_fixture", "bounded-objective-pilot": "externally_reviewed_objective_outcomes",
                        "native-objective-candidate": "native_empirical_objective_candidate"}[purpose],
        "source_run_group": "synthetic-run-" + str(tag), "source_combat_id": "synthetic-combat-" + str(tag),
        "branch_family": "synthetic-branch-" + str(tag), "public_state_digest": digest(["synthetic-state", tag]),
        "source_artifact_sha256": digest(["synthetic-artifact", tag]), "source_record_sha256": digest(["synthetic-record", tag]),
        "producer_receipt_sha256": None if purpose == "engineering-fixture" else digest(["synthetic-producer-receipt", tag]),
        "conditioned_public_input_digest": public_input_digest(public), "targets_sha256": digest(targets),
        "native_run": True, "actual_seed": seed, "source_draw_seed": 100000 + tag,
        "native_source_run_identity": source_run_identity(seed)}
    objective = {"format": OBJECTIVE_FORMAT, "objective_spec_sha256": digest("synthetic-objective-spec"),
        "evaluator_source_sha256": digest("synthetic-evaluator"), "calibration_evidence_sha256": digest("synthetic-calibration-test"),
        "evaluation_design_sha256": digest("synthetic-independent-samples"), "endpoint": ENDPOINT,
        "continuation_policy_id": "synthetic-contract-continuation", "independent_final_evaluation": True,
        "public_conditioning_scope": "full_v5", "objective_profile_status": "calibrated", "objective_calibrated": True}
    return {"format": RECORD_FORMAT, "public_input": public, "targets": targets, "audit_only": audit, "objective": objective}


def rebind(record):
    record["audit_only"]["targets_sha256"] = digest(record["targets"])
    record["audit_only"]["conditioned_public_input_digest"] = public_input_digest(record["public_input"])
    return record


def protection_fixture():
    tokens = ["source_run_group:synthetic-historic-unrelated"]
    return {"schema_version": REGISTRY_VERSION, "public_identity_scheme": OLD_IDENTITY,
            "source": {"manifest_sha256": digest("synthetic-protection-manifest"),
                "split_state_sha256": digest("synthetic-split-state"), "versions": {"fixture": "v1"},
                "frozen_test_shards": []}, "components": {component_id(tokens): {"split": "test", "tokens": tokens}}}


def session_fixture(*, purpose="engineering-fixture", finite=False, auxiliary=False):
    config = config_fixture()
    # All real Dataset/Session objects remain engineering fixtures. Inference
    # tests request hypothetical pilot receipts through manifest_fixture only.
    records = {"train": [record_fixture(1, finite=finite, auxiliary=auxiliary)],
               "validation": [record_fixture(7, auxiliary=auxiliary)]}
    datasets = {split: PolicyDatasetV5(rows, config, split=split, purpose="engineering-fixture") for split, rows in records.items()}
    session = PolicyTrainingSessionV5(datasets, config, training_fixture(config), protection_fixture())
    session._synthetic_receipt_purpose = purpose
    return session


def reviewed_fixture(session):
    """Fabricated structural receipt used only to test rejection/selection gates."""
    frozen = deepcopy(session.frozen)
    if session._synthetic_receipt_purpose == "bounded-objective-pilot":
        frozen["purpose"] = "bounded-objective-pilot"
        receipt_key = digest("SYNTHETIC STRUCTURAL RECEIPT - NOT A NATIVE SOURCE")
        frozen["producer_receipts"] = {receipt_key: digest("SYNTHETIC ATTEMPT METADATA")}
        for rows in frozen["splits"].values():
            for row in rows: row["producer_receipt_sha256"] = receipt_key
    frozen["review"] = {"format": REVIEW_FORMAT, "accepted": True,
        "inputs_sha256": digest({k: v for k, v in frozen.items() if k != "review"}),
        "reviewed_by": "SYNTHETIC TEST ONLY", "evidence_sha256": digest("not-real-review"),
        "objective_supervision_reviewed": True, "full_public_conditioning_reviewed": True, "source_isolation_reviewed": True}
    authorization = {"format": AUTHORIZATION_FORMAT, "authorized": True, "purpose": "bounded-objective-pilot",
        "authorization_id": "SYNTHETIC TEST ONLY - NEVER EXECUTE", "frozen_inputs_sha256": digest(frozen),
        "review_sha256": digest(frozen["review"])}
    return frozen, authorization


def manifest_fixture(session, *, consumed_indices=(0,)):
    """In-memory structural fixture, never exported as learned weights."""
    frozen, authorization = reviewed_fixture(session)
    descriptors = [frozen["splits"]["train"][i] for i in consumed_indices]
    coverage = merge_coverage(r["coverage"] for r in descriptors)
    state_hash = state_digest(session.model.state_dict())
    progress = {"format": "nosl.training.full-policy.progress.v5.1", "optimizer_steps": 1,
        "action_policy_optimizer_steps": int(coverage["action_policy"]["eligible_roots"] > 0), "consumed": coverage,
        "committed_batches": [{"step": 1, "records": [r["record_sha256"] for r in descriptors],
            "before_state_sha256": state_hash, "after_state_sha256": state_hash}]}
    return {"format": BUNDLE_FORMAT, "policy_version": POLICY_VERSION, "public_schema": PUBLIC_SCHEMA,
        "model_version": MODEL_VERSION, "config_sha256": digest(session.config), "weights_sha256": digest("not-a-file"),
        "state_sha256": state_hash, "implementation": inference_fingerprint(), "frozen_inputs": frozen,
        "supervision_progress": progress, "supervision_sha256": digest({"frozen_inputs": frozen, "progress": progress}),
        "authorization": authorization, "status": "EXPERIMENTAL_UNPROMOTED", "trained": True, "optimizer_steps": 1,
        "calibrated": False, "promoted": False, "formal_training_run": False,
        "safe_learned_plan_execution_verified": False, "test_labels_read": False, "applicability_profile": APPLICABILITY_PROFILE}
