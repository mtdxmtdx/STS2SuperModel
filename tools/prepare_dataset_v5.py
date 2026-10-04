#!/usr/bin/env python3
"""Prepare one immutable reviewed v5 cohort; never generate labels or fit."""
import argparse
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from nosl.prepare_v5 import persist_snapshot
from nosl.schema_v5 import load_config, loads


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("candidates", type=Path, help="New candidate envelope JSONL, not diagnostic tape records")
    parser.add_argument("--attempts", required=True, type=Path, help="Complete ordered attempt ledger JSON")
    parser.add_argument("--admission", required=True, type=Path, help="Externally reviewed exact-bound receipt JSON")
    parser.add_argument("--protection", required=True, type=Path)
    parser.add_argument("--config", type=Path, default=ROOT / "configs/data_pipeline.v5.json")
    parser.add_argument("--student-config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    paths = (args.candidates, args.attempts, args.admission, args.protection, args.config, args.student_config)
    before = {p: p.read_bytes() for p in paths}
    records = [loads(line) for line in before[args.candidates].decode().splitlines() if line.strip()]
    values = [loads(before[p].decode()) for p in (args.attempts, args.admission, args.protection, args.config, args.student_config)]
    if any(p.read_bytes() != value for p, value in before.items()): raise ValueError("inputs changed during read")
    result = persist_snapshot(args.output_dir, records, *values)
    print(__import__("json").dumps(result["report"], allow_nan=False))


if __name__ == "__main__": main()
