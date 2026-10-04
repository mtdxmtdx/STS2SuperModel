#!/usr/bin/env python3
"""Inspect an exact constructed native-tape report through full-policy batch_loss.

Forward-only engineering check. Does not start a native run, create a training
session, backpropagate, construct an optimizer, authorize fitting or save weights.
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw_report", type=Path)
    parser.add_argument("--config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    parser.add_argument("--normalized-output", type=Path,
        help="Optional engineering-only full-v5 JSONL, retaining the exact whole report in each row")
    args = parser.parse_args(argv)
    import torch
    from nosl.constructed_native_policy_v5 import adapt_report
    from nosl.data_policy_v5 import PolicyDatasetV5, supervision_coverage
    from nosl.model_v5 import StudentV5
    from nosl.policy_v5 import digest, merge_coverage, state_digest
    from nosl.schema_v5 import load_config
    from nosl.train_policy_v5 import batch_loss, execution_gate

    config, payload = load_config(args.config), args.raw_report.read_bytes()
    calls = []
    def forbidden(*unused, **also_unused):
        calls.append(True)
        raise AssertionError("Constructed native-tape forward check attempted learning")
    with ExitStack() as stack:
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            stack.enter_context(patch(target, side_effect=forbidden))
        records = adapt_report(payload, config)
        dataset = PolicyDatasetV5(records, config, split="train", purpose="engineering-fixture")
        coverage = merge_coverage(supervision_coverage(record, config) for record in records)
        gate = execution_gate({"purpose": dataset.purpose, "training_supervision": coverage})
        assert not gate["accepted"]
        torch.set_num_threads(1)
        with torch.random.fork_rng():
            torch.manual_seed(1729)
            model = StudentV5(config)
        before = state_digest(model.state_dict()); model.eval()
        with torch.no_grad():
            loss, terms = batch_loss(model, dataset.records, config)
        after = state_digest(model.state_dict())
        assert before == after and not calls and all(p.grad is None for p in model.parameters())
        assert torch.isfinite(loss) and not loss.requires_grad and loss.grad_fn is None
        assert all(row["pairwise"] == row["equivalent"] == 0 for row in terms)
        assert all(not key.startswith("plan.") or value == 0 for row in terms for key, value in row.items())
        if args.normalized_output is not None:
            args.normalized_output.parent.mkdir(parents=True, exist_ok=True)
            args.normalized_output.write_text("".join(json.dumps(record, sort_keys=True, separators=(",", ":"),
                allow_nan=False) + "\n" for record in records), encoding="utf-8")
            reread = PolicyDatasetV5.from_jsonl(args.normalized_output, config, split="train", purpose="engineering-fixture")
            assert reread.records == records
    print(json.dumps({"status": "passed", "check": "constructed_native_tape_full_policy_batch_loss_forward_only",
        "raw_report_sha256": hashlib.sha256(payload).hexdigest(), "record_sha256": [digest(r) for r in records],
        "source_kind": records[0]["audit_only"]["source_kind"], "collection_summary": records[0]["audit_only"]["collection_summary"],
        "objectives": [r["objective"] for r in records], "config_sha256": hashlib.sha256(args.config.read_bytes()).hexdigest(),
        "parameter_count": sum(p.numel() for p in model.parameters()), "model_state_before_sha256": before,
        "model_state_after_sha256": after, "parameters_unchanged": before == after, "loss": float(loss), "terms": terms,
        "action_masks": [[a["masks"] for a in r["targets"]["actions"]] for r in records], "supervision_coverage": coverage,
        "fit_gate": gate, "engineering_dataset_accepted": True, "backward_calls": 0, "optimizer_constructions": 0,
        "optimizer_steps": 0, "gradients_created": False, "weights_saved": False, "trainable": False,
        "natural_native_admission": False, "objective_calibrated": False}, indent=2, allow_nan=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
