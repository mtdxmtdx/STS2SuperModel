"""Deterministic public-state ShopPolicy prototype."""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .contracts import validate_public_payload
from .deck_features import card_tags
from .deck_health import current_jobs, deck_health


QUALITY = "EstimatedByHeuristic"
SOURCE = "global-shop-campfire-prototype"


def _action_type(candidate: Mapping[str, Any]) -> str:
    return str(candidate.get("action_type", candidate.get("candidate_role", "unknown"))).lower().replace("-", "_")


def _kind(candidate: Mapping[str, Any]) -> str:
    text = _action_type(candidate).replace(":", "_")
    role = str(candidate.get("candidate_role", "")).lower().replace(":", "_")
    if "leave" in text or role == "leave":
        return "leave"
    if "remove" in text or role == "remove":
        return "remove"
    if "potion" in text or role == "potion":
        return "buy_potion"
    if "relic" in text or role == "relic":
        return "buy_relic"
    if "card" in text or role in {"card", "offer", "buy_card"}:
        return "buy_card"
    return "unknown"


def _price(candidate: Mapping[str, Any]) -> float:
    for key in ("price", "gold_cost", "purchase_cost", "cost"):
        value = candidate.get(key)
        if value is not None:
            try:
                return max(0.0, float(value))
            except (TypeError, ValueError):
                pass
    return 0.0


def _public_count(state: Mapping[str, Any], field: str, fallback: str) -> int:
    value = state.get(field)
    if value is not None:
        try:
            return max(0, int(value))
        except (TypeError, ValueError):
            pass
    items = state.get(fallback, ()) or ()
    return len(items) if isinstance(items, Sequence) and not isinstance(items, (str, bytes)) else 0


def _stable_sort_key(candidate: Mapping[str, Any]) -> tuple[str, str, str]:
    return (str(candidate.get("action_id") or ""), str(candidate.get("semantic_id") or ""), _kind(candidate))


def _candidate_list(state: Mapping[str, Any]) -> list[dict[str, Any]]:
    """Merge visible offers/options with legal actions by stable action ID."""

    merged: dict[str, dict[str, Any]] = {}
    anonymous: list[dict[str, Any]] = []
    for source_name in ("visible_offers", "visible_options", "legal_actions"):
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
                continue
            key = str(action_id)
            if key in merged:
                old = merged[key]
                old.update(item)
            else:
                merged[key] = item
    # Missing stable IDs remain visible and explicitly unresolved; they are
    # never replaced by an index-derived identifier.
    result = list(merged.values()) + anonymous
    return sorted(result, key=_stable_sort_key)


def _base_result(candidate: Mapping[str, Any], score: float, reason: str, confidence: float, semantic_status: str = "known") -> dict[str, Any]:
    return {
        "action_id": candidate.get("action_id"),
        "stable_id_missing": candidate.get("action_id") is None,
        "action_type": candidate.get("action_type"),
        "candidate_role": candidate.get("candidate_role"),
        "semantic_id": candidate.get("semantic_id"),
        "legal": bool(candidate.get("legal", True)),
        "score": round(float(score), 6),
        "reason": reason,
        "confidence": round(max(0.0, min(1.0, confidence)), 6),
        "semantic_status": semantic_status,
        "quality": QUALITY,
        "source": SOURCE,
        "reliable": False,
    }


def score_shop_candidate(state: Mapping[str, Any], candidate: Mapping[str, Any]) -> dict[str, Any]:
    """Score one shop candidate without changing the supplied legality."""

    validate_public_payload(state)
    validate_public_payload(candidate)
    kind = _kind(candidate)
    gold = float(state.get("gold", 0) or 0)
    price = _price(candidate)
    health = deck_health(state)
    jobs = current_jobs(state)
    tags = set(card_tags(candidate)) | {str(tag).lower() for tag in candidate.get("tags", [])}
    if kind == "leave":
        return _base_result(candidate, 0.0, "preserves resources", 0.95)
    if kind == "remove":
        score = 0.35 * health["dead_draw_rate"] + 0.12 * min(1.0, health["deck_size"] / 20.0) - 0.01 * price
        return _base_result(candidate, score, "removes dilution or dead draw burden", 0.85)
    if kind == "buy_card":
        tag_job = {"attack": "frontload", "frontload": "frontload", "aoe": "aoe", "block": "block", "mitigation": "block", "scaling": "scaling", "draw": "draw_energy", "energy": "draw_energy", "discard": "draw_energy"}
        score = sum(jobs[job] * (1.0 if job in {"frontload", "block"} else 0.8) for tag, job in tag_job.items() if tag in tags)
        score -= 0.10 * (price / max(gold, 1.0))
        score -= 0.08 * float(health["dead_draw_rate"] > 0.45 and bool(tags & {"status", "curse"}))
        if not candidate.get("semantic_id"):
            return _base_result(candidate, score - 0.1, "unknown card semantics; conservative estimate", 0.1, "Uncalculable")
        reason = "fills current deck-health job" if tags & set(tag_job) else "candidate has no measured job match"
        return _base_result(candidate, score, reason, 0.72)
    if kind == "buy_relic":
        # Relic semantics are not guessed from names; visible structured tags
        # can provide only a small, explainable prior.
        relic_count = _public_count(state, "relic_count", "relic_public")
        score = 0.12 + 0.08 * min(1.0, gold / 200.0) - 0.08 * (price / max(gold, 1.0)) - 0.01 * min(relic_count, 12)
        if not candidate.get("semantic_id"):
            return _base_result(candidate, score - 0.08, "unknown relic semantics", 0.1, "Uncalculable")
        return _base_result(candidate, score, "keeps permanent-value purchase as a soft option", 0.45)
    if kind == "buy_potion":
        hp_ratio = float(state.get("hp", 0) or 0) / max(float(state.get("max_hp", 0) or 0), 1.0)
        potion_count = _public_count(state, "potion_count", "potion_public")
        score = 0.10 + 0.22 * max(0.0, 0.55 - hp_ratio) + 0.04 * max(0.0, 3.0 - potion_count) - 0.08 * (price / max(gold, 1.0))
        if not candidate.get("semantic_id"):
            return _base_result(candidate, score - 0.05, "unknown potion semantics", 0.1, "Uncalculable")
        return _base_result(candidate, score, "values resource flexibility under current HP and slots", 0.55)
    return _base_result(candidate, -0.1, "unknown shop action", 0.1, "Uncalculable")


def rank_shop_candidates(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> list[dict[str, Any]]:
    validate_public_payload(state)
    source = list(candidates) if candidates is not None else _candidate_list(state)
    scored = []
    gold_before = float(state.get("gold", 0) or 0)
    for candidate in source:
        item = score_shop_candidate(state, candidate)
        price = _price(candidate)
        item["price"] = round(price, 6)
        item["gold_before"] = int(gold_before)
        item["gold_after"] = round(max(0.0, gold_before - price), 6)
        scored.append(item)
    # Legality is an input mask.  Illegal options remain in the returned set,
    # but are placed after legal options and can never be selected.
    scored.sort(key=lambda item: (not item["legal"], -item["score"], str(item.get("action_id") or "")))
    for rank, item in enumerate(scored, 1):
        item["rank"] = rank
    return scored


def select_shop_action(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> dict[str, Any] | None:
    ranked = rank_shop_candidates(state, candidates)
    return next((item for item in ranked if item["legal"]), None)
