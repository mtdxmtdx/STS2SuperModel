"""Opt-in contextual student. The legacy core implementation is unchanged.

The extra branch encodes public anchor/plan and native entry facts. Its compact
hashing and core-score bottleneck are lossy engineering choices, not a claim of
adequate policy strength or a counterfactual budget guarantee.
"""
from __future__ import annotations

import json
import torch
from torch import nn

from .model import Student, feature_hash
from .public_identity import canonical_public_input, normalize_numeric_leaves
from .schema_v2 import EXTENSION_EVENTS, PLAN_HEADS, legacy_input, validate_config, validate_public


def public_extension_features(public: dict, dim: int) -> list[float]:
    context = public["controller_context"]
    plan = {"status": context["status"]}
    if context["status"] != "inactive":
        # Template/baseline references and transport revision tokens are not learned IDs.
        plan.update({key: context[key] for key in ("exitReason", "target", "startPlayerTurn",
                     "deadlinePlayerTurn", "hpSafetyFloor", "lastObservedPlayerTurn")})
        plan["anchor"] = canonical_public_input(context["anchor"])
        anchored = plan["anchor"]
        anchored["candidate_actions"] = [a for a, legal in zip(anchored["candidate_actions"], anchored["legal_mask"]) if legal]
        anchored["legal_mask"] = [True] * len(anchored["candidate_actions"])
        plan["observedEvents"] = canonical_public_input({"observation": {
            "history": context["observedEvents"]}})["observation"]["history"]
    entries = []
    for position, event in enumerate(public["observation"]["history"]):
        if event["kind"] in EXTENSION_EVENTS:
            detail = normalize_numeric_leaves(json.loads(event["detail"]))
            if event["kind"] == "native_entry_assets":
                detail["deck"] = sorted(detail["deck"], key=lambda c: json.dumps(c, sort_keys=True))
            entries.append({"kind": event["kind"], "position": position, "detail": detail})
    return feature_hash({"controller": plan, "publicExtensionEvents": entries}, "student-v2-public", dim)


class StudentV2(nn.Module):
    def __init__(self, config: dict):
        super().__init__()
        self.config = validate_config(config)
        self.core = Student(config["base_config"])
        d = config["base_config"]["hidden_dim"]
        self.context = nn.Sequential(nn.Linear(config["context_hash_dim"], d), nn.ReLU(), nn.Linear(d, d))
        self.adapter = nn.Sequential(nn.Linear(d + 5, d), nn.ReLU(), nn.Linear(d, d), nn.ReLU())
        self.heads = nn.ModuleDict({name: nn.Linear(d, 1) for name in self.core.heads})
        self.hp_distribution = nn.Linear(d, config["base_config"]["hp_bins"])
        self.plan_heads = nn.ModuleDict({name: nn.Linear(2 * d, 1) for name in PLAN_HEADS})

    def forward(self, public: dict):
        validate_public(public, self.config)
        output = self.core(legacy_input(public))
        context = self.context(self.core.tensor(public_extension_features(public, self.config["context_hash_dim"])))
        scores = torch.stack([output[name] for name in self.core.heads], dim=-1)
        hidden = self.adapter(torch.cat([scores, context.expand(scores.shape[0], -1)], dim=-1))
        for name, head in self.heads.items(): output[name] = output[name] + head(hidden).squeeze(-1)
        output["hp_distribution"] = output["hp_distribution"] + self.hp_distribution(hidden)
        whole_plan = torch.cat([context, hidden[output["legal_mask"]].mean(dim=0)])
        output["plan"] = {name: head(whole_plan).squeeze(-1) for name, head in self.plan_heads.items()}
        output["ranking_score"] = output["value"].masked_fill(~output["legal_mask"], -torch.inf)
        return output
