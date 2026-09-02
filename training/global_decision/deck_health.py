"""Interpretable deck-health and current-job heuristics."""

from __future__ import annotations

from typing import Any, Mapping

from .deck_features import DeckFeatureEncoder


def deck_health(state: Mapping[str, Any]) -> dict[str, float]:
    return dict(DeckFeatureEncoder().encode(state)["deck_health"])


def current_jobs(state: Mapping[str, Any]) -> dict[str, float]:
    """Return soft deficits; values are priorities, not hard archetype rules."""

    health = deck_health(state)
    hp = float(state.get("hp", 0) or 0)
    max_hp = float(state.get("max_hp", 0) or 0)
    hp_ratio = hp / max_hp if max_hp > 0 else 0.0
    act = int(state.get("act", 1) or 1)
    jobs = {
        "frontload": max(0.0, 0.55 - health["frontload_score"]),
        "aoe": max(0.0, 0.35 - health["aoe_score"]),
        "block": max(0.0, 0.45 - health["block_score"]),
        "scaling": max(0.0, (0.35 if act >= 2 else 0.20) - health["scaling_score"]),
        "draw_energy": max(0.0, 0.30 - max(health["draw_score"], health["energy_score"])),
        "dead_draw": min(1.0, health["dead_draw_rate"] + health["status_burden"]),
    }
    if hp_ratio < 0.35:
        jobs["block"] = min(1.0, jobs["block"] + 0.25)
        jobs["frontload"] = min(1.0, jobs["frontload"] + 0.10)
    return jobs
