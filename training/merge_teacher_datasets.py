#!/usr/bin/env python3
"""Merge teacher JSONL shards and reject conflicting public-state labels."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

from deduplicate_teacher_states import label_signature


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, action="append", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    first: dict[str, dict[str, Any]] = {}
    signatures: dict[str, set[str]] = {}
    occurrences: dict[str, int] = {}
    input_rows = 0
    for path in args.input:
        with path.open(encoding="utf-8") as stream:
            for line_no, line in enumerate(stream, 1):
                if not line.strip():
                    continue
                row = json.loads(line)
                state_hash = row.get("state_hash_public") or row.get("public_state_hash")
                if not state_hash:
                    raise ValueError(f"{path}:{line_no}: state_hash_public is required")
                key = str(state_hash)
                input_rows += 1
                first.setdefault(key, row)
                signatures.setdefault(key, set()).add(label_signature(row))
                occurrences[key] = occurrences.get(key, 0) + 1
    conflicts = sorted(key for key, values in signatures.items() if len(values) > 1)
    if conflicts:
        raise ValueError(f"conflicting labels for {len(conflicts)} public states: {conflicts[:10]}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as stream:
        for key in sorted(first):
            stream.write(json.dumps(first[key], ensure_ascii=False, separators=(",", ":")) + "\n")
    report = {
        "schema_version": 1,
        "inputs": [{"path": str(path), "sha256": sha256(path)} for path in args.input],
        "output": str(args.output),
        "output_sha256": sha256(args.output),
        "input_rows": input_rows,
        "output_rows": len(first),
        "removed_rows": input_rows - len(first),
        "duplicate_state_groups": sum(count > 1 for count in occurrences.values()),
        "conflicting_state_groups": len(conflicts),
        "ordering": "state_hash_public ascending; first input wins only when labels agree",
    }
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
