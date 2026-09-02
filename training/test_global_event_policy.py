from __future__ import annotations

import json

import pytest

from training.global_decision.contracts import PublicStateLeakageError
from training.global_decision.event_policy import rank_event_options, select_event_option


def _event_state(hp: float = 30.0, gold: int = 50) -> dict:
    options = [
        {"action_id": "event:E1:option:heal", "event_id": "E1", "page_id": "p1", "option_id": "heal", "action_type": "event_option", "effects": {"hp_delta": 12}, "legal": True},
        {"action_id": "event:E1:option:gold", "event_id": "E1", "page_id": "p1", "option_id": "gold", "action_type": "event_option", "effects": {"gold_delta": 80}, "legal": True},
        {"action_id": "event:E1:option:proceed", "event_id": "E1", "page_id": "p1", "option_id": "proceed", "action_type": "proceed", "legal": True},
        {"action_id": "event:E1:option:leave", "event_id": "E1", "page_id": "p1", "option_id": "leave", "action_type": "leave", "legal": True},
        {"action_id": "event:E1:option:cancel", "event_id": "E1", "page_id": "p1", "option_id": "cancel", "action_type": "cancel", "legal": False},
    ]
    return {"schema_version": "global-public-v1", "event_id": "E1", "page_id": "p1", "hp": hp, "max_hp": 75, "gold": gold, "deck_public": [{"semantic_id": "BASH", "type": "attack", "cost": 2, "tags": ["attack"]}], "relic_public": [], "potion_public": [], "visible_options": options, "legal_actions": options}


def test_event_preserves_options_and_navigation_actions() -> None:
    ranked = rank_event_options(_event_state())
    assert {item["action_id"] for item in ranked} == {"event:E1:option:heal", "event:E1:option:gold", "event:E1:option:proceed", "event:E1:option:leave", "event:E1:option:cancel"}
    assert select_event_option(_event_state())["action_id"] != "event:E1:option:cancel"
    assert all({"score", "rank", "confidence", "legal", "reason", "quality", "source", "reliable"} <= item.keys() for item in ranked)


def test_event_hp_and_gold_change_soft_scores() -> None:
    low = rank_event_options(_event_state(hp=10, gold=0))
    high = rank_event_options(_event_state(hp=70, gold=200))
    low_heal = next(item for item in low if item["option_id"] == "heal")
    high_heal = next(item for item in high if item["option_id"] == "heal")
    low_gold = next(item for item in low if item["option_id"] == "gold")
    high_gold = next(item for item in high if item["option_id"] == "gold")
    assert low_heal["score"] != high_heal["score"]
    assert low_gold["score"] != high_gold["score"]


def test_unknown_event_outcome_is_uncalculable_and_repeatable() -> None:
    state = _event_state()
    unknown = {"action_id": "event:E1:option:hidden", "event_id": "E1", "option_id": "hidden", "action_type": "event_option", "legal": True}
    a = rank_event_options(state, [unknown])
    b = rank_event_options(json.loads(json.dumps(state)), [unknown])
    assert a == b
    assert a[0]["semantic_status"] == "Uncalculable"
    assert a[0]["confidence"] < 0.1


def test_event_public_leakage_rejected() -> None:
    with pytest.raises(PublicStateLeakageError):
        rank_event_options(dict(_event_state(), seed="hidden"))
