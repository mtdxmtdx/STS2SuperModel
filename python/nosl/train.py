"""Reproducible training engineering. M6 smoke never saves or promotes weights."""
from __future__ import annotations

import argparse
import json
import hashlib
import os
import resource
import time
import random
from pathlib import Path

import torch
from torch.nn import functional as F

from .data import DecisionDataset, validate_targets, prepared_dataset, canonical_object_digest
from .public_identity import PUBLIC_IDENTITY_SCHEME, public_input_digest
from .model import Student
from .reproducibility import implementation_fingerprint
from .schema import HEADS, SchemaError, load_config, integer


def seed_everything(seed: int, threads: int = 1) -> None:
    random.seed(seed)
    torch.manual_seed(seed)
    torch.use_deterministic_algorithms(True)
    torch.set_num_threads(threads)


def distribution_target(atoms: list[dict], config: dict, device) -> torch.Tensor:
    """Linear bin interpolation preserves the mean, including exact endpoints."""
    target = torch.zeros(config["hp_bins"], device=device)
    for atom in atoms:
        location = atom["hp"] * (config["hp_bins"] - 1) / config["hp_max"]
        lo = int(location)
        hi = min(lo + 1, config["hp_bins"] - 1)
        fraction = location - lo
        target[lo] += atom["probability"] * (1 - fraction)
        target[hi] += atom["probability"] * fraction
    return target


def decision_loss(output: dict, targets: dict, public: dict, config: dict):
    validate_targets(targets, public, config)
    # Keep a differentiable zero without touching -inf illegal-action ranking scores.
    zero = output["value"].sum() * 0
    terms = {}
    for head in HEADS:
        losses = []
        for row in targets["actions"]:
            if not row["masks"][head]:
                continue
            prediction = output[head][row["action_index"]]
            if head == "hp_distribution":
                target = distribution_target(row[head], config, prediction.device)
                losses.append(-(target * F.log_softmax(prediction, dim=-1)).sum() * row.get("sample_weight", 1.0))
            else:
                target = prediction.new_tensor(row[head])
                if head in ("win_probability", "death_probability"):
                    # Proper scoring rule; uncertainty is a probability, not a hard winner.
                    losses.append(F.binary_cross_entropy_with_logits(prediction, target) * row.get("sample_weight", 1.0))
                elif head in ("expected_final_hp", "value"):
                    # Squared error elicits the conditional arithmetic mean.
                    # Huber loss would downweight rare, large utility losses.
                    losses.append(F.mse_loss(prediction, target / 100) * row.get("sample_weight", 1.0))
                else:
                    losses.append(F.mse_loss(prediction, target) * row.get("sample_weight", 1.0))
        terms[head] = torch.stack(losses).mean() if losses else zero
    pairs = [F.softplus(-(output["value"][pair["preferred"]] - output["value"][pair["other"]])) * pair["weight"]
             for pair in targets["pairwise"] if pair["weight"] > 0]
    terms["pairwise"] = torch.stack(pairs).mean() if pairs else zero
    equivalent = targets["equivalent_action_set"]
    if equivalent:
        # Set-valued supervision maximizes total set probability; it does not force
        # arbitrary ordering within an equivalence set.
        terms["equivalent"] = (torch.logsumexp(output["ranking_score"], 0)
                               - torch.logsumexp(output["value"][equivalent], 0))
    else:
        terms["equivalent"] = zero
    total = sum(config["loss_weights"][name] * term for name, term in terms.items())
    return total, {key: float(value.detach()) for key, value in terms.items()}


def batch_loss(model: Student, records: list[dict]):
    totals, diagnostics = [], []
    for record in records:
        output = model(record["public_input"])
        loss, terms = decision_loss(output, record["targets"], record["public_input"], model.config)
        totals.append(loss)
        diagnostics.append(terms)
    return torch.stack(totals).mean(), diagnostics


def check_split_isolation(train: DecisionDataset, validation: DecisionDataset) -> None:
    # Each identifier is independently isolated, so transitive grouping done by
    # preparation cannot accidentally be bypassed by combining only whole tuples.
    # Never trust a caller-supplied digest to establish isolation. Raw JSONL
    # users may bypass M5 preparation; exact public duplicates still cannot cross.
    actual_train = {public_input_digest(record["public_input"]) for record in train.records}
    actual_validation = {public_input_digest(record["public_input"]) for record in validation.records}
    if actual_train & actual_validation:
        raise SchemaError("train/validation leakage in recomputed public_input digest")
    keys = ("source_run_group", "source_combat_id", "branch_family", "public_state_digest")
    for key in keys:
        left, right = set(), set()
        for dataset, group in ((train, left), (validation, right)):
            for row in dataset.records:
                value = row["audit_only"].get(key)
                if not isinstance(value, str) or not value:
                    raise SchemaError(f"missing provenance {key}")
                group.add(value)
        if left & right:
            raise SchemaError(f"train/validation leakage in {key}")


def smoke(dataset: DecisionDataset, config: dict) -> dict:
    started = time.perf_counter()
    seed_everything(config["seed"], config.get("torch_threads", 1))
    model = Student(config)
    records = dataset.records[:config["batch_size"]]
    if not any(any(row["masks"].values()) for record in records for row in record["targets"]["actions"]):
        raise SchemaError("smoke batch contains no supervised targets")
    loss, terms = batch_loss(model, records)
    loss.backward()
    gradients = [p.grad for p in model.parameters() if p.grad is not None]
    if not torch.isfinite(loss) or not gradients or not all(torch.isfinite(g).all() for g in gradients):
        raise RuntimeError("non-finite forward/backward")
    return {"status": "SINGLE_BATCH_FORWARD_BACKWARD_PASS", "records": len(records),
            "parameters": sum(p.numel() for p in model.parameters()), "loss": float(loss.detach()),
            "loss_terms": terms, "torch_version": torch.__version__, "seed": config["seed"],
            "optimizer_steps": 0, "weights_written": False, "formal_training_run": False,
            "wall_seconds": time.perf_counter() - started, "peak_rss_mib": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024,
            "candidates": sum(len(r["public_input"]["candidate_actions"]) for r in records),
            "history_events": sum(len(r["public_input"]["observation"]["history"]) for r in records)}



def config_hash(config: dict) -> str:
    return hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def freeze_inputs(datasets: dict[str, DecisionDataset], config: dict) -> dict:
    for left, right in (("train", "validation"), ("train", "test"), ("validation", "test")):
        check_split_isolation(datasets[left], datasets[right])
    version_keys = ("simulator_commit", "rules_version", "label_endpoint", "teacher_version", "objective_version")
    schemas = {record["public_input"]["observation"]["schema"] for data in datasets.values() for record in data.records}
    if len(schemas) != 1:
        raise SchemaError("mixed observation versions in one experiment")
    versions = {}
    for key in version_keys:
        values = set()
        for data in datasets.values():
            for record in data.records:
                value = record["audit_only"].get(key)
                if not isinstance(value, str) or not value:
                    raise SchemaError(f"missing required audit provenance: {key}")
                values.add(value)
        if len(values) != 1:
            raise SchemaError(f"mixed label provenance for {key}")
        versions[key] = next(iter(values))
    stable_versions, root_continuations = None, []
    required = {"teacher", "continuation", "objective", "public_schema", "simulator"}
    for data in datasets.values():
        for record in data.records:
            audit = record["audit_only"]
            stable = audit.get("versions")
            if not isinstance(stable, dict) or not required <= stable.keys() or not all(isinstance(value, str) and value for value in stable.values()):
                raise SchemaError("missing stable audit.versions provenance")
            expected = {"teacher": audit["teacher_version"], "objective": audit["objective_version"],
                        "simulator": audit["simulator_commit"], "public_schema": record["public_input"]["schema_version"]}
            if any(stable[key] != value for key, value in expected.items()):
                raise SchemaError("stable audit versions disagree with record provenance")
            if "observation_schema" in stable and stable["observation_schema"] != record["public_input"]["observation"]["schema"]:
                raise SchemaError("stable observation schema disagrees with public input")
            full = audit.get("continuation_version")
            if not isinstance(full, str) or not full or full.split(":", 1)[0] != stable["continuation"]:
                raise SchemaError("full continuation digest disagrees with stable family")
            if stable_versions is None:
                stable_versions = dict(stable)
            elif stable != stable_versions:
                raise SchemaError("mixed stable audit.versions provenance")
            # Full frozen-tree IDs remain in immutable per-root audit records.
            # Bind their association here too, without making root IDs features.
            root_continuations.append([audit["source_run_group"], audit["source_combat_id"], audit["branch_family"], audit["public_state_digest"], full])
    versions["continuation_version"] = stable_versions["continuation"]
    # Test content is frozen and checked for overlap only; test outcomes never
    # select checkpoints or enter training/validation loss.
    return {"public_identity_scheme": PUBLIC_IDENTITY_SCHEME, "config_sha256": config_hash(config), "observation_schema": next(iter(schemas)), "splits": {name: {"sha256": data.sha256, "roots": len(data)} for name, data in datasets.items()},
            "implementation": implementation_fingerprint(),
            "versions": versions, "stable_versions": stable_versions,
            "per_root_continuation_audit_sha256": canonical_object_digest(sorted(root_continuations)),
            "test_used_for_model_selection": False}


def atomic_save(value: dict, path: Path) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    torch.save(value, temporary)
    os.replace(temporary, path)


def checkpoint_state(model: Student, optimizer, frozen: dict, budget: dict, epoch: int, offset: int,
                     steps: int, order: list[int], trace: list[dict]) -> dict:
    implementation = implementation_fingerprint()
    if "implementation" in frozen and frozen["implementation"] != implementation:
        raise SchemaError("checkpoint rejected: implementation or runtime changed during pilot")
    # Also bind low-level engineering serialization callers, which need not have
    # datasets. Never silently upgrade an old checkpoint in restore_checkpoint.
    frozen = {**frozen, "implementation": implementation}
    return {"format": "nosl.experimental.checkpoint.v1", "model": model.state_dict(), "optimizer": optimizer.state_dict(),
            "frozen": frozen, "budget": budget, "epoch": epoch, "offset": offset, "optimizer_steps": steps,
            "order": order, "trace": trace, "torch_rng": torch.get_rng_state(), "python_rng": random.getstate(),
            "formal_training_run": False, "promoted": False}


def restore_checkpoint(path: Path, model: Student, optimizer, frozen: dict, budget: dict) -> dict:
    state = torch.load(path, map_location="cpu", weights_only=True)
    if state.get("format") != "nosl.experimental.checkpoint.v1":
        raise SchemaError("checkpoint is not an experimental pilot")
    saved_frozen = state.get("frozen")
    if not isinstance(saved_frozen, dict) or not isinstance(saved_frozen.get("implementation"), dict):
        raise SchemaError("checkpoint lacks implementation provenance; cannot safely resume")
    implementation = implementation_fingerprint()
    if (saved_frozen["implementation"] != implementation
            or ("implementation" in frozen and frozen["implementation"] != implementation)):
        raise SchemaError("resume rejected: implementation, Python/PyTorch runtime, or CPU settings changed")
    if saved_frozen != {**frozen, "implementation": implementation}:
        raise SchemaError("resume rejected: config, split hashes, or label provenance changed")
    if state["budget"] != budget:
        raise SchemaError("resume cannot expand or reset the authorized lifetime pilot budget")
    integer(state["optimizer_steps"], "checkpoint steps", 0, budget["max_steps"])
    integer(state["epoch"], "checkpoint epoch", 0, budget["max_epochs"])
    integer(state["offset"], "checkpoint offset", 0, len(state["order"]))
    if state["order"] and (len(set(state["order"])) != len(state["order"]) or set(state["order"]) != set(range(len(state["order"])))):
        raise SchemaError("invalid checkpoint shuffle order")
    model.load_state_dict(state["model"], strict=True)
    optimizer.load_state_dict(state["optimizer"])
    torch.set_rng_state(state["torch_rng"])
    random.setstate(state["python_rng"])
    return state


def evaluate(model: Student, dataset: DecisionDataset) -> dict:
    model.eval()
    accum = {"loss": [], "win_brier": [], "death_brier": [], "hp_mae": [], "value_mae": [], "value_mse": [],
             "pairwise_agreement": [], "equivalent_set_agreement": [],
             "empirical_teacher_mean_regret": [], "empirical_teacher_best_action_agreement": []}
    unresolved = 0
    incomplete_value_roots, single_action_roots = 0, 0
    with torch.no_grad():
        for record in dataset.records:
            output = model(record["public_input"])
            loss, _ = decision_loss(output, record["targets"], record["public_input"], model.config)
            accum["loss"].append(float(loss))
            for row in record["targets"]["actions"]:
                index = row["action_index"]
                unresolved += not any(row["masks"].values())
                for head, metric in (("win_probability", "win_brier"), ("death_probability", "death_brier")):
                    if row["masks"][head]:
                        accum[metric].append((float(torch.sigmoid(output[head][index])) - row[head]) ** 2)
                if row["masks"]["expected_final_hp"]:
                    accum["hp_mae"].append(abs(float(output["expected_final_hp"][index]) * 100 - row["expected_final_hp"]))
                if row["masks"]["value"]:
                    error = float(output["value"][index]) * 100 - row["value"]
                    accum["value_mae"].append(abs(error))
                    accum["value_mse"].append(error ** 2)
            legal = [index for index, allowed in enumerate(record["public_input"]["legal_mask"]) if allowed]
            by_index = {row["action_index"]: row for row in record["targets"]["actions"]}
            if len(legal) < 2:
                single_action_roots += 1
            elif not all(by_index[index]["masks"]["value"] for index in legal):
                # Do not select the best only among completed/easy candidates.
                incomplete_value_roots += 1
            else:
                selected = int(output["ranking_score"].argmax())
                best_mean = max(by_index[index]["value"] for index in legal)
                selected_mean = by_index[selected]["value"]
                # These are descriptive finite-sample mean estimates, not new
                # ranking labels, significance evidence, or student rollouts.
                accum["empirical_teacher_mean_regret"].append(max(0., best_mean - selected_mean))
                accum["empirical_teacher_best_action_agreement"].append(float(abs(best_mean - selected_mean) <= 1e-9))
            for pair in record["targets"]["pairwise"]:
                if pair["weight"] > 0:
                    delta = float(output["value"][pair["preferred"]] - output["value"][pair["other"]])
                    accum["pairwise_agreement"].append((float(delta > 0) + .5 * float(delta == 0), pair["weight"]))
            equivalent = record["targets"]["equivalent_action_set"]
            if equivalent:
                accum["equivalent_set_agreement"].append(float(int(output["ranking_score"].argmax()) in equivalent))
    result = {key: {"value": None if not values else sum(values) / len(values), "count": len(values)}
              for key, values in accum.items() if key != "pairwise_agreement"}
    pairs = accum["pairwise_agreement"]
    result["pairwise_agreement"] = {"value": sum(a * w for a, w in pairs) / sum(w for _, w in pairs) if pairs else None, "count": len(pairs)}
    result["unresolved_actions"] = unresolved
    result["evaluation_roots"] = len(dataset.records)
    result["probabilities_calibrated"] = False
    result["empirical_teacher_ranking_semantics"] = {
        "population": "roots with at least two legal candidates and all legal value targets available",
        "regret_definition": "maximum empirical teacher mean utility minus selected action empirical mean utility",
        "value_units": "same utility units as teacher value targets; not HP alone",
        "best_mean_tie_tolerance": 1e-9,
        "single_action_roots_skipped": single_action_roots,
        "incomplete_value_roots_skipped": incomplete_value_roots,
        "uncertainty_accounted_for": False,
        "statistically_certified": False,
        "warning": "small-N estimates can be noisy and optimistic when selecting the largest sampled mean; distinct from certified pairwise labels",
        "student_rollout_win_rate_measured": False,
    }
    return result


def before_fit_validation_reference(model: Student, validation: DecisionDataset, trace: list[dict], *, resume: bool) -> dict:
    """Capture random initialization once; never relabel learned resume weights."""
    if resume:
        references = [entry for entry in trace if entry.get("phase") == "before_fit_random_initialization"]
        if len(references) != 1 or references[0].get("epoch") != 0 or references[0].get("optimizer_steps") != 0:
            raise SchemaError("resume checkpoint lacks one valid before-fit random-initialization reference")
        reference = references[0].get("validation")
        if not isinstance(reference, dict):
            raise SchemaError("resume before-fit validation reference is malformed")
        return reference
    if trace:
        raise SchemaError("new pilot before-fit reference requires an empty trace")
    reference = evaluate(model, validation)
    trace.append({"phase": "before_fit_random_initialization", "epoch": 0, "optimizer_steps": 0,
                  "validation": reference})
    return reference


def validate_pilot_budget(max_steps: int, max_epochs: int, max_roots: int, dataset_roots: int) -> dict:
    # Explicit bounds are part of the experiment identity, including on resume.
    integer(max_steps, "pilot max_steps", 1, 50000)
    integer(max_epochs, "pilot max_epochs", 1, 5)
    integer(max_roots, "pilot max_roots", 1, 10000)
    if dataset_roots > max_roots:
        raise SchemaError("dataset exceeds authorized root cap; never silently truncate or resample")
    return {"max_steps": max_steps, "max_epochs": max_epochs, "max_roots": max_roots}


def pilot_train(datasets: dict[str, DecisionDataset], config: dict, output: Path, *, budget: dict,
                confirmed: bool = False, resume: bool = False, stop_after_steps: int | None = None) -> dict:
    if stop_after_steps is not None:
        integer(stop_after_steps, "stop_after_steps", 1, 50000)
    if not confirmed:
        raise SchemaError("pilot requires explicit bounded-trial authorization; formal training is disabled")
    validate_pilot_budget(**budget, dataset_roots=len(datasets["train"]))
    if not any(row["masks"]["value"] and row.get("sample_weight", 1) > 0 for record in datasets["train"].records for row in record["targets"]["actions"]):
        raise SchemaError("pilot has no objective-resolved ranking/value labels")
    if any(record["audit_only"].get("engineering_smoke") is True for data in datasets.values() for record in data.records):
        raise SchemaError("engineering-smoke corpus cannot be used for a pilot fit")
    seed_everything(config["seed"], config.get("torch_threads", 1))
    # Fingerprint effective settings after deterministic initialization, so an
    # ordinary fresh process and its resumed process bind the same fit runtime.
    frozen = freeze_inputs(datasets, config)
    model = Student(config)
    optimizer = torch.optim.AdamW(model.parameters(), lr=config["learning_rate"])
    epoch, offset, steps, order, trace = 0, 0, 0, [], []
    if resume:
        state = restore_checkpoint(output / "checkpoint.pt", model, optimizer, frozen, budget)
        epoch, offset, steps, order, trace = (state[key] for key in ("epoch", "offset", "optimizer_steps", "order", "trace"))
    else:
        output.mkdir(parents=True, exist_ok=False)
        (output / "config.json").write_text(json.dumps(config, indent=2) + "\n")
        (output / "inputs.json").write_text(json.dumps(frozen, indent=2) + "\n")
    started = time.perf_counter()
    initial_validation = before_fit_validation_reference(model, datasets["validation"], trace, resume=resume)
    if not resume:
        # Persist the reference before the first step so a resumed run retains
        # the same baseline, frozen inputs and zero-step RNG/optimizer state.
        atomic_save(checkpoint_state(model, optimizer, frozen, budget, epoch, offset, steps, order, trace), output / "checkpoint.pt")
        (output / "metrics.json").write_text(json.dumps(trace, indent=2, allow_nan=False) + "\n")
    initial_steps = steps
    while epoch < budget["max_epochs"] and steps < budget["max_steps"]:
        if stop_after_steps is not None and steps - initial_steps >= stop_after_steps:
            break
        if not order:
            order = list(range(len(datasets["train"])))
            random.shuffle(order)
        model.train()
        indices = order[offset:offset + config["batch_size"]]
        optimizer.zero_grad(set_to_none=True)
        # Sequential microbatches keep activation memory bounded by one decision,
        # while accumulating an equivalent decision-mean batch gradient.
        train_loss = 0.0
        supervised = [index for index in indices if any(any(row["masks"].values()) and row.get("sample_weight", 1) > 0
                      for row in datasets["train"][index]["targets"]["actions"])]
        for index in supervised:
            record = datasets["train"][index]
            loss, _ = decision_loss(model(record["public_input"]), record["targets"], record["public_input"], config)
            if not torch.isfinite(loss):
                raise RuntimeError("non-finite pilot loss")
            (loss / len(supervised)).backward()
            train_loss += float(loss.detach()) / len(supervised)
        if supervised:
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0, error_if_nonfinite=True)
            optimizer.step()
            steps += 1
        offset += len(indices)
        if offset == len(order):
            metrics = evaluate(model, datasets["validation"])
            trace.append({"epoch": epoch + 1, "optimizer_steps": steps, "last_batch_train_loss": train_loss,
                          "validation": metrics})
            epoch, offset, order = epoch + 1, 0, []
        # Each committed checkpoint includes matching optimizer/RNG/budget progress.
        atomic_save(checkpoint_state(model, optimizer, frozen, budget, epoch, offset, steps, order, trace), output / "checkpoint.pt")
        (output / "metrics.json").write_text(json.dumps(trace, indent=2, allow_nan=False) + "\n")
    atomic_save(model.state_dict(), output / "weights.pt")
    state = checkpoint_state(model, optimizer, frozen, budget, epoch, offset, steps, order, trace)
    atomic_save(state, output / "checkpoint.pt")
    final_validation = evaluate(model, datasets["validation"])
    manifest = {"format": "nosl.student.bundle.v1", "status": "EXPERIMENTAL_UNPROMOTED", "trained": steps > 0,
                "calibrated": False, "weights_sha256": hashlib.sha256((output / "weights.pt").read_bytes()).hexdigest(),
                "public_schema": config["schema_version"], "config_sha256": config_hash(config),
                "objective_id": frozen["versions"]["objective_version"], "objective_calibrated": config["objective_calibrated"],
                "continuation_policy_id": frozen["versions"]["continuation_version"], "frozen_inputs": frozen,
                "validation_before_fit": initial_validation, "validation": final_validation, "budget": budget, "optimizer_steps": steps, "epochs_completed": epoch, "next_offset": offset,
                "wall_seconds_this_invocation": time.perf_counter() - started, "peak_rss_mib": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024,
                "formal_training_run": False, "pilot_trial": True, "test_evaluated": False,
                "budget_exhausted": steps >= budget["max_steps"] or epoch >= budget["max_epochs"]}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2, allow_nan=False) + "\n")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    data_group = parser.add_mutually_exclusive_group(required=True)
    data_group.add_argument("--data")
    data_group.add_argument("--prepared", help="verified M5 sharded dataset directory")
    parser.add_argument("--mode", choices=("smoke", "pilot", "formal"), default="smoke")
    parser.add_argument("--validation")
    parser.add_argument("--test")
    parser.add_argument("--output")
    parser.add_argument("--confirm-pilot", action="store_true")
    parser.add_argument("--max-steps", type=int)
    parser.add_argument("--max-epochs", type=int)
    parser.add_argument("--max-roots", type=int)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--stop-after-steps", type=int, help="optional interruption rehearsal; lifetime budget still applies")
    args = parser.parse_args()
    if args.mode == "formal":
        parser.error("formal training is disabled; only an explicitly authorized bounded pilot is implemented")
    config = load_config(args.config)
    config_digest = canonical_object_digest(config)
    dataset = prepared_dataset(args.prepared, "train", config, config_digest) if args.prepared else DecisionDataset(args.data, config)
    if args.mode == "smoke":
        result = smoke(dataset, config)
    else:
        if not all((args.confirm_pilot, args.prepared or args.validation, args.prepared or args.test, args.output, args.max_steps, args.max_epochs, args.max_roots)):
            parser.error("pilot requires --confirm-pilot, --validation, --test, --output, and all three lifetime budget limits")
        budget = validate_pilot_budget(args.max_steps, args.max_epochs, args.max_roots, len(dataset))
        datasets = ({split: prepared_dataset(args.prepared, split, config, config_digest) for split in ("train", "validation", "test")} if args.prepared else
                    {"train": dataset, "validation": DecisionDataset(args.validation, config), "test": DecisionDataset(args.test, config)})
        result = pilot_train(datasets, config, Path(args.output), budget=budget, confirmed=True, resume=args.resume,
                             stop_after_steps=args.stop_after_steps)
    print(json.dumps(result, allow_nan=False))


if __name__ == "__main__":
    main()
