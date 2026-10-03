"""Untrained map adapter over the unchanged v4 student. No fitting entry point.

Each declared graph contributes explicit numeric/type node and edge entities.
Coordinates are visible geometry, not native object IDs. Set pooling forgets
native insertion order; the ordered graph sequence retains public chronology.
This bounded representation is deliberately lossy and is no sufficiency claim.
"""
from __future__ import annotations

import torch
from torch import nn

from .model_v4 import StudentV4, _bounded
from .public_identity_v5 import canonical_public_input
from .schema_v2 import PLAN_HEADS
from .schema_v5 import NODE_TYPES, _v4_mechanics_projection, coordinate_key, validate_config, validate_public

MAP_SUMMARY_NAMES = ("capture_count", "complete_count", "missing_count")
GRAPH_SUMMARY_NAMES = ("complete", "missing", "act_index", "event_position", "node_count", "edge_count",
                       "boss_count", "min_row", "max_row", "min_col", "max_col") + tuple("type." + k for k in NODE_TYPES)
NODE_NAMES = ("row", "col", "starting", "boss", "current", "offered", "in_degree", "out_degree") + tuple("type." + k for k in NODE_TYPES)
EDGE_NAMES = tuple(prefix + name for prefix in ("from.", "to.") for name in NODE_NAMES)


def map_features(evidence):
    """Call only after validation; absence, Missing and Complete stay distinct."""
    graphs, owners = [], {}
    for event in evidence["events"]:
        payload = event["payload"]
        if payload["kind"] == "owner_started": owners[event["ownerOrdinal"]] = payload["actIndex"]
        if payload["kind"] != "map": continue
        capture = payload["currentMap"]
        complete = capture["status"] == "complete"
        numeric = [float(complete), float(not complete), _bounded(owners[event["ownerOrdinal"]], 3),
                   _bounded(event["eventOrdinal"], 100), _bounded(len(capture["nodes"]), 100),
                   _bounded(len(capture["edges"]), 100), _bounded(len(capture["bossNodes"]), 2)]
        if not complete:
            graphs.append((numeric + [0.] * (4 + len(NODE_TYPES)), [], [])); continue
        nodes = {coordinate_key(node["coordinate"]): node["nodeType"] for node in capture["nodes"]}
        pairs = [(coordinate_key(edge["from"]), coordinate_key(edge["to"])) for edge in capture["edges"]]
        start = coordinate_key(capture["startingNode"])
        bosses = {coordinate_key(value) for value in capture["bossNodes"]}
        current = coordinate_key(payload["current"])
        offered = {coordinate_key(option["coordinate"]) for option in payload["options"]}
        incoming, outgoing = {key: 0 for key in nodes}, {key: 0 for key in nodes}
        for a, b in pairs: incoming[b] += 1; outgoing[a] += 1
        entities = {key: [_bounded(key[0], 16), _bounded(key[1], 7), float(key == start), float(key in bosses),
                          float(key == current), float(key in offered), _bounded(incoming[key], 4), _bounded(outgoing[key], 4)]
                    + [float(kind == k) for k in NODE_TYPES] for key, kind in nodes.items()}
        rows, cols = [key[0] for key in nodes], [key[1] for key in nodes]
        summary = numeric + [_bounded(min(rows), 16), _bounded(max(rows), 16), _bounded(min(cols), 7), _bounded(max(cols), 7)]
        summary += [_bounded(sum(kind == k for kind in nodes.values()), 10) for k in NODE_TYPES]
        graphs.append((summary, list(entities.values()), [entities[a] + entities[b] for a, b in pairs]))
    summary = [_bounded(len(graphs), 10), _bounded(sum(g[0][0] for g in graphs), 10), _bounded(sum(g[0][1] for g in graphs), 10)]
    return summary, graphs


def public_map_features(public):
    normalized = canonical_public_input(public)
    anchor = normalized["controller_context"].get("anchor")
    return map_features(normalized["public_evidence"]), map_features(anchor["public_evidence"]) if anchor is not None else None


class StudentV5(nn.Module):
    def __init__(self, config):
        super().__init__()
        self.config = validate_config(config)
        self.mechanics = StudentV4(config["base_config"])
        base = config["base_config"]["base_config"]["base_config"]["base_config"]
        hidden = base["hidden_dim"]
        self.node_projection = nn.Sequential(nn.Linear(len(NODE_NAMES), hidden), nn.Tanh())
        self.edge_projection = nn.Sequential(nn.Linear(len(EDGE_NAMES), hidden), nn.Tanh())
        self.graph_projection = nn.Sequential(nn.Linear(len(GRAPH_SUMMARY_NAMES) + 2 * hidden, hidden), nn.Tanh())
        self.graph_encoder = nn.GRU(hidden, hidden, batch_first=True)
        self.map_summary = nn.Sequential(nn.Linear(len(MAP_SUMMARY_NAMES), hidden), nn.Tanh())
        self.channel = nn.Linear(3 * hidden, hidden)
        self.context = nn.Sequential(nn.Linear(2 * hidden + 1, hidden), nn.Tanh())
        self.adapter = nn.Sequential(nn.Linear(hidden + 5, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU())
        self.heads = nn.ModuleDict({name: nn.Linear(hidden, 1) for name in self.mechanics.heads})
        self.hp_distribution = nn.Linear(hidden, base["hp_bins"])
        self.plan_heads = nn.ModuleDict({name: nn.Linear(2 * hidden, 1) for name in PLAN_HEADS})

    @property
    def core(self): return self.mechanics.mechanics.core

    def _channel(self, features):
        summary, graphs = features
        summary_state = self.map_summary(self.core.tensor(summary))
        def pool(rows, projection):
            return projection(self.core.tensor(rows)).mean(dim=0) if rows else torch.zeros_like(summary_state)
        values = [self.graph_projection(torch.cat((self.core.tensor(s), pool(n, self.node_projection),
                   pool(e, self.edge_projection)))) for s, n, e in graphs]
        if values:
            encoded, last = self.graph_encoder(torch.stack(values).unsqueeze(0))
            terminal, mean = last[0, 0], encoded[0].mean(dim=0)
        else: terminal = mean = torch.zeros_like(summary_state)
        return self.channel(torch.cat((terminal, mean, summary_state)))

    def forward(self, public):
        validate_public(public, self.config)
        output = self.mechanics(_v4_mechanics_projection(public))
        root, anchor = public_map_features(public)
        root_context = self._channel(root)
        anchor_context = self._channel(anchor) if anchor is not None else torch.zeros_like(root_context)
        context = self.context(torch.cat((root_context, anchor_context, self.core.tensor([float(anchor is not None)]))))
        scores = torch.stack([output[name] for name in self.heads], dim=-1)
        hidden = self.adapter(torch.cat((scores, context.expand(len(scores), -1)), dim=-1))
        for name, head in self.heads.items(): output[name] = output[name] + head(hidden).squeeze(-1)
        output["hp_distribution"] = output["hp_distribution"] + self.hp_distribution(hidden)
        whole = torch.cat((context, hidden[output["legal_mask"]].mean(dim=0)))
        output["plan"] = {name: output["plan"][name] + head(whole).squeeze(-1) for name, head in self.plan_heads.items()}
        output["ranking_score"] = output["value"].masked_fill(~output["legal_mask"], -torch.inf)
        return output
