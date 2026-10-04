#!/usr/bin/env python3
"""Guarded forward/loss interoperability of fresh constructed v5 evidence.

This is deliberately separate from the consumed one-backward connectivity check.
It forbids gradient and optimizer entry points and never writes model weights.
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
    from nosl.finite_hunt_v5 import adapt_record, validate_adapted_record
    from nosl.loss_v5 import decision_loss
    from nosl.model_v5 import StudentV5
    from nosl.policy_v5 import state_digest, validate_output
    from nosl.schema import HEADS
    from nosl.schema_v5 import load_config, loads
    config = load_config(args.config)
    payload = args.raw.read_bytes()
    record = adapt_record(loads(payload.decode("utf-8")), config, source_raw_bytes=payload)
    validate_adapted_record(record, config)
    calls = []

    def forbidden(*unused, **also_unused):
        calls.append(True)
        raise AssertionError("Fresh v5 forward check attempted learning")

    with ExitStack() as stack:
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            stack.enter_context(patch(target, side_effect=forbidden))
        torch.set_num_threads(1)
        torch.manual_seed(1729)
        model = StudentV5(config)
        before = state_digest(model.state_dict())
        with torch.no_grad():
            output = validate_output(model(record["public_input"]), record["public_input"], config)
            loss, terms = decision_loss(output, record["targets"], record["public_input"], config)
        after = state_digest(model.state_dict())
        assert before == after and not calls and all(parameter.grad is None for parameter in model.parameters())
        assert torch.isfinite(loss) and all(terms[head] == 0 for head in HEADS)
        assert terms["pairwise"] == terms["equivalent"] == 0
    print(json.dumps({
        "status": "passed", "check": "fresh_constructed_full_v5_forward_loss_only",
        "raw_sha256": hashlib.sha256(payload).hexdigest(),
        "config_sha256": hashlib.sha256(args.config.read_bytes()).hexdigest(),
        "parameter_count": sum(parameter.numel() for parameter in model.parameters()),
        "model_state_before_sha256": before, "model_state_after_sha256": after,
        "parameters_unchanged": before == after, "loss": float(loss), "terms": terms,
        "plan_masks": record["targets"]["plan"]["masks"], "action_supervision_rows": 0,
        "backward_calls": 0, "optimizer_constructions": 0, "optimizer_steps": 0,
        "gradients_created": False, "weights_saved": False, "trainable": False,
        "natural_native_admission": False, "full_policy_record_admission": False,
    }, indent=2, allow_nan=False))


if __name__ == "__main__":
    main()
