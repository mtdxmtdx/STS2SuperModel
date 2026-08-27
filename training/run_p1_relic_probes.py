#!/usr/bin/env python3
"""Run v0.111 relic fixtures and emit ShadowDiff reports.

The runner deliberately resolves all paths relative to this checkout so it
can be used from an isolated worktree without touching another clone.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
SHADOW_DIFF = ROOT / "training/ShadowDiff/bin/Release/net9.0/STS2BestChoice.ShadowDiff.exe"
FIXTURE_DIR = ROOT / "training/fixtures"
DATA_DIR = ROOT / "data"

PROBES: dict[str, list[int]] = {
    "p1-relic-tough-bandages": [0],
    "p1-relic-tungsten-rod": [0, 1],
    "p1-relic-unceasing-top": [0, 1, 2],
}


def run_fixture(name: str, ordinals: list[int]) -> list[dict]:
    fixture_path = FIXTURE_DIR / f"{name}-commands.jsonl"
    trace_path = DATA_DIR / f"{name}-trace.jsonl"
    if not fixture_path.is_file():
        return [{"fixture": name, "error": f"missing fixture {fixture_path}"}]
    trace_path.unlink(missing_ok=True)

    commands = [line.strip() for line in fixture_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    env = os.environ.copy()
    env["STS2_TRACE_PATH"] = str(trace_path)
    proc = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env,
    )
    try:
        assert proc.stdin is not None and proc.stdout is not None
        for command in commands:
            proc.stdin.write(command + "\n")
            proc.stdin.flush()
            line = proc.stdout.readline()
            while line and not line.startswith("{"):
                line = proc.stdout.readline()
            if not line:
                break
            reply = json.loads(line)
            if reply.get("type") in ("error", "save_error"):
                return [{"fixture": name, "error": f"CLI error: {reply.get('message')}"}]
            if reply.get("type") == "quit_result":
                break
    finally:
        try:
            proc.stdin.close()  # type: ignore[union-attr]
        except OSError:
            pass
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.terminate()
            proc.wait(timeout=10)

    if not trace_path.is_file():
        return [{"fixture": name, "error": "trace file was not produced"}]

    reports: list[dict] = []
    for ordinal in ordinals:
        suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
        report_path = DATA_DIR / f"p1-csharp-{name.removeprefix('p1-')}-diff-report{suffix}.json"
        result = subprocess.run(
            [str(SHADOW_DIFF), str(trace_path), str(report_path), str(ordinal)],
            capture_output=True, text=True, encoding="utf-8", timeout=120,
        )
        try:
            payload = json.loads(result.stdout)
        except (json.JSONDecodeError, TypeError):
            reports.append({"fixture": name, "ordinal": ordinal, "error":
                            f"ShadowDiff failed: {result.returncode} {result.stderr[-400:]}"})
            continue
        mismatches = [field["field"] for field in payload.get("fields", []) if not field.get("match")]
        reports.append({
            "fixture": name,
            "ordinal": ordinal,
            "match": payload.get("match"),
            "mismatch_count": payload.get("mismatch_count"),
            "confidence": payload.get("confidence"),
            "action_kind": payload.get("action_kind"),
            "normalized_action_id": payload.get("normalized_action_id"),
            "mismatched_fields": mismatches,
            "report": str(report_path.relative_to(ROOT)).replace("\\", "/"),
        })
    return reports


def main() -> int:
    names = sys.argv[1:] or list(PROBES)
    failures = 0
    reports_count = 0
    for name in names:
        if name not in PROBES:
            print(f"skip unknown fixture: {name}")
            continue
        for report in run_fixture(name, PROBES[name]):
            reports_count += 1
            if "error" in report or not report.get("match"):
                failures += 1
                print(f"FAIL {report['fixture']} ordinal={report.get('ordinal')}: "
                      f"{report.get('error', report.get('mismatched_fields'))}")
            else:
                print(f"PASS {report['fixture']} ordinal={report['ordinal']} "
                      f"confidence={report['confidence']} mismatches={report['mismatch_count']}")
    print(f"\n{len(names)} fixtures, {reports_count} reports, {failures} failed reports")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
