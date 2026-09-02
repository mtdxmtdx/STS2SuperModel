"""Public-map route features for the deterministic route prototype.

Only the map graph supplied by the caller is inspected.  Nodes marked
``visible=false`` keep their topology/ID but their room type is treated as
unknown, so a seed cannot leak future room contents into a route feature.
"""

from __future__ import annotations

from collections import Counter, defaultdict, deque
from typing import Any, Iterable, Mapping, Sequence

from .contracts import validate_public_payload
from .deck_health import deck_health


ROOM_TYPES = ("combat", "elite", "campfire", "shop", "event", "treasure", "boss", "unknown")
_ALIASES = {
    "combat": "combat", "enemy": "combat", "normal": "combat", "normalcombat": "combat",
    "elite": "elite", "elitenode": "elite", "elitecombat": "elite",
    "restsite": "campfire", "campfire": "campfire", "rest": "campfire",
    "shop": "shop", "merchant": "shop", "event": "event", "unknown": "unknown",
    "treasure": "treasure", "chest": "treasure", "boss": "boss", "bosssite": "boss",
    "start": "start", "map": "unknown",
}


def canonical_room_type(value: Any) -> str:
    text = "".join(ch for ch in str(value or "unknown").lower() if ch.isalnum())
    return _ALIASES.get(text, "unknown")


def _node_items(graph: Mapping[str, Any]) -> Iterable[Mapping[str, Any]]:
    nodes = graph.get("nodes", [])
    if isinstance(nodes, Mapping):
        nodes = list(nodes.values())
    if isinstance(nodes, Sequence) and not isinstance(nodes, (str, bytes)):
        for item in nodes:
            if isinstance(item, Mapping):
                yield item
    rows = graph.get("rows", [])
    if isinstance(rows, Sequence) and not isinstance(rows, (str, bytes)):
        for row in rows:
            if isinstance(row, Sequence) and not isinstance(row, (str, bytes)):
                for item in row:
                    if isinstance(item, Mapping):
                        yield item


def _edge_pair(edge: Any) -> tuple[str, str] | None:
    if isinstance(edge, Mapping):
        source = edge.get("from", edge.get("source", edge.get("parent")))
        target = edge.get("to", edge.get("target", edge.get("child")))
        if source is not None and target is not None:
            return str(source), str(target)
    if isinstance(edge, Sequence) and not isinstance(edge, (str, bytes)) and len(edge) >= 2:
        return str(edge[0]), str(edge[1])
    return None


def normalize_map_graph(visible_map_graph: Mapping[str, Any]) -> dict[str, Any]:
    """Normalize a public graph without consulting seed or hidden state."""

    validate_public_payload(visible_map_graph)
    nodes: dict[str, dict[str, Any]] = {}
    for ordinal, item in enumerate(_node_items(visible_map_graph)):
        node_id = str(item.get("id") or item.get("node_id") or f"map:unknown:{ordinal}")
        visible = bool(item.get("visible", True))
        nodes[node_id] = {
            "id": node_id,
            "row": int(item.get("row", 0) or 0),
            "col": int(item.get("col", ordinal) or 0),
            "type": canonical_room_type(item.get("type")) if visible else "unknown",
            "visible": visible,
            "children": item.get("children", ()),
        }
    children: dict[str, set[str]] = defaultdict(set)
    for edge in visible_map_graph.get("edges", []) or []:
        pair = _edge_pair(edge)
        if pair:
            children[pair[0]].add(pair[1])
            nodes.setdefault(pair[0], {"id": pair[0], "row": 0, "col": 0, "type": "unknown", "visible": False})
            nodes.setdefault(pair[1], {"id": pair[1], "row": 0, "col": 0, "type": "unknown", "visible": False})
    for node_id, item in nodes.items():
        nested_children = item.get("children")
        if isinstance(nested_children, Sequence) and not isinstance(nested_children, (str, bytes)):
            for child in nested_children:
                if isinstance(child, Mapping):
                    child_id = child.get("id") or child.get("node_id")
                else:
                    child_id = child
                if child_id is not None:
                    children[node_id].add(str(child_id))
    ordered_children = {
        node_id: tuple(sorted(values, key=lambda child: (nodes[child]["row"], nodes[child]["col"], child)))
        for node_id, values in children.items()
    }
    current = visible_map_graph.get("current") or visible_map_graph.get("current_node")
    return {"nodes": nodes, "children": ordered_children, "current": str(current) if current is not None else None}


def reachable_nodes(visible_map_graph: Mapping[str, Any], current_node: str | None = None) -> tuple[str, ...]:
    graph = normalize_map_graph(visible_map_graph)
    start = current_node or graph["current"]
    if not start or start not in graph["nodes"]:
        return ()
    seen: set[str] = {start}
    queue: deque[str] = deque([start])
    while queue:
        node_id = queue.popleft()
        for child in graph["children"].get(node_id, ()):
            if child not in seen:
                seen.add(child)
                queue.append(child)
    return tuple(sorted(seen, key=lambda node: (graph["nodes"][node]["row"], graph["nodes"][node]["col"], node)))


def route_features(
    state: Mapping[str, Any],
    path: Sequence[str],
    *,
    node_summaries: Mapping[str, Mapping[str, Any]] | None = None,
) -> dict[str, Any]:
    """Build features for one public candidate path."""

    graph = normalize_map_graph(state.get("visible_map_graph") or {})
    selected_nodes = [graph["nodes"][node_id] for node_id in path if node_id in graph["nodes"]]
    counts = Counter(node["type"] if node["type"] in ROOM_TYPES else "unknown" for node in selected_nodes)
    summaries = node_summaries or {}
    expected_loss = sum(float(summaries.get(node_id, {}).get("expected_hp_loss_proxy", 0.0) or 0.0) for node_id in path)
    survival = 1.0
    for node_id in path:
        risk = float(summaries.get(node_id, {}).get("death_risk_proxy", 0.0) or 0.0)
        survival *= max(0.0, 1.0 - min(1.0, max(0.0, risk)))
    profile = state.get("visible_encounter_profile") or {}
    hp = float(state.get("hp", 0) or 0)
    max_hp = float(state.get("max_hp", 0) or 0)
    return {
        "node_type_counts": {room: int(counts.get(room, 0)) for room in ROOM_TYPES},
        "elite_count": int(counts.get("elite", 0)),
        "campfire_count": int(counts.get("campfire", 0)),
        "shop_count": int(counts.get("shop", 0)),
        "path_length": len(path),
        "reachable_branch_count": len(graph["children"].get(str(state.get("current_node") or graph.get("current") or ""), ())),
        "hp_ratio": hp / max_hp if max_hp > 0 else 0.0,
        "gold": int(state.get("gold", 0) or 0),
        "deck_health": deck_health(state),
        "expected_hp_loss_proxy": round(expected_loss, 6),
        "death_risk_proxy": round(1.0 - survival, 6),
        "visible_enemy_count": int(profile.get("enemy_count", 0) or 0),
    }


def path_sort_key(path: Sequence[str], graph: Mapping[str, Any]) -> tuple[Any, ...]:
    return tuple((graph["nodes"].get(node, {}).get("row", 0), graph["nodes"].get(node, {}).get("col", 0), node) for node in path)
