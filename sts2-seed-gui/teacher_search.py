"""Depth-limited expectimax for explicit global-decision branch trees.

The GUI supplies only a branch specification; the caller is responsible for
producing outcomes from the real CLI, shadow simulator, or another validated
transition service.  Outcome probabilities are explicit and are never
inferred from branch count.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from typing import Any


def _canonical(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def fingerprint(value: Any) -> str:
    return hashlib.sha256(_canonical(value).encode("utf-8")).hexdigest().upper()


@dataclass
class SearchResult:
    value: float
    best_actions: list[str]
    action_values: list[dict[str, Any]]
    nodes_evaluated: int


class SearchError(ValueError):
    pass


def root_from_cli_response(response: dict[str, Any], outcomes_by_action: dict[str, list[dict[str, Any]]] | None = None) -> dict[str, Any]:
    """Build a public action root from a CLI decision response.

    The response is treated as public data.  Outcome branches are supplied by
    the validated CLI/shadow provider through ``outcomes_by_action``.
    """
    if not isinstance(response, dict):
        raise SearchError("cli_response must be an object")
    decision = str(response.get("decision") or "unknown").lower()
    act = response.get("act", (response.get("context") or {}).get("act", 0))
    candidates: list[dict[str, Any]] = []
    raw = response.get("action_candidates")
    if not isinstance(raw, list):
        if decision == "map_select":
            raw = response.get("choices") or []
        elif decision in {"event_choice", "rest_site"}:
            raw = response.get("options") or []
        elif decision == "card_reward":
            raw = response.get("cards") or response.get("options") or []
        elif decision == "shop":
            raw = []
            for kind, key in (("card", "cards"), ("relic", "relics"), ("potion", "potions")):
                for entry in response.get(key) or []:
                    if isinstance(entry, dict):
                        enriched = dict(entry)
                        enriched["_shop_kind"] = kind
                        raw.append(enriched)
            if response.get("card_removal_cost") is not None:
                raw.append({"_shop_kind": "remove", "index": 0, "cost": response.get("card_removal_cost")})
        else:
            raw = []
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            continue
        action_id = item.get("action_id")
        if not action_id:
            if decision == "map_select" and "row" in item and "col" in item:
                action_id = f"map:{act}:{item['row']}:{item['col']}"
            elif decision == "event_choice":
                option_id = item.get("option_id", index)
                action_id = f"event:{response.get('event_id', 'unknown')}:option:{option_id}"
            elif decision == "rest_site":
                action_id = f"rest:{item.get('option_id', index)}"
            elif decision == "card_reward":
                action_id = f"reward:{item.get('instance_id') or item.get('id') or index}"
            elif decision == "shop":
                kind = item.get("_shop_kind", "offer")
                action_id = f"shop:{kind}:{item.get('offer_id') or item.get('id') or item.get('index', index)}"
            else:
                action_id = f"{decision}:{index}"
        action = dict(item)
        action["action_id"] = str(action_id)
        if not action.get("cli_action"):
            if decision == "map_select":
                action["cli_action"] = "select_map_node"
                action["args"] = {"row": item.get("row"), "col": item.get("col")}
            elif decision == "event_choice":
                action["cli_action"] = "choose_option"
                action["args"] = {"option_index": item.get("index", index)}
            elif decision == "rest_site":
                action["cli_action"] = "choose_option"
                action["args"] = {"option_index": item.get("index", index)}
            elif decision == "card_reward":
                action["cli_action"] = "select_card_reward"
                action["args"] = {"card_index": item.get("index", index)}
            elif decision == "shop":
                kind = item.get("_shop_kind", "card")
                action["cli_action"] = {"card": "buy_card", "relic": "buy_relic", "potion": "buy_potion", "remove": "remove_card"}.get(kind, "leave_room")
                field = {"card": "card_index", "relic": "relic_index", "potion": "potion_index"}.get(kind)
                action["args"] = {field: item.get("index", index)} if field else {}
        action["outcomes"] = (outcomes_by_action or {}).get(str(action_id), item.get("outcomes") or [])
        candidates.append(action)
    return {"state": response.get("player") or response.get("public_state") or {}, "actions": candidates}


def _terminal_value(node: dict[str, Any]) -> float | None:
    value = node.get("terminal_value", node.get("value"))
    return float(value) if isinstance(value, (int, float)) else None


def expectimax(node: dict[str, Any], depth: int = 1, discount: float = 1.0) -> SearchResult:
    if depth < 0:
        raise SearchError("depth must be non-negative")
    terminal = _terminal_value(node)
    node_reward = float(node.get("immediate_reward", 0.0)) if isinstance(node.get("immediate_reward", 0.0), (int, float)) else 0.0
    actions = node.get("actions") or []
    if terminal is not None and (depth == 0 or not actions):
        return SearchResult(node_reward + terminal, [], [], 1)
    if depth == 0:
        raise SearchError("non-terminal node reached at depth 0 without a value")
    if not isinstance(actions, list) or not actions:
        raise SearchError("non-terminal node requires a non-empty actions list")
    evaluated: list[dict[str, Any]] = []
    nodes = 1
    for action in actions:
        if not isinstance(action, dict) or not action.get("action_id"):
            raise SearchError("each action requires action_id")
        outcomes = action.get("outcomes") or []
        if not isinstance(outcomes, list) or not outcomes:
            raise SearchError(f"action {action.get('action_id')} requires outcomes")
        probabilities: list[float] = []
        for outcome in outcomes:
            if not isinstance(outcome, dict) or not isinstance(outcome.get("probability"), (int, float)):
                raise SearchError(f"action {action['action_id']} outcomes require explicit probability")
            probability = float(outcome["probability"])
            if probability < 0:
                raise SearchError("outcome probabilities must be non-negative")
            probabilities.append(probability)
        total = sum(probabilities)
        if abs(total - 1.0) > 1e-6:
            raise SearchError(f"action {action['action_id']} probabilities must sum to 1 (got {total})")
        expected = 0.0
        outcome_traces: list[dict[str, Any]] = []
        for outcome, probability in zip(outcomes, probabilities):
            child = outcome.get("next_node")
            outcome_value = outcome.get("value")
            if isinstance(child, dict):
                child_result = expectimax(child, depth - 1, discount)
                child_value = child_result.value
                nodes += child_result.nodes_evaluated
            elif isinstance(outcome_value, (int, float)):
                child_value = float(outcome_value)
                nodes += 1
            else:
                raise SearchError(f"outcome for {action['action_id']} requires next_node or value")
            expected += probability * child_value
            outcome_traces.append({"probability": probability, "value": child_value, "state_hash": fingerprint(child) if isinstance(child, dict) else None})
        evaluated.append({"action_id": str(action["action_id"]), "value": round(discount * (node_reward + expected), 8), "outcomes": outcome_traces})
    best_value = max(item["value"] for item in evaluated)
    best_actions = sorted(item["action_id"] for item in evaluated if item["value"] == best_value)
    return SearchResult(best_value, best_actions, sorted(evaluated, key=lambda item: item["action_id"]), nodes)


def search_payload(payload: dict[str, Any]) -> dict[str, Any]:
    root = payload.get("root")
    if not isinstance(root, dict):
        raise SearchError("root must be an object")
    result = expectimax(root, int(payload.get("depth", 1)), float(payload.get("discount", 1.0)))
    return {"value": result.value, "best_actions": result.best_actions, "action_values": result.action_values, "nodes_evaluated": result.nodes_evaluated, "root_state_hash": fingerprint(root)}
