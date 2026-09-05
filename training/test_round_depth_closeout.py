import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ROUND_DEPTH = ROOT / "data" / "combat_model" / "round-depth-v1"
HOLDOUTS = ROOT / "data" / "combat_model" / "holdouts"


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def test_round_depth_probe_and_merged_dataset_pass_acceptance() -> None:
    probe = load(ROUND_DEPTH / "cost-probe.json")
    profile = load(ROUND_DEPTH / "coverage-profile.json")
    quality = load(ROUND_DEPTH / "quality-gate.json")

    assert probe["verdict"] == "pass"
    assert [row["turns"] for row in probe["levels"]] == [3, 6, 10, 14]
    selected = next(row for row in probe["levels"] if row["turns"] == probe["selection"]["selected_turns"])
    assert selected["turns"] == 14
    assert selected["reliable_ratio"] >= 0.70
    assert selected["search_complete_ratio"] == 1
    assert selected["budget_bound_count"] == 0

    assert quality["verdict"] == "pass"
    assert quality["failures"] == []
    assert quality["public_leakage_count"] == 0
    assert quality["duplicate_states"]["error_count"] == 0
    assert quality["malformed_lines"] == []
    assert profile["reliable_coverage"]["round_ge_8"]["ratio"] >= 0.20
    ratios = [entry["ratio"] for entry in profile["all_rows"]["character_distribution"].values()]
    assert all(0.45 <= ratio <= 0.55 for ratio in ratios)


def test_round_deep_holdout_is_frozen_and_repeatable() -> None:
    holdout = load(HOLDOUTS / "holdout-round-deep-v1.json")
    repeat = load(HOLDOUTS / "holdout-round-deep-v1-repeat-verification.json")

    assert holdout["holdout_id"] == "holdout-round-deep-v1"
    assert holdout["coverage_profile"]["round_min"] >= 8
    assert holdout["coverage_profile"]["round_max"] == 10
    assert repeat["verdict"] == "pass"
    assert repeat["byte_identical"] is True
    assert repeat["first_sha256"] == repeat["second_sha256"]
