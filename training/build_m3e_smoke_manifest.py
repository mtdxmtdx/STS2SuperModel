"""Build a small deterministic CLI↔shadow M3e evidence manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path(__file__).resolve().parents[1] / "data")
    parser.add_argument("--output", type=Path, default=None)
    args = parser.parse_args()
    data = args.data
    report_names = [
        "m3e-live-report.json",
        "m3e-live-strike-report.json",
        "m3e-live-potion-report.json",
        "m3e-live-endturn-report.json",
        "m3e-live-random-target-report.json",
        "m3e-live-multi-random-target-report.json",
        "m3e-live-random-orb-report.json",
    ]
    repeat_names = [
        "m3e-live-report-repeat.json",
        "m3e-live-strike-report-repeat.json",
        "m3e-live-potion-report-repeat.json",
        "m3e-live-endturn-report-repeat.json",
        "m3e-live-random-target-report-repeat.json",
        "m3e-live-multi-random-target-report-repeat.json",
        "m3e-live-random-orb-report-repeat.json",
    ]
    rows: list[dict] = []
    failures: list[str] = []
    for name in report_names:
        path = data / name
        if not path.is_file():
            failures.append(f"missing:{name}")
            continue
        report = json.loads(path.read_text(encoding="utf-8"))
        for key, expected in LOCK.items():
            if report.get(key) != expected:
                failures.append(f"lock:{name}:{key}")
        if report.get("match") is not True or report.get("mismatch_count") != 0:
            failures.append(f"mismatch:{name}")
        rows.append({
            "report": name,
            "trace": str(data / (name.replace("-report.json", "-trace.jsonl"))),
            "confidence": report.get("confidence"),
            "outcome_quality": report.get("outcome_quality"),
            "match": report.get("match"),
            "mismatch_count": report.get("mismatch_count"),
            "action_id": report.get("normalized_action_id"),
            "sha256": sha256(path),
        })
    repeat_hashes: dict[str, str] = {}
    for name in repeat_names:
        path = data / name
        if not path.is_file():
            failures.append(f"missing:{name}")
            continue
        repeat_hashes[name] = sha256(path)
    # Each repeat file is paired with the corresponding first-run report.
    for first, repeat in zip(report_names, repeat_names):
        if (data / first).is_file() and (data / repeat).is_file() and sha256(data / first) != sha256(data / repeat):
            failures.append(f"repeat:{first}")
    quality_counts: dict[str, int] = {}
    for row in rows:
        quality_counts[row["confidence"]] = quality_counts.get(row["confidence"], 0) + 1
    result = {
        "schema_version": 1,
        "version_lock": LOCK,
        "reports": rows,
        "report_count": len(rows),
        "repeat_report_count": len(repeat_hashes),
        "quality_counts": quality_counts,
        "reliable_mismatch_free_count": sum(row["confidence"] == "Reliable" for row in rows),
        "failures": failures,
        "verdict": "pass" if not failures else "fail",
    }
    output = args.output or data / "m3e-live-smoke-manifest.json"
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": result["verdict"], "report_count": len(rows), "repeats": len(repeat_hashes), "failures": len(failures)}))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
