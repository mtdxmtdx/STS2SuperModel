#!/usr/bin/env python3
"""Paired public-state runtime probes for D2 relic effects not isolated by an action diff."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from typing import Any, Callable

from collect_nosl_root_states import read_json, send


ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
DATA = ROOT / "data"
LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}
DECK = ["STRIKE_IRONCLAD"] * 10 + ["DEFEND_IRONCLAD"] * 10


def state_from(reply: dict[str, Any]) -> dict[str, Any]:
    return reply.get("public_observation") or (reply if reply.get("decision") == "combat_play" else {})


def snapshot(process: subprocess.Popen[str]) -> dict[str, Any]:
    return state_from(send(process, {"cmd": "get_combat_snapshot", "view": "public"}))


def action(process: subprocess.Popen[str], name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    reply = send(process, {"cmd": "action", "action": name, "args": args or {}})
    state = state_from(reply)
    if state:
        return state
    try:
        return snapshot(process)
    except Exception:
        return {
            "decision": reply.get("decision") or reply.get("type"),
            "post_combat_player": reply.get("post_combat_player"),
        }


def power_amount(state: dict[str, Any], power_id: str) -> int:
    for power in state.get("player_powers") or []:
        if power.get("id") == power_id:
            return int(power.get("amount") or 0)
    return 0


def metrics(state: dict[str, Any]) -> dict[str, Any]:
    player = state.get("player") or state.get("post_combat_player") or {}
    return {
        "decision": state.get("decision"),
        "hp": player.get("hp"),
        "block": player.get("block"),
        "energy": state.get("energy"),
        "stars": state.get("stars"),
        "hand_count": len(state.get("hand") or []),
        "draw_pile_count": state.get("draw_pile_count"),
        "discard_pile_count": state.get("discard_pile_count"),
        "strength": power_amount(state, "STRENGTH_POWER"),
        "focus": power_amount(state, "FOCUS_POWER"),
        "plating": power_amount(state, "PLATING_POWER"),
        "enemy_hp": {enemy.get("instance_id"): enemy.get("hp") for enemy in state.get("enemies") or []},
    }


def run_scenario(relic_id: str | None, scenario: str, subject_id: str) -> dict[str, Any]:
    process = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )
    relics = [relic_id] if relic_id else []
    try:
        ready = read_json(process)
        if not ready.get("compatible"):
            raise RuntimeError(str(ready))
        encounter = "TERROR_EEL_ELITE" if subject_id == "SLING_OF_COURAGE" else "SEAPUNK_WEAK"
        character = "Regent" if subject_id == "DIVINE_RIGHT" else "Ironclad"
        potions: list[str] = []
        hp = 40
        if scenario == "paper_krane":
            potions = ["WEAK_POTION"]
        elif scenario == "book_repair":
            encounter = "SLIMES_WEAK"
            potions = ["POTION_OF_DOOM"]
        elif scenario == "lizard_tail":
            hp = 1
            potions = ["FOUL_POTION"]
        elif scenario == "abacus":
            potions = ["SWIFT_POTION", "SWIFT_POTION"]
        send(process, {"cmd": "start_run", "character": character, "seed": f"d2-paired-{subject_id.lower()}-{scenario}", "ascension": 0})
        setup: dict[str, Any] = {"cmd": "set_player", "relics": relics, "hp": hp, "max_hp": 80, "potions": potions}
        if character == "Ironclad":
            setup["deck"] = DECK[:10] if scenario == "abacus" else DECK
        send(process, setup)
        entered = send(process, {"cmd": "enter_room", "type": "combat", "encounter": encounter})
        state = state_from(entered)
        if scenario != "combat_start":
            send(process, {"cmd": "set_combat_resources", "energy": 10 if scenario == "abacus" else 3, "stars": 0})
        state = snapshot(process)

        if scenario == "paper_krane":
            state = action(process, "use_potion", {"potion_index": 0, "target_index": 0})
            state = action(process, "end_turn")
        elif scenario == "book_repair":
            state = action(process, "use_potion", {"potion_index": 0, "target_index": 0})
            state = action(process, "end_turn")
        elif scenario == "lizard_tail":
            state = action(process, "use_potion", {"potion_index": 0})
        elif scenario in {"runic_pyramid", "ringing_triangle"}:
            state = action(process, "end_turn")
        elif scenario == "abacus":
            state = action(process, "use_potion", {"potion_index": 0})
            for _ in range(3):
                current = snapshot(process)
                candidate = next(card for card in current["hand"] if card.get("can_play"))
                args = {"card_index": candidate["index"]}
                if candidate.get("target_type") == "AnyEnemy":
                    args["target_index"] = 0
                state = action(process, "play_card", args)
            state = action(process, "use_potion", {"potion_index": 0})

        try:
            send(process, {"cmd": "quit"})
        except Exception:
            pass
        process.wait(timeout=30)
        return metrics(state)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


SCENARIOS = {
    "DIVINE_RIGHT": ("combat_start", lambda c, r: r["stars"] - c["stars"] == 3),
    "DATA_DISK": ("combat_start", lambda c, r: r["focus"] - c["focus"] == 1),
    "GORGET": ("combat_start", lambda c, r: r["plating"] - c["plating"] == 4),
    "EMBER_TEA": ("combat_start", lambda c, r: r["strength"] - c["strength"] == 2),
    "SWORD_OF_JADE": ("combat_start", lambda c, r: r["strength"] - c["strength"] == 3),
    "SLING_OF_COURAGE": ("combat_start", lambda c, r: r["strength"] - c["strength"] == 2),
    "PAPER_KRANE": ("paper_krane", lambda c, r: r["hp"] is not None and c["hp"] is not None and r["hp"] > c["hp"]),
    "BOOK_REPAIR_KNIFE": ("book_repair", lambda c, r: r["hp"] is not None and c["hp"] is not None and r["hp"] - c["hp"] == 3),
    "LIZARD_TAIL": ("lizard_tail", lambda c, r: r["hp"] == 40 and c["hp"] in {0, None}),
    "RUNIC_PYRAMID": ("runic_pyramid", lambda c, r: r["hand_count"] > c["hand_count"]),
    "RINGING_TRIANGLE": ("ringing_triangle", lambda c, r: r["hand_count"] > c["hand_count"]),
    "THE_ABACUS": ("abacus", lambda c, r: r["block"] is not None and c["block"] is not None and r["block"] - c["block"] == 6),
}

DIRECT_SHADOW_REPORTS = {
    "BOOK_REPAIR_KNIFE": "p1-csharp-relic-d2-book-repair-knife-diff-report-3.json",
    "LIZARD_TAIL": "p1-csharp-relic-d2-lizard-tail-diff-report.json",
    "PAPER_KRANE": "p1-csharp-relic-d2-paper-krane-diff-report-1.json",
    "RINGING_TRIANGLE": "p1-csharp-relic-d2-ringing-triangle-diff-report.json",
    "RUNIC_PYRAMID": "p1-csharp-relic-d2-runic-pyramid-diff-report.json",
    "THE_ABACUS": "p1-csharp-relic-d2-the-abacus-diff-report-3.json",
}


def build_report(relic_id: str) -> dict[str, Any]:
    scenario, predicate = SCENARIOS[relic_id]
    control = run_scenario(None, scenario, relic_id)
    treated = run_scenario(relic_id, scenario, relic_id)
    shadow_name = DIRECT_SHADOW_REPORTS.get(relic_id)
    shadow_report = json.loads((DATA / shadow_name).read_text(encoding="utf-8")) if shadow_name else None
    shadow_match = shadow_report is None or (
        shadow_report.get("match") is True and int(shadow_report.get("mismatch_count", -1)) == 0
    )
    match = bool(predicate(control, treated) and shadow_match)
    return {
        "schema_version": 1,
        **LOCK,
        "fixture": f"d2-relic-{relic_id.lower().replace('_', '-')}-paired",
        "trace_id": f"trace-v0111-d2-relic-{relic_id.lower().replace('_', '-')}-paired",
        "action_ordinal": 0,
        "action_kind": "paired_runtime_observation",
        "normalized_action_id": f"observe_relic:{relic_id.lower()}:{scenario}",
        "confidence": "Reliable" if match else "Estimated",
        "comparison_scope": "strict_public_state",
        "chance_present": False,
        "random_operator": "None",
        "probability_known": True,
        "outcome_quality": "Exact",
        "probability_mass_covered": 1.0,
        "match": match,
        "mismatch_count": 0 if match else 1,
        "mismatches": [] if match else [{"field": f"relic.{relic_id}.paired_delta", "control": control, "actual": treated}],
        "fields": [{
            "field": f"relic.{relic_id}.paired_delta",
            "projected": "effect_observed",
            "actual": "effect_observed" if match else "effect_not_observed",
            "match": match,
        }],
        "identity_comparison": "paired_public_effect_delta",
        "control": control,
        "actual": treated,
        "shadow_diff_evidence": shadow_name,
        "shadow_diff_match": shadow_match,
        "shadow_diff_mismatch_count": shadow_report.get("mismatch_count") if shadow_report else None,
        "method": (
            "same seed/setup/action sequence with only the relic changed, plus a zero-mismatch CLI-to-shadow action report"
            if shadow_name else
            "same seed/setup/combat-root sequence with only the relic changed; the start-of-combat effect is already materialized in the public root"
        ),
    }


def canonical(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ids", nargs="*")
    args = parser.parse_args()
    ids = args.ids or sorted(SCENARIOS)
    unknown = sorted(set(ids) - set(SCENARIOS))
    if unknown:
        raise SystemExit(f"unknown paired scenarios: {', '.join(unknown)}")
    results = []
    for relic_id in ids:
        first = build_report(relic_id)
        second = build_report(relic_id)
        first_bytes = canonical(first)
        second_bytes = canonical(second)
        slug = relic_id.lower().replace("_", "-")
        report_path = DATA / f"d2-relic-{slug}-paired-report.json"
        repeat_path = DATA / f"d2-relic-{slug}-paired-repeat.json"
        report_path.write_bytes(first_bytes)
        repeat = {
            "schema_version": 1,
            "verdict": "pass" if first_bytes == second_bytes and first["match"] else "fail",
            "byte_identical": first_bytes == second_bytes,
            "first_sha256": hashlib.sha256(first_bytes).hexdigest().upper(),
            "second_sha256": hashlib.sha256(second_bytes).hexdigest().upper(),
            "report_match": first["match"],
            "version_lock": LOCK,
        }
        repeat_path.write_text(json.dumps(repeat, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        results.append({"relic_id": relic_id, "match": first["match"], "repeat": repeat["verdict"]})
        print(json.dumps(results[-1]))
    passed = all(row["match"] and row["repeat"] == "pass" for row in results)
    summary = {"schema_version": 1, "verdict": "pass" if passed else "fail", "results": results, "version_lock": LOCK}
    (DATA / "d2-relic-paired-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
