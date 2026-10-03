#!/usr/bin/env python3
"""Adapt a new native v5 raw candidate export; never promote diagnostics."""
import argparse
from pathlib import Path
import sys
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from nosl.native_v5 import adapt_native_candidates, adapt_native_attempts
from nosl.prepare_v5 import canonical_bytes
from nosl.schema_v5 import load_config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw_candidates", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--attempts", required=True, type=Path, help="Complete native report attempt array JSON")
    parser.add_argument("--attempts-output", required=True, type=Path)
    parser.add_argument("--config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    args = parser.parse_args()
    raw = args.raw_candidates.read_bytes()
    raw_attempts = args.attempts.read_bytes()
    rows = adapt_native_candidates(raw, load_config(args.config))
    attempts = adapt_native_attempts(raw_attempts, rows)
    if args.attempts.read_bytes() != raw_attempts: raise ValueError("attempt artifact changed during read")
    if args.output.exists() or args.attempts_output.exists(): raise FileExistsError("outputs must be new artifacts")
    if args.raw_candidates.read_bytes() != raw: raise ValueError("source artifact changed during read")
    with args.output.open("xb") as handle:
        for row in rows: handle.write(canonical_bytes(row) + b"\n")
    with args.attempts_output.open("xb") as handle: handle.write(canonical_bytes(attempts))
    print(f"Adapted {len(rows)} auxiliary candidates and {len(attempts)} attempts; cohort admission and fitting remain separate")


if __name__ == "__main__": main()
