"""Recompute character distribution directly from a labelled JSONL dataset."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


LOCK_FIELDS = (
    "game_version", "game_commit", "assembly_sha256", "cli_protocol_version",
    "trace_schema",
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    counts: Counter[str] = Counter()
    confidence: dict[str, Counter[str]] = defaultdict(Counter)
    version_lock: dict[str, Any] | None = None
    rows = 0
    with args.input.open(encoding="utf-8") as stream:
        for line in stream:
            if not line.strip():
                continue
            row = json.loads(line)
            if version_lock is None:
                version_lock = {field: row.get(field) for field in LOCK_FIELDS}
                version_lock["feature_schema_version"] = "combat-feature-v1"
                version_lock["source_record_feature_schema_version"] = row.get("feature_schema_version")
            character = str(row.get("character") or "Unknown")
            counts[character] += 1
            confidence[character][str(row.get("confidence") or "Unknown")] += 1
            rows += 1
    report = {
        "schema_version": 1,
        "source": str(args.input),
        "source_sha256": hashlib.sha256(args.input.read_bytes()).hexdigest().upper(),
        "row_count": rows,
        "version_lock": version_lock or {},
        "character_distribution": {
            character: {
                "rows": count,
                "ratio": count / max(rows, 1),
                "confidence": dict(sorted(confidence[character].items())),
            }
            for character, count in sorted(counts.items())
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
