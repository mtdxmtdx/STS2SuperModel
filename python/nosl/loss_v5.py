"""Engineering loss evaluation only. No optimizer, backward, or fitting API."""
from torch.nn import functional as F

from .data_v5 import validate_targets
from .schema_v2 import PLAN_HEADS
from .train import decision_loss as action_loss


def decision_loss(output, targets, public, config):
    # Validate full graph conditioning and exact anchors before evaluating any
    # mask. Reuse only scalar action-head arithmetic, with no public projection.
    validate_targets(targets, public, config)
    v2_config = config["base_config"]["base_config"]["base_config"]
    loss, terms = action_loss(output, {k: v for k, v in targets.items() if k != "plan"}, public, v2_config["base_config"])
    plan = targets.get("plan")
    for head in PLAN_HEADS:
        prediction = output["plan"][head]
        term = prediction * 0
        if plan and plan["masks"][head]:
            target = prediction.new_tensor(plan[head])
            term = F.binary_cross_entropy_with_logits(prediction, target) if head == "specified_success_probability" else F.mse_loss(prediction, target / 100)
        loss = loss + v2_config["plan_loss_weights"][head] * term
        terms["plan." + head] = float(term.detach())
    return loss, terms
