#!/usr/bin/env python3
"""Fast quality gate for the relic/card semantic rework."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"
LOCK = {"game_version": "v0.111.0", "game_commit": "41cef1ea", "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9", "cli_protocol_version": "0.2.0", "trace_schema": 1}


def load(name: str) -> dict:
    return json.loads((DATA / name).read_text(encoding="utf-8-sig"))


def main() -> int:
    failures: list[str] = []
    repeat = load("p1-repeat-verification.json")
    reports = []
    for name in sorted(repeat.get("sha256", {})):
        path = DATA / name
        if not path.is_file():
            failures.append(f"missing report: {name}")
            continue
        report = load(name)
        reports.append(report)
        for key, expected in LOCK.items():
            if report.get(key) != expected:
                failures.append(f"{name}: version mismatch {key}")
        if report.get("match") is not True or report.get("mismatch_count") != 0:
            failures.append(f"{name}: mismatch_count/match gate")
        confidence = report.get("confidence")
        if confidence == "Reliable":
            if report.get("outcome_quality") != "Exact":
                failures.append(f"{name}: Reliable without Exact outcome")
            if report.get("comparison_scope") not in {"strict_public_state", "terminal_summary"}:
                failures.append(f"{name}: Reliable with non-strict scope")
            if report.get("probability_known") is not True:
                failures.append(f"{name}: Reliable without known probability")
    if len(reports) != repeat.get("expected_report_count"):
        failures.append("repeat manifest/report count mismatch")

    card = load("card-semantic-signature-report.json")
    summary = card.get("summary", {})
    if summary.get("machine_checks", {}).get("signature_projection_collisions") != 0:
        failures.append("card signature projection collisions")
    if not (DATA / "card-semantic-evidence-manifest.json").is_file():
        failures.append("card evidence manifest missing")
    else:
        manifest = load("card-semantic-evidence-manifest.json")
        for fixture in manifest.get("fixture_registry", []):
            if not fixture.get("fixture_id", "").startswith("p1-card-"):
                failures.append("non-card fixture leaked into card evidence manifest")

    coverage = load("relics/v0.111/relic-coverage.json")
    cov = coverage.get("summary", {})
    if cov.get("unknown_count") != 0:
        failures.append("relic unknown_count is non-zero")
    ids = {item.get("relic_id"): item for item in coverage.get("relics", [])}
    for rid in ("GIRYA", "BOOKMARK", "DATA_DISK", "PARRYING_SHIELD", "UNCEASING_TOP"):
        if ids.get(rid, {}).get("support_status") == "OutOfScope":
            failures.append(f"combat relic misclassified OutOfScope: {rid}")

    result = {
        "schema_version": 1,
        "version_lock": LOCK,
        "report_count": len(reports),
        "quality_counts": repeat.get("quality_counts", {}),
        "relic_summary": cov,
        "card_summary": {k: summary.get(k) for k in ("single_player_variants", "signatures", "signatures_behavior_verified", "signatures_with_behavior_gap", "reliable_eligible_signatures")},
        "failures": failures,
        "verdict": "pass" if not failures else "fail",
    }
    (DATA / "semantic-rework-gate.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": result["verdict"], "report_count": len(reports), "failures": len(failures)}, ensure_ascii=False))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
