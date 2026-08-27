import copy
import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent / "collectors"))
from teacher_worker import LOCK, TeacherWorker, aggregate_hidden_states


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
    assert output["search_complete"] is False
    assert len(output["teacher_top_k"]) == 2
    assert set(output["objectives"]) == {"Balanced", "HighestDamage", "MinimumLoss"}


def test_missing_teacher_snapshot_is_not_silently_labelled():
    source = record()
    source.pop("teacher_snapshot")
    with pytest.raises(ValueError, match="teacher snapshot"):
        TeacherWorker().process(source)


def test_evaluator_response_is_attached_and_preserves_protocol():
    source = record()

    def evaluator(request):
        assert request["protocol"] == "sts2.teacher-evaluator.v1"
        assert request["version"] == LOCK
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
    assert output["confidence"] == "Reliable"
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
