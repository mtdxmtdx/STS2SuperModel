"""Offline-only dispatcher for the global decision prototypes."""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .ancient_policy import rank_ancient_options
from .campfire_policy import rank_campfire_candidates
from .contracts import stable_hash, validate_public_payload
from .event_policy import rank_event_options
from .reward_heuristic import rank_offer
from .route_planner import RoutePlanner
from .shop_policy import rank_shop_candidates


DECISION_TYPES = ("route", "reward", "shop", "campfire", "event", "ancient")
QUALITY = "EstimatedByHeuristic"
SOURCE = "global-orchestrator-prototype"


def _public_candidates(state: Mapping[str, Any], fields: Sequence[str]) -> list[dict[str, Any]]:
    merged: dict[str, dict[str, Any]] = {}
    anonymous: list[dict[str, Any]] = []
    for field in fields:
        values = state.get(field, ()) or ()
        if isinstance(values, Mapping):
            values = list(values.values())
        if not isinstance(values, Sequence) or isinstance(values, (str, bytes)):
            continue
        for raw in values:
            if not isinstance(raw, Mapping):
                continue
            item = dict(raw)
            action_id = item.get("action_id")
            if action_id is None:
                anonymous.append(item)
            elif str(action_id) in merged:
                merged[str(action_id)].update(item)
            else:
                merged[str(action_id)] = item
    return sorted(list(merged.values()) + anonymous, key=lambda item: (str(item.get("action_id") or ""), str(item.get("candidate_index") or "")))


def _reward_candidates(state: Mapping[str, Any]) -> list[dict[str, Any]]:
    """Join visible reward display data to the stable legal-action rows."""
    legal_rows = [item for item in _public_candidates(state, ("legal_actions",)) if item.get("action_id") is not None]
    offers = [item for item in _public_candidates(state, ("visible_offers",)) if item.get("action_id") is not None or item.get("candidate_index") is not None or item.get("index") is not None]
    if not legal_rows:
        return _public_candidates(state, ("visible_offers",))
    by_position = {int(item.get("candidate_index", item.get("index", -1))): item for item in offers if str(item.get("candidate_index", item.get("index", ""))).lstrip("-").isdigit()}
    result: list[dict[str, Any]] = []
    for row in legal_rows:
        item = dict(by_position.get(int(row.get("candidate_index", -1)), {}))
        item.update(row)
        result.append(item)
    return result


def _state_hash(state: Mapping[str, Any]) -> str:
    supplied = state.get("state_public_hash")
    if supplied:
        return str(supplied)
    allowed = ("schema_version", "character", "act", "floor", "current_node", "current_room_type", "hp", "max_hp", "gold", "deck_public", "relic_public", "potion_public", "visible_map_graph", "visible_options", "visible_offers", "legal_actions")
    return stable_hash({key: state.get(key) for key in allowed})


def _uniform(item: Mapping[str, Any], *, source: str = SOURCE, reason: str | None = None, confidence: float | None = None) -> dict[str, Any]:
    output = dict(item)
    output.setdefault("action_id", None)
    output["stable_id_missing"] = output.get("action_id") is None
    output["legal"] = bool(output.get("legal", True))
    output.setdefault("score", 0.0)
    output.setdefault("rank", 0)
    output.setdefault("confidence", 0.1 if output["stable_id_missing"] else (0.35 if confidence is None else confidence))
    output.setdefault("reason", reason or "prototype candidate score")
    output["quality"] = QUALITY
    output["source"] = source
    output["reliable"] = False
    return output


class GlobalDecisionOrchestrator:
    """Dispatch public states to an existing prototype without side effects."""

    def __init__(self, *, route_planner: RoutePlanner | None = None) -> None:
        self.route_planner = route_planner or RoutePlanner()

    def decide(self, state: Mapping[str, Any], decision_type: str | None = None) -> dict[str, Any]:
        validate_public_payload(state)
        kind = str(decision_type or state.get("decision_type") or "").lower()
        if kind not in DECISION_TYPES:
            raise ValueError(f"unsupported decision_type: {kind or '<missing>'}")
        if kind == "route":
            candidates = self._route_candidates(state)
        elif kind == "reward":
            candidates = [_uniform(item, source="global-reward-prototype") for item in rank_offer(state, _reward_candidates(state))]
        elif kind == "shop":
            candidates = [_uniform(item) for item in rank_shop_candidates(state)]
        elif kind == "campfire":
            candidates = [_uniform(item) for item in rank_campfire_candidates(state)]
        elif kind == "event":
            candidates = [_uniform(item) for item in rank_event_options(state)]
        else:
            candidates = [_uniform(item) for item in rank_ancient_options(state)]
        candidates.sort(key=lambda item: (int(item.get("rank", 0) or 0), str(item.get("action_id") or "")))
        legal_mask = [bool(item.get("legal", False)) for item in candidates]
        selected = next((item for item in candidates if item.get("legal", False)), None)
        return {
            "decision_type": kind,
            "state_public_hash": _state_hash(state),
            "candidates": candidates,
            "selected_action_id": selected.get("action_id") if selected else None,
            "score": selected.get("score") if selected else None,
            "rank": selected.get("rank") if selected else None,
            "confidence": selected.get("confidence") if selected else None,
            "reason": selected.get("reason") if selected else "no legal candidate",
            "legal_mask": legal_mask,
            "quality": QUALITY,
            "source": SOURCE,
            "reliable": False,
            "model_stage": "prototype",
        }

    def _route_candidates(self, state: Mapping[str, Any]) -> list[dict[str, Any]]:
        plan = self.route_planner.plan(state)
        legal_by_action = {str(item.get("action_id")): bool(item.get("legal", False)) for item in _public_candidates(state, ("legal_actions",)) if item.get("action_id") is not None}
        result: list[dict[str, Any]] = []
        for rank, candidate in enumerate(plan.candidates, 1):
            path_key = stable_hash(list(candidate.path))[:16]
            action_id = f"route:path:{path_key}"
            next_action = f"route:{candidate.path[0]}" if candidate.path else None
            legal = legal_by_action.get(action_id, legal_by_action.get(next_action, False))
            summaries = list(candidate.summaries.values())
            confidence = min((float(item.get("confidence", 0.25)) for item in summaries), default=0.25)
            result.append(_uniform({
                "action_id": action_id,
                "transport_action": next_action,
                "path": list(candidate.path),
                "legal": legal,
                "score": candidate.score,
                "rank": rank,
                "confidence": confidence,
                "reason": "public-map route candidate",
            }))
        return result


def orchestrate(state: Mapping[str, Any], decision_type: str | None = None) -> dict[str, Any]:
    return GlobalDecisionOrchestrator().decide(state, decision_type)
