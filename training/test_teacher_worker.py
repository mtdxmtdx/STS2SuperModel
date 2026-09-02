import copy
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent / "collectors"))
from teacher_worker import LOCK, TeacherWorker, aggregate_hidden_states
from snapshot_adapter import build_nosl_belief_state, rebuild_combat_snapshot, rebuild_nosl_combat_snapshot


def record():
    return {
        "record_id": "r1",
        **LOCK,
        "public_state": {
            "hand": [
                {"instance_id": "c1", "id": "CARD.STRIKE", "stats": {"damage": 6}},
                {"instance_id": "c2", "id": "CARD.DEFEND", "stats": {"block": 5}},
            ]
        },
        "legal_actions": [
            {"kind": "PlayCard", "action_id": "play:c1", "source_instance_id": "c1", "source_model_id": "CARD.STRIKE", "effective_energy_cost": 1, "legal": True},
            {"kind": "PlayCard", "action_id": "play:c2", "source_instance_id": "c2", "source_model_id": "CARD.DEFEND", "effective_energy_cost": 1, "legal": True},
            {"kind": "EndTurn", "action_id": "end_turn", "effective_energy_cost": 0, "legal": True},
        ],
        "state_hash_public": "A" * 64,
        "state_hash_teacher": "B" * 64,
        "teacher_snapshot": {"available": True, "rng_raw_words_exposed": False},
    }


def test_heuristic_fallback_emits_nonempty_estimated_label():
    output = TeacherWorker(top_k=2, allow_heuristic_fallback=True).process(record())
    assert output["teacher_best_actions"]
    assert output["confidence"] == "Estimated"
    assert output["label_quality"] == "EstimatedByHeuristic"
    assert output["search_complete"] is False
    assert len(output["teacher_top_k"]) == 2
    assert set(output["objectives"]) == {"Balanced", "HighestDamage", "MinimumLoss"}
    assert output["teacher_mode"] == "NOSL_BOUNDED"
    assert len(output["belief_signature"]) == 64


def test_missing_teacher_snapshot_does_not_block_public_evaluator_input():
    source = record()
    source.pop("teacher_snapshot")
    seen = {}

    def evaluator(request):
        seen.update(request)
        return {
            "teacher_best_actions": ["play:c1"],
            "teacher_top_k": [],
            "action_values": {"play:c1": 6},
            "confidence": "Estimated",
            "search_complete": False,
        }

    output = TeacherWorker(evaluator=evaluator).process(source)
    assert output["teacher_best_actions"] == ["play:c1"]
    assert "teacher_snapshot" not in seen
    assert seen["nosl_belief_state"]["belief_signature"]


def test_evaluator_response_is_attached_and_preserves_protocol():
    source = record()

    def evaluator(request):
        assert request["protocol"] == "sts2.teacher-evaluator.v1"
        assert request["version"] == LOCK
        assert request["teacher_mode"] == "NOSL_BOUNDED"
        assert request["search"]["offline_exact"] is False
        assert request["search"]["budget_ms"] == 500
        assert request["search"]["maximum_expanded_nodes"] == 2_000_000
        assert request["search"]["maximum_chance_branches"] == 32
        return {
            "teacher_best_actions": ["play:c1"],
            "teacher_top_k": [{"action_id": "play:c1", "value": 6, "rank": 1, "death_probability": 0}],
            "action_values": {"play:c1": 6},
            "objectives": {name: {"best_actions": ["play:c1"], "value": 6} for name in ("Balanced", "HighestDamage", "MinimumLoss")},
            "death_probability": 0,
            "search_budget_ms": 100,
            "expanded_nodes": 12,
            "chance_branch": {"produced": False, "kind": "none"},
            "confidence": "Reliable",
            "search_complete": True,
            "risk_events": [],
        }

    output = TeacherWorker(evaluator=evaluator).process(source)
    assert output["teacher_best_actions"] == ["play:c1"]
    assert output["confidence"] == "Estimated"
    assert output["expanded_nodes"] == 12


def test_hidden_state_aggregation_reports_variance_and_policy_sensitivity():
    first = TeacherWorker(top_k=1, allow_heuristic_fallback=True).process(record())
    second = copy.deepcopy(first)
    second["state_hash_teacher"] = "C" * 64
    second["teacher_best_actions"] = ["play:c2"]
    second["action_values"] = {"play:c1": 1.0, "play:c2": 9.0}
    rows = aggregate_hidden_states([first, second])
    assert len(rows) == 1
    assert rows[0]["teacher_state_hashes"] == ["B" * 64, "C" * 64]
    assert rows[0]["hidden_state_sensitive"] is True
    assert rows[0]["action_value_variance"]["play:c1"] > 0


def test_snapshot_adapter_preserves_ids_and_marks_lossy_semantics():
    snap, warnings = rebuild_combat_snapshot(
        {"round": 2, "hand": [{"instance_id": "c1", "id": "CARD.STRIKE", "cost": 1, "stats": {"damage": 6}}]},
        {"round": 1, "player": {"hp": 50, "max_hp": 80, "energy": 3}, "enemies": []},
    )
    assert snap["hand"][0]["instance_id"] == "c1"
    assert snap["hand"][0]["effects"][0]["kind"] == "Damage"
    assert "enemy_state_missing" in warnings


def test_nosl_belief_ignores_hidden_rng_and_draw_order():
    public = {
        "round": 1,
        "hand": [{"instance_id": "h1", "id": "CARD.STRIKE", "stats": {"damage": 6}}],
        "player": {"deck": [
            {"instance_id": "d1", "id": "CARD.STRIKE"},
            {"instance_id": "d2", "id": "CARD.DEFEND"},
            {"instance_id": "d3", "id": "CARD.DEFEND"},
        ]},
    }
    first = build_nosl_belief_state(public)
    altered = json.loads(json.dumps(public))
    altered["teacher_snapshot"] = {
        "rng_streams": {"Shuffle": {"state0": 123, "state1": 456}},
        "draw_pile": ["CARD.DEFEND", "CARD.STRIKE"],
    }
    second = build_nosl_belief_state(altered)
    assert first.belief_signature == second.belief_signature
    assert first.remaining_card_multiset == (("DEFEND", 2),)
    snapshot, _ = rebuild_nosl_combat_snapshot(public)
    assert snapshot["draw_pile"] == []
    assert [card["model_id"] for card in snapshot["discard_pile"]] == ["DEFEND", "DEFEND"]
    assert all(card["instance_id"].startswith("belief:draw:") for card in snapshot["discard_pile"])
    assert snapshot["rng_streams"] is None
    changed_public = copy.deepcopy(public)
    changed_public["player"]["hp"] = 1
    assert build_nosl_belief_state(changed_public).belief_signature != first.belief_signature


def test_teacher_evaluator_receives_nosl_input_not_raw_teacher_snapshot():
    source = record()
    seen = {}

    def evaluator(request):
        seen.update(request)
        return {
            "teacher_best_actions": ["play:c1"],
            "teacher_top_k": [{"action_id": "play:c1", "value": 6, "rank": 1, "death_probability": 0}],
            "action_values": {"play:c1": 6},
            "objectives": {name: {"best_actions": ["play:c1"], "value": 6} for name in ("Balanced", "HighestDamage", "MinimumLoss")},
            "death_probability": 0, "search_budget_ms": 100, "expanded_nodes": 1,
            "chance_branch": {"produced": False, "kind": "none"}, "confidence": "Reliable",
            "search_complete": True, "risk_events": [],
        }

    TeacherWorker(evaluator=evaluator).process(source)
    assert "teacher_snapshot" not in seen
    assert seen["combat_snapshot"]["draw_pile"] == []
    assert seen["combat_snapshot"]["rng_streams"] is None
    assert "rng_state_words" not in seen["nosl_belief_state"]
    assert "future_draw_order" not in seen["nosl_belief_state"]


def test_nosl_exact_request_uses_large_chance_branch_cap():
    source = record()
    seen = {}

    def evaluator(request):
        seen.update(request)
        return {
            "teacher_best_actions": ["play:c1"],
            "teacher_top_k": [],
            "action_values": {"play:c1": 1},
            "objectives": {},
            "confidence": "Reliable",
            "search_complete": True,
            "risk_events": [],
        }

    TeacherWorker(evaluator=evaluator, nosl_exact=True).process(source)
    assert seen["search"]["offline_exact"] is True
    assert seen["search"]["maximum_chance_branches"] == 100_000_000


def test_nosl_belief_state_matches_frozen_schema():
    jsonschema = pytest.importorskip("jsonschema")
    public = {
        "round": 1,
        "hand": [{"instance_id": "h1", "id": "CARD.STRIKE"}],
        "player": {"deck": [{"instance_id": "h1", "id": "CARD.STRIKE"}]},
    }
    belief = build_nosl_belief_state(public)
    schema = json.loads((Path(__file__).parent / "schemas" / "nosl-belief-state-v1.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator(schema).validate(belief.to_dict())
