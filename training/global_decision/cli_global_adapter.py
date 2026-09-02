"""Read-only adapter for the v0.111 headless CLI decision JSON.

The adapter never guesses an ID from localized text.  A decision remains
incomplete when the current CLI response lacks a stable offer/event ID; the
caller can still inspect the transport index, but it will not be eligible for
the global prototype's complete-action gate.
"""

from __future__ import annotations

from copy import deepcopy
from typing import Any, Mapping, Optional

from .contracts import GlobalActionCandidate, GlobalOfferSnapshot, GlobalRunStatePublic, stable_hash
from .stable_ids import action_id, candidate_features_hash


def _context(payload: Mapping[str, Any]) -> dict[str, Any]:
    context = deepcopy(payload.get("context") or {})
    if not isinstance(context, dict):
        context = {}
    return context


def _player(payload: Mapping[str, Any]) -> dict[str, Any]:
    player = deepcopy(payload.get("player") or {})
    return player if isinstance(player, dict) else {}


def _decision_type(payload: Mapping[str, Any]) -> str:
    value = payload.get("decision") or payload.get("decision_type") or ""
    return str(value).strip().lower()


def _base_state(payload: Mapping[str, Any]) -> dict[str, Any]:
    context = _context(payload)
    player = _player(payload)
    act = int(payload.get("act", context.get("act", 1)) or 1)
    floor = int(payload.get("floor", context.get("floor", 0)) or 0)
    return {
        "schema_version": "global-public-v1",
        "character": str(context.get("character", player.get("character", "Unknown"))),
        "act": act,
        "floor": floor,
        "ascension": int(payload.get("ascension", context.get("ascension", 0)) or 0),
        "current_node": context.get("current_node"),
        "current_room_type": str(context.get("room_type", payload.get("decision", "unknown"))),
        "hp": float(player.get("hp", player.get("current_hp", 0)) or 0),
        "max_hp": float(player.get("max_hp", 0) or 0),
        "gold": int(player.get("gold", 0) or 0),
        "deck_public": deepcopy(player.get("deck") or []),
        "relic_public": deepcopy(player.get("relics") or []),
        "potion_public": deepcopy(player.get("potions") or []),
        "visible_map_graph": deepcopy(payload.get("map") or payload.get("visible_map_graph") or {}),
        "visible_encounter_profile": deepcopy(payload.get("encounter_profile") or payload.get("visible_encounter_profile") or {}),
        "visible_options": deepcopy(payload.get("options") or []),
        "visible_offers": [],
        "public_history": deepcopy(payload.get("public_history") or []),
        "combat_summary": None,
        "field_completeness": {},
        "provenance": {"source": "cli", "adapter_version": "global-cli-adapter-v1"},
    }


def _make_candidate(
    *,
    action_type: str,
    semantic_id: Optional[str],
    transport_action: str,
    transport_args: Mapping[str, Any],
    index: int,
    role: str,
    snapshot_hash: str,
    parent_decision_id: str,
    offer_id: Optional[str] = None,
    option_id: Optional[str] = None,
    act: Optional[int] = None,
    row: Optional[int] = None,
    col: Optional[int] = None,
    legal: bool = True,
    restriction_reason: Optional[str] = None,
    missing: Optional[list[str]] = None,
) -> GlobalActionCandidate:
    aid = action_id(
        action_type,
        act=act,
        row=row,
        col=col,
        snapshot_hash=snapshot_hash,
        offer_id=offer_id,
        semantic_id=semantic_id,
        option_id=option_id,
    )
    descriptor = {
        "action_type": action_type,
        "semantic_id": semantic_id,
        "offer_id": offer_id,
        "option_id": option_id,
        "candidate_role": role,
    }
    if aid is None and missing is not None:
        missing.append(f"candidates[{index}].stable_id")
    return GlobalActionCandidate(
        action_id=aid,
        action_type=action_type,
        semantic_id=semantic_id,
        transport_action=transport_action,
        transport_args=dict(transport_args),
        legal=legal,
        restriction_reason=restriction_reason,
        candidate_index=index,
        offer_snapshot_hash=snapshot_hash,
        parent_decision_id=parent_decision_id,
        candidate_role=role,
        candidate_semantic_features_hash=candidate_features_hash(descriptor),
        source_confidence="observed_cli" if aid else "incomplete_cli",
    )


def _candidate_descriptors(payload: Mapping[str, Any], decision: str) -> list[dict[str, Any]]:
    if decision == "map_select":
        return [
            {"action_type": "route", "semantic_id": choice.get("type"), "row": choice.get("row"), "col": choice.get("col")}
            for choice in payload.get("choices", [])
        ]
    if decision == "card_reward":
        return [
            {"action_type": "reward", "semantic_id": card.get("id"), "offer_id": card.get("offer_id")}
            for card in payload.get("cards", [])
        ]
    if decision in {"rest_site", "event_choice", "ancient_choice"}:
        values = payload.get("options", [])
        return [
            {
                "action_type": "event" if decision == "event_choice" else "ancient" if decision == "ancient_choice" else "campfire_option",
                "semantic_id": payload.get("event_id") if decision != "rest_site" else None,
                "option_id": option.get("option_id"),
            }
            for option in values
        ]
    if decision == "shop":
        result: list[dict[str, Any]] = []
        for item_type, key in (("shop_card", "cards"), ("shop_relic", "relics"), ("shop_potion", "potions")):
            result.extend(
                {"action_type": item_type, "semantic_id": item.get("id"), "offer_id": item.get("offer_id")}
                for item in payload.get(key, [])
            )
        return result
    return []


def adapt_cli_decision(
    payload: Mapping[str, Any],
    *,
    game_metadata: Optional[Mapping[str, Any]] = None,
) -> dict[str, Any]:
    """Normalize one CLI decision response into public state and offer data."""

    decision = _decision_type(payload)
    if not decision:
        raise ValueError("CLI payload has no decision type")
    base = _base_state(payload)
    missing: list[str] = []
    context = _context(payload)
    player = _player(payload)
    if not context.get("character") and not player.get("character"):
        missing.append("context.character")
    if payload.get("act") is None and context.get("act") is None:
        missing.append("context.act")
    if payload.get("floor") is None and context.get("floor") is None:
        missing.append("context.floor")
    if player.get("hp") is None and player.get("current_hp") is None:
        missing.append("player.hp")
    for key in ("max_hp", "gold"):
        if player.get(key) is None:
            missing.append(f"player.{key}")
    for key in ("deck", "relics", "potions"):
        if key not in player:
            missing.append(f"player.{key}")
    descriptors = _candidate_descriptors(payload, decision)
    snapshot_hash = stable_hash(
        {
            "decision": decision,
            "context": _context(payload),
            "candidates": descriptors,
        }
    )
    parent_id = str(payload.get("decision_id") or snapshot_hash)
    candidates: list[GlobalActionCandidate] = []
    if decision == "map_select":
        for index, choice in enumerate(payload.get("choices", [])):
            row, col = choice.get("row"), choice.get("col")
            if row is None or col is None:
                missing.append(f"choices[{index}].row_col")
            candidates.append(
                _make_candidate(
                    action_type="route",
                    semantic_id=choice.get("type"),
                    transport_action="select_map_node",
                    transport_args={"row": row, "col": col},
                    index=index,
                    role="route",
                    snapshot_hash=snapshot_hash,
                    parent_decision_id=parent_id,
                    act=int(base["act"]),
                    row=int(row) if row is not None else None,
                    col=int(col) if col is not None else None,
                    missing=missing,
                )
            )
    elif decision == "card_reward":
        for index, card in enumerate(payload.get("cards", [])):
            semantic_id = card.get("id")
            offer_id = card.get("offer_id")
            if not semantic_id:
                missing.append(f"cards[{index}].id")
            if not offer_id:
                missing.append(f"cards[{index}].offer_id")
            candidates.append(
                _make_candidate(
                    action_type="reward",
                    semantic_id=semantic_id,
                    offer_id=offer_id,
                    transport_action="select_card_reward",
                    transport_args={"index": card.get("index", index)},
                    index=index,
                    role="offer",
                    snapshot_hash=snapshot_hash,
                    parent_decision_id=parent_id,
                    missing=missing,
                )
            )
        if payload.get("can_skip", True):
            candidates.append(
                _make_candidate(
                    action_type="reward_skip",
                    semantic_id=None,
                    transport_action="skip_card_reward",
                    transport_args={},
                    index=len(candidates),
                    role="skip",
                    snapshot_hash=snapshot_hash,
                    parent_decision_id=parent_id,
                )
            )
    elif decision == "shop":
        for item_type, key, transport in (
            ("shop_card", "cards", "buy_card"),
            ("shop_relic", "relics", "buy_relic"),
            ("shop_potion", "potions", "buy_potion"),
        ):
            for item in payload.get(key, []):
                index = len(candidates)
                semantic_id, offer_id = item.get("id"), item.get("offer_id")
                if not semantic_id:
                    missing.append(f"{key}[{index}].id")
                if not offer_id:
                    missing.append(f"{key}[{index}].offer_id")
                candidates.append(
                    _make_candidate(
                        action_type=item_type,
                        semantic_id=semantic_id,
                        offer_id=offer_id,
                        transport_action=transport,
                        transport_args={"index": item.get("index", index)},
                        index=index,
                        role="offer",
                        snapshot_hash=snapshot_hash,
                        parent_decision_id=parent_id,
                        missing=missing,
                    )
                )
        removal = payload.get("card_removal_cost")
        if removal is not None:
            candidates.append(
                _make_candidate(
                    action_type="shop_remove",
                    semantic_id=None,
                    transport_action="remove_card",
                    transport_args={"cost": removal},
                    index=len(candidates),
                    role="option",
                    snapshot_hash=snapshot_hash,
                    parent_decision_id=parent_id,
                )
            )
        candidates.append(
            _make_candidate(
                action_type="shop_leave",
                semantic_id=None,
                transport_action="leave_room",
                transport_args={},
                index=len(candidates),
                role="leave",
                snapshot_hash=snapshot_hash,
                parent_decision_id=parent_id,
            )
        )
    elif decision in {"proceed", "leave", "leave_room"}:
        normalized = "leave" if decision in {"leave", "leave_room"} else "proceed"
        candidates.append(
            _make_candidate(
                action_type=normalized,
                semantic_id=None,
                transport_action="leave_room" if normalized == "leave" else "proceed",
                transport_args={},
                index=0,
                role="leave" if normalized == "leave" else "option",
                snapshot_hash=snapshot_hash,
                parent_decision_id=parent_id,
            )
        )
    elif decision in {"rest_site", "event_choice", "ancient_choice"}:
        event_id = payload.get("event_id")
        if decision != "rest_site" and not event_id:
            missing.append("event_id")
        for index, option in enumerate(payload.get("options", [])):
            option_id = option.get("option_id")
            if not option_id:
                missing.append(f"options[{index}].option_id")
            if decision == "rest_site":
                atype, transport, role = "campfire_option", "choose_rest_option", "option"
            elif decision == "event_choice":
                atype, transport, role = "event", "choose_option", "option"
            else:
                atype, transport, role = "ancient", "choose_option", "option"
            candidates.append(
                _make_candidate(
                    action_type=atype,
                    semantic_id=event_id,
                    option_id=option_id,
                    transport_action=transport,
                    transport_args={"index": option.get("index", index), "option_id": option_id},
                    index=index,
                    role=role,
                    snapshot_hash=snapshot_hash,
                    parent_decision_id=parent_id,
                    missing=missing,
                )
            )
    else:
        raise ValueError(f"unsupported global decision: {decision}")

    legal_complete = bool(candidates) and not missing and all(candidate.action_id for candidate in candidates)
    base["visible_offers"] = [dict(candidate.to_dict()) for candidate in candidates]
    base["legal_actions_complete"] = legal_complete
    base["field_completeness"] = {
        "legal_actions_complete": legal_complete,
        "missing_fields": sorted(set(missing)),
    }
    if game_metadata:
        base["provenance"].update(dict(game_metadata))
    base["state_public_hash"] = stable_hash(base)
    state = GlobalRunStatePublic.from_dict(base)
    snapshot = GlobalOfferSnapshot(
        offer_snapshot_hash=snapshot_hash,
        decision_type=decision,
        candidates=tuple(candidates),
        candidate_order=tuple(candidate.action_id or f"missing:{candidate.candidate_index}" for candidate in candidates),
        visible_context_hash=stable_hash(_context(payload)),
        legal_actions_complete=legal_complete,
        source="cli",
    )
    return {
        "state": state.to_dict(),
        "offer_snapshot": snapshot.to_dict(),
        "legal_actions_complete": legal_complete,
        "missing_fields": sorted(set(missing)),
    }


normalize_cli_response = adapt_cli_decision
