from __future__ import annotations

from training.global_decision.deck_features import DeckFeatureEncoder
from training.global_decision.deck_health import current_jobs


def test_deck_features_include_required_context_and_no_seed() -> None:
    state = {
        "schema_version": "global-public-v1",
        "state_public_hash": "0" * 64,
        "character": "Ironclad",
        "act": 1,
        "floor": 4,
        "ascension": 5,
        "hp": 40,
        "max_hp": 75,
        "gold": 120,
        "deck_public": [
            {"card_instance_id": "card:BASH:000", "semantic_id": "BASH", "type": "attack", "cost": 2, "tags": ["attack", "frontload"]},
            {"card_instance_id": "card:DEFEND_R:000", "semantic_id": "DEFEND_R", "type": "skill", "cost": 1, "tags": ["block"]},
        ],
        "relic_public": [],
        "potion_public": [],
    }
    encoded = DeckFeatureEncoder().encode(state)
    assert encoded["context"]["hp_ratio"] == 40 / 75
    assert encoded["context"]["ascension"] == 5
    assert encoded["deck_health"]["frontload_score"] > 0
    assert encoded["deck_health"]["block_score"] > 0
    assert "seed" not in repr(encoded).lower()
    assert set(current_jobs(state)) == {"frontload", "aoe", "block", "scaling", "draw_energy", "dead_draw"}
