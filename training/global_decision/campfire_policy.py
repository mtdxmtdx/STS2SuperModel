"""Deterministic public-state CampfirePolicy prototype."""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .contracts import validate_public_payload
from .deck_features import card_tags
from .deck_health import deck_health


QUALITY = "EstimatedByHeuristic"
SOURCE = "global-shop-campfire-prototype"


def _kind(candidate: Mapping[str, Any]) -> str:
    text = str(candidate.get("action_type", candidate.get("candidate_role", "unknown"))).lower().replace(":", "_")
    role = str(candidate.get("candidate_role", "")).lower()
    if "rest" in text or role == "rest":
        return "rest"
    if "smith" in text or "upgrade" in text or role in {"smith", "upgrade"}:
        return "smith"
    if "leave" in text or role == "leave":
        return "leave"
    return "unknown"


def _stable_key(candidate: Mapping[str, Any]) -> tuple[str, str, str]:
    return (str(candidate.get("action_id") or ""), str(candidate.get("card_instance_id") or ""), _kind(candidate))


def _candidate_list(state: Mapping[str, Any]) -> list[dict[str, Any]]:
    merged: dict[str, dict[str, Any]] = {}
    anonymous: list[dict[str, Any]] = []
    for source_name in ("visible_options", "legal_actions"):
        values = state.get(source_name, ()) or ()
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
    return sorted(list(merged.values()) + anonymous, key=_stable_key)


def _target_card(state: Mapping[str, Any], candidate: Mapping[str, Any]) -> Mapping[str, Any] | None:
    target_id = candidate.get("card_instance_id") or candidate.get("target_id")
    for card in state.get("deck_public", ()) or ():
        if not isinstance(card, Mapping):
            continue
        if target_id and str(card.get("card_instance_id") or card.get("instance_id")) == str(target_id):
            return card
    embedded = candidate.get("card")
    return embedded if isinstance(embedded, Mapping) else None


def _result(candidate: Mapping[str, Any], score: float, reason: str, confidence: float, semantic_status: str = "known") -> dict[str, Any]:
    return {
        "action_id": candidate.get("action_id"),
        "stable_id_missing": candidate.get("action_id") is None,
        "action_type": candidate.get("action_type"),
        "candidate_role": candidate.get("candidate_role"),
        "card_instance_id": candidate.get("card_instance_id") or candidate.get("target_id"),
        "legal": bool(candidate.get("legal", True)),
        "score": round(float(score), 6),
        "reason": reason,
        "confidence": round(max(0.0, min(1.0, confidence)), 6),
        "semantic_status": semantic_status,
        "quality": QUALITY,
        "source": SOURCE,
        "reliable": False,
    }


def score_campfire_candidate(state: Mapping[str, Any], candidate: Mapping[str, Any]) -> dict[str, Any]:
    validate_public_payload(state)
    validate_public_payload(candidate)
    kind = _kind(candidate)
    hp = float(state.get("hp", 0) or 0)
    max_hp = max(float(state.get("max_hp", 0) or 0), 1.0)
    hp_ratio = hp / max_hp
    health = deck_health(state)
    if kind == "rest":
        # Recovery value increases smoothly as HP falls; no hard threshold.
        score = 0.55 + 1.6 * max(0.0, 0.65 - hp_ratio) + 0.25 * (1.0 - hp_ratio)
        return _result(candidate, score, "recovery value rises with missing HP", 0.85)
    if kind == "smith":
        card = _target_card(state, candidate)
        if card is None:
            return _result(candidate, 0.0, "unknown smith target", 0.1, "Uncalculable")
        upgrade = float(card.get("upgrade_level", card.get("upgrade", 0)) or 0)
        if upgrade > 0:
            return _result(candidate, 0.08 - 0.05 * upgrade, "target is already upgraded", 0.65)
        tags = set(card_tags(card)) | {str(x).lower() for x in card.get("tags", [])}
        job_tags = {"attack", "frontload", "aoe", "block", "mitigation", "scaling", "draw", "energy"}
        key_card_bonus = 0.45 if tags & job_tags else 0.15
        score = key_card_bonus + 0.25 * max(0.0, 0.55 - health["upgrade_density"]) + 0.10 * hp_ratio
        return _result(candidate, score, "upgrades an unupgraded card that fills a deck-health job", 0.78 if tags & job_tags else 0.5)
    if kind == "leave":
        return _result(candidate, 0.0, "leaves campfire resources unused", 0.95)
    return _result(candidate, -0.1, "unknown campfire action", 0.1, "Uncalculable")


def rank_campfire_candidates(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> list[dict[str, Any]]:
    validate_public_payload(state)
    source = list(candidates) if candidates is not None else _candidate_list(state)
    scored = [score_campfire_candidate(state, item) for item in source]
    scored.sort(key=lambda item: (not item["legal"], -item["score"], str(item.get("action_id") or "")))
    for rank, item in enumerate(scored, 1):
        item["rank"] = rank
    return scored


def select_campfire_action(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> dict[str, Any] | None:
    return next((item for item in rank_campfire_candidates(state, candidates) if item["legal"]), None)
