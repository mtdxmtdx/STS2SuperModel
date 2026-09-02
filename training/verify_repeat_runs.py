#!/usr/bin/env python3
"""Run the P0/P1 semantic probes twice and compare report hashes."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

from run_p1_power_probes import P0_PROBES, PROBES as POWER_PROBES
from run_p1_relic_probes import PROBES as RELIC_PROBES
from run_p1_card_probes import CARD_PROBES


ROOT = Path(__file__).resolve().parents[1]
DATA_DIR = ROOT / "data"


def _power_report_name(name: str, ordinal: int, ordinals: list[int]) -> str:
    prefix = "p0-csharp" if name.startswith("p0-") else "p1-csharp"
    slug = name.removeprefix("p0-").removeprefix("p1-power-")
    suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
    return f"{prefix}-{slug}-diff-report{suffix}.json"


def expected_report_names() -> set[str]:
    names: set[str] = set()
    for fixture, ordinals in {**P0_PROBES, **POWER_PROBES}.items():
        names.update(_power_report_name(fixture, ordinal, ordinals) for ordinal in ordinals)
    for fixture, ordinals in RELIC_PROBES.items():
        for ordinal in ordinals:
            suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
            names.add(f"p1-csharp-{fixture.removeprefix('p1-')}-diff-report{suffix}.json")
    for fixture, ordinals in CARD_PROBES.items():
        for ordinal in ordinals:
            suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
            names.add(f"p1-csharp-{fixture.removeprefix('p1-')}-diff-report{suffix}.json")
    return names


def report_hashes(names: set[str] | None = None) -> dict[str, str]:
    expected = names or expected_report_names()
    return {
        name: hashlib.sha256((DATA_DIR / name).read_bytes()).hexdigest()
        for name in sorted(expected)
        if (DATA_DIR / name).is_file()
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


def annotate_repeat_hashes(hashes: dict[str, str]) -> None:
    """Persist the raw, pre-annotation hash in each generated report."""
    for name, digest in hashes.items():
        path = DATA_DIR / name
        payload = json.loads(path.read_text(encoding="utf-8"))
        payload["repeat_sha256"] = digest
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path,
                        default=DATA_DIR / "p1-repeat-verification.json")
    args = parser.parse_args()
    commands = [
        [sys.executable, "training/run_p1_power_probes.py", "--include-p0", "--allow-degraded"],
        [sys.executable, "training/run_p1_relic_probes.py"],
        [sys.executable, "training/run_p1_card_probes.py"],
    ]
    first = [run(command) for command in commands]
    if any(item["returncode"] != 0 for item in first):
        payload = {"schema_version": 1, "verdict": "fail", "reason": "first_run_failed", "runs": first}
        args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        return 1
    expected = expected_report_names()
    actual = {path.name for path in DATA_DIR.glob("p0-csharp-*-diff-report*.json")}
    actual |= {path.name for path in DATA_DIR.glob("p1-csharp-*-diff-report*.json")}
    unexpected = sorted(actual - expected)
    hashes_a = report_hashes(expected)
    second = [run(command) for command in commands]
    hashes_b = report_hashes(expected)
    missing = sorted(set(hashes_a) - set(hashes_b))
    added = sorted(set(hashes_b) - set(hashes_a))
    different = sorted(name for name in set(hashes_a) & set(hashes_b)
                       if hashes_a[name] != hashes_b[name])
    quality_counts = {"Reliable": 0, "Estimated": 0, "Uncalculable": 0, "Unknown": 0}
    for name in sorted(hashes_a):
        report_path = DATA_DIR / name
        if not report_path.is_file():
            continue
        try:
            confidence = json.loads(report_path.read_text(encoding="utf-8")).get("confidence")
        except (OSError, json.JSONDecodeError):
            confidence = "Unknown"
        quality_counts[confidence if confidence in quality_counts else "Unknown"] += 1
    payload = {
        "schema_version": 1,
        "verdict": "pass" if not missing and not added and not different and not unexpected
                    and len(hashes_a) > 0
                    and all(item["returncode"] == 0 for item in second) else "fail",
        "report_count": len(hashes_a),
        "quality_counts": quality_counts,
        "run_a": first,
        "run_b": second,
        "different_reports": different,
        "missing_reports": missing,
        "added_reports": added,
        "unexpected_reports": unexpected,
        "expected_report_count": len(expected),
        "report_groups": {
            "p0": sum(name.startswith("p0-") for name in expected),
            "p1_power": sum(name.startswith("p1-csharp-") and "relic-" not in name and "card-" not in name for name in expected),
            "p1_relic": sum(name.startswith("p1-csharp-relic-") for name in expected),
            "p1_card": sum(name.startswith("p1-csharp-card-") for name in expected),
        },
        "version_lock": {
            "game_version": "v0.111.0",
            "game_commit": "41cef1ea",
            "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0",
            "trace_schema": 1,
        },
        "sha256": hashes_a,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if payload["verdict"] == "pass":
        annotate_repeat_hashes(hashes_a)
    print(json.dumps({"verdict": payload["verdict"], "report_count": len(hashes_a),
                      "different": len(different), "missing": len(missing), "added": len(added)},
                     ensure_ascii=False))
    return 0 if payload["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
