from __future__ import annotations

import json

import pytest

from training.global_decision.campfire_policy import rank_campfire_candidates, select_campfire_action
from training.global_decision.contracts import PublicStateLeakageError


def _campfire_state(hp: float = 20.0, upgraded: bool = False) -> dict:
    card = {"card_instance_id": "card:BASH:000", "semantic_id": "BASH", "type": "attack", "cost": 2, "tags": ["attack"], "upgrade_level": int(upgraded)}
    candidates = [
        {"action_id": "campfire:rest", "action_type": "campfire_rest", "candidate_role": "rest", "legal": True},
        {"action_id": "campfire:smith:card:BASH:000", "action_type": "campfire_smith", "candidate_role": "smith", "card_instance_id": "card:BASH:000", "legal": True},
        {"action_id": "campfire:leave", "action_type": "campfire_leave", "candidate_role": "leave", "legal": True},
    ]
    return {
        "schema_version": "global-public-v1", "character": "Ironclad", "act": 1, "floor": 8,
        "hp": hp, "max_hp": 75, "gold": 90, "deck_public": [card], "relic_public": [], "potion_public": [],
        "visible_options": candidates, "legal_actions": candidates,
    }


def test_low_hp_prefers_rest_and_retains_smith_leave() -> None:
    state = _campfire_state(hp=15)
    ranked = rank_campfire_candidates(state)
    assert ranked[0]["action_id"] == "campfire:rest"
    assert {item["action_id"] for item in ranked} == {"campfire:rest", "campfire:smith:card:BASH:000", "campfire:leave"}
    assert select_campfire_action(state)["action_id"] == "campfire:rest"


def test_key_unupgraded_card_increases_smith_value() -> None:
    unupgraded = next(item for item in rank_campfire_candidates(_campfire_state(hp=70, upgraded=False)) if item["candidate_role"] == "smith")
    upgraded = next(item for item in rank_campfire_candidates(_campfire_state(hp=70, upgraded=True)) if item["candidate_role"] == "smith")
    assert unupgraded["score"] > upgraded["score"]
    assert next(item for item in rank_campfire_candidates(_campfire_state(hp=70, upgraded=False)))["candidate_role"] == "smith"


def test_campfire_repeat_and_public_leakage() -> None:
    state = _campfire_state()
    assert rank_campfire_candidates(state) == rank_campfire_candidates(json.loads(json.dumps(state)))
    with pytest.raises(PublicStateLeakageError):
        rank_campfire_candidates(dict(state, rng_state={"x": 1}))

