#!/usr/bin/env python3
"""Generate batch-1 relic CLI fixtures (v0.111.0).

Drives the live CLI interactively to pick deterministic card indices for a
planned action script, then writes fixed-index JSONL fixtures under
training/fixtures/. Each fixture is replayable byte-for-byte because the
seed fixes the draw order; the dynamic pass only reads indices, it never
changes state.

Usage: python training/gen_relic_batch1_fixtures.py
"""
from __future__ import annotations

import json
import subprocess
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
FIXTURES = ROOT / "training/fixtures"

STRIKE = "STRIKE_IRONCLAD"
DEFEND = "DEFEND_IRONCLAD"
# Inflame: 1-cost Ironclad Power (+2 Strength) used to complete the
# Rainbow Ring attack/skill/power sequence.
POWER = "INFLAME"


def run_cli(commands, expect_states=False):
    env = os.environ.copy()
    proc = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env,
    )
    replies = []
    try:
        for command in commands:
            proc.stdin.write(json.dumps(command) + "\n")
            proc.stdin.flush()
            line = proc.stdout.readline()
            while line and not line.startswith("{"):
                line = proc.stdout.readline()
            if not line:
                break
            reply = json.loads(line)
            replies.append(reply)
            if reply.get("type") in ("error", "save_error"):
                raise RuntimeError(f"CLI error: {reply.get('message')}")
    finally:
        try:
            proc.stdin.close()
        except OSError:
            pass
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.terminate()
            proc.wait(timeout=10)
    return replies


def last_snapshot(replies):
    for reply in reversed(replies):
        obs = reply.get("public_observation") or (reply if reply.get("hand") is not None else None)
        if obs and obs.get("hand") is not None:
            return obs
    return None


def find_index(hand, card_type):
    for card in hand:
        if card["type"] == card_type:
            return card["index"]
    raise RuntimeError(f"no {card_type} in hand {[ (c['index'], c['type'], c.get('can_play')) for c in hand ]}")


def find_any_index(hand):
    for card in hand:
        if card["type"] in ("Attack", "Skill"):
            return card["index"], card["type"]
    raise RuntimeError(f"no playable card in hand {[ (c['index'], c['type'], c.get('can_play')) for c in hand ]}")


def alive_enemy_index(snap):
    for enemy in snap.get("enemies", []):
        if enemy.get("hp", 0) > 0:
            return enemy["index"]
    raise RuntimeError("no alive enemy")


def build_fixture(name, seed, relics, deck, encounter, hp=80, plan=None, max_hp=None):
    """plan: list of steps.
    ("play", card_type, target_index) — play first playable card of that type.
    ("end_turn",) — end the turn.
    Writes a JSONL fixture with fixed indices discovered interactively.
    """
    plan = plan or []
    actions = []
    for step in plan:
        if step[0] == "end_turn":
            actions.append({"cmd": "action", "action": "end_turn", "args": {}})
            continue
        _, card_type, target = step
        # snapshot before the play to resolve the index
        probe_cmds = [
            {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0},
            {"cmd": "set_player", "relics": relics, "hp": hp, "max_hp": max_hp or hp, "deck": deck},
            {"cmd": "enter_room", "type": "combat", "encounter": encounter},
            {"cmd": "get_combat_snapshot", "view": "public"},
        ]
        for prior in actions:
            probe_cmds.append(prior)
        probe_cmds.append({"cmd": "get_combat_snapshot", "view": "public"})
        replies = run_cli(probe_cmds)
        snap = last_snapshot(replies)
        if card_type == "Any":
            index, card_type = find_any_index(snap["hand"])
        else:
            index = find_index(snap["hand"], card_type)
        if target == "any_enemy":
            target = alive_enemy_index(snap)
        actions.append({"cmd": "action", "action": "play_card",
                        "args": {"card_index": index, "target_index": target}})

    fixture = [
        {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0},
        {"cmd": "set_player", "relics": relics, "hp": hp, "max_hp": hp, "deck": deck},
        {"cmd": "enter_room", "type": "combat", "encounter": encounter},
        {"cmd": "get_combat_snapshot", "view": "public"},
        *actions,
        {"cmd": "quit"},
    ]
    path = FIXTURES / f"{name}-commands.jsonl"
    path.write_text("".join(json.dumps(c) + "\n" for c in fixture), encoding="utf-8")
    print(f"wrote {path} ({len(actions)} actions)")


def main():
    attack_deck = [STRIKE] * 9 + [DEFEND]
    skill_deck = [DEFEND] * 9 + [STRIKE]

    # SHURIKEN + KUNAI + ORNAMENTAL_FAN: 3 attacks on turn 1 (all three
    # relics trigger), end turn (counter reset), 2 attacks on turn 2.
    build_fixture(
        "p1-relic-attack-counters", "p1-attack-counters-seed",
        ["SHURIKEN", "KUNAI", "ORNAMENTAL_FAN"], attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"),
              ("end_turn",),
              ("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy")],
    )

    # LETTER_OPENER: 3 skills on turn 1 -> 5 damage to ALL on the 3rd.
    build_fixture(
        "p1-relic-letter-opener", "p1-letter-opener-seed",
        ["LETTER_OPENER"], skill_deck, "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("play", "Skill", 0), ("play", "Skill", 0),
              ("end_turn",)],
    )

    # RAINBOW_RING: attack + skill + power on turn 1.
    build_fixture(
        "p1-relic-rainbow-ring", "p1-rainbow-ring-seed",
        ["RAINBOW_RING"], [STRIKE, DEFEND, POWER, DEFEND, DEFEND], "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Skill", 0), ("play", "Power", 0)],
    )

    # MERCURY_HOURGLASS: end turn -> 3 damage to ALL on turn-2 start.
    build_fixture(
        "p1-relic-mercury-hourglass", "p1-mercury-hourglass-seed",
        ["MERCURY_HOURGLASS"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # MR_STRUGGLES: turn-number damage, two transitions.
    build_fixture(
        "p1-relic-mr-struggles", "p1-mr-struggles-seed",
        ["MR_STRUGGLES"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # SAI: block on turn start.
    build_fixture(
        "p1-relic-sai", "p1-sai-seed",
        ["SAI"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # CANDELABRA: energy on turn 2 only.
    build_fixture(
        "p1-relic-candelabra", "p1-candelabra-seed",
        ["CANDELABRA"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # CHANDELIER: energy on turn 3 only.
    build_fixture(
        "p1-relic-chandelier", "p1-chandelier-seed",
        ["CHANDELIER"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",), ("end_turn",)],
    )

    # FAKE_HAPPY_FLOWER: 5-turn cycle, trigger on the 5th turn start.
    build_fixture(
        "p1-relic-fake-happy-flower", "p1-fake-happy-flower-seed",
        ["FAKE_HAPPY_FLOWER"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",)] * 5,
    )

    # FAKE_ORICHALCUM: end turn at 0 block, enemy attack reduced by 3.
    build_fixture(
        "p1-relic-fake-orichalcum", "p1-fake-orichalcum-seed",
        ["FAKE_ORICHALCUM"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",)],
    )


if __name__ == "__main__":
    main()
