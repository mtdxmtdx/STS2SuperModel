from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent / "collectors"))
from random_belief import draw_one, draw_sequence, random_combination, uniform_choice, weighted_pool


def test_draw_one_is_exact_without_replacement():
    outcomes = draw_one({"STRIKE": 3, "DEFEND": 2, "BASH": 1})
    assert [(item.label, item.probability) for item in outcomes] == [
        ("BASH", 1 / 6), ("DEFEND", 2 / 6), ("STRIKE", 3 / 6)
    ]
    assert abs(sum(item.probability for item in outcomes) - 1.0) < 1e-12
    assert dict(outcomes[-1].value) == {"BASH": 1, "DEFEND": 2, "STRIKE": 2}


def test_draw_sequence_merges_equivalent_multiset_states():
    outcomes = draw_sequence({"A": 2, "B": 1}, 2)
    assert abs(sum(item.probability for item in outcomes) - 1.0) < 1e-12
    by_state = {tuple(item.value): item.probability for item in outcomes}
    assert by_state[(("B", 1),)] == 1 / 3
    assert by_state[(("A", 1),)] == 2 / 3


def test_random_selection_and_weighted_pool_have_unit_mass():
    assert len(random_combination(["a", "b", "c"], 2)) == 3
    assert abs(sum(item.probability for item in uniform_choice([1, 2, 3])) - 1.0) < 1e-12
    weighted = weighted_pool([("a", 2), ("b", 1)])
    assert [item.probability for item in weighted] == [2 / 3, 1 / 3]
