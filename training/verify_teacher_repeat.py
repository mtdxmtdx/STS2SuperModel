#!/usr/bin/env python3
"""Compare teacher datasets while excluding explicitly volatile runtime metrics."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
from typing import Any


VOLATILE_SEARCH_METRICS = {
    "cpu_time",
    "allocated_bytes",
    "gen0_collections",
    "gen1_collections",
    "gen2_collections",
    "bytes_per_node",
}


def file_digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def semantic_row(row: dict[str, Any]) -> dict[str, Any]:
    result = copy.deepcopy(row)
    metrics = result.get("search_metrics")
    if isinstance(metrics, dict):
        for key in VOLATILE_SEARCH_METRICS:
            metrics.pop(key, None)
    return result


def canonical(row: dict[str, Any]) -> bytes:
    return json.dumps(
        semantic_row(row), ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")


def load(path: Path) -> list[dict[str, Any]]:
    rows = []
    with path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError(f"{path}:{line_no}: row must be an object")
            rows.append(value)
    return rows


def compare(first: Path, second: Path) -> dict[str, Any]:
    left = load(first)
    right = load(second)
    left_by_state = {str(row.get("state_hash_public") or row.get("public_state_hash")): row for row in left}
    right_by_state = {str(row.get("state_hash_public") or row.get("public_state_hash")): row for row in right}
    missing_in_second = sorted(set(left_by_state) - set(right_by_state))
    added_in_second = sorted(set(right_by_state) - set(left_by_state))
    different = []
    for index, key in enumerate(sorted(set(left_by_state) & set(right_by_state))):
        a, b = left_by_state[key], right_by_state[key]
        if canonical(a) != canonical(b):
            different.append({
                "index": index,
                "state_hash_public": key,
                "first_record_id": a.get("record_id"),
                "second_record_id": b.get("record_id"),
            })
    semantic_hasher_a = hashlib.sha256()
    semantic_hasher_b = hashlib.sha256()
    for key in sorted(left_by_state):
        semantic_hasher_a.update(canonical(left_by_state[key]) + b"\n")
    for key in sorted(right_by_state):
        semantic_hasher_b.update(canonical(right_by_state[key]) + b"\n")
    return {
        "schema_version": 1,
        "verdict": "pass" if len(left) == len(right) and not missing_in_second and not added_in_second and not different else "fail",
        "first": str(first),
        "second": str(second),
        "first_rows": len(left),
        "second_rows": len(right),
        "different_rows": len(different),
        "different_examples": different[:20],
        "missing_in_second": missing_in_second[:20],
        "added_in_second": added_in_second[:20],
        "ignored_fields": [f"search_metrics.{key}" for key in sorted(VOLATILE_SEARCH_METRICS)],
        "first_raw_sha256": file_digest(first),
        "second_raw_sha256": file_digest(second),
        "first_semantic_sha256": semantic_hasher_a.hexdigest().upper(),
        "second_semantic_sha256": semantic_hasher_b.hexdigest().upper(),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--first", required=True, type=Path)
    parser.add_argument("--second", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = compare(args.first, args.second)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if report["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
