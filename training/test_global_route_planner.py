from __future__ import annotations

import copy

from training.global_decision.route_planner import RoutePlanner


def _state(hp: float = 70.0, gold: int = 80, deck=None) -> dict:
    return {
        "schema_version": "global-public-v1", "state_public_hash": "", "character": "Ironclad", "act": 1, "floor": 4,
        "hp": hp, "max_hp": 75, "gold": gold, "deck_public": deck or [{"semantic_id": "BASH", "type": "attack", "cost": 2, "tags": ["attack"]}],
        "relic_public": [], "potion_public": [], "visible_encounter_profile": {"enemy_count": 1}, "current_node": "map:1:0:0",
        "visible_map_graph": {"current": "map:1:0:0", "nodes": [
            {"id": "map:1:0:0", "row": 0, "col": 0, "type": "Start", "visible": True},
            {"id": "map:1:1:0", "row": 1, "col": 0, "type": "Elite", "visible": True},
            {"id": "map:1:1:1", "row": 1, "col": 1, "type": "Combat", "visible": True},
            {"id": "map:1:2:0", "row": 2, "col": 0, "type": "Campfire", "visible": True},
            {"id": "map:1:2:1", "row": 2, "col": 1, "type": "Shop", "visible": True},
        ], "edges": [["map:1:0:0", "map:1:1:0"], ["map:1:0:0", "map:1:1:1"], ["map:1:1:0", "map:1:2:0"], ["map:1:1:1", "map:1:2:1"]]},
    }


def test_high_hp_can_value_elite_without_elite_hard_rule() -> None:
    plan = RoutePlanner(horizon=2, beam_width=2).plan(_state(hp=74))
    assert len(plan.candidates) == 4
    assert plan.selected_path[0] == "map:1:1:0"
    assert len(plan.candidates) > 1


def test_low_hp_replans_toward_recovery_and_keeps_all_paths() -> None:
    high = RoutePlanner(horizon=2).plan(_state(hp=74))
    low_state = _state(hp=12)
    low = RoutePlanner(horizon=2).replan(low_state)
    assert len(low.candidates) == len(high.candidates)
    assert low.selected_path[0] == "map:1:1:1"
    assert low.selected_path != high.selected_path


def test_gold_and_deck_updates_change_route_scores_deterministically() -> None:
    planner = RoutePlanner(horizon=2)
    poor = planner.plan(_state(gold=0))
    rich = planner.plan(_state(gold=250))
    poor_shop = next(item for item in poor.candidates if "map:1:2:1" in item.path)
    rich_shop = next(item for item in rich.candidates if "map:1:2:1" in item.path)
    assert rich_shop.score != poor_shop.score
    upgraded = planner.plan(_state(deck=[{"semantic_id": "DEFEND", "type": "skill", "cost": 1, "tags": ["block"], "upgrade_level": 1}]))
    assert upgraded.candidates[0].features["deck_health"] != poor.candidates[0].features["deck_health"]


def test_repeat_is_byte_stable() -> None:
    state = _state()
    planner = RoutePlanner(horizon=2, beam_width=3, algorithm="dp")
    assert planner.plan(copy.deepcopy(state)).to_dict() == planner.plan(copy.deepcopy(state)).to_dict()
