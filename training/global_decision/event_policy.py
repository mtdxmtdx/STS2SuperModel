"""Public-observation EventPolicy prototype.

Only explicitly structured effects already present on the current screen are
scored.  An option whose outcome is not exposed remains in the candidate set
with ``semantic_status=Uncalculable`` rather than being guessed from text.
"""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .contracts import validate_public_payload
from .deck_features import card_tags
from .deck_health import deck_health


QUALITY = "EstimatedByHeuristic"
SOURCE = "global-event-ancient-prototype"


def _as_candidates(state: Mapping[str, Any]) -> list[dict[str, Any]]:
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
            if action_id is None:
                event_id = item.get("event_id") or state.get("event_id")
                option_id = item.get("option_id")
                if event_id is not None and option_id is not None:
                    item["action_id"] = f"event:{event_id}:option:{option_id}"
                    action_id = item["action_id"]
            if action_id is None:
                anonymous.append(item)
            elif str(action_id) in merged:
                merged[str(action_id)].update(item)
            else:
                merged[str(action_id)] = item
    return sorted(list(merged.values()) + anonymous, key=lambda item: (str(item.get("action_id") or ""), str(item.get("option_id") or "")))


def _effects(candidate: Mapping[str, Any]) -> Mapping[str, Any] | None:
    for key in ("effects", "structured_effects", "public_effects"):
        value = candidate.get(key)
        if isinstance(value, Mapping):
            return value
    return None


def _number(effects: Mapping[str, Any], *keys: str) -> float | None:
    for key in keys:
        if key in effects:
            try:
                return float(effects[key])
            except (TypeError, ValueError):
                return None
    return None


def _result(candidate: Mapping[str, Any], score: float, reason: str, confidence: float, status: str = "known") -> dict[str, Any]:
    return {
        "action_id": candidate.get("action_id"),
        "stable_id_missing": candidate.get("action_id") is None,
        "event_id": candidate.get("event_id"),
        "ancient_id": candidate.get("ancient_id"),
        "page_id": candidate.get("page_id"),
        "option_id": candidate.get("option_id"),
        "action_type": candidate.get("action_type"),
        "candidate_role": candidate.get("candidate_role"),
        "legal": bool(candidate.get("legal", True)),
        "score": round(float(score), 6),
        "rank": 0,
        "confidence": round(max(0.0, min(1.0, confidence)), 6),
        "reason": reason,
        "semantic_status": status,
        "quality": QUALITY,
        "source": SOURCE,
        "reliable": False,
    }


def score_public_option(state: Mapping[str, Any], candidate: Mapping[str, Any], *, domain: str = "event") -> dict[str, Any]:
    """Score only public structured deltas; no text-based hidden-outcome guess."""

    validate_public_payload(state)
    validate_public_payload(candidate)
    effects = _effects(candidate)
    action_text = " ".join(str(candidate.get(key, "")) for key in ("action_type", "candidate_role", "option_id")).lower()
    if effects is None:
        if any(token in action_text for token in ("proceed", "leave", "cancel")):
            return _result(candidate, 0.0, "navigation option with no resource delta", 0.85)
        return _result(candidate, 0.0, "public outcome is not structured", 0.05, "Uncalculable")

    hp = float(state.get("hp", 0) or 0)
    max_hp = max(float(state.get("max_hp", 0) or 0), 1.0)
    hp_ratio = hp / max_hp
    health = deck_health(state)
    score = 0.0
    reasons: list[str] = []
    known_delta = False
    hp_delta = _number(effects, "hp_delta", "health_delta", "heal")
    if hp_delta is not None:
        known_delta = True
        score += hp_delta / max_hp * (1.8 if hp_ratio < 0.45 else 0.8)
        reasons.append("public HP change")
    gold_delta = _number(effects, "gold_delta", "money_delta")
    if gold_delta is not None:
        known_delta = True
        score += gold_delta / 100.0 * (1.0 if float(state.get("gold", 0) or 0) < 100 else 0.65)
        reasons.append("public gold change")
    card_delta = _number(effects, "card_count_delta", "cards_delta")
    if card_delta is not None:
        known_delta = True
        score += card_delta * (-0.12 if card_delta > 0 else 0.20)
        reasons.append("public deck-size change")
    remove_delta = _number(effects, "remove_card_count", "removed_cards")
    if remove_delta is not None:
        known_delta = True
        score += remove_delta * (0.18 + 0.20 * health["dead_draw_rate"])
        reasons.append("public card removal")
    curse_delta = _number(effects, "curse_delta", "status_delta", "negative_status_delta")
    if curse_delta is not None:
        known_delta = True
        score -= abs(curse_delta) * (0.25 + 0.35 * health["dead_draw_rate"])
        reasons.append("public curse/status burden")
    risk = _number(effects, "risk", "death_risk", "risk_delta")
    if risk is not None:
        known_delta = True
        score -= max(0.0, risk) * (1.5 if hp_ratio < 0.45 else 0.8)
        reasons.append("structured risk estimate")
    relic_delta = _number(effects, "relic_delta", "relic_count_delta")
    potion_delta = _number(effects, "potion_delta", "potion_count_delta")
    if relic_delta is not None:
        known_delta = True
        score += 0.18 * relic_delta
        reasons.append("public relic change")
    if potion_delta is not None:
        known_delta = True
        score += 0.10 * potion_delta
        reasons.append("public potion change")
    if not known_delta:
        return _result(candidate, 0.0, "effects object has no recognized public delta", 0.08, "Uncalculable")
    confidence = 0.55 if domain == "event" else 0.50
    return _result(candidate, score, "; ".join(reasons), confidence)


def rank_event_options(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> list[dict[str, Any]]:
    validate_public_payload(state)
    source = list(candidates) if candidates is not None else _as_candidates(state)
    scored = [score_public_option(state, item, domain="event") for item in source]
    scored.sort(key=lambda item: (not item["legal"], -item["score"], str(item.get("action_id") or "")))
    for rank, item in enumerate(scored, 1):
        item["rank"] = rank
    return scored


def select_event_option(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]] | None = None) -> dict[str, Any] | None:
    return next((item for item in rank_event_options(state, candidates) if item["legal"]), None)
