import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COVERAGE = ROOT / "data/relics/v0.111/relic-coverage.json"
CLOSEOUT = ROOT / "data/relics/v0.111/d2-relic-closeout.json"
TARGET_IDS = {
    "DIVINE_RIGHT", "DATA_DISK", "GORGET", "STONE_CRACKER", "GIRYA",
    "EMBER_TEA", "SWORD_OF_JADE", "SLING_OF_COURAGE", "GREMLIN_HORN",
    "BOOK_REPAIR_KNIFE", "LIZARD_TAIL", "RUNIC_PYRAMID", "BOOKMARK",
    "RINGING_TRIANGLE", "PAPER_PHROG", "PAPER_KRANE", "BIIIG_HUG",
    "THE_ABACUS", "GALACTIC_DUST", "HISTORY_COURSE",
}


def test_d2_targets_have_current_turn_and_terminal_classification() -> None:
    coverage = json.loads(COVERAGE.read_text(encoding="utf-8-sig"))
    rows = {row["relic_id"]: row for row in coverage["relics"] if row["relic_id"] in TARGET_IDS}
    assert set(rows) == TARGET_IDS
    assert all(row.get("affects_current_turn") is not None for row in rows.values())
    assert rows["DIVINE_RIGHT"]["reliable_eligible"] is True

    assert CLOSEOUT.is_file()
    closeout = json.loads(CLOSEOUT.read_text(encoding="utf-8-sig"))
    assert {row["relic_id"] for row in closeout["relics"]} == TARGET_IDS
    assert all(row["terminal_status"] in {"Reliable", "UnsupportedWithReason"} for row in closeout["relics"])
    assert all(row["reason"] for row in closeout["relics"])


def test_relic_injection_contract_remains_available_after_d1() -> None:
    import collect_nosl_root_states as collector

    assert collector.parse_injection_sets("DIVINE_RIGHT|GIRYA+GORGET") == [
        ["DIVINE_RIGHT"],
        ["GIRYA", "GORGET"],
    ]
