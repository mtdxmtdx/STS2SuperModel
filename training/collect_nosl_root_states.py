#!/usr/bin/env python3
"""Collect public NOSL combat roots from the v0.111 headless CLI.

The CLI is used only as a state source. Public rows never contain ordered pile
identities; the teacher snapshot is retained separately for later public-zone
multiset materialization and audit linkage.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any
import re


LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "simulator_version": "cli-v0111-headless",
    "scorer_version": "not-applicable",
    "semantic_database_version": "game-runtime-v0111",
    "feature_schema_version": "1",
    "model_version": "none",
    "generator_config_hash": "1DEB4733836B2B4CB051E4810C467E1B72A4706BEDFE45FF7C675A9D7CCDC383",
}


def digest(value: Any) -> str:
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()).hexdigest().upper()


def read_json(process: subprocess.Popen[str]) -> dict[str, Any]:
    while True:
        line = process.stdout.readline()  # type: ignore[union-attr]
        if not line:
            raise RuntimeError("CLI exited before returning JSON")
        line = line.strip()
        if line.startswith("{"):
            return json.loads(line)


def send(process: subprocess.Popen[str], command: dict[str, Any]) -> dict[str, Any]:
    process.stdin.write(json.dumps(command, ensure_ascii=False) + "\n")  # type: ignore[union-attr]
    process.stdin.flush()  # type: ignore[union-attr]
    return read_json(process)


def public_only(observation: dict[str, Any]) -> dict[str, Any]:
    value = json.loads(json.dumps(observation, ensure_ascii=False))
    for zone in ("draw_pile", "discard_pile", "exhaust_pile"):
        value.get("player", {}).pop(zone, None)
        value.pop(zone, None)
    return value


CHARACTER_DECKS: dict[str, tuple[str, list[str]]] = {
    "Ironclad": ("The Ironclad", [
        "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD",
        "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BASH",
    ]),
    "Silent": ("The Silent", [
        "STRIKE_SILENT", "STRIKE_SILENT", "STRIKE_SILENT", "STRIKE_SILENT", "STRIKE_SILENT",
        "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "NEUTRALIZE", "SURVIVOR",
    ]),
}


def parse_injection_sets(value: str) -> list[list[str]]:
    """Parse deterministic round-robin sets; `EMPTY` denotes an empty slot set."""
    result: list[list[str]] = []
    for raw_set in re.split(r"[|,]", value):
        raw_set = raw_set.strip()
        if not raw_set or raw_set.upper() == "EMPTY":
            result.append([])
            continue
        members = [member.strip().upper() for member in raw_set.split("+") if member.strip()]
        if not members:
            raise ValueError("injection set must contain an ID or EMPTY")
        result.append(members)
    if not result:
        raise ValueError("injection sets must contain at least one set")
    return result


def collect_one(
    executable: Path,
    seed: str,
    turns: int,
    encounter: str,
    character: str,
    potions: list[str] | None = None,
    relics: list[str] | None = None,
) -> list[dict[str, Any]]:
    if character not in CHARACTER_DECKS:
        raise ValueError(f"unsupported collector character: {character}")
    character_name, deck = CHARACTER_DECKS[character]
    process = subprocess.Popen(
        [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )
    try:
        ready = read_json(process)
        if not ready.get("compatible"):
            raise RuntimeError(f"CLI incompatible for {seed}: {ready}")
        send(process, {"cmd": "start_run", "character": character, "seed": seed, "ascension": 0})
        player_setup: dict[str, Any] = {"cmd": "set_player", "deck": deck}
        if potions is not None:
            player_setup["potions"] = potions
        if relics is not None:
            player_setup["relics"] = relics
        send(process, player_setup)
        entered = send(process, {"cmd": "enter_room", "type": "combat", "encounter": encounter})
        if entered.get("decision") != "combat_play":
            raise RuntimeError(f"seed {seed} did not enter combat: {entered.get('decision')}")
        send(process, {"cmd": "set_combat_resources", "energy": 3, "stars": 0})

        rows: list[dict[str, Any]] = []
        for step in range(max(1, turns + 1)):
            public_reply = send(process, {"cmd": "get_combat_snapshot", "view": "public"})
            teacher_reply = send(process, {"cmd": "get_combat_snapshot", "view": "teacher"})
            observation = public_reply.get("public_observation")
            teacher = teacher_reply.get("teacher_snapshot")
            if not isinstance(observation, dict) or not isinstance(teacher, dict):
                break
            public = public_only(observation)
            public_hash = str(public_reply.get("public_state_hash") or digest(public))
            row = {
                **LOCK,
                "schema_version": 1,
                "trace_schema": 1,
                "record_id": digest([seed, step, public_hash])[:24].lower(),
                "episode_id": f"trace-v0111-root-{seed}",
                "character": character_name,
                "ascension": 0,
                "act": int(public.get("context", {}).get("act", 1)),
                "floor": int(public.get("context", {}).get("floor", 1)),
                "combat_id": digest([seed, "combat"])[:24].lower(),
                "round": int(public.get("round", step + 1)),
                "state_hash_public": public_hash,
                "state_hash_teacher": digest(teacher)[:24].lower(),
                "public_state": public,
                "teacher_snapshot": teacher,
                "legal_actions": public.get("action_candidates", []),
                "legal_actions_complete": True,
                "injected_potions": list(potions or []),
                "injected_relics": list(relics or []),
            }
            rows.append(row)
            if step < turns:
                response = send(process, {"cmd": "action", "action": "end_turn"})
                if response.get("decision") != "combat_play":
                    break
        send(process, {"cmd": "quit"})
        process.wait(timeout=30)
        return rows
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--seeds", type=Path, help="UTF-8 file with one seed per line")
    parser.add_argument("--seed-prefix", help="Generate seeds as <prefix>-00000 ... <prefix>-<count-1>")
    parser.add_argument("--seed-count", type=int, default=0)
    parser.add_argument("--turns", type=int, default=3)
    parser.add_argument("--encounter", default="SEAPUNK_WEAK")
    parser.add_argument(
        "--encounters",
        help="Comma-separated encounter IDs assigned to seeds round-robin; overrides --encounter",
    )
    parser.add_argument(
        "--characters",
        help="Comma-separated supported characters assigned to seeds round-robin (Ironclad,Silent)",
    )
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--potions", help="Comma-separated fixed potion IDs injected into every root")
    parser.add_argument(
        "--potion-sets",
        help="Round-robin potion sets separated by | or comma; + joins potions; EMPTY selects no potions",
    )
    parser.add_argument("--relics", help="Comma-separated fixed relic IDs injected into every root")
    parser.add_argument(
        "--relic-sets",
        help="Round-robin relic sets separated by | or comma; + joins relics; EMPTY selects no relics",
    )
    args = parser.parse_args()
    if args.seeds is not None and (args.seed_prefix is not None or args.seed_count):
        raise SystemExit("choose either --seeds or --seed-prefix/--seed-count")
    if args.seeds is None and (args.seed_prefix is None or args.seed_count <= 0):
        raise SystemExit("provide --seeds or a positive --seed-prefix with --seed-count")
    seeds = (
        [line.strip() for line in args.seeds.read_text(encoding="utf-8").splitlines() if line.strip()]
        if args.seeds is not None
        else [f"{args.seed_prefix}-{index:05d}" for index in range(args.seed_count)]
    )
    if args.workers <= 0 or args.turns < 0:
        raise SystemExit("workers must be positive and turns must be non-negative")
    encounters = (
        [value.strip() for value in args.encounters.split(",") if value.strip()]
        if args.encounters
        else [args.encounter]
    )
    if not encounters:
        raise SystemExit("encounters must contain at least one encounter ID")
    characters = (
        [value.strip() for value in args.characters.split(",") if value.strip()]
        if args.characters
        else ["Ironclad"]
    )
    unsupported = sorted(set(characters) - set(CHARACTER_DECKS))
    if unsupported:
        raise SystemExit(f"unsupported characters: {', '.join(unsupported)}")
    if not characters:
        raise SystemExit("characters must contain at least one character")
    if args.potions and args.potion_sets:
        raise SystemExit("choose either --potions or --potion-sets")
    if args.relics and args.relic_sets:
        raise SystemExit("choose either --relics or --relic-sets")
    potion_sets = (
        parse_injection_sets(args.potion_sets)
        if args.potion_sets
        else [[value.strip().upper() for value in args.potions.split(",") if value.strip()]]
        if args.potions is not None
        else [None]
    )
    relic_sets = (
        parse_injection_sets(args.relic_sets)
        if args.relic_sets
        else [[value.strip().upper() for value in args.relics.split(",") if value.strip()]]
        if args.relics is not None
        else [None]
    )
    jobs = [
        (
            seed,
            encounters[index % len(encounters)],
            characters[index % len(characters)],
            potion_sets[index % len(potion_sets)],
            relic_sets[index % len(relic_sets)],
        )
        for index, seed in enumerate(seeds)
    ]
    if args.workers == 1:
        groups = [collect_one(args.executable, seed, args.turns, encounter, character, potions, relics) for seed, encounter, character, potions, relics in jobs]
    else:
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            groups = list(pool.map(lambda job: collect_one(args.executable, job[0], args.turns, job[1], job[2], job[3], job[4]), jobs))
    rows = [row for group in groups for row in group]
    rows.sort(key=lambda row: (row["episode_id"], row["round"], row["state_hash_public"]))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in rows) + "\n", encoding="utf-8")
    print(json.dumps({
        "seeds": len(seeds),
        "rows": len(rows),
        "encounters": encounters,
        "characters": characters,
        "potion_sets": potion_sets,
        "relic_sets": relic_sets,
        "output": str(args.output),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
