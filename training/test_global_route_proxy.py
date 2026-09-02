from __future__ import annotations

from training.global_decision.combat_summary_proxy import estimate_combat_summary


def test_proxy_is_explicitly_estimated_and_not_reliable() -> None:
    state = {"hp": 70, "max_hp": 75, "deck_public": [], "visible_encounter_profile": {"enemy_count": 1}}
    result = estimate_combat_summary(state, {"id": "map:1:1:0", "type": "Elite"})
    assert result.quality == "EstimatedByHeuristic"
    assert result.source == "global-route-prototype"
    assert result.to_dict()["reliable"] is False

