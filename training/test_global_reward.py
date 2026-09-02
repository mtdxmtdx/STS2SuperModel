from __future__ import annotations

from training.global_decision.reward_heuristic import rank_offer
from training.global_decision.synthetic_global_states import generate_row


def test_reward_heuristic_keeps_skip_and_quality() -> None:
    row = generate_row(7)
    state = row["state_public"]
    candidates = list(row["offer_snapshot"]["candidates"])
    ranked = rank_offer(state, candidates)
    assert len(ranked) == 4
    assert {item["label_source"] for item in ranked} == {"EstimatedByHeuristic"}
    assert {item["quality"] for item in ranked} == {"EstimatedByHeuristic"}
    assert any(item.get("candidate_role") == "skip" for item in ranked)
    assert [item["rank"] for item in ranked] == [1, 2, 3, 4]

