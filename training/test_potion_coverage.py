import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COVERAGE = ROOT / "data" / "potions" / "v0.111" / "potion-coverage.json"
RUNTIME = ROOT / "data" / "potions" / "v0.111" / "potion-runtime-catalog.json"


def test_potion_coverage_closes_runtime_registry() -> None:
    coverage = json.loads(COVERAGE.read_text(encoding="utf-8"))
    runtime = json.loads(RUNTIME.read_text(encoding="utf-8"))
    rows = coverage["potions"]
    summary = coverage["summary"]

    assert summary["total_potions"] == runtime["total_potions"] == len(rows) == 66
    assert {row["potion_id"] for row in rows} == {
        row["potion_id"] for row in runtime["potions"]
    }
    assert summary["unknown_count"] == 0
    assert all(row["next_action"] for row in rows)
    assert summary["structured_count"] == 66
    assert summary["runtime_probed_count"] == 30
    assert summary["reliable_eligible_count"] == sum(
        row["reliable_eligible"] for row in rows
    )


def test_potion_simulator_claims_match_adapter_audit() -> None:
    rows = json.loads(COVERAGE.read_text(encoding="utf-8"))["potions"]
    catalog = json.loads(RUNTIME.read_text(encoding="utf-8"))["potions"]
    supported = {row["potion_id"] for row in rows if row["simulator_supported"]}
    reliable = {row["potion_id"] for row in rows if row["reliable_eligible"]}

    assert supported == {row["potion_id"] for row in catalog if row["simulator_supported"]}
    assert len(supported) == 28
    assert reliable == supported
