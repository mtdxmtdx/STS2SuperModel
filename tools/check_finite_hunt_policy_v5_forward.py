#!/usr/bin/env python3
"""One fresh constructed plan through the actual full-policy batch loss.

No training session, protection/review/authorization receipt, optimizer, backward
pass or weights export is created. This is a forward-only engineering check.
"""
from contextlib import ExitStack
import argparse
import hashlib
import json
from pathlib import Path
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("--config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    args = parser.parse_args()
    import torch
    from nosl.data_policy_v5 import PolicyDatasetV5, supervision_coverage
    from nosl.finite_hunt_policy_v5 import adapt_record
    from nosl.model_v5 import StudentV5
    from nosl.policy_v5 import digest, state_digest
    from nosl.schema import HEADS
    from nosl.schema_v5 import load_config, loads
    from nosl.train_policy_v5 import batch_loss, execution_gate

    config, payload = load_config(args.config), args.raw.read_bytes()
    calls = []

    def forbidden(*unused, **also_unused):
        calls.append(True)
        raise AssertionError("Constructed full-policy forward check attempted learning")

    with ExitStack() as stack:
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            stack.enter_context(patch(target, side_effect=forbidden))
        record = adapt_record(loads(payload.decode("utf-8")), config, source_raw_bytes=payload)
        dataset = PolicyDatasetV5([record], config, split="train", purpose="engineering-fixture")
        coverage = supervision_coverage(record, config)
        # A readiness probe from actual purpose/coverage only, not a fabricated
        # frozen training receipt. Engineering plan evidence cannot open fit.
        gate = execution_gate({"purpose": dataset.purpose, "training_supervision": coverage})
        assert not gate["accepted"]
        torch.set_num_threads(1)
        with torch.random.fork_rng():
            torch.manual_seed(1729)
            model = StudentV5(config)
        before = state_digest(model.state_dict())
        model.eval()
        with torch.no_grad():
            loss, terms = batch_loss(model, dataset.records, config)
        after = state_digest(model.state_dict())
        assert before == after and not calls and all(p.grad is None for p in model.parameters())
        assert torch.isfinite(loss) and not loss.requires_grad and loss.grad_fn is None
        assert all(row[head] == 0 for row in terms for head in HEADS)
        assert all(row["pairwise"] == row["equivalent"] == 0 for row in terms)
    print(json.dumps({
        "status": "passed", "check": "fresh_constructed_full_policy_batch_loss_forward_only",
        "raw_sha256": hashlib.sha256(payload).hexdigest(),
        "record_sha256": digest(record), "objective": record["objective"],
        "config_sha256": hashlib.sha256(args.config.read_bytes()).hexdigest(),
        "parameter_count": sum(p.numel() for p in model.parameters()),
        "model_state_before_sha256": before, "model_state_after_sha256": after,
        "parameters_unchanged": before == after, "loss": float(loss), "terms": terms,
        "plan_masks": record["targets"]["plan"]["masks"], "supervision_coverage": coverage,
        "fit_gate": gate, "engineering_dataset_accepted": True,
        "backward_calls": 0, "optimizer_constructions": 0, "optimizer_steps": 0,
        "gradients_created": False, "weights_saved": False, "trainable": False,
        "natural_native_admission": False, "objective_calibrated": False,
    }, indent=2, allow_nan=False))


if __name__ == "__main__":
    main()
