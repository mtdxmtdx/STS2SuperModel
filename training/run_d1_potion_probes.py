#!/usr/bin/env python3
"""Run version-locked CLI/ShadowDiff probes for D1 potion semantics."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
SHADOW = ROOT / "training/ShadowDiff/bin/Release/net9.0/STS2BestChoice.ShadowDiff.exe"
CATALOG = ROOT / "data/potions/v0.111/potion-catalog.json"
DATA = ROOT / "data"
DECK = ["STRIKE_IRONCLAD"] * 10 + ["DEFEND_IRONCLAD"] * 10


def read_json(process: subprocess.Popen[str]) -> dict[str, Any]:
    assert process.stdout is not None
    while True:
        line = process.stdout.readline()
        if not line:
            raise RuntimeError("CLI exited before returning JSON")
        if line.lstrip().startswith("{"):
            return json.loads(line)


def send(process: subprocess.Popen[str], command: dict[str, Any]) -> dict[str, Any]:
    assert process.stdin is not None
    process.stdin.write(json.dumps(command, separators=(",", ":")) + "\n")
    process.stdin.flush()
    result = read_json(process)
    if result.get("type") in {"error", "save_error"}:
        raise RuntimeError(str(result.get("message") or result))
    return result


def supported_rows() -> list[dict[str, Any]]:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8-sig"))
    return [row for row in catalog["potions"] if row.get("simulator_supported")]


def capture(potion: dict[str, Any], trace: Path) -> None:
    trace.unlink(missing_ok=True)
    env = os.environ.copy()
    env["STS2_TRACE_PATH"] = str(trace)
    process = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env,
    )
    try:
        ready = read_json(process)
        if not ready.get("compatible"):
            raise RuntimeError(f"incompatible CLI: {ready}")
        potion_id = potion["potion_id"]
        send(process, {"cmd": "start_run", "character": "Ironclad", "seed": f"d1-potion-{potion_id.lower()}", "ascension": 0})
        send(process, {"cmd": "set_player", "hp": 40, "max_hp": 80, "deck": DECK, "potions": [potion_id]})
        send(process, {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"})
        send(process, {"cmd": "set_combat_resources", "energy": 1, "stars": 0})
        send(process, {"cmd": "get_combat_snapshot", "view": "public"})
        send(process, {"cmd": "get_combat_snapshot", "view": "teacher"})
        args: dict[str, Any] = {"potion_index": 0}
        if potion.get("target_type") == "AnyEnemy":
            args["target_index"] = 0
        send(process, {"cmd": "action", "action": "use_potion", "args": args})
        send(process, {"cmd": "get_combat_snapshot", "view": "public"})
        send(process, {"cmd": "get_combat_snapshot", "view": "teacher"})
        send(process, {"cmd": "quit"})
        process.wait(timeout=30)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


def run_one(potion: dict[str, Any], output_dir: Path) -> dict[str, Any]:
    potion_id = potion["potion_id"]
    slug = potion_id.lower().replace("_", "-")
    trace = output_dir / f"d1-potion-{slug}-trace.jsonl"
    report = output_dir / f"d1-csharp-potion-{slug}-diff-report.json"
    try:
        capture(potion, trace)
        result = subprocess.run(
            [str(SHADOW), str(trace), str(report), "0"],
            cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=120,
        )
        payload = json.loads(result.stdout)
        return {
            "potion_id": potion_id,
            "returncode": result.returncode,
            "match": payload.get("match"),
            "mismatch_count": payload.get("mismatch_count"),
            "confidence": payload.get("confidence"),
            "comparison_scope": payload.get("comparison_scope"),
            "report": report.name,
            "report_sha256": hashlib.sha256(report.read_bytes()).hexdigest().upper(),
            "mismatches": [row.get("field") for row in payload.get("mismatches", [])],
        }
    except Exception as exc:  # per-ID terminal evidence, not silent degradation
        return {"potion_id": potion_id, "error": f"{type(exc).__name__}: {exc}"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ids", nargs="*")
    parser.add_argument("--output-dir", type=Path, default=DATA)
    parser.add_argument("--summary", type=Path, default=DATA / "d1-potion-probe-summary.json")
    args = parser.parse_args()
    by_id = {row["potion_id"]: row for row in supported_rows()}
    selected = args.ids or sorted(by_id)
    unknown = sorted(set(selected) - set(by_id))
    if unknown:
        raise SystemExit(f"not simulator-supported in potion-catalog: {', '.join(unknown)}")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    results = [run_one(by_id[potion_id], args.output_dir) for potion_id in selected]
    strict = [row for row in results if row.get("confidence") == "Reliable" and row.get("match") is True and row.get("mismatch_count") == 0 and row.get("comparison_scope") == "strict_public_state"]
    payload = {
        "schema_version": 1,
        "verdict": "pass" if len(strict) == len(results) else "degraded",
        "requested": len(results),
        "strict_reliable": len(strict),
        "results": results,
        "version_lock": {
            "game_version": "v0.111.0",
            "game_commit": "41cef1ea",
            "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0",
            "trace_schema": 1,
            "feature_schema_version": "combat-feature-v1",
        },
    }
    args.summary.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for row in results:
        print(json.dumps(row, ensure_ascii=False))
    print(json.dumps({"verdict": payload["verdict"], "requested": len(results), "strict_reliable": len(strict)}))
    return 0 if payload["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
