#!/usr/bin/env python3
"""Explicit one-backward synthetic connectivity check; never a fitting command.

This is deliberately outside unittest discovery. Run only for an independently
authorized one-call check after code review. It cannot create an optimizer,
clip gradients, execute a training loop, serialize weights or promote a model.
"""
from __future__ import annotations

import argparse
from contextlib import ExitStack
import hashlib
import json
from pathlib import Path
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/python")]

import torch
from full_policy_v5_fixtures import record_fixture
from nosl.model_v5 import StudentV5
from nosl.policy_v5 import digest, require, state_digest
from nosl.schema_v5 import PUBLIC_SCHEMA, load_config
from nosl.train import seed_everything
from nosl.train_policy_v5 import batch_loss, implementation_fingerprint


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute-one-authorized-backward", action="store_true")
    parser.add_argument("--report", required=True, help="new JSON report path; never a weights file")
    args = parser.parse_args(argv)
    require(args.execute_one_authorized_backward, "separate_explicit_one_backward_authorization_required")
    report_path = Path(args.report)
    require(not report_path.exists() and report_path.suffix == ".json", "new_json_report_path_required")
    config_path = ROOT / "configs/student.v5.engineering.json"
    config = load_config(config_path)
    base = config["base_config"]["base_config"]["base_config"]["base_config"]
    # These two exact records are synthetic, contain no authentic utility labels,
    # and never leave this process. One active anchor connects both plan heads.
    records = [record_fixture(1), record_fixture(2, finite=True)]
    require(len(records) <= base["batch_size"] and base["hidden_dim"] == 128, "exact_current_v5_minibatch_required")
    seed_everything(base["seed"], 1)
    model = StudentV5(config)
    before = {name: tensor.detach().clone() for name, tensor in model.state_dict().items()}
    before_hash = state_digest(before)
    requests = 0
    engine_entries = 0
    original_backward = torch.Tensor.backward
    original_engine_backward = torch.autograd.backward

    def once(tensor, *arguments, **keywords):
        nonlocal requests
        requests += 1
        require(requests == 1, "second_backward_forbidden")
        return original_backward(tensor, *arguments, **keywords)

    def engine_once(*arguments, **keywords):
        nonlocal engine_entries
        engine_entries += 1
        require(engine_entries == 1, "second_autograd_engine_entry_forbidden")
        return original_engine_backward(*arguments, **keywords)

    with ExitStack() as guards:
        guards.enter_context(patch("torch.Tensor.backward", once))
        guards.enter_context(patch("torch.autograd.backward", engine_once))
        for target in ("torch.autograd.grad", "torch.optim.AdamW", "torch.optim.SGD", "torch.save",
                       "torch.nn.utils.clip_grad_norm_", "nosl.train_policy_v5.train_bounded",
                       "nosl.train_policy_v5.PolicyTrainingSessionV5.train_next"):
            guards.enter_context(patch(target, side_effect=AssertionError("connectivity check forbids " + target)))
        loss, terms = batch_loss(model, records, config)
        loss.backward()
    require(requests == engine_entries == 1 and bool(torch.isfinite(loss)), "one_finite_backward_required")
    groups = {"map_nodes": "node_projection.", "map_edges": "edge_projection.",
        "map_chronology": "graph_encoder.", "value_ranking": "heads.value.",
        "win_probability": "heads.win_probability.", "death_probability": "heads.death_probability.",
        "expected_final_hp": "heads.expected_final_hp.", "hp_distribution": "hp_distribution.",
        "potion_net_change": "heads.potion_net_change.",
        "specified_success_probability": "plan_heads.specified_success_probability.",
        "extra_net_hp_loss": "plan_heads.extra_net_hp_loss.", "inherited_mechanics": "mechanics."}
    gradients = {}
    for group, prefix in groups.items():
        values = [p.grad for name, p in model.named_parameters() if name.startswith(prefix) and p.grad is not None]
        require(bool(values) and all(bool(torch.isfinite(g).all()) for g in values)
                and any(bool(torch.count_nonzero(g)) for g in values), "missing_finite_nonzero_gradient:" + group)
        gradients[group] = {"tensors": len(values), "nonzero_entries": sum(int(torch.count_nonzero(g)) for g in values),
                            "finite": True}
    all_gradients = [p.grad for p in model.parameters() if p.grad is not None]
    require(all(bool(torch.isfinite(g).all()) for g in all_gradients), "nonfinite_gradient")
    after = model.state_dict()
    require(all(torch.equal(before[name], tensor) for name, tensor in after.items()), "parameters_changed")
    after_hash = state_digest(after)
    require(before_hash == after_hash, "parameter_bytes_changed")
    report = {"status": "SINGLE_SYNTHETIC_FULL_V5_CONNECTIVITY_PASS", "public_schema": PUBLIC_SCHEMA,
        "config_version": config["config_version"],
        "model_version": config["model_version"], "student_config_sha256": digest(config),
        "student_config_file_sha256": hashlib.sha256(config_path.read_bytes()).hexdigest(),
        "implementation": implementation_fingerprint(), "minibatches": 1, "records": len(records),
        "fixture_records_sha256": digest(records), "backward_calls": requests,
        "autograd_engine_entries": engine_entries, "optimizer_steps": 0,
        "fit_loops": 0, "weights_written": False, "parameters_changed": False,
        "parameter_state_sha256_before": before_hash, "parameter_state_sha256_after": after_hash,
        "parameter_count": sum(p.numel() for p in model.parameters()), "loss": float(loss.detach()),
        "loss_terms": terms, "gradient_groups": gradients, "all_gradients_finite": True,
        "fixture_source_sha256": hashlib.sha256((ROOT / "tests/python/full_policy_v5_fixtures.py").read_bytes()).hexdigest(),
        "check_source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "formal_training_run": False, "trained_model_created": False, "policy_promoted": False,
        "claim_limit": "synthetic graph connectivity only; no real labels, calibration or student-performance evidence"}
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("status", "backward_calls", "optimizer_steps", "weights_written", "parameters_changed")}))
    return 0


if __name__ == "__main__": raise SystemExit(main())
