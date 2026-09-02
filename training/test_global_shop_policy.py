from __future__ import annotations

import json

import pytest

from training.global_decision.contracts import PublicStateLeakageError
from training.global_decision.shop_policy import rank_shop_candidates, select_shop_action


def _shop_state(gold: int = 120, hp: float = 50.0) -> dict:
    candidates = [
        {"action_id": "shop:card:screen-a:STRIKE", "action_type": "shop_card", "candidate_role": "card", "semantic_id": "STRIKE", "tags": ["attack"], "price": 80, "legal": True},
        {"action_id": "shop:relic:screen-a:RELIC_A", "action_type": "shop_relic", "candidate_role": "relic", "semantic_id": "RELIC_A", "price": 150, "legal": False, "restriction_reason": "insufficient_gold"},
        {"action_id": "shop:potion:screen-a:POTION_A", "action_type": "shop_potion", "candidate_role": "potion", "semantic_id": "POTION_A", "price": 40, "legal": True},
        {"action_id": "shop:remove", "action_type": "shop_remove", "candidate_role": "remove", "price": 75, "legal": True},
        {"action_id": "shop:leave", "action_type": "shop_leave", "candidate_role": "leave", "legal": True},
    ]
    return {
        "schema_version": "global-public-v1", "character": "Ironclad", "act": 1, "floor": 6,
        "hp": hp, "max_hp": 75, "gold": gold, "deck_public": [{"card_instance_id": "card:0", "semantic_id": "BASH", "type": "attack", "cost": 2, "tags": ["attack"]}],
        "relic_public": [], "potion_public": [], "visible_offers": candidates,
        "visible_options": [], "legal_actions": candidates,
    }


def test_shop_keeps_complete_stable_candidate_set_and_masks_illegal() -> None:
    state = _shop_state(gold=120)
    ranked = rank_shop_candidates(state)
    assert {item["action_id"] for item in ranked} == {"shop:card:screen-a:STRIKE", "shop:relic:screen-a:RELIC_A", "shop:potion:screen-a:POTION_A", "shop:remove", "shop:leave"}
    assert select_shop_action(state)["action_id"] != "shop:relic:screen-a:RELIC_A"
    assert all(item["quality"] == "EstimatedByHeuristic" and item["source"] == "global-shop-campfire-prototype" and item["reliable"] is False for item in ranked)


def test_gold_changes_purchase_opportunity_value() -> None:
    poor = rank_shop_candidates(_shop_state(gold=80))
    rich = rank_shop_candidates(_shop_state(gold=200))
    poor_card = next(item for item in poor if item["action_id"].startswith("shop:card"))
    rich_card = next(item for item in rich if item["action_id"].startswith("shop:card"))
    assert poor_card["score"] != rich_card["score"]


def test_existing_relic_and_potion_counts_change_purchase_value() -> None:
    base = rank_shop_candidates(_shop_state())
    crowded = _shop_state()
    crowded["relic_public"] = [{"semantic_id": f"RELIC_{i}"} for i in range(8)]
    crowded["potion_public"] = [{"semantic_id": f"POTION_{i}"} for i in range(3)]
    crowded_ranked = rank_shop_candidates(crowded)
    base_relic = next(item for item in base if item["candidate_role"] == "relic")
    crowded_relic = next(item for item in crowded_ranked if item["candidate_role"] == "relic")
    base_potion = next(item for item in base if item["candidate_role"] == "potion")
    crowded_potion = next(item for item in crowded_ranked if item["candidate_role"] == "potion")
    assert base_relic["score"] != crowded_relic["score"]
    assert base_potion["score"] != crowded_potion["score"]


def test_unknown_semantics_are_explicitly_uncalculable() -> None:
    candidate = {"action_id": "shop:card:screen-a:UNKNOWN", "action_type": "shop_card", "candidate_role": "card", "price": 20, "legal": True}
    result = rank_shop_candidates(_shop_state(), [candidate])[0]
    assert result["semantic_status"] == "Uncalculable"
    assert result["confidence"] < 0.2


def test_shop_repeat_and_public_leakage() -> None:
    state = _shop_state()
    assert rank_shop_candidates(state) == rank_shop_candidates(json.loads(json.dumps(state)))
    with pytest.raises(PublicStateLeakageError):
        rank_shop_candidates(dict(state, seed="not-an-input"))
