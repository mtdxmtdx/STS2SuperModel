#!/usr/bin/env python3
"""Preserve native v5 empirical values in a sealed, unadmitted policy export."""
import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from nosl.native_policy_v5 import (adapt_native_policy_candidates, admit_native_policy_cohort,
                                 validate_policy_producer_receipt, _digest)
from nosl.schema_v5 import load_config, loads


def encoded(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw_candidates", type=Path)
    parser.add_argument("--attempts", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path, help="Original native report with the full predeclared source list")
    parser.add_argument("--protection", required=True, type=Path, help="Complete imported metadata-only history registry")
    parser.add_argument("--config", type=Path, default=ROOT / "configs/student.v5.engineering.json")
    parser.add_argument("--output", required=True, type=Path, help="A new immutable export directory")
    parser.add_argument("--review", type=Path, help="Optional separately supplied exact-cohort review; never grants fitting permission")
    args = parser.parse_args()
    paths = [args.raw_candidates, args.attempts, args.report, args.protection, args.config]
    if args.review is not None: paths.append(args.review)
    inputs = {path: path.read_bytes() for path in paths}
    config = load_config(args.config)
    protection = loads(inputs[args.protection].decode())
    records, receipt = adapt_native_policy_candidates(inputs[args.raw_candidates], inputs[args.attempts],
                                                       inputs[args.report], config, protection)
    if args.review is not None:
        records, receipt = admit_native_policy_cohort(receipt, loads(inputs[args.review].decode()), config)
    validate_policy_producer_receipt(receipt, config, protection)
    if any(path.read_bytes() != before for path, before in inputs.items()):
        raise ValueError("source/config/protection/review changed during adaptation")
    args.output.mkdir(parents=True, exist_ok=False)
    outputs = {
        "records.jsonl": b"".join(encoded(row) + b"\n" for row in records),
        "producer-receipts.json": encoded({_digest(receipt): receipt}),
    }
    import hashlib
    for name, payload in outputs.items():
        with (args.output / name).open("xb") as handle: handle.write(payload)
    summary = {"format": "nosl.native-full-policy.export.v5.1", "producer_receipt_sha256": _digest(receipt),
        "records": len(records), "attempts": len(receipt["attempts"]),
        "source_value_rows": sum(action["masks"]["value"] for row in records for action in row["targets"]["actions"]),
        "pairwise_pairs": 0, "finite_plan_labels": 0, "objective_calibrated": False,
        "cohort_admitted": receipt["review"] is not None, "fit_authorized": False, "formal_labels": False,
        "files": {name: {"sha256": hashlib.sha256(payload).hexdigest(), "bytes": len(payload)} for name, payload in outputs.items()}}
    # This final marker is written only after all source/receipt checks succeed.
    with (args.output / "summary.json").open("xb") as handle: handle.write(encoded(summary))
    print(f"Preserved {summary['source_value_rows']} native value rows in {len(records)} full-v5 records and {len(receipt['attempts'])} attempts; fitting remains unauthorized")


if __name__ == "__main__": main()
