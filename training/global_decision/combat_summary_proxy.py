"""Public-information combat cost proxy used by RoutePlanner only.

This deliberately does not call the real combat simulator.  It provides a
diagnostic estimate and is never eligible for a Reliable label.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from .contracts import validate_public_payload
from .deck_health import deck_health
from .route_features import canonical_room_type


@dataclass(frozen=True)
class CombatSummaryProxy:
    node_id: str
    expected_win_probability: float
    expected_hp_loss: float
    death_probability: float
    expected_turns: float
    confidence: float = 0.25
    quality: str = "EstimatedByHeuristic"
    source: str = "global-route-prototype"

    @property
    def expected_hp_loss_proxy(self) -> float:
        return self.expected_hp_loss

    @property
    def death_risk_proxy(self) -> float:
        return self.death_probability

    def to_dict(self) -> dict[str, Any]:
        return {
            "node_id": self.node_id,
            "expected_win_probability": self.expected_win_probability,
            "expected_hp_loss": self.expected_hp_loss,
            "death_probability": self.death_probability,
            "expected_turns": self.expected_turns,
            "expected_hp_loss_proxy": self.expected_hp_loss_proxy,
            "death_risk_proxy": self.death_risk_proxy,
            "confidence": self.confidence,
            "quality": self.quality,
            "source": self.source,
            "reliable": False,
        }


def estimate_combat_summary(state: Mapping[str, Any], node: Mapping[str, Any]) -> CombatSummaryProxy:
    """Estimate risk from current public deck/HP and the visible room type."""

    validate_public_payload(state)
    validate_public_payload(node)
    node_id = str(node.get("id") or node.get("node_id") or "unknown")
    room = canonical_room_type(node.get("type"))
    health = deck_health(state)
    hp = float(state.get("hp", 0) or 0)
    max_hp = float(state.get("max_hp", 0) or 0)
    hp_ratio = hp / max_hp if max_hp > 0 else 0.0
    readiness = 0.40 * health["frontload_score"] + 0.25 * health["block_score"] + 0.20 * health["scaling_score"] + 0.15 * health["draw_score"]
    if room == "elite":
        base_loss, base_risk, turns = 15.0, 0.18, 4.5
    elif room == "boss":
        base_loss, base_risk, turns = 25.0, 0.30, 6.0
    elif room == "combat":
        base_loss, base_risk, turns = 8.0, 0.08, 3.0
    else:
        base_loss, base_risk, turns = 0.0, 0.0, 0.0
    if base_loss == 0.0:
        return CombatSummaryProxy(node_id, 1.0, 0.0, 0.0, turns)
    loss = max(0.0, base_loss * (1.15 - 0.45 * readiness) * (1.20 - 0.35 * hp_ratio))
    risk = min(0.95, max(0.0, base_risk + 0.22 * (1.0 - hp_ratio) - 0.10 * readiness))
    return CombatSummaryProxy(node_id, round(1.0 - risk, 6), round(loss, 6), round(risk, 6), turns)


def proxy_by_node(state: Mapping[str, Any], nodes: Mapping[str, Mapping[str, Any]]) -> dict[str, dict[str, Any]]:
    return {node_id: estimate_combat_summary(state, node).to_dict() for node_id, node in nodes.items()}
