from __future__ import annotations

from pathlib import Path
import hashlib

from training.global_decision.global_dataset_audit import audit_global_prototype


def test_global_prototype_quality_audit_passes() -> None:
    root = Path(__file__).resolve().parents[1] / "data" / "global_prototype"
    source = root / "global-synthetic-act1-v0.jsonl"
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    audit = audit_global_prototype(root)
    after = hashlib.sha256(source.read_bytes()).hexdigest()
    assert audit["passed"] is True
    assert audit["public_leakage_count"] == 0
    assert audit["reliable_count"] == 0
    assert audit["jsonl"]["malformed"] == 0
    assert audit["splits"]["scenario_split_conflicts"] == 0
    assert before == after == audit["source_data_sha256"]
    assert audit["repeatability"]["repeat_jsonl_equal"] is True
    assert audit["repeatability"]["model_prediction"]["repeat_equal"] is True
    assert all(audit["repeatability"]["smoke_report_hashes"].values())
    assert audit["coverage"]["coverage_failures"] == {}


def test_audit_reports_required_smoke_coverage() -> None:
    root = Path(__file__).resolve().parents[1] / "data" / "global_prototype"
    coverage = audit_global_prototype(root)["coverage"]["smoke"]
    assert {"buy_card", "buy_relic", "buy_potion", "remove", "leave"} <= set(coverage["shop_poor_gold"]["kinds"])
    assert {"rest", "smith", "leave"} <= set(coverage["campfire_low_hp"]["kinds"])
    assert {"normal", "proceed", "leave", "cancel"} <= set(coverage["event_low_hp"]["kinds"])
    assert {"normal", "proceed", "leave", "cancel"} <= set(coverage["ancient_low_hp"]["kinds"])
