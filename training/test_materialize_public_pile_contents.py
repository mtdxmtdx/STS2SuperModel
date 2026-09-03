import copy

from materialize_public_pile_contents import materialize
from collectors.snapshot_adapter import build_nosl_belief_state, rebuild_nosl_combat_snapshot


def _row():
    deck = [
        {"instance_id": "deck-a", "id": "CARD.A", "name": "A", "cost": 1,
         "type": "Attack", "stats": {"damage": 6}},
        {"instance_id": "deck-b", "id": "CARD.B", "name": "B", "cost": 1,
         "type": "Skill", "stats": {"block": 5}},
    ]
    return {
        "state_hash_public": "legacy",
        "public_state": {
            "hand": [], "draw_pile_count": 1, "discard_pile_count": 1,
            "player": {"deck": deck},
        },
        "teacher_snapshot": {
            "draw_pile": [{"instance_id": "card-b", "id": "B", "type": "Skill", "upgraded": False}],
            "discard_pile": [{"instance_id": "card-a", "id": "A", "type": "Attack", "upgraded": False}],
            "exhaust_pile": [],
            "rng_counters": {"Shuffle": 99},
        },
    }


def test_materialization_copies_only_public_zone_semantic_multisets():
    row = materialize(_row())
    public = row["public_state"]
    snapshot, warnings = rebuild_nosl_combat_snapshot(public)

    assert public["draw_pile_multiset"] == [
        {"model_id": "B", "upgraded": False, "count": 1},
    ]
    assert public["discard_pile_multiset"] == [
        {"model_id": "A", "upgraded": False, "count": 1},
    ]
    assert "draw_pile" not in public
    assert "discard_pile" not in public
    assert [card["model_id"] for card in snapshot["draw_pile"]] == ["B"]
    assert [card["model_id"] for card in snapshot["discard_pile"]] == ["A"]
    assert snapshot["global_restrictions"] == ["nosl_unordered_draw_pool"]
    assert not any(warning.startswith("uncalculable_") for warning in warnings)
    assert "rng_counters" not in public
    assert row["public_pile_materialization"]["rng_fields_copied"] is False


def test_belief_signature_ignores_pile_order_but_keeps_draw_discard_partition():
    row = materialize(_row())
    public = row["public_state"]
    reordered = copy.deepcopy(public)
    reordered["draw_pile_multiset"] = list(reversed(reordered["draw_pile_multiset"]))
    assert build_nosl_belief_state(public).belief_signature == build_nosl_belief_state(reordered).belief_signature

    swapped = copy.deepcopy(public)
    swapped["draw_pile_multiset"], swapped["discard_pile_multiset"] = (
        swapped["discard_pile_multiset"], swapped["draw_pile_multiset"]
    )
    assert build_nosl_belief_state(public).belief_signature != build_nosl_belief_state(swapped).belief_signature
