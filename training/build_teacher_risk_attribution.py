#!/usr/bin/env python3
"""Summarize teacher-label quality and risk reasons for a JSONL dataset."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path
from typing import Any


def build(path: Path) -> dict[str, Any]:
    labels = Counter()
    qualities = Counter()
    risks = Counter()
    restrictions = Counter()
    rows = 0
    states: set[str] = set()
    for line_no, line in enumerate(path.open(encoding="utf-8"), 1):
        if not line.strip():
            continue
        row = json.loads(line)
        rows += 1
        state = row.get("state_hash_public") or row.get("public_state_hash")
        if state:
            states.add(str(state))
        labels[str(row.get("confidence", "Unknown"))] += 1
        qualities[str(row.get("label_quality", "Unknown"))] += 1
        for risk in row.get("risk_events", []) or []:
            risks[str(risk)] += 1
        for value in (row.get("restricted_reasons") or {}).values():
            for reason in str(value).split(","):
                reason = reason.strip()
                if reason:
                    restrictions[reason] += 1
        for warning in row.get("reconstruction_warnings", []) or []:
            risks[str(warning)] += 1
    return {
        "schema_version": 1,
        "source": str(path),
        "row_count": rows,
        "unique_state_count": len(states),
        "confidence_counts": dict(sorted(labels.items())),
        "label_quality_counts": dict(sorted(qualities.items())),
        "risk_event_counts": dict(risks.most_common()),
        "restricted_reason_counts": dict(restrictions.most_common()),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = build(args.input)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
