#!/usr/bin/env python3
"""Paired runtime probe for Ring of the Drake's +2 hand draw."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from typing import Any

from collect_nosl_root_states import read_json, send


LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}


def observe(executable: Path, seed: str, relics: list[str]) -> list[dict[str, Any]]:
    process = subprocess.Popen(
        [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )
    deck = ["DEFEND_IRONCLAD"] * 40
    try:
        ready = read_json(process)
        if not ready.get("compatible"):
            raise RuntimeError(f"CLI incompatible: {ready}")
        send(process, {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0})
        send(process, {"cmd": "set_player", "relics": relics, "hp": 200, "max_hp": 200, "deck": deck})
        send(process, {"cmd": "enter_room", "type": "combat", "encounter": "SEAPUNK_WEAK"})
        observations = []
        for index in range(3):
            reply = send(process, {"cmd": "get_combat_snapshot", "view": "public"})
            state = reply.get("public_observation") or {}
            observations.append({
                "round": int(state.get("round") or 0),
                "hand_count": len(state.get("hand") or []),
                "relic_ids": sorted(
                    str(relic.get("id")) for relic in (state.get("player") or {}).get("relics") or []
                ),
                "public_state_hash": reply.get("public_state_hash"),
            })
            if index < 2:
                send(process, {"cmd": "action", "action": "end_turn", "args": {}})
        send(process, {"cmd": "quit"})
        process.wait(timeout=30)
        return observations
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


def build_report(executable: Path) -> dict[str, Any]:
    seed = "p1-ring-of-the-drake-paired-v1"
    control = observe(executable, seed, [])
    treated = observe(executable, seed, ["RING_OF_THE_DRAKE"])
    deltas = [right["hand_count"] - left["hand_count"] for left, right in zip(control, treated)]
    matched = deltas == [2, 2, 2] and [row["round"] for row in treated] == [1, 2, 3]
    fields = [
        {
            "field": "relic.ids",
            "projected": ["RING_OF_THE_DRAKE"],
            "actual": treated[0]["relic_ids"],
            "match": treated[0]["relic_ids"] == ["RING_OF_THE_DRAKE"],
        },
        {
            "field": "relic.RING_OF_THE_DRAKE.hand_draw_delta",
            "projected": [2, 2, 2],
            "actual": deltas,
            "match": deltas == [2, 2, 2],
        },
    ]
    return {
        "schema_version": 1,
        **LOCK,
        "trace_id": "trace-v0111-p1-ring-of-the-drake-paired-v1",
        "fixture": "p1-relic-ring-of-the-drake-paired-control",
        "seed": seed,
        "simulator": "paired-v0.111-headless-control",
        "action_ordinal": 0,
        "action_kind": "paired_runtime_observation",
        "normalized_action_id": "observe_turn_start_draw:ring_of_the_drake:rounds_1_2_3",
        "confidence": "Reliable" if matched else "Estimated",
        "chance_present": False,
        "random_operator": "None",
        "probability_known": True,
        "outcome_quality": "Exact",
        "probability_mass_covered": 1.0,
        "comparison_scope": "strict_public_state",
        "identity_comparison": "aggregate_count_exact",
        "match": matched and all(field["match"] for field in fields),
        "mismatch_count": sum(not field["match"] for field in fields),
        "mismatches": [field for field in fields if not field["match"]],
        "control_observations": control,
        "relic_observations": treated,
        "fields": fields,
        "method": (
            "same seed, character, 40-card deck, encounter and rounds; only relic list differs; "
            "compares public hand counts, never card order or RNG state"
        ),
    }


def canonical(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeat-output", required=True, type=Path)
    args = parser.parse_args()
    first = build_report(args.executable)
    second = build_report(args.executable)
    first_bytes, second_bytes = canonical(first), canonical(second)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(first_bytes)
    repeat = {
        "schema_version": 1,
        "verdict": "pass" if first_bytes == second_bytes and first["match"] else "fail",
        "first_sha256": hashlib.sha256(first_bytes).hexdigest().upper(),
        "second_sha256": hashlib.sha256(second_bytes).hexdigest().upper(),
        "byte_identical": first_bytes == second_bytes,
        "report_match": first["match"],
        "version_lock": LOCK,
    }
    args.repeat_output.write_text(json.dumps(repeat, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"report": first, "repeat": repeat}, ensure_ascii=False))
    return 0 if repeat["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
