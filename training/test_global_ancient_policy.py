from __future__ import annotations

import json

import pytest

from training.global_decision.ancient_policy import rank_ancient_options, select_ancient_option
from training.global_decision.contracts import PublicStateLeakageError


def _ancient_state(hp: float = 40.0, gold: int = 80) -> dict:
    options = [
        {"ancient_id": "ANCIENT_A", "page_id": "p", "option_id": "trade_hp", "action_type": "ancient_option", "effects": {"hp_delta": -8, "gold_delta": 100}, "legal": True},
        {"ancient_id": "ANCIENT_A", "page_id": "p", "option_id": "leave", "action_type": "leave", "legal": True},
        {"ancient_id": "ANCIENT_A", "page_id": "p", "option_id": "proceed", "action_type": "proceed", "legal": True},
        {"ancient_id": "ANCIENT_A", "page_id": "p", "option_id": "cancel", "action_type": "cancel", "legal": False},
    ]
    return {"schema_version": "global-public-v1", "ancient_id": "ANCIENT_A", "page_id": "p", "hp": hp, "max_hp": 75, "gold": gold, "deck_public": [], "relic_public": [], "potion_public": [], "visible_options": options, "legal_actions": options}


def test_ancient_ids_are_stable_and_complete() -> None:
    ranked = rank_ancient_options(_ancient_state())
    assert ranked[0]["action_id"].startswith("ancient:ANCIENT_A:option:")
    assert {item["option_id"] for item in ranked} == {"trade_hp", "leave", "proceed", "cancel"}
    assert select_ancient_option(_ancient_state())["action_id"] != "ancient:ANCIENT_A:option:cancel"


def test_ancient_repeat_and_public_leakage() -> None:
    state = _ancient_state()
    assert rank_ancient_options(state) == rank_ancient_options(json.loads(json.dumps(state)))
    with pytest.raises(PublicStateLeakageError):
        rank_ancient_options(dict(state, rng_state={"counter": 1}))
