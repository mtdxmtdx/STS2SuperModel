"""Full-v5 objective policy training engineering; default CLI is readiness only.

A separate reviewed objective corpus and exact bounded execution authorization
are mandatory. Auxiliary datasets/receipts cannot enter this interface. The
single-pass fitting implementation is provided for later authorized use; merely
loading, inspecting or reviewing it does not run training or write weights.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import random

import torch

from .data_policy_v5 import PolicyDatasetV5, supervision_coverage, validate_isolation
from .loss_v5 import decision_loss
from .model_v5 import StudentV5
from .policy_v5 import (BUNDLE_FORMAT, INPUTS_FORMAT, INFERENCE_SOURCES, POLICY_VERSION, OBJECTIVE_IDENTITY_FIELDS,
    APPLICABILITY_PROFILE,
    authorization_verified, digest, empty_progress, implementation_fingerprint as inference_fingerprint,
    merge_coverage, require, review_verified, state_digest, validate_manifest,
    validate_output, validate_training_config, validate_training_evidence)
from .prepare_v5 import SOURCE_FILES as PREPARATION_SOURCES
from .reproducibility import implementation_fingerprint as runtime_fingerprint, source_hashes
from .schema import SchemaError, integer, object_keys
from .schema_v5 import MODEL_VERSION, PUBLIC_SCHEMA, load_config, loads
from .train import atomic_save, seed_everything

CHECKPOINT_FORMAT = "nosl.training.full-policy.checkpoint.v5.1"
TRAINING_SOURCES = tuple(dict.fromkeys((*INFERENCE_SOURCES, *PREPARATION_SOURCES,
    "native_policy_v5.py", "data_policy_v5.py", "train_policy_v5.py", "loss_v5.py", "train.py", "vocabulary.py")))


def implementation_fingerprint():
    value = runtime_fingerprint()
    value.update(format="nosl.training.full-policy.implementation.v5.1", source_sha256=source_hashes(TRAINING_SOURCES))
    return value


def _descriptor(record, config):
    audit = record["audit_only"]
    return {"record_sha256": digest(record), "public_input_sha256": audit["conditioned_public_input_digest"],
            "targets_sha256": audit["targets_sha256"], "objective_sha256": digest(record["objective"]),
            "objective_identity_sha256": digest({k: record["objective"][k] for k in OBJECTIVE_IDENTITY_FIELDS}),
            "producer_receipt_sha256": audit["producer_receipt_sha256"],
            "coverage": supervision_coverage(record, config)}


def freeze_inputs(datasets, config, training, protection, *, review=None):
    validate_training_config(training, config)
    require(torch.get_num_threads() == training["torch_threads"], "configured_torch_threads_do_not_match_runtime")
    require(isinstance(datasets, dict) and set(datasets) == {"train", "validation"}, "train_and_validation_only")
    purposes = set()
    for split, dataset in datasets.items():
        require(type(dataset) is PolicyDatasetV5 and dataset.split == split and dataset.config == config,
                "exact_full_v5_dataset_required")
        dataset.verify_integrity(); purposes.add(dataset.purpose)
        require(len(dataset) <= training["max_" + ("training" if split == "train" else "validation") + "_roots"],
                "root_budget_exceeded")
    require(len(purposes) == 1, "mixed_dataset_purposes")
    producers = validate_isolation(datasets, protection)
    descriptors = {split: [_descriptor(r, config) for r in dataset.records] for split, dataset in datasets.items()}
    objective = {k: datasets["train"].records[0]["objective"][k] for k in OBJECTIVE_IDENTITY_FIELDS}
    require(all(row["objective_identity_sha256"] == digest(objective) for rows in descriptors.values() for row in rows),
            "mixed_objective_spec_evaluator_calibration_or_continuation")
    # No zero-signal roots are silently deleted or counted as consumed losses.
    for rows in descriptors.values():
        for row in rows:
            coverage = row["coverage"]
            require(any(v["loss_roots"] for section in ("action_heads", "plan_heads")
                        for v in coverage[section].values()) or coverage["action_policy"]["eligible_roots"] > 0,
                    "record_has_no_enabled_supervision")
    result = {"format": INPUTS_FORMAT, "policy_version": POLICY_VERSION, "public_schema": PUBLIC_SCHEMA,
        "model_version": MODEL_VERSION, "purpose": purposes.pop(), "config_sha256": digest(config),
        "training_config": deepcopy(training), "implementation": implementation_fingerprint(),
        "protection_sha256": digest(protection), "splits": descriptors,
        "objective_identity": objective,
        "producer_receipts": producers,
        "training_supervision": merge_coverage(r["coverage"] for r in descriptors["train"]),
        "test_labels_read": False, "review": deepcopy(review)}
    validate_training_evidence(result, empty_progress(), config, final_state_sha256=None)
    return result


def execution_gate(frozen, authorization=None):
    reasons = []
    if not isinstance(frozen, dict):
        reasons.append("new_full_v5_objective_corpus_required")
    else:
        if frozen.get("purpose") != "bounded-objective-pilot": reasons.append("unadmitted_or_engineering_records_cannot_fit")
        if frozen.get("training_supervision", {}).get("action_policy", {}).get("eligible_roots", 0) <= 0:
            reasons.append("enabled_objective_policy_supervision_required")
        if not review_verified(frozen): reasons.append("exact_external_objective_review_required")
        if not authorization_verified(authorization, frozen): reasons.append("exact_bounded_execution_authorization_required")
    return {"status": "AUTHORIZED_BOUNDED_OBJECTIVE_PILOT" if not reasons else "FIT_BLOCKED",
            "accepted": not reasons, "reasons": reasons, "formal_training_run": False,
            "policy_promoted": False, "current_auxiliary_artifacts_eligible": False}


def batch_loss(model, records, config):
    """Full action/ranking/plan loss; caller decides whether gradients are enabled.

This function neither backpropagates nor commits any supervision. The exact same
function is used by the bounded trainer and the separately authorized one-batch
connectivity check.
"""
    from .data_policy_v5 import validate_record
    require(type(model) is StudentV5 and model.config == config and bool(records), "exact_v5_model_and_nonempty_batch_required")
    losses, terms = [], []
    for record in records:
        validate_record(record, config)
        public = record["public_input"]
        output = validate_output(model(public), public, config)
        loss, details = decision_loss(output, record["targets"], public, config)
        require(loss.ndim == 0 and bool(torch.isfinite(loss)), "nonfinite_training_loss")
        losses.append(loss); terms.append(details)
    return torch.stack(losses).mean(), terms


def evaluate(model, dataset):
    require(type(dataset) is PolicyDatasetV5 and dataset.split == "validation"
            and dataset.config == model.config, "full_v5_validation_only")
    dataset.verify_integrity()
    was_training = model.training
    model.eval()
    try:
        with torch.no_grad():
            losses = [float(batch_loss(model, [record], model.config)[0]) for record in dataset.records]
    finally:
        model.train(was_training)
    return {"roots": len(losses), "mean_loss": sum(losses) / len(losses), "probabilities_calibrated": False,
            "student_rollout_performance_measured": False, "test_labels_read": False, "test_evaluated": False}


def _write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def _validate_state_tensors(state, expected):
    require(isinstance(state, dict) and set(state) == set(expected)
            and all(isinstance(state[k], torch.Tensor) and state[k].shape == v.shape and state[k].dtype == v.dtype
                    for k, v in expected.items()), "checkpoint_model_tensor_schema_mismatch")
    state_digest(state)


class PolicyTrainingSessionV5:
    """Own the model, immutable cohort, optimizer, and actual committed batches.

    A failed learning operation poisons this session. Resume an earlier verified
    checkpoint instead of counting, retrying or exporting an uncertain step.
    """
    def __init__(self, datasets, config, training, protection, *, review=None, authorization=None):
        self.config, self.training = deepcopy(config), deepcopy(training)
        validate_training_config(self.training, self.config)
        self.datasets, self.protection = datasets, deepcopy(protection)
        self.review, self.authorization = deepcopy(review), deepcopy(authorization)
        self.frozen = freeze_inputs(datasets, self.config, self.training, self.protection, review=self.review)
        # Model construction is the only initialization, never optimizer creation.
        with torch.random.fork_rng():
            torch.manual_seed(self.training["seed"])
            self.model = StudentV5(deepcopy(self.config))
        self._optimizer, self._progress = None, empty_progress()
        self._order = list(range(len(datasets["train"])))
        random.Random(self.training["seed"]).shuffle(self._order)
        self._cursor, self._failed = 0, False
        self._expected_state = state_digest(self.model.state_dict())
        self._initial_state = self._expected_state

    @property
    def progress(self): return deepcopy(self._progress)

    def verify_integrity(self):
        require(not self._failed, "failed_session_requires_verified_checkpoint_resume")
        require(self.model.config == self.config and all(p.requires_grad for p in self.model.parameters()),
                "model_config_or_parameter_mask_changed")
        require(freeze_inputs(self.datasets, self.config, self.training, self.protection, review=self.review) == self.frozen,
                "frozen_inputs_or_implementation_changed")
        require(state_digest(self.model.state_dict()) == self._expected_state, "model_changed_outside_committed_step")
        validate_training_evidence(self.frozen, self._progress, self.config, final_state_sha256=self._expected_state)
        self._validate_cursor()

    def _validate_cursor(self):
        expected_order = list(range(len(self.datasets["train"])))
        random.Random(self.training["seed"]).shuffle(expected_order)
        require(self._order == expected_order, "deterministic_training_order_changed")
        ids = [self.frozen["splits"]["train"][i]["record_sha256"] for i in self._order[:self._cursor]]
        require([key for batch in self._progress["committed_batches"] for key in batch["records"]] == ids,
                "cursor_does_not_match_committed_records")
        require(self._cursor == len(ids) and (self._cursor == len(self._order)
                or self._cursor % self.training["batch_size"] == 0), "invalid_lifetime_cursor")
        if self._progress["committed_batches"]:
            require(self._progress["committed_batches"][0]["before_state_sha256"] == self._initial_state,
                    "initial_model_chain_mismatch")

    def _new_optimizer(self):
        return torch.optim.AdamW(self.model.parameters(), lr=self.training["learning_rate"],
                                 weight_decay=self.training["weight_decay"])

    def _optimizer_schema(self, saved):
        require(isinstance(saved, dict) and set(saved) == {"state", "param_groups"}
                and isinstance(saved["state"], dict) and isinstance(saved["param_groups"], list)
                and len(saved["param_groups"]) == 1, "optimizer_snapshot_schema_invalid")
        group = saved["param_groups"][0]
        defaults = dict(lr=self.training["learning_rate"], weight_decay=self.training["weight_decay"], betas=(.9, .999),
            eps=1e-8, amsgrad=False, maximize=False, foreach=None, capturable=False, differentiable=False, fused=None)
        parameters = list(self.model.parameters())
        require(isinstance(group, dict) and set(group) == {*defaults, "params"}
                and all(group[k] == v for k, v in defaults.items())
                and group["params"] == list(range(len(parameters)))
                and all(type(k) is int for k in group["params"]), "optimizer_parameters_or_settings_changed")
        steps = []
        for index, value in saved["state"].items():
            require(type(index) is int and 0 <= index < len(parameters), "optimizer_parameter_id_invalid")
            object_keys(value, ("step", "exp_avg", "exp_avg_sq"), "AdamW state")
            parameter = parameters[index]
            require(all(isinstance(value[k], torch.Tensor) and value[k].shape == parameter.shape
                        and value[k].dtype == parameter.dtype for k in ("exp_avg", "exp_avg_sq")),
                    "optimizer_moment_schema_changed")
            require(all(isinstance(v, torch.Tensor) and v.device.type == "cpu" and bool(torch.isfinite(v).all())
                        for v in value.values()), "nonfinite_optimizer_state")
            step = value["step"]
            require(step.numel() == 1 and float(step).is_integer() and 1 <= float(step) <= self._progress["optimizer_steps"],
                    "optimizer_lifetime_step_invalid")
            steps.append(int(step))
        require(bool(steps) and max(steps) == self._progress["optimizer_steps"], "optimizer_missing_committed_step")

    def train_next(self, *, execute=False):
        """Exactly one future minibatch/optimizer step after all external gates."""
        require(execute is True, "explicit_execute_required")
        self.verify_integrity()
        gate = execution_gate(self.frozen, self.authorization)
        require(gate["accepted"], ",".join(gate["reasons"]))
        require(self._cursor < len(self._order) and self._progress["optimizer_steps"] < self.training["max_optimizer_steps"],
                "authorized_lifetime_budget_exhausted")
        if self._optimizer is None: self._optimizer = self._new_optimizer()
        elif self._progress["optimizer_steps"]: self._optimizer_schema(self._optimizer.state_dict())
        indices = self._order[self._cursor:self._cursor + self.training["batch_size"]]
        records = self.datasets["train"].records
        records = [records[index] for index in indices]
        before = self._expected_state
        self._failed = True  # cleared only after the complete commit is recorded
        self.model.train()
        self._optimizer.zero_grad(set_to_none=True)
        loss, terms = batch_loss(self.model, records, self.config)
        loss.backward()
        torch.nn.utils.clip_grad_norm_(self.model.parameters(), self.training["gradient_clip_norm"], error_if_nonfinite=True)
        self._optimizer.step()
        after = state_digest(self.model.state_dict())
        coverage = merge_coverage(supervision_coverage(r, self.config) for r in records)
        self._progress["optimizer_steps"] += 1
        self._progress["action_policy_optimizer_steps"] += int(coverage["action_policy"]["eligible_roots"] > 0)
        self._progress["consumed"] = merge_coverage((self._progress["consumed"], coverage))
        self._progress["committed_batches"].append({"step": self._progress["optimizer_steps"],
            "records": [digest(r) for r in records], "before_state_sha256": before, "after_state_sha256": after})
        self._cursor += len(indices)
        self._expected_state, self._failed = after, False
        self.verify_integrity()
        return {"optimizer_steps": self._progress["optimizer_steps"], "loss": float(loss.detach()), "terms": terms}

    def checkpoint(self):
        self.verify_integrity()
        if self._progress["optimizer_steps"]:
            require(execution_gate(self.frozen, self.authorization)["accepted"], "checkpoint_authorization_invalid")
            self._optimizer_schema(self._optimizer.state_dict())
        return {"format": CHECKPOINT_FORMAT, "config": deepcopy(self.config), "frozen_inputs": deepcopy(self.frozen),
            "authorization": deepcopy(self.authorization), "progress": self.progress, "order": list(self._order),
            "cursor": self._cursor, "initial_state_sha256": self._initial_state,
            "model": deepcopy(self.model.state_dict()), "optimizer": deepcopy(self._optimizer.state_dict()) if self._optimizer else None,
            "torch_rng": torch.get_rng_state(), "formal_training_run": False, "promoted": False}

    def restore(self, path):
        """Validate on a detached session before replacing any live state."""
        self.verify_integrity()
        state = torch.load(path, map_location="cpu", weights_only=True)
        object_keys(state, ("format", "config", "frozen_inputs", "authorization", "progress", "order", "cursor",
            "initial_state_sha256", "model", "optimizer", "torch_rng", "formal_training_run", "promoted"), "full v5 checkpoint")
        require(state["format"] == CHECKPOINT_FORMAT and state["config"] == self.config
                and state["frozen_inputs"] == self.frozen and state["authorization"] == self.authorization
                and state["order"] == self._order and state["initial_state_sha256"] == self._initial_state
                and state["formal_training_run"] is False and state["promoted"] is False, "checkpoint_identity_or_budget_changed")
        _validate_state_tensors(state["model"], self.model.state_dict())
        model_hash = state_digest(state["model"])
        validate_training_evidence(self.frozen, state["progress"], self.config, final_state_sha256=model_hash)
        integer(state["cursor"], "checkpoint_cursor", 0, len(self._order))
        steps = state["progress"]["optimizer_steps"]
        require(not steps or execution_gate(self.frozen, self.authorization)["accepted"], "resume_authorization_invalid")
        require(steps > 0 or state["optimizer"] is None and model_hash == self._initial_state,
                "zero_step_checkpoint_has_learned_state")
        rng = state["torch_rng"]
        require(isinstance(rng, torch.Tensor) and rng.dtype == torch.uint8 and rng.device.type == "cpu"
                and rng.shape == torch.get_rng_state().shape, "invalid_checkpoint_rng")
        probe = PolicyTrainingSessionV5(self.datasets, self.config, self.training, self.protection,
                                       review=self.review, authorization=self.authorization)
        probe.model.load_state_dict(state["model"], strict=True)
        probe._progress, probe._cursor, probe._expected_state = deepcopy(state["progress"]), state["cursor"], model_hash
        probe.verify_integrity()
        if steps:
            probe._optimizer_schema(state["optimizer"])
            probe._optimizer = probe._new_optimizer()
            probe._optimizer.load_state_dict(state["optimizer"])
            probe._optimizer_schema(probe._optimizer.state_dict())
        with torch.random.fork_rng(): torch.set_rng_state(rng)
        self.model, self._optimizer = probe.model, probe._optimizer
        self._progress, self._cursor, self._expected_state = probe._progress, probe._cursor, probe._expected_state
        torch.set_rng_state(rng)
        return self.progress

    def _bundle_manifest(self, weights_sha256):
        steps = self._progress["optimizer_steps"]
        return {"format": BUNDLE_FORMAT, "policy_version": POLICY_VERSION, "public_schema": PUBLIC_SCHEMA,
            "model_version": MODEL_VERSION, "config_sha256": digest(self.config),
            "weights_sha256": weights_sha256,
            "state_sha256": self._expected_state, "implementation": inference_fingerprint(),
            "frozen_inputs": deepcopy(self.frozen), "supervision_progress": self.progress,
            "supervision_sha256": digest({"frozen_inputs": self.frozen, "progress": self._progress}),
            "authorization": deepcopy(self.authorization), "status": "EXPERIMENTAL_UNPROMOTED", "trained": steps > 0,
            "optimizer_steps": steps, "calibrated": False, "promoted": False, "formal_training_run": False,
            "safe_learned_plan_execution_verified": False, "test_labels_read": False, "applicability_profile": APPLICABILITY_PROFILE}

    def export_bundle(self, directory, *, reuse_existing=False):
        """Derive learned status only from this session's actual committed ledger."""
        self.verify_integrity()
        steps = self._progress["optimizer_steps"]
        require(not steps or execution_gate(self.frozen, self.authorization)["accepted"], "export_authorization_invalid")
        if steps: self._optimizer_schema(self._optimizer.state_dict())
        directory = Path(directory)
        if directory.exists():
            require(reuse_existing is True, "new_bundle_directory_required")
            from .inference_policy_v5 import InferencePolicyV5
            existing = InferencePolicyV5.from_bundle(directory)
            require(existing.config == self.config and existing.manifest == self._bundle_manifest(existing.manifest["weights_sha256"]),
                    "existing_bundle_does_not_match_current_committed_session")
            return existing.manifest
        directory.mkdir(parents=True)
        _write_json(directory / "config.json", self.config)
        atomic_save(self.model.state_dict(), directory / "weights.pt")
        manifest = self._bundle_manifest(hashlib.sha256((directory / "weights.pt").read_bytes()).hexdigest())
        validate_manifest(manifest, self.config, self.model.state_dict())
        _write_json(directory / "manifest.json", manifest)
        return manifest


def train_bounded(session, directory, *, execute=False, resume=False, stop_after_steps=None):
    """One deterministic epoch, lifetime budget and exact resume; never auto-run."""
    require(type(session) is PolicyTrainingSessionV5 and execute is True, "explicit_full_v5_training_session_required")
    session.verify_integrity()
    gate = execution_gate(session.frozen, session.authorization)
    require(gate["accepted"], ",".join(gate["reasons"]))
    if stop_after_steps is not None: integer(stop_after_steps, "stop_after_steps", 0, session.training["max_optimizer_steps"])
    directory = Path(directory)
    if resume:
        session.restore(directory / "checkpoint.pt")
    else:
        require(not directory.exists(), "new_training_directory_required")
        directory.mkdir(parents=True)
    initial = session.progress["optimizer_steps"]
    atomic_save(session.checkpoint(), directory / "checkpoint.pt")
    while session._cursor < len(session._order) and session.progress["optimizer_steps"] < session.training["max_optimizer_steps"]:
        if stop_after_steps is not None and session.progress["optimizer_steps"] - initial >= stop_after_steps: break
        session.train_next(execute=True)
        atomic_save(session.checkpoint(), directory / "checkpoint.pt")
    report = {"status": "BOUNDED_OBJECTIVE_PILOT_STOPPED", "optimizer_steps": session.progress["optimizer_steps"],
        "consumed_roots": session._cursor, "epochs_completed": int(session._cursor == len(session._order)),
        "formal_training_run": False, "promoted": False, "validation": evaluate(session.model, session.datasets["validation"])}
    _write_json(directory / "report.json", report)
    session.export_bundle(directory / ("bundle-step-" + str(session.progress["optimizer_steps"])),
                          reuse_existing=resume and session.progress["optimizer_steps"] == initial)
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--student-config", required=True)
    parser.add_argument("--training-config", required=True)
    parser.add_argument("--prepared", help="new directory with train.jsonl, validation.jsonl and protection.json")
    parser.add_argument("--review"); parser.add_argument("--authorization"); parser.add_argument("--output")
    input_kind = parser.add_mutually_exclusive_group()
    input_kind.add_argument("--engineering-fixture", action="store_true")
    input_kind.add_argument("--native-objective-candidate", action="store_true", help="readiness/forward only; never admitted for fitting")
    parser.add_argument("--forward-check", action="store_true")
    parser.add_argument("--execute-bounded-policy-pilot", action="store_true")
    parser.add_argument("--resume", action="store_true"); parser.add_argument("--stop-after-steps", type=int)
    args = parser.parse_args(argv)
    try:
        config = load_config(args.student_config)
        training = validate_training_config(loads(Path(args.training_config).read_text()), config)
        seed_everything(training["seed"], training["torch_threads"])
        require(not args.engineering_fixture or not args.execute_bounded_policy_pilot, "engineering_fixtures_cannot_fit")
        require(not args.native_objective_candidate or not args.execute_bounded_policy_pilot, "unadmitted_native_candidates_cannot_fit")
        require(not args.resume or args.execute_bounded_policy_pilot, "resume_requires_execute")
        require(args.stop_after_steps is None or args.execute_bounded_policy_pilot, "step_limit_requires_execute")
        session = None
        if args.prepared:
            path = Path(args.prepared)
            purpose = ("engineering-fixture" if args.engineering_fixture else
                       "native-objective-candidate" if args.native_objective_candidate else "bounded-objective-pilot")
            receipts_path = None if args.engineering_fixture else path / "producer-receipts.json"
            datasets = {split: PolicyDatasetV5.from_jsonl(path / (split + ".jsonl"), config, split=split, purpose=purpose,
                        producer_receipts_path=receipts_path)
                        for split in ("train", "validation")}
            protection = loads((path / "protection.json").read_text())
            review = loads(Path(args.review).read_text()) if args.review else None
            authorization = loads(Path(args.authorization).read_text()) if args.authorization else None
            session = PolicyTrainingSessionV5(datasets, config, training, protection, review=review, authorization=authorization)
        if args.execute_bounded_policy_pilot:
            require(session is not None and args.output is not None, "prepared_corpus_and_output_required")
            result = train_bounded(session, args.output, execute=True, resume=args.resume, stop_after_steps=args.stop_after_steps)
        else:
            result = {**execution_gate(session.frozen if session else None, session.authorization if session else None),
                "mode": "readiness", "optimizer_steps": 0, "backward_calls": 0, "weights_written": False,
                "frozen_inputs": session.frozen if session else None}
            if args.forward_check:
                require(session is not None, "forward_check_requires_prepared_corpus")
                result["validation"] = evaluate(session.model, session.datasets["validation"])
        print(json.dumps(result, indent=2, allow_nan=False))
        return 0
    except (SchemaError, ValueError, OSError, TypeError, KeyError, RecursionError, OverflowError) as error:
        print(json.dumps({"status": "FIT_BLOCKED", "reason": str(error), "formal_training_run": False, "promoted": False}))
        return 2


if __name__ == "__main__": raise SystemExit(main())
