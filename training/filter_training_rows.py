#!/usr/bin/env python3
"""Deterministically filter JSONL training rows by public combat attributes."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--minimum-round", type=int, default=0)
    parser.add_argument("--confidence")
    args = parser.parse_args()
    selected = []
    input_rows = 0
    with args.input.open(encoding="utf-8") as stream:
        for line_no, line in enumerate(stream, 1):
            if not line.strip():
                continue
            input_rows += 1
            row = json.loads(line)
            round_number = int(row.get("round") or (row.get("public_state") or {}).get("round") or 0)
            if round_number < args.minimum_round:
                continue
            if args.confidence and row.get("confidence") != args.confidence:
                continue
            selected.append(row)
    selected.sort(key=lambda row: (
        str(row.get("episode_id") or row.get("trace_id") or ""),
        int(row.get("round") or 0),
        str(row.get("state_hash_public") or ""),
    ))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as stream:
        for row in selected:
            stream.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    print(json.dumps({
        "schema_version": 1,
        "input_rows": input_rows,
        "output_rows": len(selected),
        "minimum_round": args.minimum_round,
        "confidence": args.confidence,
        "ordering": "episode_id,round,state_hash_public",
        "output": str(args.output),
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
