import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CLOSEOUT = ROOT / "data" / "relics" / "v0.111" / "turnstart-evidence-closeout.json"


def test_turnstart_closeout_has_terminal_status_for_all_35_candidates() -> None:
    document = json.loads(CLOSEOUT.read_text(encoding="utf-8"))
    summary = document["summary"]
    rows = document["relics"]

    assert document["verdict"] == "pass"
    assert summary["target_count"] == len(rows) == 35
    assert summary["reliable_count"] == 1
    assert summary["pending_count"] == 34
    assert summary["strict_evidence_rule_changed"] is False
    assert all(row["terminal_status"] in {"Reliable", "PendingWithReason"} for row in rows)
    assert all(row["terminal_reason"] for row in rows)
    assert next(row for row in rows if row["relic_id"] == "RING_OF_THE_DRAKE")["terminal_status"] == "Reliable"


def test_semantic_hold_status_uses_current_catalog_truth() -> None:
    document = json.loads(CLOSEOUT.read_text(encoding="utf-8"))
    assert document["semantic_holds"] == [
        {
            "relic_id": "PARRYING_SHIELD",
            "status": "PartiallySupported",
            "reason": document["semantic_holds"][0]["reason"],
        }
    ]
    assert document["resolved_stale_hold"]["relic_id"] == "UNCEASING_TOP"
    assert document["resolved_stale_hold"]["reliable_eligible"] is True
