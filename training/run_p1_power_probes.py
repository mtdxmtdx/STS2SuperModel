#!/usr/bin/env python3
"""Run the P1 Power CLI fixtures, capture traces, and produce ShadowDiff reports.

Each entry declares the fixture command file and the action ordinals that must
produce a zero-mismatch differential report. Promotion evidence for a Power
requires: fixture executed end-to-end, confidence=Reliable, mismatch_count=0,
repeatable across runs, with pre/post-trigger states compared.

Usage:
    python run_p1_power_probes.py            # run all P1 probes
    python run_p1_power_probes.py thorns     # run selected probes (name substring)
    python run_p1_power_probes.py --include-p0
                                             # also rerun the 21 P0 fixtures
    python run_p1_power_probes.py --allow-degraded
                                             # keep zero-mismatch Estimated/
                                             # Uncalculable reports for audit;
                                             # strict promotion remains the default
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, "sts2-cli-v0111", "src", "Sts2Headless", "bin", "Debug", "net9.0", "Sts2Headless.exe")
SHADOW_DIFF = os.path.join(ROOT, "training", "ShadowDiff", "bin", "Release", "net9.0", "STS2BestChoice.ShadowDiff.exe")
FIXTURE_DIR = os.path.join(ROOT, "training", "fixtures")
DATA_DIR = os.path.join(ROOT, "data")

# P1 Power probes: fixture name -> ordinals to diff.
# The ordinals always include the power-granting play plus the trigger plays so
# both pre- and post-trigger structured state is compared. Fixtures that only
# cover a turn boundary with non-reconstructible enemy intents keep end_turn in
# the fixture for observation but exclude it from the diff ordinals (the same
# documented P0 happy-flower rule).
PROBES: dict[str, list[int]] = {
    "p1-power-thorns": [0, 1],
    "p1-power-accuracy": [0, 1],
    "p1-power-plating": [0, 1],
    "p1-power-poison": [0, 1],
    # PANACHE: power play, first Shiv (counter starts stepping), the Shiv that
    # exhausts four total cards, and the fifth-card AoE trigger.
    "p1-power-panache": [0, 1, 4, 5],
    "p1-power-rage": [0, 1, 2, 3],
    "p1-power-flame-barrier": [0, 1],
    "p1-power-corruption": [0, 1],
    "p1-power-infinite-blades": [0, 1],
    "p1-power-envenom": [0, 1],
    "p1-power-buffer": [0, 1],
}

# P0 matrix (fixtures and traces are already tracked); rerun for regression.
P0_PROBES: dict[str, list[int]] = {
    "p0-vajra-strength": [0],
    "p0-smooth-stone-dexterity": [0],
    "p0-akabeko-vigor": [0],
    "p0-bag-of-marbles": [0],
    "p0-lantern": [0],
    "p0-bag-of-preparation": [0],
    "p0-pen-nib": [0],
    "p0-ring-of-the-snake": [0],
    "p0-multi-enemy-targets": [0, 1, 2],
    "p0-art-of-war": [0, 1],
    # p0-happy-flower keeps only the single-turn boundary; see run_p0_probes.py.
    "p0-happy-flower": [0],
    "p0-orichalcum": [0],
    "p0-vajra-strength-end-turn": [0],
    "p0-centennial-puzzle": [0],
    "p0-pen-nib-double": [12],
    "p0-inflame-strength": [0, 1],
    "p0-demon-form": [0, 1],
    "p0-barricade": [0, 1, 2],
    "p0-rupture": [0, 1],
    "p0-neutralize-weak": [0, 1],
    "p0-afterimage": [0, 1],
}


def _capture_trace(name: str) -> tuple[str | None, str]:
    """Feed the fixture commands to the CLI; return (error, trace_path)."""
    fixture_path = os.path.join(FIXTURE_DIR, f"{name}-commands.jsonl")
    trace_path = os.path.join(DATA_DIR, f"{name}-trace.jsonl")
    if not os.path.isfile(fixture_path):
        return f"missing fixture {fixture_path}", trace_path

    if os.path.exists(trace_path):
        os.remove(trace_path)

    with open(fixture_path, encoding="utf-8") as f:
        cmds = [line.strip() for line in f if line.strip()]
    env = os.environ.copy()
    env["STS2_TRACE_PATH"] = trace_path
    proc = subprocess.Popen(
        [CLI], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env,
    )
    for cmd in cmds:
        proc.stdin.write(cmd + "\n")
        proc.stdin.flush()
        line = proc.stdout.readline()
        while line and not line.startswith("{"):
            line = proc.stdout.readline()
        if not line:
            break
        try:
            reply = json.loads(line)
        except json.JSONDecodeError:
            continue
        if reply.get("type") in ("error", "save_error"):
            proc.stdin.close()
            proc.terminate()
            proc.wait(timeout=10)
            return f"CLI error: {reply.get('message')}", trace_path
        if reply.get("type") == "quit_result":
            break
    try:
        proc.stdin.close()
    except OSError:
        pass
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=10)

    if not os.path.isfile(trace_path):
        return "trace file was not produced", trace_path
    return None, trace_path


def _report_name(name: str, ordinal: int, ordinals: list[int], prefix: str) -> str:
    suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
    return os.path.join(DATA_DIR, f"{prefix}-{name.removeprefix('p0-').removeprefix('p1-power-')}-diff-report{suffix}.json")


def run_fixture(name: str, ordinals: list[int]) -> list[dict]:
    prefix = "p0-csharp" if name.startswith("p0-") else "p1-csharp"
    error, trace_path = _capture_trace(name)
    if error:
        return [{"fixture": name, "error": error}]

    reports = []
    for ordinal in ordinals:
        report_path = _report_name(name, ordinal, ordinals, prefix)
        result = subprocess.run(
            [SHADOW_DIFF, trace_path, report_path, str(ordinal)],
            capture_output=True, text=True, encoding="utf-8", timeout=120,
        )
        try:
            payload = json.loads(result.stdout)
        except (json.JSONDecodeError, TypeError):
            reports.append({
                "fixture": name, "ordinal": ordinal, "error":
                    f"ShadowDiff failed: {result.returncode} {result.stderr[-400:]}",
            })
            continue
        reports.append({
            "fixture": name,
            "ordinal": ordinal,
            "match": payload.get("match"),
            "mismatch_count": payload.get("mismatch_count"),
            "confidence": payload.get("confidence"),
            "action_kind": payload.get("action_kind"),
            "normalized_action_id": payload.get("normalized_action_id"),
            "mismatched_fields": [
                f["field"] for f in payload.get("fields", []) if not f.get("match")
            ],
            "report": os.path.relpath(report_path, ROOT),
        })
    return reports


def main() -> int:
    argv = sys.argv[1:]
    include_p0 = "--include-p0" in argv
    allow_degraded = "--allow-degraded" in argv
    names = [a for a in argv if not a.startswith("--")]
    matrix: dict[str, list[int]] = {}
    if include_p0:
        matrix.update(P0_PROBES)
    targets = list(PROBES)
    if names:
        targets = [n for n in PROBES if any(token in n for token in names)]
    for name in targets:
        matrix[name] = PROBES[name]

    failures = 0
    degraded = 0
    total_reports = 0
    for name in matrix:
        for report in run_fixture(name, matrix[name]):
            total_reports += 1
            if "error" in report:
                failures += 1
                print(f"FAIL {report['fixture']} ordinal={report.get('ordinal')}: {report['error']}")
                continue
            if report["match"] and report["confidence"] == "Reliable":
                status = "PASS"
            elif report["match"] and allow_degraded and report["confidence"] in {"Estimated", "Uncalculable"}:
                status = "DEGRADED"
                degraded += 1
            else:
                status = "MISMATCH"
            if status == "MISMATCH":
                failures += 1
            print(
                f"{status} {report['fixture']} ordinal={report['ordinal']} "
                f"kind={report['action_kind']} confidence={report['confidence']} "
                f"mismatches={report['mismatch_count']}"
                + (f" fields={report['mismatched_fields']}" if report["mismatched_fields"] else "")
            )
    print(f"\n{len(matrix)} fixtures, {total_reports} reports, {failures} failed reports, {degraded} degraded reports")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
