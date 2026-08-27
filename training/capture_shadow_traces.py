#!/usr/bin/env python3
"""Capture deterministic CLI traces for P0 shadow differential validation."""

from __future__ import annotations

import os
import sys
from pathlib import Path

CLI_DIR = Path(__file__).parent.parent / "sts2-cli-v0111"
sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(CLI_DIR / "tests"))

import test_v0111_consistency as tv
from replay_action import resolve_action

DATA_DIR = Path(__file__).parent.parent / "data"


def capture_bash_trace():
    trace_path = DATA_DIR / "p0-bash-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()

    # Step 1: Initialize with deck containing BASH at top
    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-diff-bash-seed", "ascension": 0},
        {"cmd": "set_player", "deck": ["BASH", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    res1 = tv.run_commands(setup, extra_env=extra_env)
    obs = res1[4]["public_observation"]

    # Step 2: Resolve BASH play
    bash_cand = next(c for c in obs["action_candidates"] if "BASH" in c.get("source_model_id", ""))
    cmd_bash = resolve_action(obs, bash_cand)

    full_cmds = setup + [cmd_bash, {"cmd": "get_combat_snapshot", "view": "public"}]
    tv.run_commands(full_cmds, extra_env=extra_env)
    print(f"Captured Bash trace: {trace_path} ({trace_path.stat().st_size} bytes)")


def capture_fire_potion_trace():
    trace_path = DATA_DIR / "p0-fire-potion-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()

    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-diff-fire-potion-seed", "ascension": 0},
        {"cmd": "set_player", "potions": ["FIRE_POTION"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    res1 = tv.run_commands(setup, extra_env=extra_env)
    obs = res1[4]["public_observation"]

    potion_cand = next(c for c in obs["action_candidates"] if c.get("kind") == "UsePotion")
    cmd_potion = resolve_action(obs, potion_cand)

    full_cmds = setup + [cmd_potion, {"cmd": "get_combat_snapshot", "view": "public"}]
    tv.run_commands(full_cmds, extra_env=extra_env)
    print(f"Captured Fire Potion trace: {trace_path} ({trace_path.stat().st_size} bytes)")


def capture_energy_potion_trace():
    trace_path = DATA_DIR / "p0-energy-potion-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()

    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-diff-energy-potion-seed", "ascension": 0},
        {"cmd": "set_player", "potions": ["ENERGY_POTION"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    res1 = tv.run_commands(setup, extra_env=extra_env)
    obs = res1[4]["public_observation"]

    potion_cand = next(c for c in obs["action_candidates"] if c.get("kind") == "UsePotion")
    cmd_potion = resolve_action(obs, potion_cand)

    full_cmds = setup + [cmd_potion, {"cmd": "get_combat_snapshot", "view": "public"}]
    tv.run_commands(full_cmds, extra_env=extra_env)
    print(f"Captured Energy Potion trace: {trace_path} ({trace_path.stat().st_size} bytes)")


def capture_bash_then_strike_trace():
    trace_path = DATA_DIR / "p0-bash-then-strike-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()
    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-bash-then-strike-seed", "ascension": 0},
        {"cmd": "set_player", "deck": ["BASH", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    initial = tv.run_commands(setup)
    observation = initial[4]["public_observation"]
    bash = next(candidate for candidate in observation["action_candidates"] if "BASH" in candidate.get("source_model_id", ""))
    bash_command = resolve_action(observation, bash)
    after_bash = tv.run_commands(setup + [bash_command, {"cmd": "get_combat_snapshot", "view": "public"}])
    post_observation = after_bash[6]["public_observation"]
    strike = next(candidate for candidate in post_observation["action_candidates"] if "STRIKE" in candidate.get("source_model_id", ""))
    strike_command = resolve_action(post_observation, strike)
    tv.run_commands(setup + [
        bash_command,
        {"cmd": "get_combat_snapshot", "view": "public"},
        strike_command,
        {"cmd": "get_combat_snapshot", "view": "public"},
    ], extra_env=extra_env)
    print(f"Captured Bash then Strike trace: {trace_path} ({trace_path.stat().st_size} bytes)")


def capture_nunchaku_trace():
    trace_path = DATA_DIR / "p0-nunchaku-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()
    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-nunchaku-seed", "ascension": 0},
        {"cmd": "set_player", "relics": ["NUNCHAKU"], "deck": ["STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    initial = tv.run_commands(setup)
    observation = initial[4]["public_observation"]
    strike = next(candidate for candidate in observation["action_candidates"] if candidate.get("kind") == "PlayCard")
    strike_command = resolve_action(observation, strike)
    tv.run_commands(setup + [strike_command, {"cmd": "get_combat_snapshot", "view": "public"}], extra_env=extra_env)
    print(f"Captured Nunchaku trace: {trace_path} ({trace_path.stat().st_size} bytes)")


def capture_entropic_chance_trace():
    trace_path = DATA_DIR / "p0-chance-entropic-trace.jsonl"
    extra_env = {"STS2_TRACE_PATH": str(trace_path)}
    if trace_path.exists():
        trace_path.unlink()
    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "p0-chance-entropic", "ascension": 0},
        {"cmd": "set_player", "potions": ["ENTROPIC_BREW"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    initial = tv.run_commands(setup)
    observation = initial[4]["public_observation"]
    potion = next(candidate for candidate in observation["action_candidates"] if candidate.get("kind") == "UsePotion")
    potion_command = resolve_action(observation, potion)
    tv.run_commands(setup + [potion_command, {"cmd": "get_combat_snapshot", "view": "public"}], extra_env=extra_env)
    print(f"Captured Entropic Brew chance trace: {trace_path} ({trace_path.stat().st_size} bytes)")


if __name__ == "__main__":
    capture_bash_trace()
    capture_fire_potion_trace()
    capture_energy_potion_trace()
    capture_bash_then_strike_trace()
    capture_nunchaku_trace()
    capture_entropic_chance_trace()
