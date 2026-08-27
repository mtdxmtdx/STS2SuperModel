#!/usr/bin/env python3
"""Stable Action ID Replay Mapping for Slay the Spire 2 v0.111.0.

Maps stable ActionCandidate IDs (source_instance_id, target_id, etc.)
to legacy CLI 0.2.0 index-based commands (card_index, potion_index, target_index)
using the current public observation snapshot.
"""

from __future__ import annotations

import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Union

from capture_cli_trace import JsonLineReader, LOCK

VERSION_LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
    "schema_version": 1,
}


class ReplayActionError(ValueError):
    """Raised when a stable action cannot be mapped to the current state."""
    pass


@dataclass(frozen=True)
class ActionCandidate:
    kind: str
    action_id: str
    source_model_id: Optional[str] = None
    source_instance_id: Optional[str] = None
    target_id: Optional[str] = None
    choice_id: Optional[str] = None
    selected_card_instance_ids: tuple[str, ...] = ()
    effective_energy_cost: int = 0
    legal: bool = True
    restriction: Optional[str] = None

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> ActionCandidate:
        return cls(
            kind=data.get("kind", ""),
            action_id=data.get("action_id", ""),
            source_model_id=data.get("source_model_id"),
            source_instance_id=data.get("source_instance_id"),
            target_id=data.get("target_id"),
            choice_id=data.get("choice_id"),
            selected_card_instance_ids=tuple(data.get("selected_card_instance_ids") or ()),
            effective_energy_cost=int(data.get("effective_energy_cost", 0)),
            legal=bool(data.get("legal", True)),
            restriction=data.get("restriction"),
        )


def _clean_id(val: Optional[str]) -> str:
    if not val:
        return ""
    val = val.strip()
    if val.startswith("CARD."):
        return val[5:]
    if val.startswith("POTION."):
        return val[7:]
    return val


def resolve_action(
    observation: Dict[str, Any],
    action: Union[Dict[str, Any], ActionCandidate],
) -> Dict[str, Any]:
    """Map a stable action to a CLI 0.2.0 command dictionary.

    Args:
        observation: The current public observation dictionary from CLI.
        action: Dict or ActionCandidate with stable ID fields.

    Returns:
        A CLI 0.2.0 action command dict, e.g.:
        {"cmd": "action", "action": "play_card", "args": {"card_index": 1, "target_index": 0}}

    Raises:
        ReplayActionError: If resolution fails for any reason before sending to CLI.
    """
    if isinstance(action, ActionCandidate):
        kind = action.kind
        source_instance_id = action.source_instance_id
        source_model_id = action.source_model_id
        target_id = action.target_id
        choice_id = action.choice_id
        selected_card_instance_ids = action.selected_card_instance_ids
    else:
        kind = action.get("kind", "")
        source_instance_id = action.get("source_instance_id")
        source_model_id = action.get("source_model_id")
        target_id = action.get("target_id")
        choice_id = action.get("choice_id")
        selected_card_instance_ids = tuple(action.get("selected_card_instance_ids") or ())

    if not kind:
        raise ReplayActionError("Action missing required 'kind' field")

    # 1. PlayCard
    if kind == "PlayCard":
        if not source_instance_id or not source_instance_id.strip():
            raise ReplayActionError("PlayCard requires a non-empty 'source_instance_id'")

        hand = observation.get("hand", []) or []
        matches = [c for c in hand if c.get("instance_id") == source_instance_id]

        if len(matches) == 0:
            available = [c.get("instance_id") for c in hand]
            raise ReplayActionError(
                f"Card instance '{source_instance_id}' not found in hand. Available: {available}"
            )
        if len(matches) > 1:
            raise ReplayActionError(
                f"Duplicate card instance '{source_instance_id}' found in hand ({len(matches)} items)"
            )

        card = matches[0]

        if source_model_id:
            card_id = _clean_id(card.get("id"))
            expected_id = _clean_id(source_model_id)
            if card_id != expected_id:
                raise ReplayActionError(
                    f"Card model ID mismatch for instance '{source_instance_id}': expected '{source_model_id}', found '{card.get('id')}'"
                )

        card_index = card.get("index")
        if card_index is None:
            card_index = hand.index(card)

        args: Dict[str, Any] = {"card_index": int(card_index)}

        # Resolve target
        target_type = card.get("target_type", "")
        enemies = [e for e in (observation.get("enemies", []) or []) if e.get("hp", 0) > 0]

        if target_id and target_id not in ("none", "null", "None", ""):
            enemy_matches = [e for e in enemies if e.get("instance_id") == target_id]
            if len(enemy_matches) == 0:
                all_enemies = [e.get("instance_id") for e in observation.get("enemies", []) or []]
                raise ReplayActionError(
                    f"Target enemy '{target_id}' not found among alive enemies. All: {all_enemies}"
                )
            if len(enemy_matches) > 1:
                raise ReplayActionError(
                    f"Duplicate target enemy '{target_id}' ({len(enemy_matches)} items)"
                )
            enemy = enemy_matches[0]
            target_index = enemy.get("index")
            if target_index is None:
                target_index = (observation.get("enemies", []) or []).index(enemy)
            args["target_index"] = int(target_index)
        elif target_type == "AnyEnemy":
            if len(enemies) > 1:
                raise ReplayActionError(
                    f"Card target_type is AnyEnemy with {len(enemies)} alive enemies, but no target_id was specified"
                )
            elif len(enemies) == 1:
                target_index = enemies[0].get("index")
                if target_index is None:
                    target_index = (observation.get("enemies", []) or []).index(enemies[0])
                args["target_index"] = int(target_index)

        return {"cmd": "action", "action": "play_card", "args": args}

    # 2. UsePotion
    elif kind == "UsePotion":
        if not source_instance_id or not source_instance_id.strip():
            raise ReplayActionError("UsePotion requires a non-empty 'source_instance_id'")

        player_data = observation.get("player", {}) or {}
        potions = player_data.get("potions") or observation.get("potions") or []
        matches = [p for p in potions if p.get("instance_id") == source_instance_id]

        if len(matches) == 0:
            available = [p.get("instance_id") for p in potions]
            raise ReplayActionError(
                f"Potion instance '{source_instance_id}' not found. Available: {available}"
            )
        if len(matches) > 1:
            raise ReplayActionError(
                f"Duplicate potion instance '{source_instance_id}' found ({len(matches)} items)"
            )

        potion = matches[0]

        if source_model_id:
            potion_id = _clean_id(potion.get("id"))
            expected_id = _clean_id(source_model_id)
            if potion_id != expected_id:
                raise ReplayActionError(
                    f"Potion model ID mismatch for instance '{source_instance_id}': expected '{source_model_id}', found '{potion.get('id')}'"
                )

        potion_index = potion.get("index")
        if potion_index is None:
            potion_index = potions.index(potion)

        args = {"potion_index": int(potion_index)}

        target_type = potion.get("target_type", "")
        enemies = [e for e in (observation.get("enemies", []) or []) if e.get("hp", 0) > 0]

        if target_id and target_id not in ("none", "null", "None", ""):
            enemy_matches = [e for e in enemies if e.get("instance_id") == target_id]
            if len(enemy_matches) == 0:
                raise ReplayActionError(
                    f"Target enemy '{target_id}' not found for potion. Alive: {[e.get('instance_id') for e in enemies]}"
                )
            if len(enemy_matches) > 1:
                raise ReplayActionError(
                    f"Duplicate target enemy '{target_id}' for potion"
                )
            enemy = enemy_matches[0]
            target_index = enemy.get("index")
            if target_index is None:
                target_index = (observation.get("enemies", []) or []).index(enemy)
            args["target_index"] = int(target_index)
        elif target_type == "AnyEnemy":
            if len(enemies) > 1:
                raise ReplayActionError(
                    f"Potion target_type is AnyEnemy with {len(enemies)} alive enemies, but no target_id specified"
                )
            elif len(enemies) == 1:
                target_index = enemies[0].get("index")
                if target_index is None:
                    target_index = (observation.get("enemies", []) or []).index(enemies[0])
                args["target_index"] = int(target_index)

        return {"cmd": "action", "action": "use_potion", "args": args}

    # 3. EndTurn
    elif kind == "EndTurn":
        return {"cmd": "action", "action": "end_turn", "args": {}}

    # 4. Choice
    elif kind == "Choice":
        if choice_id is None:
            raise ReplayActionError("Choice requires 'choice_id'")
        if selected_card_instance_ids:
            if observation.get("decision") != "card_select":
                raise ReplayActionError("Card Choice requires a current card_select observation")
            if observation.get("choice_id") != choice_id:
                raise ReplayActionError(
                    f"Choice ID mismatch: expected {observation.get('choice_id')!r}, got {choice_id!r}"
                )
            cards = observation.get("cards") or []
            selected_indices = []
            for instance_id in selected_card_instance_ids:
                matches = [card for card in cards if card.get("instance_id") == instance_id]
                if len(matches) != 1:
                    raise ReplayActionError(
                        f"Selected card instance '{instance_id}' matched {len(matches)} current options"
                    )
                selected_indices.append(int(matches[0].get("index", cards.index(matches[0]))))
            if len(set(selected_indices)) != len(selected_indices):
                raise ReplayActionError("Card Choice contains duplicate selected instances")
            minimum = int(observation.get("min_select", 0))
            maximum = int(observation.get("max_select", len(cards)))
            if not minimum <= len(selected_indices) <= maximum:
                raise ReplayActionError(
                    f"Card Choice selected {len(selected_indices)} cards; expected {minimum}..{maximum}"
                )
            return {
                "cmd": "action",
                "action": "select_cards",
                "args": {"indices": ",".join(str(index) for index in selected_indices)},
            }
        try:
            choice_idx = int(choice_id)
        except ValueError:
            raise ReplayActionError(f"Choice 'choice_id' must be integer-convertible: {choice_id!r}")
        return {"cmd": "action", "action": "choose_option", "args": {"option_index": choice_idx}}

    else:
        raise ReplayActionError(f"Unsupported action kind: '{kind}'")


class ReplayRunner:
    """Executes stable action sequences against a running CLI headless process."""

    def __init__(
        self,
        executable: Path,
        library: Optional[Path] = None,
        response_timeout_seconds: float = 30.0,
    ):
        self.executable = Path(executable)
        self.library = Path(library) if library else None
        self.response_timeout_seconds = response_timeout_seconds

    def run_replay(
        self,
        setup_commands: Sequence[Dict[str, Any]],
        actions: Sequence[Union[Dict[str, Any], ActionCandidate]],
        trace_path: Optional[Path] = None,
    ) -> List[Dict[str, Any]]:
        """Run setup commands followed by dynamically resolved stable actions.

        After each action, re-reads the public combat snapshot so all indices are refreshed.
        """
        env = os.environ.copy()
        if self.library:
            env["STS2_LIB"] = str(self.library)
        if trace_path:
            env["STS2_TRACE_PATH"] = str(trace_path)
            trace_path.parent.mkdir(parents=True, exist_ok=True)
            if trace_path.exists():
                trace_path.unlink()

        process = subprocess.Popen(
            [str(self.executable)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1,
            env=env,
        )
        reader = JsonLineReader(process.stdout)
        responses: List[Dict[str, Any]] = []

        try:
            ready = reader.next(self.response_timeout_seconds)
            for key, expected in LOCK.items():
                if ready.get(key) != expected:
                    raise RuntimeError(f"version gate failed: {key}={ready.get(key)!r}, expected {expected!r}")
            if ready.get("compatible") is not True:
                raise RuntimeError(f"CLI incompatible: {ready.get('compatibility_error')}")
            responses.append(ready)

            # 1. Run setup commands
            for cmd in setup_commands:
                process.stdin.write(json.dumps(cmd, ensure_ascii=False) + "\n")
                process.stdin.flush()
                resp = reader.next(self.response_timeout_seconds)
                responses.append(resp)
                if resp.get("type") == "error":
                    raise RuntimeError(f"Setup command failed: {resp}")

            # 2. Get initial combat snapshot if not already in combat_play
            current_obs = None
            last_resp = responses[-1]
            if last_resp.get("type") == "combat_snapshot" and "public_observation" in last_resp:
                current_obs = last_resp["public_observation"]
            elif last_resp.get("decision") == "combat_play":
                current_obs = last_resp

            if current_obs is None:
                # Request public snapshot
                process.stdin.write('{"cmd":"get_combat_snapshot","view":"public"}\n')
                process.stdin.flush()
                snap_resp = reader.next(self.response_timeout_seconds)
                responses.append(snap_resp)
                current_obs = snap_resp.get("public_observation", {})

            # 3. Execute stable actions one by one
            for action in actions:
                resolved_cmd = resolve_action(current_obs, action)
                process.stdin.write(json.dumps(resolved_cmd, ensure_ascii=False) + "\n")
                process.stdin.flush()
                action_resp = reader.next(self.response_timeout_seconds)
                responses.append(action_resp)
                if action_resp.get("type") == "error":
                    raise RuntimeError(f"Replay action execution failed: {action_resp}")

                # Update current observation for next step
                if action_resp.get("decision") == "combat_play":
                    current_obs = action_resp
                elif action_resp.get("type") == "combat_snapshot" and "public_observation" in action_resp:
                    current_obs = action_resp["public_observation"]
                else:
                    # Refresh snapshot
                    process.stdin.write('{"cmd":"get_combat_snapshot","view":"public"}\n')
                    process.stdin.flush()
                    refresh_resp = reader.next(self.response_timeout_seconds)
                    responses.append(refresh_resp)
                    current_obs = refresh_resp.get("public_observation", action_resp)

            # Quit cleanly
            process.stdin.write('{"cmd":"quit"}\n')
            process.stdin.flush()
            quit_resp = reader.next(self.response_timeout_seconds)
            responses.append(quit_resp)
            process.wait(timeout=10)

        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)

        return responses
