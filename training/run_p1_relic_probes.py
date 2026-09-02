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
    "p1-relic-unceasing-top-empty": [0, 1, 2, 3, 4],
    "p1-relic-bronze-scales": [0, 1],
    "p1-relic-blood-vial": [0],
    "p1-relic-brimstone": [0, 1],
    "p1-relic-nunchaku": [0, 1],
    "p1-relic-anchor": [0],
    # Batch 1 (v0.111 attack/turn relics).
    "p1-relic-attack-counters": [2, 3, 4, 5],
    "p1-relic-letter-opener": [2, 3],
    "p1-relic-rainbow-ring": [1, 2],
    "p1-relic-mercury-hourglass": [0, 1],
    "p1-relic-mr-struggles": [0, 1],
    "p1-relic-sai": [0],
    "p1-relic-candelabra": [0, 1],
    # Chandelier triggers at the start of turn 3 (ordinal 1). Ordinal 2 spans
    # the round 3->4 transition where SEAPUNK_WEAK runs a Buff+Defend intent
    # whose amounts the public observation does not export (known simulator
    # gap), so that transition is not part of the diff matrix.
    "p1-relic-chandelier": [0, 1],
    "p1-relic-fake-happy-flower": [0, 4],
    "p1-relic-fake-orichalcum": [0],
    # Batch 2 (v0.111 turn-end/damage-modifier/combat-start relics).
    "p1-relic-turn-end-block": [0, 1],
    "p1-relic-parrying-shield": [2],
    "p1-relic-parrying-shield-multi": [2],
    "p1-relic-screaming-flagon": [5],
    "p1-relic-kusarigama": [2, 3, 4, 5],
    "p1-relic-paels-tears": [1, 2],
    "p1-relic-strike-dummy": [0, 1, 2, 3],
    "p1-relic-snecko-eye": [1, 2],
    "p1-relic-whispering-earring": [0, 1],
    "p1-relic-combat-start-carried": [0, 1],
    # Batch 3 (scheduled block/draw, turn-7 strike, exhaust counter, carry-over).
    "p1-relic-self-forming-clay": [0, 1],
    "p1-relic-pocketwatch": [0, 1],
    "p1-relic-stone-calendar": [6],
    "p1-relic-joss-paper": [2, 4],
    "p1-relic-ice-cream": [1, 2],
    "p1-relic-ninja-scroll": [0],
    # Batch 4 (combat-start carried: potions / conditional DEX / Confused).
    "p1-relic-delicate-frond": [0, 1],
    "p1-relic-belt-buckle": [0, 1],
    "p1-relic-fake-snecko-eye": [0, 1],
    # Batch 5 (damage floor / heal trigger / block double / free 5th card).
    "p1-relic-the-boot": [0, 1, 2],
    "p1-relic-vambrace": [0, 1],
    "p1-relic-brilliant-scarf": [3, 5],
    "p1-relic-pantograph": [0, 1],
    # Batch 6 (combat-start effect relics carried by the snapshot; verified
    # empirically that they do NOT re-apply on later turns).
    "p1-relic-festive-popper": [0, 1],
    "p1-relic-royal-poison": [0, 1],
    "p1-relic-red-mask": [0, 1],
    "p1-relic-twisted-funnel": [0, 1],
    # Batch 7 (max-energy growth relics).
    "p1-relic-bread": [0, 1],
    "p1-relic-paels-flesh": [0, 1],
    # Batch 8 (combat-end heals, unlocked by the CLI terminal trace row).
    "p1-relic-combat-end-heal": [2],
    # Batch 9 (cycle draws, turn-start exhaust, cross-combat carried counters).
    "p1-relic-pendulum": [0, 1],
    
    
    
    "p1-relic-cross-combat-carried": [0, 1],
    # Batch 10 (per-play trigger relics).
    "p1-relic-wind-block": [0, 1, 2],
    "p1-relic-cost-gated": [0, 1, 2],
    # Batch 11 (conditional/limit relics).
    "p1-relic-beating-remnant": [0, 1],
    "p1-relic-seal-of-gold": [0, 1],
    "p1-relic-velvet-choker": [0, 1],
    "p1-relic-power-triggers": [0, 1],
    # Batch 13 (exhaust triggers + shiv/count relics, teacher-disambiguated).
    "p1-relic-tuning-fork": [0, 1, 2, 3],
    "p1-relic-booming-conch": [0],
    # Batch 14 (turn-N triggers, per-turn draw, max-energy carried family).
    "p1-relic-turn-n-triggers": [0, 1],
    "p1-relic-per-turn-draw": [0, 1],
    "p1-relic-max-energy-carried": [0, 1],
    # Batch 15 (block-break / potion / block-persist relics).
    "p1-relic-hand-drill": [1],
    # Batch 15b (per-turn draw + carried max-energy/tea family).
    "p1-relic-fiddle": [0, 1],
    "p1-relic-tea-carried": [0, 1],
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
    degraded = 0
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
                status = "PASS" if report.get("confidence") == "Reliable" else "DEGRADED"
                if status == "DEGRADED":
                    degraded += 1
                print(f"{status} {report['fixture']} ordinal={report['ordinal']} "
                      f"confidence={report['confidence']} mismatches={report['mismatch_count']}")
    print(f"\n{len(names)} fixtures, {reports_count} reports, {failures} failed reports, {degraded} degraded reports")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
