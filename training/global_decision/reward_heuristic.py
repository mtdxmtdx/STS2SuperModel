"""Explainable RewardPolicy baseline for card rewards and Skip."""

from __future__ import annotations

from typing import Any, Mapping, Sequence

from .deck_features import card_tags
from .deck_health import current_jobs, deck_health


ROLE_WEIGHTS = {
    "frontload": 1.00,
    "aoe": 0.85,
    "block": 0.95,
    "scaling": 0.70,
    "draw_energy": 0.65,
    "dead_draw": -0.60,
}


def _candidate_tags(candidate: Mapping[str, Any]) -> set[str]:
    return set(card_tags(candidate)) | {str(x).lower() for x in candidate.get("tags", [])}


def score_candidate(state: Mapping[str, Any], candidate: Mapping[str, Any]) -> dict[str, Any]:
    """Score one candidate; output is explicitly heuristic/estimated."""

    role = str(candidate.get("candidate_role", "offer")).lower()
    if role in {"skip", "leave"} or str(candidate.get("action_type", "")).lower() in {"reward_skip", "skip"}:
        health = deck_health(state)
        jobs = current_jobs(state)
        urgent = sum(jobs[key] * ROLE_WEIGHTS.get(key, 0.0) for key in jobs)
        score = 0.35 - 0.25 * urgent + 0.15 * health["dead_draw_rate"]
        return {
            "score": round(score, 6),
            "reason": "skip preserves cycle quality when current job deficits are small",
            "quality": "EstimatedByHeuristic",
            "label_source": "EstimatedByHeuristic",
        }

    jobs = current_jobs(state)
    tags = _candidate_tags(candidate)
    score = 0.0
    reasons: list[str] = []
    tag_job = {
        "attack": "frontload",
        "frontload": "frontload",
        "aoe": "aoe",
        "block": "block",
        "mitigation": "block",
        "scaling": "scaling",
        "draw": "draw_energy",
        "energy": "draw_energy",
        "discard": "draw_energy",
    }
    for tag, job in tag_job.items():
        if tag in tags:
            contribution = jobs[job] * ROLE_WEIGHTS[job]
            score += contribution
            if contribution > 0.05:
                reasons.append(f"fills {job} job")
    if "status" in tags or "curse" in tags or "quest" in tags:
        penalty = jobs["dead_draw"] * 0.45
        score -= penalty
        reasons.append("adds status/quest burden")
    cost = float(candidate.get("cost", candidate.get("card_cost", 0)) or 0)
    if cost >= 3:
        score -= 0.08 if int(state.get("act", 1) or 1) == 1 else 0.03
        reasons.append("high cost opportunity cost")
    if int(deck_health(state)["deck_size"]) >= 30:
        score -= 0.10
        reasons.append("large deck dilution")
    if not reasons:
        reasons.append("weak immediate job match")
    return {
        "score": round(score, 6),
        "reason": "; ".join(reasons),
        "quality": "EstimatedByHeuristic",
        "label_source": "EstimatedByHeuristic",
    }


def rank_offer(state: Mapping[str, Any], candidates: Sequence[Mapping[str, Any]]) -> list[dict[str, Any]]:
    scored: list[dict[str, Any]] = []
    for index, candidate in enumerate(candidates):
        item = dict(candidate)
        item.update(score_candidate(state, candidate))
        item["candidate_index"] = int(candidate.get("candidate_index", index))
        scored.append(item)
    scored.sort(key=lambda item: (-float(item["score"]), str(item.get("action_id") or ""), int(item["candidate_index"])))
    for rank, item in enumerate(scored, 1):
        item["rank"] = rank
    return scored
