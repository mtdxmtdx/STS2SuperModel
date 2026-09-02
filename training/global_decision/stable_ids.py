"""Stable IDs for global actions and synthetic card instances."""

from __future__ import annotations

from typing import Any, Mapping, Optional

from .contracts import stable_hash


def card_instance_id(semantic_id: str, ordinal: int) -> str:
    if not semantic_id:
        raise ValueError("semantic_id is required for a stable card instance ID")
    if ordinal < 0:
        raise ValueError("ordinal must be non-negative")
    return f"card:{semantic_id}:{ordinal:03d}"


def relic_instance_id(semantic_id: str, ordinal: int) -> str:
    if not semantic_id:
        raise ValueError("semantic_id is required for a stable relic instance ID")
    return f"relic:{semantic_id}:{ordinal:03d}"


def potion_instance_id(semantic_id: str, ordinal: int) -> str:
    if not semantic_id:
        raise ValueError("semantic_id is required for a stable potion instance ID")
    return f"potion:{semantic_id}:{ordinal:03d}"


def action_id(
    action_type: str,
    *,
    act: Optional[int] = None,
    row: Optional[int] = None,
    col: Optional[int] = None,
    snapshot_hash: Optional[str] = None,
    offer_id: Optional[str] = None,
    semantic_id: Optional[str] = None,
    option_id: Optional[str] = None,
    card_instance_id_value: Optional[str] = None,
) -> Optional[str]:
    """Build a canonical action ID; return None when the source lacks an ID."""

    kind = action_type.strip().lower()
    if kind == "route" and None not in (act, row, col):
        return f"route:map:{int(act)}:{int(row)}:{int(col)}"
    if kind in {"reward", "shop_card", "shop_relic", "shop_potion"}:
        if not snapshot_hash or not offer_id:
            return None
        prefix = {
            "reward": "reward:offer",
            "shop_card": "shop:card",
            "shop_relic": "shop:relic",
            "shop_potion": "shop:potion",
        }[kind]
        return f"{prefix}:{snapshot_hash}:{offer_id}"
    if kind == "campfire_option" and option_id:
        return f"campfire:option:{option_id}"
    if kind in {"reward_skip", "shop_leave", "leave", "proceed", "shop_remove", "campfire_rest"}:
        prefix = {
            "reward_skip": "reward:skip",
            "shop_leave": "shop:leave",
            "leave": "leave",
            "proceed": "proceed",
            "shop_remove": "shop:remove",
            "campfire_rest": "campfire:rest",
        }[kind]
        return prefix
    if kind == "campfire_smith" and card_instance_id_value:
        return f"campfire:smith:{card_instance_id_value}"
    if kind in {"event", "ancient"} and option_id:
        prefix = "event" if kind == "event" else "ancient"
        if semantic_id:
            return f"{prefix}:{semantic_id}:option:{option_id}"
    if kind == "boss_reward" and offer_id:
        return f"boss_reward:{offer_id}"
    return None


def candidate_features_hash(candidate: Mapping[str, Any]) -> str:
    """Hash semantic candidate fields, excluding transport/index-only fields."""

    ignored = {"candidate_index", "transport_args", "transport_action", "action_id"}
    return stable_hash({k: v for k, v in candidate.items() if k not in ignored})
