import json
from pathlib import Path

import collect_nosl_root_states as collector


ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT.parent / "STS2BestChoice" / "Mod" / "LiveCombatSnapshotAdapter.cs"


def test_potion_set_parser_supports_fixed_empty_and_rotating_sets() -> None:
    assert hasattr(collector, "parse_injection_sets")
    parse = collector.parse_injection_sets
    assert parse("FIRE_POTION|EMPTY,BLOCK_POTION+ENERGY_POTION") == [
        ["FIRE_POTION"],
        [],
        ["BLOCK_POTION", "ENERGY_POTION"],
    ]


def test_potion_catalog_exists_and_contains_runtime_dynamic_values() -> None:
    path = ROOT / "data" / "potions" / "v0.111" / "potion-catalog.json"
    assert path.is_file()
    catalog = json.loads(path.read_text(encoding="utf-8-sig"))
    assert catalog["total_potions"] == 66
    fire = next(row for row in catalog["potions"] if row["potion_id"] == "FIRE_POTION")
    assert fire["runtime_type"].endswith(".FirePotion")
    assert fire["target_type"] == "AnyEnemy"
    assert fire["dynamic_vars"]["Damage"] > 0


def test_potion_cost_and_unknown_priority_are_explicit() -> None:
    source = ADAPTER.read_text(encoding="utf-8")
    assert "OpportunityCost: 8m" not in source
    assert "PotionOpportunityCost" in source
    assert "PriorityHint: effects.Count == 0 ? null" in source
