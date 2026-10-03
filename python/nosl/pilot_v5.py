"""Bounded auxiliary-only v5 pilot interface; the default CLI never fits.

An admitted dataset is not execution permission. Real fitting requires both a
reviewed bounded-pilot snapshot and an independently supplied exact-bound
authorization, plus the explicit execute flag. No policy export or promotion.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path
import random

import torch

from .data import canonical_object_digest
from .inference_v5 import V5_INFERENCE_SOURCES
from .loss_v5 import decision_loss
from .model_v5 import StudentV5
from .native_v5 import AUXILIARY_HEADS, validate_candidate
from .prepare_v5 import PreparedDatasetV5, SOURCE_FILES, verify_snapshot
from .public_identity_v5 import PUBLIC_IDENTITY_SCHEME
from .reproducibility import implementation_fingerprint as runtime_fingerprint, source_hashes
from .schema import SchemaError, integer, object_keys
from .schema_v5 import MODEL_VERSION, PUBLIC_SCHEMA, load_config, loads, validate_config
from .train import atomic_save, seed_everything

CONFIG_FORMAT = "nosl.student.bounded-pilot.config.v5"
INPUTS_FORMAT = "nosl.student.bounded-pilot.inputs.v5"
AUTHORIZATION_FORMAT = "nosl.student.bounded-pilot.authorization.v5"
CHECKPOINT_FORMAT = "nosl.experimental.auxiliary-checkpoint.v5"
MAX_EPOCHS = 1
MAX_STEPS = 700
ROOT = Path(__file__).resolve().parents[2]
TRAINING_SOURCES = tuple(dict.fromkeys((*V5_INFERENCE_SOURCES, *SOURCE_FILES,
    "train.py", "loss_v5.py", "pilot_v5.py", "reproducibility.py", "vocabulary.py")))


def require(condition, reason):
    if not condition:
        raise SchemaError("pilot_v5:" + reason)


def validate_pilot_config(config, student):
    validate_config(student)
    object_keys(config, ("format", "experiment_id", "public_schema", "model_version", "student_config_sha256",
        "seed", "torch_threads", "batch_size", "learning_rate", "weight_decay", "gradient_clip_norm",
        "max_epochs", "max_optimizer_steps", "max_training_roots", "max_validation_roots",
        "auxiliary_heads", "formal_training", "policy_promotion"), "v5 pilot config")
    require(config["format"] == CONFIG_FORMAT and config["public_schema"] == PUBLIC_SCHEMA
            and config["model_version"] == MODEL_VERSION, "config_schema_or_model_mismatch")
    require(config["student_config_sha256"] == canonical_object_digest(student), "student_config_mismatch")
    require(isinstance(config["experiment_id"], str) and bool(config["experiment_id"].strip()), "experiment_id_missing")
    for name, minimum, maximum in (("seed", 0, 2**32 - 1), ("torch_threads", 1, 8), ("batch_size", 1, 64),
            ("max_epochs", 1, MAX_EPOCHS), ("max_optimizer_steps", 1, MAX_STEPS),
            ("max_training_roots", 1, 5000), ("max_validation_roots", 1, 5000)):
        integer(config[name], name, minimum, maximum)
    for name, minimum, maximum in (("learning_rate", 0, .01), ("weight_decay", 0, 1), ("gradient_clip_norm", 0, 10)):
        value = config[name]
        require(type(value) in (float, int) and math.isfinite(value)
                and minimum <= value <= maximum and (name == "weight_decay" or value > 0), "invalid_" + name)
    require(config["auxiliary_heads"] == list(AUXILIARY_HEADS)
            and config["formal_training"] is False and config["policy_promotion"] is False,
            "only_unpromoted_auxiliary_pilot_supported")
    weights = student["base_config"]["base_config"]["base_config"]["base_config"]["loss_weights"]
    require(all(weights[h] > 0 for h in AUXILIARY_HEADS), "auxiliary_losses_must_be_enabled")
    return config


def implementation_fingerprint():
    value = runtime_fingerprint()
    value.update(format="nosl.student.bounded-pilot.implementation.v5", source_sha256=source_hashes(TRAINING_SOURCES))
    for name in ("tools/train_pilot_v5.py", "tools/prepare_dataset_v5.py", "tools/native_protection_v5.py", "tools/adapt_native_v5.py"):
        value["source_sha256"][name] = hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
    return value


def freeze_inputs(datasets, student, pilot):
    """Only train/validation are decoded; legacy protection is metadata only.

    PreparedDatasetV5 verifies its own snapshot's other shards as opaque bytes.
    Neither that reader nor this function opens historical frozen test paths.
    """
    validate_pilot_config(pilot, student)
    require(isinstance(datasets, dict) and set(datasets) == {"train", "validation"}, "train_validation_only")
    for split, data in datasets.items():
        require(type(data) is PreparedDatasetV5 and data.split == split and data.config == student,
                "matching_PreparedDatasetV5_required")
        data.verify_integrity()
        maximum = pilot["max_training_roots" if split == "train" else "max_validation_roots"]
        require(0 < len(data) <= maximum, "split_root_budget_exceeded:" + split)
        # Repeat the auxiliary contract after integrity checking the live list.
        for record in data.records:
            validate_candidate(record, student)
            require(any(row["masks"][h] and row.get("sample_weight", 1) > 0
                        for row in record["targets"]["actions"] for h in AUXILIARY_HEADS), "unsupervised_root")
    train, validation = datasets["train"], datasets["validation"]
    require(train.root.resolve() == validation.root.resolve() and train.manifest == validation.manifest
            and train.manifest_sha256 == validation.manifest_sha256 and train.purpose == validation.purpose,
            "splits_must_share_snapshot")
    manifest, paths, _, digest = verify_snapshot(train.root, student, expected_purpose=train.purpose)
    require(manifest == train.manifest and digest == train.manifest_sha256, "snapshot_changed_after_loading")
    receipt = loads(paths["admission"].read_text(encoding="utf-8"))
    return {"format": INPUTS_FORMAT, "public_schema": PUBLIC_SCHEMA, "model_version": MODEL_VERSION,
        "public_identity_scheme": PUBLIC_IDENTITY_SCHEME, "student_config_sha256": canonical_object_digest(student),
        "pilot_config_sha256": canonical_object_digest(pilot), "purpose": train.purpose,
        "manifest_sha256": digest, "admission_sha256": manifest["admission_sha256"],
        "protection_sha256": manifest["protection_sha256"], "review": deepcopy(receipt["review"]),
        "versions": deepcopy(manifest["versions"]), "implementation": implementation_fingerprint(),
        "splits": {s: {"roots": len(d), "shard_sha256": manifest["files"][s]["sha256"],
                         "records_sha256": d.records_sha256} for s, d in datasets.items()},
        "synthetic_runtime": any("synthetic" in r["audit_only"]["runtime_dependencies"]["runtime_version"].lower()
                                 for d in datasets.values() for r in d.records),
        "policy_supervision_roots": 0, "test_labels_read": False, "test_evaluated": False,
        "test_used_for_model_selection": False}


def execution_gate(frozen, pilot, authorization=None):
    """Integrity checks are not signatures or proof of a human review.

    The authorization must come from the separately approved operator workflow;
    this module never generates it, changes a receipt, or accepts diagnostic data.
    """
    reasons = []
    if frozen is None:
        reasons.append("reviewed_PreparedDatasetV5_required")
    else:
        if frozen.get("format") != INPUTS_FORMAT or frozen.get("purpose") != "bounded-pilot":
            reasons.append("reviewed_bounded_pilot_cohort_required")
        if frozen.get("synthetic_runtime") is not False:
            reasons.append("real_native_cohort_required")
        review = frozen.get("review", {})
        if review.get("decision") != "accepted" or review.get("evidence_kind") != "predeclared_fresh_cohort_quality":
            reasons.append("reviewed_real_cohort_receipt_required")
        if any(frozen.get(k) is not False for k in ("test_labels_read", "test_evaluated", "test_used_for_model_selection")):
            reasons.append("test_targets_must_remain_unread")
    expected = {"format", "authorized", "purpose", "authorization_id", "reviewed_by", "approval_kind",
                "frozen_inputs_sha256", "admission_sha256", "protection_sha256", "review_evidence_sha256",
                "pilot_config_sha256", "max_epochs", "max_optimizer_steps"}
    if not isinstance(authorization, dict) or set(authorization) != expected:
        reasons.append("separate_exact_bound_authorization_required")
    elif frozen is not None:
        match = (authorization["format"] == AUTHORIZATION_FORMAT and authorization["authorized"] is True
            and authorization["purpose"] == "bounded-pilot" and authorization["approval_kind"] == "reviewed_real_cohort"
            and all(isinstance(authorization[k], str) and authorization[k].strip() for k in ("authorization_id", "reviewed_by"))
            and authorization["frozen_inputs_sha256"] == canonical_object_digest(frozen)
            and authorization["admission_sha256"] == frozen["admission_sha256"]
            and authorization["protection_sha256"] == frozen["protection_sha256"]
            and authorization["review_evidence_sha256"] == frozen["review"]["evidence_sha256"]
            and authorization["pilot_config_sha256"] == canonical_object_digest(pilot)
            and all(type(authorization[k]) is int and authorization[k] == pilot[k]
                    for k in ("max_epochs", "max_optimizer_steps")))
        if not match:
            reasons.append("authorization_binding_mismatch")
    return {"status": "AUTHORIZED_BOUNDED_AUXILIARY_PILOT" if not reasons else "FIT_BLOCKED",
        "accepted": not reasons, "reasons": reasons,
        "frozen_inputs_sha256": canonical_object_digest(frozen) if frozen is not None else None,
        "formal_training_run": False, "policy_ready": False, "promoted": False}


def new_model_optimizer(student, pilot):
    validate_pilot_config(pilot, student)
    seed_everything(pilot["seed"], pilot["torch_threads"])
    model = StudentV5(deepcopy(student))
    optimizer = torch.optim.AdamW(_auxiliary_parameters(model), lr=pilot["learning_rate"], weight_decay=pilot["weight_decay"])
    return model, optimizer


def _auxiliary_parameters(model):
    # A zero loss gradient would still let AdamW decay an unavailable head.
    # Exclude utility and finite-plan head parameters altogether. Shared encoder
    # updates can change their outputs, so those outputs still cannot be used.
    for name, parameter in model.named_parameters():
        parameter.requires_grad_(".heads.value." not in "." + name and ".plan_heads." not in "." + name)
    return [p for p in model.parameters() if p.requires_grad]


def _order(frozen, pilot):
    indices = list(range(frozen["splits"]["train"]["roots"]))
    random.Random(pilot["seed"]).shuffle(indices)
    return indices


def _validate_binding(model, optimizer, frozen, pilot):
    require(type(model) is StudentV5 and type(optimizer) is torch.optim.AdamW, "v5_model_and_AdamW_required")
    validate_pilot_config(pilot, model.config)
    require(frozen.get("format") == INPUTS_FORMAT and frozen.get("public_schema") == PUBLIC_SCHEMA
            and frozen.get("model_version") == MODEL_VERSION
            and frozen.get("student_config_sha256") == canonical_object_digest(model.config)
            and frozen.get("pilot_config_sha256") == canonical_object_digest(pilot), "frozen_schema_or_config_mismatch")
    require(frozen.get("implementation") == implementation_fingerprint(), "implementation_or_runtime_changed")
    require(all(p.device.type == "cpu" for p in model.parameters()), "CPU_only")
    for name, parameter in model.named_parameters():
        enabled = ".heads.value." not in "." + name and ".plan_heads." not in "." + name
        require(parameter.requires_grad is enabled, "auxiliary_parameter_mask_changed")
    require(len(optimizer.param_groups) == 1
            and optimizer.param_groups[0]["params"] == [p for p in model.parameters() if p.requires_grad],
            "optimizer_parameters_changed")
    defaults = dict(lr=pilot["learning_rate"], weight_decay=pilot["weight_decay"], betas=(.9, .999), eps=1e-8,
                    amsgrad=False, maximize=False, foreach=None, capturable=False, differentiable=False, fused=None)
    require(all(optimizer.param_groups[0].get(k) == v for k, v in defaults.items()), "optimizer_settings_changed")


def _validate_progress(state, frozen, pilot):
    roots, batch = frozen["splits"]["train"]["roots"], pilot["batch_size"]
    steps = integer(state.get("optimizer_steps"), "optimizer_steps", 0, pilot["max_optimizer_steps"])
    cursor = integer(state.get("cursor"), "cursor", 0, roots)
    epoch = integer(state.get("epochs_completed"), "epochs_completed", 0, pilot["max_epochs"])
    require((cursor == roots or cursor % batch == 0) and steps == math.ceil(cursor / batch)
            and epoch == int(cursor == roots) and state.get("order") == _order(frozen, pilot), "invalid_lifetime_progress")
    require(state.get("auxiliary_trained") is (steps > 0) and state.get("policy_ready") is False
            and state.get("promoted") is False and state.get("formal_training_run") is False,
            "invalid_training_or_promotion_claim")
    optimizer = state.get("optimizer")
    require(isinstance(optimizer, dict) and isinstance(optimizer.get("state"), dict), "optimizer_state_missing")
    if steps == 0:
        require(not optimizer["state"], "zero_step_optimizer_must_be_empty")
    else:
        require(execution_gate(frozen, pilot, state.get("authorization"))["accepted"], "learned_checkpoint_requires_authorization")
        adam_steps = []
        for item in optimizer["state"].values():
            value = item.get("step") if isinstance(item, dict) else None
            require(isinstance(value, torch.Tensor) and value.numel() == 1 and torch.isfinite(value).all()
                    and float(value).is_integer() and 1 <= float(value) <= steps, "optimizer_lifetime_steps_mismatch")
            adam_steps.append(float(value))
        require(bool(adam_steps) and max(adam_steps) == steps, "learned_optimizer_state_missing")
    for values in (state.get("model", {}), optimizer["state"]):
        def finite(value):
            if isinstance(value, torch.Tensor):
                return value.device.type == "cpu" and bool(torch.isfinite(value).all())
            if isinstance(value, dict): return all(finite(v) for v in value.values())
            return True
        require(finite(values), "checkpoint_contains_nonfinite_state")


def _validate_optimizer_snapshot(saved, optimizer):
    """Check serialized IDs and moments before PyTorch remaps or casts them."""
    require(isinstance(saved, dict) and set(saved) == {"state", "param_groups"}
            and isinstance(saved["state"], dict) and isinstance(saved["param_groups"], list),
            "optimizer_state_schema_changed")
    expected = optimizer.state_dict()["param_groups"]
    require(len(saved["param_groups"]) == len(expected), "optimizer_parameter_ids_changed")
    parameters = {}
    for group, expected_group, live_group in zip(saved["param_groups"], expected, optimizer.param_groups):
        require(isinstance(group, dict) and set(group) == set(expected_group), "optimizer_group_schema_changed")
        ids = group["params"]
        require(isinstance(ids, list) and all(type(value) is int for value in ids)
                and ids == expected_group["params"], "optimizer_parameter_ids_changed")
        require({k: v for k, v in group.items() if k != "params"}
                == {k: v for k, v in expected_group.items() if k != "params"}, "optimizer_settings_changed")
        parameters.update(zip(ids, live_group["params"]))
    for parameter_id, item in saved["state"].items():
        require(type(parameter_id) is int and parameter_id in parameters and isinstance(item, dict)
                and set(item) == {"step", "exp_avg", "exp_avg_sq"}, "optimizer_state_schema_changed")
        parameter = parameters[parameter_id]
        require(all(isinstance(item[k], torch.Tensor) and item[k].shape == parameter.shape
                    and item[k].dtype == parameter.dtype for k in ("exp_avg", "exp_avg_sq")),
                "optimizer_moment_schema_changed")
        require(isinstance(item["step"], torch.Tensor) and item["step"].numel() == 1,
                "optimizer_step_schema_changed")
        require(all(value.device.type == "cpu" and bool(torch.isfinite(value).all()) for value in item.values()),
                "optimizer_contains_nonfinite_state")


def checkpoint_state(model, optimizer, frozen, pilot, *, cursor=0, optimizer_steps=0, authorization=None):
    """Zero-step serialization is permitted for engineering fixtures; no fitting."""
    _validate_binding(model, optimizer, frozen, pilot)
    state = {"format": CHECKPOINT_FORMAT, "public_schema": PUBLIC_SCHEMA, "model_version": MODEL_VERSION,
        "student_config": deepcopy(model.config), "pilot_config": deepcopy(pilot), "frozen": deepcopy(frozen),
        "authorization": deepcopy(authorization), "model": deepcopy(model.state_dict()),
        "optimizer": deepcopy(optimizer.state_dict()), "cursor": cursor, "order": _order(frozen, pilot),
        "epochs_completed": int(cursor == frozen["splits"]["train"]["roots"]), "optimizer_steps": optimizer_steps,
        "torch_rng": torch.get_rng_state(), "auxiliary_trained": optimizer_steps > 0,
        "policy_ready": False, "formal_training_run": False, "promoted": False}
    _validate_progress(state, frozen, pilot)
    return state


def restore_checkpoint(path, model, optimizer, frozen, pilot, *, authorization=None):
    """Exact source/config/data/receipt/budget locks across a checkpoint chain."""
    _validate_binding(model, optimizer, frozen, pilot)
    state = torch.load(path, map_location="cpu", weights_only=True)
    require(isinstance(state, dict) and state.get("format") == CHECKPOINT_FORMAT
            and state.get("model_version") == MODEL_VERSION and state.get("public_schema") == PUBLIC_SCHEMA,
            "checkpoint_schema_or_model_mismatch")
    require(state.get("frozen") == frozen and state.get("pilot_config") == pilot
            and state.get("student_config") == model.config and state.get("authorization") == authorization,
            "resume_identity_or_lifetime_budget_changed")
    _validate_optimizer_snapshot(state.get("optimizer"), optimizer)
    _validate_progress(state, frozen, pilot)
    expected_model = model.state_dict()
    require(isinstance(state.get("model"), dict) and set(state["model"]) == set(expected_model)
            and all(isinstance(state["model"][k], torch.Tensor)
                    and state["model"][k].shape == v.shape and state["model"][k].dtype == v.dtype
                    for k, v in expected_model.items()), "checkpoint_tensor_schema_changed")
    # Validate using disposable instances before touching the caller's state.
    with torch.random.fork_rng():
        probe = StudentV5(deepcopy(model.config))
        probe.load_state_dict(state["model"], strict=True)
        probe_optimizer = torch.optim.AdamW(_auxiliary_parameters(probe), lr=pilot["learning_rate"], weight_decay=pilot["weight_decay"])
        probe_optimizer.load_state_dict(state["optimizer"])
        _validate_binding(probe, probe_optimizer, frozen, pilot)
        # load_state_dict casts moments and rewrites serialized parameter IDs.
        # Check its resulting tensors before touching the caller's live state.
        _validate_optimizer_snapshot(probe_optimizer.state_dict(), optimizer)
        torch.set_rng_state(state["torch_rng"])
    model.load_state_dict(state["model"], strict=True)
    optimizer.load_state_dict(state["optimizer"])
    torch.set_rng_state(state["torch_rng"])
    return state


def evaluate_auxiliary(model, dataset, *, include_predictions=False):
    """Validation-only no-grad diagnostics, never selected actions or rankings."""
    require(type(dataset) is PreparedDatasetV5 and dataset.split == "validation"
            and type(model) is StudentV5 and model.config == dataset.config, "prepared_v5_validation_required")
    dataset.verify_integrity()
    model.eval()
    losses, predictions = [], []
    counts = {h: 0 for h in AUXILIARY_HEADS}
    with torch.no_grad():
        for record in dataset.records:
            output = model(record["public_input"])
            loss, _ = decision_loss(output, record["targets"], record["public_input"], model.config)
            require(bool(torch.isfinite(loss)), "nonfinite_validation_loss")
            losses.append(float(loss))
            for row in record["targets"]["actions"]:
                values = {}
                for head in AUXILIARY_HEADS:
                    available = row["masks"][head] and row.get("sample_weight", 1) > 0
                    counts[head] += int(available)
                    value = output[head][row["action_index"]]
                    if head in ("win_probability", "death_probability"): value = value.sigmoid()
                    elif head == "hp_distribution": value = value.softmax(-1)
                    elif head == "expected_final_hp": value = value * 100
                    require(bool(torch.isfinite(value).all()), "nonfinite_auxiliary_prediction")
                    values[head] = value.tolist() if available else None
                if include_predictions:
                    predictions.append({"public_input_digest": record["audit_only"]["conditioned_public_input_digest"],
                                        "action_index": row["action_index"], "auxiliary": values})
    return {"status": "EXPERIMENTAL_AUXILIARY_ONLY", "roots": len(losses), "mean_loss": sum(losses) / len(losses),
        "available_action_targets": counts, "selected_action": None, "policy_ready": False,
        "prediction_scope": "teacher_continuation_under_declared_prior", "predictions": predictions,
        "test_labels_read": False, "test_evaluated": False}


def pilot_train(datasets, student, pilot, output, *, authorization=None, execute=False, resume=False,
                stop_after_steps=None):
    """Future explicitly invoked fitting; this is not called by engineering tests.

    A single deterministic pass and cumulative cursor enforce <=1 epoch and
    <=700 optimizer steps across resumes. No test-based selection or export.
    """
    require(execute is True, "explicit_bounded_pilot_invocation_required")
    validate_pilot_config(pilot, student)
    seed_everything(pilot["seed"], pilot["torch_threads"])
    frozen = freeze_inputs(datasets, student, pilot)
    gate = execution_gate(frozen, pilot, authorization)
    require(gate["accepted"], ",".join(gate["reasons"]))
    if stop_after_steps is not None:
        integer(stop_after_steps, "stop_after_steps", 0, pilot["max_optimizer_steps"])
    model, optimizer = new_model_optimizer(student, pilot)
    output = Path(output)
    cursor = steps = 0
    if resume:
        state = restore_checkpoint(output / "checkpoint.pt", model, optimizer, frozen, pilot, authorization=authorization)
        cursor, steps = state["cursor"], state["optimizer_steps"]
    else:
        output.mkdir(parents=True, exist_ok=False)
    initial_steps = steps
    order = _order(frozen, pilot)
    atomic_save(checkpoint_state(model, optimizer, frozen, pilot, cursor=cursor, optimizer_steps=steps,
                                authorization=authorization), output / "checkpoint.pt")
    while cursor < len(order) and steps < pilot["max_optimizer_steps"]:
        if stop_after_steps is not None and steps - initial_steps >= stop_after_steps:
            break
        require(freeze_inputs(datasets, student, pilot) == frozen, "inputs_changed_before_optimizer_step")
        indices = order[cursor:cursor + pilot["batch_size"]]
        model.train()
        optimizer.zero_grad(set_to_none=True)
        for index in indices:
            record = datasets["train"][index]
            loss, _ = decision_loss(model(record["public_input"]), record["targets"], record["public_input"], student)
            require(bool(torch.isfinite(loss)), "nonfinite_training_loss")
            (loss / len(indices)).backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), pilot["gradient_clip_norm"], error_if_nonfinite=True)
        optimizer.step()
        cursor += len(indices)
        steps += 1
        atomic_save(checkpoint_state(model, optimizer, frozen, pilot, cursor=cursor, optimizer_steps=steps,
                                    authorization=authorization), output / "checkpoint.pt")
    result = {"status": "BOUNDED_AUXILIARY_PILOT_STOPPED", "optimizer_steps": steps,
        "epochs_completed": int(cursor == len(order)), "consumed_roots": cursor,
        "budget_exhausted": cursor == len(order) or steps == pilot["max_optimizer_steps"],
        "validation": evaluate_auxiliary(model, datasets["validation"]),
        "formal_training_run": False, "policy_ready": False, "promoted": False}
    (output / "report.json").write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--student-config", default=str(ROOT / "configs/student.v5.engineering.json"))
    parser.add_argument("--pilot-config", default=str(ROOT / "configs/student.v5.bounded-pilot.json"))
    parser.add_argument("--prepared")
    parser.add_argument("--authorization")
    parser.add_argument("--output")
    parser.add_argument("--execute-bounded-pilot", action="store_true")
    parser.add_argument("--engineering-fixture", action="store_true", help="dry-run fixture loading only")
    parser.add_argument("--forward-check", action="store_true", help="no-grad validation diagnostics in dry-run")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--stop-after-steps", type=int)
    args = parser.parse_args(argv)
    try:
        student = load_config(args.student_config)
        pilot = validate_pilot_config(loads(Path(args.pilot_config).read_text(encoding="utf-8")), student)
        seed_everything(pilot["seed"], pilot["torch_threads"])
        require(not args.engineering_fixture or not args.execute_bounded_pilot, "engineering_fixture_cannot_execute")
        require(not args.resume or args.execute_bounded_pilot, "resume_requires_explicit_execute")
        require(args.stop_after_steps is None or args.execute_bounded_pilot, "step_limit_requires_explicit_execute")
        datasets = None
        if args.prepared:
            purpose = "engineering-fixture" if args.engineering_fixture else "bounded-pilot"
            datasets = {s: PreparedDatasetV5(args.prepared, s, student, purpose=purpose) for s in ("train", "validation")}
        authorization = loads(Path(args.authorization).read_text(encoding="utf-8")) if args.authorization else None
        if args.execute_bounded_pilot:
            require(datasets is not None and args.output is not None, "prepared_dataset_and_output_required")
            result = pilot_train(datasets, student, pilot, args.output, authorization=authorization, execute=True,
                                 resume=args.resume, stop_after_steps=args.stop_after_steps)
        else:
            frozen = freeze_inputs(datasets, student, pilot) if datasets is not None else None
            result = {**execution_gate(frozen, pilot, authorization), "mode": "dry-run", "optimizer_steps": 0,
                      "backward_calls": 0, "weights_written": False, "frozen_inputs": frozen}
            if args.forward_check:
                require(datasets is not None, "forward_check_requires_prepared_dataset")
                model, _ = new_model_optimizer(student, pilot)
                result["validation"] = evaluate_auxiliary(model, datasets["validation"], include_predictions=True)
        print(json.dumps(result, indent=2, allow_nan=False))
        return 0
    except (SchemaError, ValueError, OSError, KeyError, TypeError) as error:
        print(json.dumps({"status": "FIT_BLOCKED", "reason": str(error), "formal_training_run": False,
                          "policy_ready": False, "promoted": False}, allow_nan=False))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
