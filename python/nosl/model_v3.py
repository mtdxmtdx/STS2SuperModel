"""Untrained v3 engineering student with explicit public run context features.

The unchanged frozen core processes mechanics; full root and anchor context
reach distinct numerical features with knownness masks. No checkpoint upgrade,
training, calibration or policy sufficiency is implied by this architecture.
"""
from __future__ import annotations

import torch
from torch import nn

from .model import Student
from .model_v2 import public_extension_features
from .schema_v2 import PLAN_HEADS, legacy_input
from .schema_v3 import _v2_mechanics_projection, validate_config, validate_public, validate_run_context

RUN_CONTEXT_FIELDS = ("act_index", "act_known", "floor", "floor_known", "combat_entry_index",
                      "combat_entry_known", "complete_from_run_start")
RUN_CONTEXT_FEATURE_NAMES = tuple("root." + key for key in RUN_CONTEXT_FIELDS) + ("anchor.present",) + tuple(
    "anchor." + key for key in RUN_CONTEXT_FIELDS)


def public_run_context_features(public: dict) -> list[float]:
    def encode(context):
        validate_run_context(context)
        known = context["completeFromRunStart"]
        return [context["actIndex"] / 4, 1., context["floor"] / 100, 1.,
                context["combatEntryIndex"] / 100 if known else 0., float(known), float(known)]
    features = encode(public["observation"]["runContext"])
    anchor = public["controller_context"].get("anchor")
    return features + ([1.] + encode(anchor["observation"]["runContext"]) if anchor is not None
                       else [0.] * (1 + len(RUN_CONTEXT_FIELDS)))


class StudentV3(nn.Module):
    def __init__(self, config: dict):
        super().__init__()
        self.config = validate_config(config)
        v2, base = config["base_config"], config["base_config"]["base_config"]
        self.core = Student(base)
        d = base["hidden_dim"]
        self.context = nn.Sequential(nn.Linear(v2["context_hash_dim"], d), nn.ReLU(), nn.Linear(d, d))
        self.run_context = nn.Sequential(nn.Linear(len(RUN_CONTEXT_FEATURE_NAMES), d), nn.ReLU(), nn.Linear(d, d))
        self.adapter = nn.Sequential(nn.Linear(d + 5, d), nn.ReLU(), nn.Linear(d, d), nn.ReLU())
        self.heads = nn.ModuleDict({name: nn.Linear(d, 1) for name in self.core.heads})
        self.hp_distribution = nn.Linear(d, base["hp_bins"])
        self.plan_heads = nn.ModuleDict({name: nn.Linear(2 * d, 1) for name in PLAN_HEADS})

    def forward(self, public: dict):
        validate_public(public, self.config)
        output = self.core(legacy_input(_v2_mechanics_projection(public)))
        # Full v3 anchors also remain in the finite-plan extension hash. Only the
        # mechanics branch receives a projection; root context is never omitted.
        context = self.context(self.core.tensor(public_extension_features(public, self.config["base_config"]["context_hash_dim"])))
        context = context + self.run_context(self.core.tensor(public_run_context_features(public)))
        scores = torch.stack([output[name] for name in self.core.heads], dim=-1)
        hidden = self.adapter(torch.cat([scores, context.expand(scores.shape[0], -1)], dim=-1))
        for name, head in self.heads.items(): output[name] = output[name] + head(hidden).squeeze(-1)
        output["hp_distribution"] = output["hp_distribution"] + self.hp_distribution(hidden)
        whole_plan = torch.cat([context, hidden[output["legal_mask"]].mean(dim=0)])
        output["plan"] = {name: head(whole_plan).squeeze(-1) for name, head in self.plan_heads.items()}
        output["ranking_score"] = output["value"].masked_fill(~output["legal_mask"], -torch.inf)
        return output
