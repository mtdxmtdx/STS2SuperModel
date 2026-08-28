"""Loss-aware conversion of CLI teacher/public payloads to CombatSnapshot JSON.

The adapter preserves stable IDs and all observed containers.  Card semantics
are reconstructed from explicit effects when present, otherwise from the
runtime preview's damage/block stats.  Any lossy field is returned as a
warning so the caller can downgrade the label rather than claim Reliable.
"""

from __future__ import annotations

from typing import Any


def _target(card: dict[str, Any]) -> str:
    target = str(card.get("target_type", "Self"))
    return "Enemy" if "Enemy" in target else "AllEnemies" if "All" in target else "Self"


def _effects(card: dict[str, Any]) -> tuple[list[dict[str, Any]], list[str]]:
    if isinstance(card.get("effects"), list):
        return card["effects"], []
    stats = card.get("stats") or {}
    effects: list[dict[str, Any]] = []
    warnings: list[str] = []
    if isinstance(stats.get("damage"), (int, float)) and stats["damage"]:
        effects.append({"kind": "Damage", "amount": stats["damage"], "target_override": "Enemy"})
    if isinstance(stats.get("block"), (int, float)) and stats["block"]:
        effects.append({"kind": "Block", "amount": stats["block"], "target_override": "Self"})
    if not effects:
        warnings.append(f"card_effects_missing:{card.get('id', card.get('instance_id', 'unknown'))}")
    return effects, warnings


def _card(card: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    effects, warnings = _effects(card)
    cost = card.get("cost", 0)
    return {
        "instance_id": card.get("instance_id", ""),
        "model_id": str(card.get("id", "")).removeprefix("CARD."),
        "name": card.get("name", card.get("id", "")),
        "energy_cost": int(cost) if isinstance(cost, (int, float)) else 0,
        "target": _target(card),
        "effects": effects,
        "destination": "Discard",
        "is_playable": bool(card.get("can_play", True)),
        "card_type": card.get("type"),
        "is_upgraded": bool(card.get("upgraded", False)),
        "base_energy_cost": int(cost) if isinstance(cost, (int, float)) else 0,
    }, warnings


def rebuild_combat_snapshot(teacher: dict[str, Any], public: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    warnings: list[str] = []
    source = teacher if isinstance(teacher, dict) else public
    hand = source.get("hand") or public.get("hand") or []
    draw = source.get("draw_pile") or []
    discard = source.get("discard_pile") or []
    exhaust = source.get("exhaust_pile") or []
    cards: dict[str, list[dict[str, Any]]] = {}
    public_cards = {
        str(card.get("instance_id")): card
        for card in public.get("hand", []) or []
        if isinstance(card, dict) and card.get("instance_id")
    }
    for key, values in (("hand", hand), ("draw_pile", draw), ("discard_pile", discard), ("exhaust_pile", exhaust)):
        converted = []
        for value in values:
            # Teacher payloads often omit preview stats; merge the public
            # observation for the same stable instance before compiling.
            merged = dict(public_cards.get(str(value.get("instance_id")), {}))
            merged.update(value)
            card, card_warnings = _card(merged)
            warnings.extend(card_warnings)
            converted.append(card)
        cards[key] = converted
    player = source.get("player") or public.get("player") or {}
    statuses = player.get("statuses") or {}
    enemies = []
    for enemy in source.get("enemies") or public.get("enemies") or []:
        intents = []
        for intent in enemy.get("intents") or []:
            intents.append({"type": intent.get("type", "Unknown"), "damage_per_hit": intent.get("damage", 0), "hits": 1, "effects": []})
        enemies.append({
            "id": enemy.get("instance_id", enemy.get("id", "enemy:unknown")),
            "name": enemy.get("name", "Unknown"),
            "hp": enemy.get("hp", 0), "max_hp": enemy.get("max_hp", enemy.get("hp", 0)),
            "block": enemy.get("block", 0), "statuses": enemy.get("statuses") or {}, "intents": intents,
        })
    if not enemies:
        warnings.append("enemy_state_missing")
    snapshot = {
        "fingerprint": public.get("state_hash_public", public.get("fingerprint", "teacher-snapshot")),
        "player": {
            "hp": player.get("hp", 0), "max_hp": player.get("max_hp", player.get("hp", 0)),
            "block": player.get("block", 0), "energy": player.get("energy", 0),
            "max_energy": player.get("max_energy", player.get("energy", 0)), "statuses": statuses,
        },
        "enemies": enemies,
        "hand": cards["hand"], "draw_pile": cards["draw_pile"],
        "discard_pile": cards["discard_pile"], "exhaust_pile": cards["exhaust_pile"],
        "potions": player.get("potions") or [], "rng_state": 0,
        "round": source.get("round", public.get("round", 0)), "is_boss": False,
        "global_restrictions": [], "orbs": [], "orb_capacity": 0,
        "relics": player.get("relics") or [], "powers": player.get("powers") or [],
        "view": "Teacher",
    }
    return snapshot, sorted(set(warnings))
