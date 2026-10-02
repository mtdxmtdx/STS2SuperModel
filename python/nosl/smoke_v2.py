"""One no-optimizer v2 forward/backward. No fitting or weight export path."""
import argparse
import json
import torch
from torch.nn import functional as F

from .data_v2 import DecisionDatasetV2, validate_targets
from .model_v2 import StudentV2
from .schema_v2 import PLAN_HEADS, legacy_input, load_config
from .train import decision_loss as legacy_loss, seed_everything


def decision_loss(output, targets, public, config):
    validate_targets(targets, public, config)
    loss, terms = legacy_loss(output, {k: v for k, v in targets.items() if k != "plan"},
                              legacy_input(public), config["base_config"])
    plan = targets.get("plan")
    for head in PLAN_HEADS:
        prediction = output["plan"][head]
        term = prediction * 0
        if plan and plan["masks"][head]:
            target = prediction.new_tensor(plan[head])
            term = F.binary_cross_entropy_with_logits(prediction, target) if head == "specified_success_probability" else F.mse_loss(prediction, target / 100)
        loss = loss + config["plan_loss_weights"][head] * term
        terms["plan." + head] = float(term.detach())
    return loss, terms


def smoke(records, config):
    if not records or len(records) > config["base_config"]["batch_size"]:
        raise ValueError("one bounded engineering batch required")
    if not any(any(r["masks"].values()) for record in records for r in record["targets"]["actions"]) and not any(
            any(record["targets"].get("plan", {}).get("masks", {}).values()) for record in records):
        raise ValueError("batch contains no real supervised targets")
    seed_everything(config["base_config"]["seed"])
    model = StudentV2(config)
    before = {name: value.clone() for name, value in model.state_dict().items()}
    losses, terms = [], []
    for record in records:
        loss, diagnostics = decision_loss(model(record["public_input"]), record["targets"], record["public_input"], config)
        losses.append(loss); terms.append(diagnostics)
    loss = torch.stack(losses).mean()
    loss.backward()
    gradients = [p.grad for p in model.parameters() if p.grad is not None]
    if not torch.isfinite(loss) or not gradients or not all(torch.isfinite(g).all() for g in gradients):
        raise RuntimeError("non-finite forward/backward")
    if any(not torch.equal(before[name], value) for name, value in model.state_dict().items()):
        raise RuntimeError("no-optimizer smoke unexpectedly changed weights")
    return {"status": "SINGLE_BATCH_FORWARD_BACKWARD_PASS", "records": len(records), "loss": float(loss.detach()),
            "loss_terms": terms, "optimizer_steps": 0, "weights_written": False, "formal_training_run": False,
            "safe_learned_plan_execution_verified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True); parser.add_argument("--data", required=True)
    args = parser.parse_args()
    config = load_config(args.config)
    data = DecisionDatasetV2(args.data, config)
    print(json.dumps(smoke(data.records[:config["base_config"]["batch_size"]], config), allow_nan=False))


if __name__ == "__main__": main()
