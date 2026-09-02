"""Public-observation Ancient option ranking prototype."""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .contracts import validate_public_payload
from .event_policy import score_public_option


def _candidates(state: Mapping[str, Any]) -> list[dict[str, Any]]:
    merged: dict[str, dict[str, Any]] = {}
    anonymous: list[dict[str, Any]] = []
    for field in ("visible_options", "legal_actions"):
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
            ancient_id = item.get("ancient_id") or state.get("ancient_id")
            option_id = item.get("option_id")
            if action_id is None and ancient_id is not None and option_id is not None:
                item["action_id"] = f"ancient:{ancient_id}:option:{option_id}"
                action_id = item["action_id"]
            if action_id is None:
                anonymous.append(item)
            elif str(action_id) in merged:
                merged[str(action_id)].update(item)
            else:
                merged[str(action_id)] = item
    return sorted(list(merged.values()) + anonymous, key=lambda item: (str(item.get("action_id") or ""), str(item.get("option_id") or "")))


def rank_ancient_options(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> list[dict[str, Any]]:
    validate_public_payload(state)
    source = list(candidates) if candidates is not None else _candidates(state)
    scored = [score_public_option(state, item, domain="ancient") for item in source]
    scored.sort(key=lambda item: (not item["legal"], -item["score"], str(item.get("action_id") or "")))
    for rank, item in enumerate(scored, 1):
        item["rank"] = rank
    return scored


def select_ancient_option(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> dict[str, Any] | None:
    return next((item for item in rank_ancient_options(state, candidates) if item["legal"]), None)
