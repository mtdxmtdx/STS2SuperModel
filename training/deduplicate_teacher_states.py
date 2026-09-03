#!/usr/bin/env python3
"""Deterministically deduplicate teacher records by public NOSL state hash."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def label_signature(row: dict[str, Any]) -> str:
    value = {
        "teacher_best_actions": row.get("teacher_best_actions", []),
        "teacher_top_k": row.get("teacher_top_k", []),
        "action_values": row.get("action_values", {}),
        "confidence": row.get("confidence"),
        "label_quality": row.get("label_quality"),
    }
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def deduplicate(input_path: Path, output_path: Path) -> dict[str, Any]:
    first: dict[str, dict[str, Any]] = {}
    signatures: dict[str, set[str]] = {}
    occurrences: dict[str, int] = {}
    total = 0
    with input_path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError(f"{input_path}:{line_no}: row must be an object")
            state_hash = row.get("state_hash_public") or row.get("public_state_hash")
            if not state_hash:
                raise ValueError(f"{input_path}:{line_no}: state_hash_public is required")
            key = str(state_hash)
            total += 1
            first.setdefault(key, row)
            occurrences[key] = occurrences.get(key, 0) + 1
            signatures.setdefault(key, set()).add(label_signature(row))

    conflicts = sorted(key for key, values in signatures.items() if len(values) > 1)
    if conflicts:
        raise ValueError(f"conflicting labels for {len(conflicts)} public states: {conflicts[:10]}")

    rows = [first[key] for key in sorted(first)]
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    result = {
        "schema_version": 1,
        "input": str(input_path),
        "output": str(output_path),
        "input_rows": total,
        "output_rows": len(rows),
        "removed_rows": total - len(rows),
        "duplicate_state_groups": sum(1 for count in occurrences.values() if count > 1),
        "conflicting_state_groups": len(conflicts),
        "ordering": "state_hash_public ascending; first source row retained",
    }
    print(json.dumps(result, ensure_ascii=False))
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    deduplicate(args.input, args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
