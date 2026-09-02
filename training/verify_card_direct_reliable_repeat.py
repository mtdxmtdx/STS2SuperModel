#!/usr/bin/env python3
"""Re-run every current direct-Reliable card report twice and verify bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parent
DATA = ROOT / "data"
SHADOW = ROOT / "training/ShadowDiff/bin/Release/net9.0/STS2BestChoice.ShadowDiff.dll"
WITNESS = DATA / "card-direct-witness-manifest.json"
REPEAT_INDEX = DATA / "card-direct-repeat-verification.json"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path: Path) -> str:
    return path.relative_to(WORKSPACE).as_posix()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output",
        type=Path,
        default=DATA / "m3e-card-direct-reliable-repeat-v49.json",
    )
    args = parser.parse_args()

    witness = json.loads(WITNESS.read_text(encoding="utf-8"))
    rows = sorted(
        (row for row in witness["rows"] if row["status"] == "direct_reliable"),
        key=lambda row: row["variant_id"],
    )
    records: list[dict[str, object]] = []
    first_hashes: dict[str, str] = {}
    second_hashes: dict[str, str] = {}

    for row in rows:
        trace = WORKSPACE / row["trace_path"]
        report = WORKSPACE / row["report_path"]
        command = ["dotnet", str(SHADOW), str(trace), str(report)]
        first = subprocess.run(command, cwd=WORKSPACE, capture_output=True, check=False)
        first_hash = digest(report) if report.is_file() else ""
        second = subprocess.run(command, cwd=WORKSPACE, capture_output=True, check=False)
        second_hash = digest(report) if report.is_file() else ""
        name = report.name
        first_hashes[name] = first_hash
        second_hashes[name] = second_hash
        records.append(
            {
                "variant_id": row["variant_id"],
                "trace": relative(trace),
                "report": name,
                "exit_a": first.returncode,
                "exit_b": second.returncode,
                "sha256": second_hash,
                "identical": first.returncode == second.returncode == 0
                and first_hash == second_hash,
            }
        )

    different = [record["variant_id"] for record in records if not record["identical"]]
    payload = {
        "schema_version": 1,
        "scope": "current_direct_reliable",
        "version_lock": witness["version_lock"],
        "report_count": len(records),
        "different_count": len(different),
        "different_variants": different,
        "verdict": "pass" if records and not different else "fail",
        "reports": records,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    repeat = json.loads(REPEAT_INDEX.read_text(encoding="utf-8"))
    repeat.setdefault("first_report_sha256", {}).update(first_hashes)
    repeat.setdefault("second_report_sha256", {}).update(second_hashes)
    repeat["comparison"] = {
        "first_count": len(repeat["first_report_sha256"]),
        "second_count": len(repeat["second_report_sha256"]),
        "common_count": len(
            set(repeat["first_report_sha256"]) & set(repeat["second_report_sha256"])
        ),
        "different_count": len(different),
        "different": different,
        "verdict": payload["verdict"],
        "current_direct_reliable_count": len(records),
    }
    REPEAT_INDEX.write_text(
        json.dumps(repeat, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps({
        "report_count": len(records),
        "different_count": len(different),
        "verdict": payload["verdict"],
        "output": str(args.output),
    }, ensure_ascii=False))
    return 0 if payload["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
