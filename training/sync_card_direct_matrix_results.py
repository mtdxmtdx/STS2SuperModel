#!/usr/bin/env python3
"""Synchronize valid direct-card matrix rows with their current reports.

Only rows whose fixture and normalized action ID match are updated. Timeout,
choice, error, and stale cross-card reports remain unchanged instead of being
silently promoted.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"


def normalized_fixture(value: object) -> str:
    text = str(value or "")
    prefix = "trace-v0111-direct3-"
    if text.startswith(prefix):
        text = text[len(prefix) :]
    return text.upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--matrix",
        type=Path,
        default=DATA / "card-direct-matrix-results.json",
    )
    parser.add_argument("--reports", type=Path, default=DATA)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    rows = json.loads(args.matrix.read_text(encoding="utf-8"))
    if not isinstance(rows, list):
        raise ValueError("card direct matrix must be a JSON array")

    aligned = 0
    changed = 0
    for row in rows:
        report_name = row.get("report")
        if not isinstance(report_name, str):
            continue
        report_path = args.reports / report_name
        if not report_path.is_file():
            continue
        try:
            report = json.loads(report_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if report.get("normalized_action_id") != row.get("normalized_action_id"):
            continue
        if normalized_fixture(report.get("fixture")) != str(row.get("variant_id", "")).upper():
            continue
        if not isinstance(report.get("match"), bool) or not isinstance(report.get("mismatch_count"), int):
            continue

        aligned += 1
        updates = {
            "decision": "combat_play" if report.get("action_kind") == "play_card" else row.get("decision"),
            "match": report["match"],
            "mismatch_count": report["mismatch_count"],
            "confidence": report.get("confidence", "Uncalculable"),
            "error": None,
        }
        if any(row.get(key) != value for key, value in updates.items()):
            changed += 1
            row.update(updates)

    output = args.output or args.matrix
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"rows": len(rows), "aligned": aligned, "changed": changed, "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
