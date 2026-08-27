#!/usr/bin/env python3
"""Run P0 CLI fixtures, capture traces, and produce ShadowDiff reports.

Each entry declares the fixture command file, the trace basename, and the
action ordinals that must produce a zero-mismatch differential report.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

ROOT = r"D:\STS2BestChoice\STS2SuperModel"
CLI = os.path.join(ROOT, r"sts2-cli-v0111\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe")
SHADOW_DIFF = os.path.join(ROOT, r"training\ShadowDiff\bin\Release\net9.0\STS2BestChoice.ShadowDiff.exe")
FIXTURE_DIR = os.path.join(ROOT, "training", "fixtures")
DATA_DIR = os.path.join(ROOT, "data")

# fixture name -> ordinals to diff
# p0-happy-flower keeps only the single-turn boundary: the multi-turn hand
# compare cannot be replayed from a counter-only RNG snapshot (shuffle order)
# and non-attack enemy intents are not reconstructible from the public view.
PROBES: dict[str, list[int]] = {
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


def run_fixture(name: str, ordinals: list[int]) -> list[dict]:
    fixture_path = os.path.join(FIXTURE_DIR, f"{name}-commands.jsonl")
    trace_path = os.path.join(DATA_DIR, f"{name}-trace.jsonl")
    if not os.path.isfile(fixture_path):
        return [{"fixture": name, "error": f"missing fixture {fixture_path}"}]

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
            return [{"fixture": name, "error": f"CLI error: {reply.get('message')}"}]
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
        return [{"fixture": name, "error": "trace file was not produced"}]

    reports = []
    for ordinal in ordinals:
        suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
        report_path = os.path.join(DATA_DIR, f"p0-csharp-{name.removeprefix('p0-')}-diff-report{suffix}.json")
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
    only = sys.argv[1:] or list(PROBES)
    failures = 0
    for name in only:
        if name not in PROBES:
            print(f"skip unknown fixture: {name}")
            continue
        for report in run_fixture(name, PROBES[name]):
            if "error" in report:
                failures += 1
                print(f"FAIL {report['fixture']} ordinal={report.get('ordinal')}: {report['error']}")
                continue
            status = "PASS" if report["match"] else "MISMATCH"
            if not report["match"]:
                failures += 1
            print(
                f"{status} {report['fixture']} ordinal={report['ordinal']} "
                f"kind={report['action_kind']} confidence={report['confidence']} "
                f"mismatches={report['mismatch_count']}"
                + (f" fields={report['mismatched_fields']}" if report["mismatched_fields"] else "")
            )
    print(f"\n{len(only)} fixtures, {failures} failed reports")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
