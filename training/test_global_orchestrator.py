from __future__ import annotations

import copy
import hashlib
import json

import pytest

from training.global_decision.ancient_policy import rank_ancient_options
from training.global_decision.contracts import PublicStateLeakageError
from training.global_decision.global_orchestrator import DECISION_TYPES, GlobalDecisionOrchestrator
from training.global_decision.route_planner import RoutePlanner
from training.test_global_campfire_policy import _campfire_state
from training.test_global_event_policy import _event_state
from training.test_global_route_planner import _state as _route_state
from training.test_global_shop_policy import _shop_state
from training.global_decision.synthetic_global_states import generate_dataset


def _route_with_legal_actions() -> dict:
    state = _route_state(hp=74)
    planner = RoutePlanner(horizon=2)
    planned = planner.plan(state)
    legal = [{"action_id": f"route:path:{hashlib.sha256(json.dumps(list(item.path), separators=(',', ':')).encode()).hexdigest()[:16]}", "legal": True} for item in planned.candidates]
    state["legal_actions"] = legal
    return state


def _states() -> dict[str, dict]:
    reward = generate_dataset(1, 20260831)[0]["state_public"]
    return {
        "route": _route_with_legal_actions(),
        "reward": reward,
        "shop": _shop_state(),
        "campfire": _campfire_state(),
        "event": _event_state(),
        "ancient": {**_event_state(), "ancient_id": "ANCIENT_A", "visible_options": [{"ancient_id": "ANCIENT_A", "option_id": "offer", "action_type": "ancient_option", "effects": {"gold_delta": 10}, "legal": True}, {"ancient_id": "ANCIENT_A", "option_id": "leave", "action_type": "leave", "legal": True}], "legal_actions": [{"ancient_id": "ANCIENT_A", "option_id": "offer", "action_type": "ancient_option", "effects": {"gold_delta": 10}, "legal": True}, {"ancient_id": "ANCIENT_A", "option_id": "leave", "action_type": "leave", "legal": True}]},
    }


def test_all_decision_types_dispatch_and_have_uniform_output() -> None:
    orchestrator = GlobalDecisionOrchestrator()
    for decision_type, state in _states().items():
        result = orchestrator.decide(state, decision_type)
        assert result["decision_type"] == decision_type
        assert result["candidates"]
        assert len(result["legal_mask"]) == len(result["candidates"])
        assert {"score", "rank", "confidence", "reason"} <= result.keys()
        assert {"score", "rank", "confidence", "reason", "quality", "source", "reliable", "legal"} <= result["candidates"][0].keys()
        assert result["quality"] == "EstimatedByHeuristic"
        assert result["reliable"] is False
        assert result["selected_action_id"] in {item["action_id"] for item in result["candidates"] if item["legal"]}


def test_illegal_actions_are_retained_but_never_selected() -> None:
    state = _shop_state(gold=20)
    result = GlobalDecisionOrchestrator().decide(state, "shop")
    illegal = {item["action_id"] for item in result["candidates"] if not item["legal"]}
    assert illegal
    assert result["selected_action_id"] not in illegal
    assert len(result["candidates"]) == 5


def test_hp_gold_and_current_node_changes_recompute() -> None:
    orchestrator = GlobalDecisionOrchestrator()
    high = orchestrator.decide(_route_with_legal_actions(), "route")
    low_state = _route_with_legal_actions()
    low_state["hp"] = 12
    low = orchestrator.decide(low_state, "route")
    assert high["selected_action_id"] != low["selected_action_id"]
    poor = orchestrator.decide(_shop_state(gold=0), "shop")
    rich = orchestrator.decide(_shop_state(gold=250), "shop")
    poor_card = next(item for item in poor["candidates"] if item.get("candidate_role") == "card")
    rich_card = next(item for item in rich["candidates"] if item.get("candidate_role") == "card")
    assert poor_card["score"] != rich_card["score"]


def test_repeat_output_hash_is_stable_and_no_leakage() -> None:
    state = _event_state()
    orchestrator = GlobalDecisionOrchestrator()
    first = json.dumps(orchestrator.decide(state, "event"), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    second = json.dumps(orchestrator.decide(copy.deepcopy(state), "event"), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    assert first == second
    assert hashlib.sha256(first).hexdigest() == "88dee07242ca20eb837599ccb4922f935a053773fc3bb4c624d6db184ca01f51"
    with pytest.raises(PublicStateLeakageError):
        orchestrator.decide(dict(state, seed="hidden"), "event")


def test_unknown_decision_type_rejected() -> None:
    with pytest.raises(ValueError):
        GlobalDecisionOrchestrator().decide(_event_state(), "global_teacher")
