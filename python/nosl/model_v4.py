"""Untrained engineering model consuming every typed evidence event in order.

The bounded numerical/hash representation and GRU are deliberately lossy, not a
claim of sufficient policy strength. No prefix is truncated or replaced by an
audit ID. Identity remains exact and independent of this feature representation.
"""
from __future__ import annotations

import hashlib
import json
import math

import torch
from torch import nn

from .evidence_v4 import FACT_ALLOWED, KINDS, OWNER_KINDS
from .model_v3 import StudentV3
from .public_identity_v4 import canonical_public_input
from .schema_v2 import PLAN_HEADS
from .schema_v4 import _v3_mechanics_projection, validate_config, validate_public

SUMMARY_NAMES = ("complete_from_run_start", "observed_run_start", "event_count", "owner_count",
                 "gap_count", "open_owner_count", "last_assets_known", "last_hp_fraction", "last_max_hp",
                 "last_gold", "last_deck_size", "last_relic_count", "last_potion_count") + tuple("kind." + k for k in KINDS)
EVENT_NUMERIC_NAMES = ("event_position", "owner_known", "owner_ordinal", "history_link_known", "history_link_distance",
                       "selection_link_known", "selection_link_distance")
EVENT_TYPE_NAMES = tuple("kind." + k for k in KINDS) + tuple("owner." + k for k in OWNER_KINDS) + tuple("fact." + k for k in FACT_ALLOWED)


def _bounded(value, scale=1):
    return value / (scale + abs(value))


def _bounded_hash(value, dim):
    """All scalar/container paths contribute; numerical magnitudes are explicit."""
    result = [0.] * dim
    count = 0
    def add(token, amount=1.):
        nonlocal count
        digest = hashlib.sha256(token.encode("utf-8")).digest()
        result[int.from_bytes(digest[:4], "little") % dim] += amount if digest[4] & 1 else -amount
        count += 1
    def visit(item, path):
        if isinstance(item, dict):
            add(path + ":object", _bounded(len(item)))
            for key in sorted(item): visit(item[key], path + "." + key)
        elif isinstance(item, list):
            add(path + ":length", _bounded(len(item)))
            for index, entry in enumerate(item): visit(entry, path + "." + str(index))
        elif type(item) in (int, float):
            add(path + ":number-known"); add(path + ":number-value", _bounded(item))
        else: add(path + "=" + json.dumps(item, ensure_ascii=False, allow_nan=False))
    visit(value, "evidence")
    divisor = math.sqrt(max(1, count))
    return [math.tanh(value / divisor) for value in result]


def evidence_features(evidence, hash_dim):
    """Return bounded explicit summary and one feature row per recorded event.

    Call only after schema validation. No audit metadata enters this function.
    Root and finite-anchor channels are encoded separately by StudentV4.
    """
    events = evidence["events"]
    kind_counts = {kind: 0 for kind in KINDS}
    owners, closed, assets, rows = {}, set(), None, []
    for event in events:
        ordinal, owner_id, payload = event["eventOrdinal"], event["ownerOrdinal"], event["payload"]
        kind = payload["kind"]; kind_counts[kind] += 1
        if kind == "owner_started": owners[owner_id] = payload["ownerKind"]
        if kind == "owner_ended": closed.add(owner_id)
        if isinstance(payload.get("assets"), dict): assets = payload["assets"]
        history = payload.get("historyThroughEventOrdinal")
        selection = payload.get("offerEventOrdinal", payload.get("decisionEventOrdinal"))
        numeric = [_bounded(ordinal, 100), float(owner_id is not None), _bounded(owner_id or 0, 10),
                   float(history is not None), _bounded(ordinal - history, 10) if history is not None else 0.,
                   float(selection is not None), _bounded(ordinal - selection, 10) if selection is not None else 0.]
        typed = [float(kind == k) for k in KINDS] + [float(owners.get(owner_id) == k) for k in OWNER_KINDS] + [
            float(payload.get("factKind") == k) for k in FACT_ALLOWED]
        rows.append(numeric + typed + _bounded_hash(event, hash_dim))
    known = assets is not None
    asset_features = ([assets["hp"] / max(1, assets["maxHp"]), _bounded(assets["maxHp"], 100),
                       _bounded(assets["gold"], 100), _bounded(len(assets["deck"]), 30),
                       _bounded(len(assets["relics"]), 10), _bounded(sum(p is not None for p in assets["potions"]), 3)]
                      if known else [0.] * 6)
    summary = [float(evidence["completeFromRunStart"]), float(events[0]["payload"]["kind"] == "run_started"),
               _bounded(len(events), 100), _bounded(len(owners), 10), _bounded(kind_counts["gap"], 10),
               _bounded(len(owners) - len(closed), 10), float(known), *asset_features]
    return summary + [_bounded(kind_counts[k], 10) for k in KINDS], rows


def public_evidence_features(public, hash_dim):
    normalized = canonical_public_input(public)
    root = evidence_features(normalized["public_evidence"], hash_dim)
    anchor = normalized["controller_context"].get("anchor")
    return root, evidence_features(anchor["public_evidence"], hash_dim) if anchor is not None else None


class StudentV4(nn.Module):
    def __init__(self, config):
        super().__init__()
        self.config = validate_config(config)
        self.mechanics = StudentV3(config["base_config"])
        base = config["base_config"]["base_config"]["base_config"]
        hidden = base["hidden_dim"]
        event_dim = len(EVENT_NUMERIC_NAMES) + len(EVENT_TYPE_NAMES) + config["evidence_hash_dim"]
        self.event_projection = nn.Sequential(nn.Linear(event_dim, hidden), nn.Tanh())
        self.evidence_encoder = nn.GRU(hidden, hidden, batch_first=True)
        self.summary = nn.Sequential(nn.Linear(len(SUMMARY_NAMES), hidden), nn.Tanh())
        self.channel = nn.Linear(3 * hidden, hidden)
        self.context = nn.Sequential(nn.Linear(2 * hidden + 1, hidden), nn.Tanh())
        self.adapter = nn.Sequential(nn.Linear(hidden + 5, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU())
        self.heads = nn.ModuleDict({name: nn.Linear(hidden, 1) for name in self.mechanics.heads})
        self.hp_distribution = nn.Linear(hidden, base["hp_bins"])
        self.plan_heads = nn.ModuleDict({name: nn.Linear(2 * hidden, 1) for name in PLAN_HEADS})

    def _channel(self, features):
        summary, rows = features
        core = self.mechanics.core
        sequence = self.event_projection(core.tensor(rows)).unsqueeze(0)
        encoded, last = self.evidence_encoder(sequence)
        # The mean gives the entire prefix a direct path, including early events.
        return self.channel(torch.cat((last[0, 0], encoded[0].mean(dim=0), self.summary(core.tensor(summary)))))

    def forward(self, public):
        validate_public(public, self.config)
        output = self.mechanics(_v3_mechanics_projection(public))
        root, anchor = public_evidence_features(public, self.config["evidence_hash_dim"])
        root_context = self._channel(root)
        anchor_context = self._channel(anchor) if anchor is not None else torch.zeros_like(root_context)
        context = self.context(torch.cat((root_context, anchor_context, self.mechanics.core.tensor([float(anchor is not None)]))))
        scores = torch.stack([output[name] for name in self.heads], dim=-1)
        hidden = self.adapter(torch.cat((scores, context.expand(len(scores), -1)), dim=-1))
        for name, head in self.heads.items(): output[name] = output[name] + head(hidden).squeeze(-1)
        output["hp_distribution"] = output["hp_distribution"] + self.hp_distribution(hidden)
        whole = torch.cat((context, hidden[output["legal_mask"]].mean(dim=0)))
        output["plan"] = {name: output["plan"][name] + head(whole).squeeze(-1) for name, head in self.plan_heads.items()}
        output["ranking_score"] = output["value"].masked_fill(~output["legal_mask"], -torch.inf)
        return output
