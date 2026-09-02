from __future__ import annotations

import json

import pytest

from training.global_decision.route_features import normalize_map_graph, reachable_nodes, route_features
from training.global_decision.synthetic_global_states import generate_dataset


def test_public_graph_features_and_stable_ids() -> None:
    state = generate_dataset(1, 20260831)[0]["state_public"]
    graph = normalize_map_graph(state["visible_map_graph"])
    assert graph["current"] == state["current_node"]
    assert reachable_nodes(state["visible_map_graph"], state["current_node"])
    path = tuple(graph["children"][graph["current"]])
    features = route_features(state, path, node_summaries={})
    for key in ("node_type_counts", "elite_count", "campfire_count", "shop_count", "path_length", "reachable_branch_count", "hp_ratio", "gold", "deck_health", "expected_hp_loss_proxy", "death_risk_proxy"):
        assert key in features
    assert all("seed" not in json.dumps(features).lower() for _ in [0])


def test_hidden_node_type_is_not_used_as_future_information() -> None:
    graph = {"current": "map:1:0:0", "nodes": [{"id": "map:1:0:0", "row": 0, "col": 0, "type": "Start", "visible": True}, {"id": "map:1:1:0", "row": 1, "col": 0, "type": "Elite", "visible": False}], "edges": [["map:1:0:0", "map:1:1:0"]]}
    normalized = normalize_map_graph(graph)
    assert normalized["nodes"]["map:1:1:0"]["type"] == "unknown"
