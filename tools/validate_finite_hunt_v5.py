#!/usr/bin/env python3
"""Validate freshly executed constructed full-v5 Hunt evidence, optionally adapt it.

No generation, admission, model fitting or optimizer operation is performed.
"""
import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from nosl.finite_hunt_v5 import adapt_jsonl, validate_adapted_record
from nosl.schema_v5 import load_config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("--config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    parser.add_argument("--output", type=Path, help="Optional new engineering JSONL artifact")
    args = parser.parse_args()
    payload = args.raw.read_bytes()
    config = load_config(args.config)
    rows = adapt_jsonl(payload, config)
    for row in rows: validate_adapted_record(row, config)
    if args.raw.read_bytes() != payload: raise ValueError("raw evidence changed during validation")
    if args.output:
        with args.output.open("xb") as handle:
            for row in rows:
                handle.write((json.dumps(row, separators=(",", ":"), ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8"))
    print(f"Validated {len(rows)} fresh constructed Hunt records; trainable:false, formal_labels:false, natural admission unavailable")


if __name__ == "__main__": main()
