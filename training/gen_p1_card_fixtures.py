#!/usr/bin/env python3
"""Generate v0.111 card semantic-pattern fixtures (plan batch C1).

Each fixture exercises one high-risk semantic pattern with real cards. The
generator performs a probe run against the headless CLI with the fixture's
fixed seed to resolve deterministic hand indices (the draw order is seeded,
so the replay of the emitted commands reproduces the same hands), then
writes the final commands JSONL without the probe-only snapshot calls.

Upgraded variants are instantiated through the engine's own rest-site SMITH
upgrade path (choose_option -> select_cards), because ModelDb only carries
canonical base-card models.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
FIXTURE_DIR = ROOT / "training/fixtures"

SEAPUNK = "SEAPUNK_WEAK"
SLIMES = "SLIMES_WEAK"
STRIKE_I = "STRIKE_IRONCLAD"
DEFEND_I = "DEFEND_IRONCLAD"
STRIKE_S = "STRIKE_SILENT"
DEFEND_S = "DEFEND_SILENT"
STRIKE_N = "STRIKE_NECROBINDER"
DEFEND_N = "DEFEND_NECROBINDER"

SPECS: list[dict] = [
    {
        "name": "p1-card-exhaust-self",
        "character": "Ironclad", "hp": 80,
        "deck": ["MOLTEN_FIST", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "MOLTEN_FIST", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # DEFILE is Ethereal: kept in hand at end of turn it vanishes.
        "name": "p1-card-ethereal",
        "character": "Necrobinder", "hp": 75,
        "deck": ["DEFILE", STRIKE_N, STRIKE_N, DEFEND_N, DEFEND_N],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"end_turn": True},
            {"end_turn": True},
        ],
    },
    {
        # SNAKEBITE is Retain: survives the end-of-turn discard, then plays.
        "name": "p1-card-retain",
        "character": "Silent", "hp": 70,
        "deck": ["SNAKEBITE", STRIKE_S, STRIKE_S, STRIKE_S, STRIKE_S],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": STRIKE_S, "target": 0},
            {"end_turn": True},
            {"play": "SNAKEBITE", "target": 0},
        ],
    },
    {
        # BACKSTAB is Innate in a 10-card deck: it must be pulled into the
        # opening hand regardless of the seeded shuffle, then self-exhausts.
        "name": "p1-card-innate",
        "character": "Silent", "hp": 70,
        "deck": [STRIKE_S, STRIKE_S, STRIKE_S, STRIKE_S, STRIKE_S,
                 STRIKE_S, STRIKE_S, STRIKE_S, STRIKE_S, "BACKSTAB"],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "BACKSTAB", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # SURVIVOR opens a hand discard selection (block + discard 1).
        "name": "p1-card-discard-select",
        "character": "Silent", "hp": 70,
        "deck": ["SURVIVOR", STRIKE_S, STRIKE_S, DEFEND_S, DEFEND_S],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "SURVIVOR"},
            {"select": [STRIKE_S]},
            {"end_turn": True},
        ],
    },
    {
        # TRUE_GRIT randomly exhausts 1 hand card (CombatCardSelection).
        "name": "p1-card-random-exhaust",
        "character": "Ironclad", "hp": 80,
        "deck": ["TRUE_GRIT", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "TRUE_GRIT"},
            {"end_turn": True},
        ],
    },
    {
        # SWORD_BOOMERANG deals 3 random-enemy hits (CombatTargets) into a
        # three-enemy encounter.
        "name": "p1-card-random-target",
        "character": "Ironclad", "hp": 80,
        "deck": ["SWORD_BOOMERANG", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SLIMES}},
            {"play": "SWORD_BOOMERANG", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # DUAL_WIELD opens a hand choice and copies the selected attack.
        "name": "p1-card-choice-copy",
        "character": "Ironclad", "hp": 80,
        "deck": ["DUAL_WIELD", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "DUAL_WIELD"},
            {"select": [STRIKE_I]},
            {"end_turn": True},
        ],
    },
    {
        # ANGER deals damage and generates a self-copy into the discard pile.
        "name": "p1-card-generate",
        "character": "Ironclad", "hp": 80,
        "deck": ["ANGER", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "ANGER", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # LEADING_STRIKE generates two SHIV cards into the hand; one Shiv is
        # then played (generated-card playability).
        "name": "p1-card-generate-shiv",
        "character": "Silent", "hp": 70,
        "deck": ["LEADING_STRIKE", STRIKE_S, STRIKE_S, STRIKE_S, STRIKE_S],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "LEADING_STRIKE", "target": 0},
            {"play": "SHIV", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # WHIRLWIND is X-cost: played with the full opening 3 energy. Single
        # enemy on purpose: the slime encounters inject SLIMED status cards
        # during the enemy turn (documented enemy-intent gap), which would
        # pollute the turn-boundary comparison.
        "name": "p1-card-x-cost",
        "character": "Ironclad", "hp": 80,
        "deck": ["WHIRLWIND", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": "WHIRLWIND", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # HAVOC auto-plays the top card of the (non-empty) draw pile and
        # exhausts it. Six-card deck: the opening draw leaves exactly one
        # card on top of the draw pile, deterministically for the seed.
        "name": "p1-card-auto-play",
        "character": "Ironclad", "hp": 80,
        "deck": ["HAVOC", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            # The early teacher snapshot carries the real draw-pile identity
            # (the one card the opening draw left behind), so the shadow's
            # auto-play can replay the played card's effect.
            {"snapshot": "teacher"},
            {"play": "HAVOC"},
            {"end_turn": True},
        ],
    },
    {
        # HEADBUTT base: damage + the discard->draw-top move. The engine
        # resolves the discard-pile pick without an interactive selector in
        # headless mode (verified: the moved card lands on the draw-pile top),
        # so the fixture captures the resulting pile transition directly.
        # Paired with the SMITH-upgraded run below for the upgrade delta.
        "name": "p1-card-move-upgrade-base",
        "character": "Ironclad", "hp": 80,
        "deck": ["HEADBUTT", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": STRIKE_I, "target": 0},
            {"play": "HEADBUTT", "target": 0},
            {"end_turn": True},
        ],
    },
    {
        # HEADBUTT upgraded via the engine's own rest-site SMITH path, then
        # the same combat sequence as the base fixture.
        "name": "p1-card-move-upgrade-up",
        "character": "Ironclad", "hp": 80,
        "deck": ["HEADBUTT", STRIKE_I, STRIKE_I, DEFEND_I, DEFEND_I],
        "rooms": [
            {"enter_room": {"type": "rest_site"}},
            {"choose_option": 1},
            {"select": ["HEADBUTT"]},
            {"enter_room": {"type": "combat", "encounter": SEAPUNK}},
            {"play": STRIKE_I, "target": 0},
            {"play": "HEADBUTT", "target": 0},
            {"end_turn": True},
        ],
    },
]


class CliProbe:
    """Minimal CLI session used to resolve deterministic card indices."""

    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)

    def send(self, command: dict) -> dict:
        assert self.proc.stdin is not None and self.proc.stdout is not None
        self.proc.stdin.write(json.dumps(command) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        while line and not line.startswith("{"):
            line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError(f"CLI stopped responding at {command}")
        reply = json.loads(line)
        if reply.get("type") in ("error", "save_error"):
            raise RuntimeError(f"CLI error for {command}: {reply.get('message')}")
        return reply

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.write('{"cmd":"quit"}\n')
                self.proc.stdin.flush()
                self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()


def card_base_id(card: dict) -> str:
    return str(card.get("id") or "").removeprefix("CARD.")


def hand_index(hand: list[dict], card_id: str) -> int:
    for card in hand:
        if card_base_id(card) == card_id:
            return int(card["index"])
    raise MissingCardError(f"card {card_id} not in hand {[card_base_id(c) for c in hand]}")


class MissingCardError(RuntimeError):
    pass


def select_indices(pending: list[dict], wanted: list[str]) -> list[int]:
    indices: list[int] = []
    for want in wanted:
        match = next((i for i, card in enumerate(pending)
                      if card_base_id(card) == want and i not in indices), None)
        if match is None:
            raise MissingCardError(
                f"wanted {want} among {[card_base_id(c) for c in pending]}")
        indices.append(match)
    return indices


def generate(spec: dict, seed: str) -> list[dict]:
    probe = CliProbe()
    try:
        probe.send({"cmd": "start_run", "character": spec["character"],
                    "seed": seed, "ascension": 0})
        probe.send({"cmd": "set_player", "relics": [], "hp": spec["hp"],
                    "max_hp": spec["hp"], "deck": spec["deck"]})

        # The CLI returns a degenerate "ok" reply for two consecutive
        # get_combat_snapshot calls, so the loop keeps exactly one tracked
        # `state` reply and never polls twice without an action in between.
        state: dict = {}

        def poll() -> dict:
            nonlocal state
            state = probe.send({"cmd": "get_combat_snapshot", "view": "public"})
            return state

        resolved: list[dict] = []
        for step in spec["rooms"]:
            if "enter_room" in step:
                room = dict(step["enter_room"])
                command = {"cmd": "enter_room", "type": room["type"]}
                if room.get("encounter"):
                    command["encounter"] = room["encounter"]
                reply = probe.send(command)
                resolved.append(command)
                state = reply if reply.get("decision") or reply.get("hand") else poll()
            elif "choose_option" in step:
                command = {"cmd": "action", "action": "choose_option",
                           "args": {"option_index": step["choose_option"]}}
                reply = probe.send(command)
                resolved.append(command)
                state = poll()
            elif "play" in step:
                if state.get("decision") == "card_select":
                    raise RuntimeError(
                        f"{spec['name']}: unresolved card_select before play step")
                if not state.get("hand"):
                    poll()
                index = hand_index(state.get("hand") or [], step["play"])
                args: dict = {"card_index": index}
                if "target" in step:
                    args["target_index"] = step["target"]
                command = {"cmd": "action", "action": "play_card", "args": args}
                state = probe.send(command)
                resolved.append(command)
            elif "select" in step:
                if state.get("decision") != "card_select":
                    poll()
                if state.get("decision") != "card_select":
                    raise RuntimeError(
                        f"{spec['name']}: expected pending card_select, got "
                        f"{state.get('decision')!r}")
                indices = select_indices(state.get("cards") or [], step["select"])
                command = {"cmd": "action", "action": "select_cards",
                           "args": {"indices": ",".join(str(i) for i in indices)}}
                state = probe.send(command)
                resolved.append(command)
            elif "snapshot" in step:
                # Read-only view: the tracked game state is unchanged, so the
                # last known state stays current (avoids a second consecutive
                # snapshot, which the CLI answers with a bare "ok").
                command = {"cmd": "get_combat_snapshot", "view": step["snapshot"]}
                probe.send(command)
                resolved.append(command)
            elif "end_turn" in step:
                command = {"cmd": "action", "action": "end_turn", "args": {}}
                state = probe.send(command)
                resolved.append(command)
            else:
                raise RuntimeError(f"unknown step {step}")
    finally:
        probe.close()
    return resolved


def emit(spec: dict, seed: str, resolved: list[dict]) -> list[str]:
    commands = [
        {"cmd": "start_run", "character": spec["character"],
         "seed": seed, "ascension": 0},
        {"cmd": "set_player", "relics": [], "hp": spec["hp"],
         "max_hp": spec["hp"], "deck": spec["deck"]},
    ]
    for command in resolved:
        commands.append(command)
        if command.get("cmd") == "enter_room" and command.get("type") == "combat" \
                and not any(c.get("view") == "public" for c in commands):
            commands.append({"cmd": "get_combat_snapshot", "view": "public"})
    commands.append({"cmd": "get_combat_snapshot", "view": "teacher"})
    return [json.dumps(command, ensure_ascii=False) for command in commands]


def main() -> int:
    FIXTURE_DIR.mkdir(exist_ok=True)
    failures = 0
    for spec in SPECS:
        written = False
        for attempt in range(5):
            seed = spec["name"] if attempt == 0 else f"{spec['name']}-{attempt + 1}"
            try:
                resolved = generate(spec, seed)
            except (RuntimeError, MissingCardError) as error:
                print(f"RETRY {spec['name']} seed={seed}: {error}")
                continue
            path = FIXTURE_DIR / f"{spec['name']}-commands.jsonl"
            path.write_text("\n".join(emit(spec, seed, resolved)) + "\n",
                            encoding="utf-8")
            print(f"WROTE {path.name} (seed={seed}, {len(resolved)} actions)")
            written = True
            break
        if not written:
            failures += 1
            print(f"FAIL {spec['name']}: no working seed in 5 attempts")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
