#!/usr/bin/env python3
"""Small independent public-state shadow for deterministic basic cards.

This is deliberately conservative: only direct Strike/Defend/Bash-style
damage/block is simulated. Unsupported effects remain unchanged and are
reported as unsupported rather than guessed.
"""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path


def value(mapping: dict, key: str, default=0):
    raw = mapping.get(key, default)
    try:
        return int(raw)
    except (TypeError, ValueError):
        return default


def simulate_action(previous: dict, action_id: str) -> tuple[dict, str | None]:
    state = copy.deepcopy(previous)
    if not action_id.startswith("play_card:"):
        return state, None
    candidate_id = action_id.removeprefix("play_card:")
    candidate_id = "play:" + candidate_id
    candidate = next((item for item in state.get("action_candidates", []) or [] if item.get("action_id") == candidate_id), None)
    if not candidate:
        return state, "candidate_not_found"
    instance_id = candidate.get("source_instance_id")
    card = next((item for item in state.get("hand", []) or [] if item.get("instance_id") == instance_id), None)
    if not card:
        return state, "card_not_found"
    player = state.setdefault("player", {})
    player["energy"] = max(0, value(player, "energy", value(state, "energy", 0)) - value(card, "cost", 0))
    state["energy"] = player["energy"]
    state["hand"] = [item for item in state.get("hand", []) or [] if item.get("instance_id") != instance_id]
    state["action_candidates"] = [
        item for item in state.get("action_candidates", []) or []
        if item.get("source_instance_id") != instance_id
    ]
    state["discard_pile_count"] = value(state, "discard_pile_count", 0) + 1
    stats = card.get("stats", {}) or {}
    if str(card.get("type")) == "Skill" and value(stats, "block"):
        player["block"] = value(player, "block", 0) + value(stats, "block")
        return state, None
    if str(card.get("type")) == "Attack" and value(stats, "damage"):
        target = candidate.get("target_id")
        enemy = next((item for item in state.get("enemies", []) or [] if item.get("instance_id") == target), None)
        if enemy is None:
            return state, "target_not_found"
        enemy["hp"] = max(0, value(enemy, "hp", 0) - value(stats, "damage"))
        return state, None
    return state, "unsupported_card_effect"


def simulate(rows: list[dict]) -> list[dict]:
    output = []
    latest_public: dict | None = None
    for row in rows:
        shadow = copy.deepcopy(row)
        public = row.get("public_observation")
        action_id = row.get("normalized_action_id", "")
        if isinstance(public, dict):
            if action_id.startswith("play_card:") and latest_public is not None:
                simulated, unsupported = simulate_action(latest_public, action_id)
                shadow["public_observation"] = simulated
                shadow["shadow_unsupported_reason"] = unsupported
            else:
                shadow["public_observation"] = copy.deepcopy(public)
            latest_public = copy.deepcopy(shadow["public_observation"])
        elif action_id.startswith("play_card:") and latest_public is not None:
            simulated, unsupported = simulate_action(latest_public, action_id)
            shadow["public_observation"] = simulated
            shadow["shadow_unsupported_reason"] = unsupported
            latest_public = copy.deepcopy(simulated)
        shadow["shadow_source"] = "independent_public_baseline"
        output.append(shadow)
    return output


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    with args.input.open(encoding="utf-8") as handle:
        rows = [json.loads(line) for line in handle if line.strip()]
    result = simulate(rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as handle:
        for row in result:
            handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    print(len(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
