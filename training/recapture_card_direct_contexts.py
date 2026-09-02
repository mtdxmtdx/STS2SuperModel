#!/usr/bin/env python3
"""Recapture stale direct-card traces in isolated, owner-correct CLI runs."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

from capture_cli_trace import JsonLineReader, LOCK, capture


ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parent
DATA = ROOT / "data"
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
SHADOW = ROOT / "training/ShadowDiff/bin/Release/net9.0/STS2BestChoice.ShadowDiff.exe"
CATALOG = WORKSPACE / "STS2BestChoice/data/cards/generated/0.111.0/cards.json"

CHARACTERS = {
    "铁甲战士": "Ironclad",
    "静默猎手": "Silent",
    "故障机器人": "Defect",
    "储君": "Regent",
    "亡灵契约师": "Necrobinder",
    "无色": "Ironclad",
    # Event/generated/status/curse cards do not own a character.  The runtime
    # can still instantiate many of them in an isolated combat deck; use an
    # Ironclad host without changing their model IDs or semantics.
    "事件": "Ironclad",
    "衍生": "Ironclad",
    "状态": "Ironclad",
    "诅咒": "Ironclad",
}


def load_targets(statuses: set[str]) -> list[dict]:
    witness = json.loads((DATA / "card-direct-witness-manifest.json").read_text(encoding="utf-8"))
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    cards = {row["id"]: row for row in catalog["cards"]}
    targets = []
    for row in witness["rows"]:
        if row.get("status") not in statuses:
            continue
        card = cards.get(row["variant_id"])
        if not card or card.get("character") not in CHARACTERS:
            continue
        targets.append({
            "variant_id": row["variant_id"],
            "character": CHARACTERS[card["character"]],
            "expected_action_id": row.get("normalized_action_id"),
        })
    return targets


def capture_resolved_choice(commands: list[dict], trace: Path, timeout: float) -> int:
    env = os.environ.copy()
    env["STS2_TRACE_PATH"] = str(trace)
    trace.parent.mkdir(parents=True, exist_ok=True)
    if trace.exists():
        trace.unlink()
    process = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env,
    )
    reader = JsonLineReader(process.stdout)

    def send(command: dict) -> dict:
        process.stdin.write(json.dumps(command, ensure_ascii=False) + "\n")
        process.stdin.flush()
        response = reader.next(timeout)
        if response.get("trace_status") == "failed":
            raise RuntimeError(f"command failed: {response}")
        return response

    try:
        ready = reader.next(timeout)
        for key, expected in LOCK.items():
            if ready.get(key) != expected:
                raise RuntimeError(
                    f"version gate failed: {key}={ready.get(key)!r}, expected {expected!r}"
                )
        response: dict = {}
        for command in commands:
            response = send(command)
        if response.get("decision") == "card_select":
            minimum = max(0, int(response.get("min_select", 1)))
            maximum = max(minimum, int(response.get("max_select", minimum)))
            candidates = response.get("action_candidates") or []
            selectable = max(
                len(response.get("cards") or []),
                sum(1 for candidate in candidates if candidate.get("legal", True)),
            )
            count = min(maximum, max(minimum, 1 if selectable else 0))
            indices = ",".join(str(index) for index in range(count))
            response = send({"cmd": "action", "action": "select_cards", "args": {"indices": indices}})
        send({"cmd": "get_combat_snapshot", "view": "public"})
        process.stdin.write('{"cmd":"quit"}\n')
        process.stdin.flush()
        reader.next(timeout)
        process.wait(timeout=10)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
    if not trace.exists():
        raise RuntimeError("CLI did not write trace output")
    return sum(1 for line in trace.open(encoding="utf-8") if line.strip())


def recapture(
    target: dict, trace_dir: Path, report_dir: Path, timeout: float,
    resolve_choice: bool, combat_resources: bool, energy: int, stars: int,
) -> dict:
    variant = target["variant_id"]
    trace = trace_dir / f"{variant}.jsonl"
    report = report_dir / f"p1-csharp-card-direct-{variant.lower()}-diff-report.json"
    commands = [
        {"cmd": "start_run", "character": target["character"], "seed": f"direct4-{variant}", "ascension": 0, "lang": "en"},
        {"cmd": "set_player", "deck": [variant] * 5, "relics": []},
        {"cmd": "enter_room", "type": "combat", "encounter": "SEAPUNK_WEAK"},
    ]
    if combat_resources:
        # Only resource-blocked fixture probes receive this test setup.  It is
        # intentionally after combat entry because PlayerCombatState does not
        # exist while setting a run deck.
        commands.append({"cmd": "set_combat_resources", "energy": energy, "stars": stars})
    commands.extend([
        {"cmd": "get_combat_snapshot", "view": "public"},
        {"cmd": "action", "action": "play_card", "args": {"card_index": 0, "target_index": 0}},
    ])
    result = dict(target)
    try:
        result["trace_lines"] = (
            capture_resolved_choice(commands, trace, timeout)
            if resolve_choice
            else capture(CLI, commands + [{"cmd": "get_combat_snapshot", "view": "public"}], trace,
                         response_timeout_seconds=timeout)
        )
    except Exception as exc:  # record per-card failure without aborting the batch
        result.update(status="capture_error", error=f"{type(exc).__name__}: {exc}")
        return result

    try:
        completed = subprocess.run(
            [str(SHADOW), str(trace), str(report), "0"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=timeout,
        )
    except subprocess.TimeoutExpired as exc:
        result.update(
            status="shadow_timeout",
            exit=None,
            error=f"ShadowDiff timed out after {timeout:g}s",
        )
        return result
    try:
        payload = json.loads(completed.stdout)
    except json.JSONDecodeError:
        result.update(status="shadow_error", exit=completed.returncode, error=completed.stderr[-500:])
        return result
    result.update(
        status="match" if payload.get("match") else "mismatch",
        exit=completed.returncode,
        match=payload.get("match"),
        mismatch_count=payload.get("mismatch_count"),
        confidence=payload.get("confidence"),
        normalized_action_id=payload.get("normalized_action_id"),
        report=report.name,
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--statuses", nargs="+", default=["fixture_not_entered_combat"])
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--limit", type=int)
    parser.add_argument("--variants", nargs="+",
                        help="restrict recapture to these variant IDs")
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--resolve-choice", action="store_true")
    parser.add_argument("--combat-resources", action="store_true",
                        help="set energy/stars to 10 after combat entry for resource-blocked probes")
    parser.add_argument("--energy", type=int, default=10,
                        help="fixture combat energy when --combat-resources is enabled")
    parser.add_argument("--stars", type=int, default=10,
                        help="fixture combat stars when --combat-resources is enabled")
    parser.add_argument("--trace-dir", type=Path, default=DATA / "card-direct-traces4")
    parser.add_argument("--report-dir", type=Path, default=DATA)
    parser.add_argument("--output", type=Path, default=DATA / "card-direct-context-recapture-v1.json")
    args = parser.parse_args()

    targets = load_targets(set(args.statuses))
    if args.variants:
        wanted = {variant.upper() for variant in args.variants}
        targets = [target for target in targets if target["variant_id"].upper() in wanted]
    if args.limit is not None:
        targets = targets[: max(0, args.limit)]
    args.trace_dir.mkdir(parents=True, exist_ok=True)
    args.report_dir.mkdir(parents=True, exist_ok=True)

    results = []
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = [
            pool.submit(
                recapture, target, args.trace_dir, args.report_dir,
                args.timeout, args.resolve_choice, args.combat_resources,
                args.energy, args.stars,
            )
            for target in targets
        ]
        for future in as_completed(futures):
            results.append(future.result())
    results.sort(key=lambda row: row["variant_id"])
    counts: dict[str, int] = {}
    for row in results:
        counts[row["status"]] = counts.get(row["status"], 0) + 1
    payload = {
        "schema_version": 1,
        "source_statuses": args.statuses,
        "target_count": len(targets),
        "counts": counts,
        "results": results,
    }
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"target_count": len(targets), "counts": counts, "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
