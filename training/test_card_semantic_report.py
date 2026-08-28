import json

from build_card_semantic_report import build


from pathlib import Path


def test_card_report_counts_variants_and_special_categories():
    cards = {
        "schema_version": 3,
        "game_version": "0.111.0",
        "cards": [
            {
                "id": "A", "character": "铁甲战士", "type": "攻击", "rarity": "初始",
                "description": "造成1点伤害。消耗。", "multiplayer_only": False,
                "upgraded": {"id": "A_UP", "description": "造成2点伤害。消耗。"},
            },
            {
                "id": "B", "character": "多人", "type": "技能", "rarity": "普通",
                "description": "随机获得1点格挡。", "multiplayer_only": True,
                "upgraded": None,
            },
        ],
    }
    semantics = {
        "schema_version": 1, "game_version": "0.111.0",
        "variants": [
            {"id": "A", "upgraded": False, "source_text": "造成1点伤害。消耗。",
             "operations": [{"kind": "damage", "id": None, "trigger": None, "timing": "immediate"}, {"kind": "status", "id": "消耗", "trigger": None, "timing": "immediate"}],
             "unparsed_clauses": [], "is_fully_structured": True, "is_immediately_executable": True,
             "is_simulator_executable": True, "runtime_handler_resolvable": True},
            {"id": "A_UP", "upgraded": True, "source_text": "造成2点伤害。消耗。",
             "operations": [{"kind": "damage", "id": None, "trigger": None, "timing": "immediate"}, {"kind": "status", "id": "消耗", "trigger": None, "timing": "immediate"}],
             "unparsed_clauses": [], "is_fully_structured": True, "is_immediately_executable": True,
             "is_simulator_executable": True, "runtime_handler_resolvable": True},
            {"id": "B", "upgraded": False, "source_text": "随机获得1点格挡。",
             "operations": [{"kind": "block", "id": None, "trigger": None, "timing": "immediate", "random_source": "CombatTargets"}],
             "unparsed_clauses": ["随机获得1点格挡"], "is_fully_structured": False, "is_immediately_executable": False,
             "is_simulator_executable": False, "runtime_handler_resolvable": False},
        ],
    }
    fixture_dir = Path(__file__).parent / ".card-report-test-fixture"
    fixture_dir.mkdir(exist_ok=True)
    cards_path = fixture_dir / "cards.json"
    semantics_path = fixture_dir / "semantics.json"
    try:
        cards_path.write_text(json.dumps(cards, ensure_ascii=False), encoding="utf-8")
        semantics_path.write_text(json.dumps(semantics, ensure_ascii=False), encoding="utf-8")
        report = build(cards_path, semantics_path)

        assert report["all"]["variants"] == 3
        assert report["single_player_combat_scope"]["variants"] == 2
        assert report["all"]["fully_structured"] == 2
        assert report["semantic_categories"]["exhaust"]["single_player_variants"] == 2
        assert report["semantic_categories"]["random"]["all_variants"] == 1
    finally:
        cards_path.unlink(missing_ok=True)
        semantics_path.unlink(missing_ok=True)
        fixture_dir.rmdir()
