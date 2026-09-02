import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from trace_to_training import action_candidates, normalize


def test_fallback_action_ids_match_cli_stable_contract():
    observation = {
        "hand": [
            {"index": 0, "id": "STRIKE", "instance_id": "card:STRIKE:000", "target_type": "AnyEnemy", "cost": 1, "can_play": True},
            {"index": 1, "id": "DEFEND", "instance_id": "card:DEFEND:000", "target_type": "Self", "cost": 1, "can_play": True},
        ],
        "enemies": [{"instance_id": "enemy:SLIME:0"}],
        "player": {"potions": [{"index": 0, "id": "FIRE_POTION", "instance_id": "potion:FIRE_POTION:000"}]},
    }

    candidates = action_candidates(observation)
    ids = {candidate["action_id"] for candidate in candidates}
    assert "play:card:STRIKE:000:enemy:SLIME:0" in ids
    assert "play:card:DEFEND:000:none" in ids
    assert "potion:potion:FIRE_POTION:000" in ids
    assert "end_turn" in ids


def test_fallback_candidates_are_marked_incomplete():
    rows = [{
        "trace_id": "trace-1",
        "trace_schema": 1,
        "game_version": "v0.111.0",
        "game_commit": "41cef1ea",
        "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        "cli_protocol_version": "0.2.0",
        "step": 0,
        "post_state_hash": "state-1",
        "public_observation": {"player": {}, "hand": [], "enemies": []},
    }]
    record = normalize(rows)[0]
    assert record["legal_actions_complete"] is False
    assert record["public_state"]["action_candidates_complete"] is False
