#!/usr/bin/env python3
"""Run the P0/P1 semantic probes twice and compare report hashes."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATA_DIR = ROOT / "data"


def report_hashes() -> dict[str, str]:
    paths = sorted(
        [*DATA_DIR.glob("p0-csharp-*-diff-report*.json"),
         *DATA_DIR.glob("p1-csharp-*-diff-report*.json")],
        key=lambda path: path.name,
    )
    return {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in paths
    }


def run(command: list[str]) -> dict[str, object]:
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                            encoding="utf-8", check=False)
    return {
        "command": command,
        "returncode": result.returncode,
        "stdout_tail": result.stdout[-2000:],
        "stderr_tail": result.stderr[-1000:],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path,
                        default=DATA_DIR / "p1-repeat-verification.json")
    args = parser.parse_args()
    commands = [
        [sys.executable, "training/run_p1_power_probes.py", "--include-p0"],
        [sys.executable, "training/run_p1_relic_probes.py"],
    ]
    first = [run(command) for command in commands]
    if any(item["returncode"] != 0 for item in first):
        payload = {"schema_version": 1, "verdict": "fail", "reason": "first_run_failed", "runs": first}
        args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        return 1
    hashes_a = report_hashes()
    second = [run(command) for command in commands]
    hashes_b = report_hashes()
    missing = sorted(set(hashes_a) - set(hashes_b))
    added = sorted(set(hashes_b) - set(hashes_a))
    different = sorted(name for name in set(hashes_a) & set(hashes_b)
                       if hashes_a[name] != hashes_b[name])
    payload = {
        "schema_version": 1,
        "verdict": "pass" if not missing and not added and not different
                    and len(hashes_a) > 0
                    and all(item["returncode"] == 0 for item in second) else "fail",
        "report_count": len(hashes_a),
        "run_a": first,
        "run_b": second,
        "different_reports": different,
        "missing_reports": missing,
        "added_reports": added,
        "sha256": hashes_a,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": payload["verdict"], "report_count": len(hashes_a),
                      "different": len(different), "missing": len(missing), "added": len(added)},
                     ensure_ascii=False))
    return 0 if payload["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
