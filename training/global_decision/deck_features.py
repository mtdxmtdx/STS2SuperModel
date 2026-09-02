"""Public deck/card feature encoding for the global prototype."""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from typing import Any, Iterable, Mapping

from .contracts import validate_public_payload


KNOWN_ROLE_TAGS = frozenset(
    {
        "attack",
        "aoe",
        "block",
        "mitigation",
        "scaling",
        "draw",
        "energy",
        "discard",
        "random_discard",
        "select_discard",
        "consume",
        "exhaust",
        "generated",
        "status",
        "curse",
        "frontload",
        "power",
        "heal",
        "remove",
        "sly",
        "quest",
        "void",
    }
)


def _as_list(value: Any) -> list[Any]:
    if value is None:
        return []
    return list(value) if isinstance(value, (list, tuple)) else [value]


def _num(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _text(card: Mapping[str, Any]) -> str:
    return " ".join(
        str(card.get(key, "")) for key in ("semantic_id", "id", "type", "description", "effect_summary")
    ).lower()


def card_semantic_id(card: Mapping[str, Any]) -> str:
    return str(card.get("semantic_id") or card.get("id") or "OOV")


def card_tags(card: Mapping[str, Any]) -> tuple[str, ...]:
    tags = {str(tag).lower() for tag in _as_list(card.get("tags"))}
    tags.update(str(tag).lower() for tag in _as_list(card.get("keywords")))
    role = card.get("role")
    if role:
        tags.add(str(role).lower())
    text = _text(card)
    keyword_hints = {
        "attack": ("attack", "damage", "strike", "slash", "bash"),
        "aoe": ("all enemies", "aoe", "each enemy", "everyone"),
        "block": ("block", "guard", "defend", "mitigation"),
        "draw": ("draw", "cycle", "discard"),
        "energy": ("energy", "star", "mana"),
        "scaling": ("strength", "poison", "doom", "focus", "scaling", "power"),
        "exhaust": ("exhaust",),
        "discard": ("discard",),
        "generated": ("create", "generate", "add a", "summon"),
        "status": ("wound", "dazed", "burn", "slimed", "status"),
        "curse": ("curse",),
        "frontload": ("damage", "vulnerable", "weak"),
    }
    for tag, hints in keyword_hints.items():
        if any(hint in text for hint in hints):
            tags.add(tag)
    return tuple(sorted(tags & KNOWN_ROLE_TAGS))


def _card_count(cards: Iterable[Mapping[str, Any]]) -> Counter[str]:
    counts: Counter[str] = Counter()
    for card in cards:
        counts[card_semantic_id(card)] += int(card.get("count", 1) or 1)
    return counts


def _token(card: Mapping[str, Any], ordinal: int) -> dict[str, Any]:
    semantic_id = card_semantic_id(card)
    return {
        "semantic_id": semantic_id,
        "instance_id": card.get("card_instance_id") or card.get("instance_id"),
        "count": int(card.get("count", 1) or 1),
        "upgrade_level": int(card.get("upgrade_level", card.get("upgrade", 0)) or 0),
        "enchantment_ids": sorted(str(x) for x in _as_list(card.get("enchantment_ids"))),
        "quest": bool(card.get("quest", False)),
        "temporary": bool(card.get("temporary", False)),
        "generated": bool(card.get("generated", False)),
        "tags": list(card_tags(card)),
        "cost": _num(card.get("cost", card.get("card_cost", 0))),
        "ordinal": ordinal,
    }


def _health(cards: list[Mapping[str, Any]]) -> dict[str, float]:
    if not cards:
        return {
            "deck_size": 0.0,
            "average_cost": 0.0,
            "attack_ratio": 0.0,
            "skill_ratio": 0.0,
            "power_ratio": 0.0,
            "frontload_score": 0.0,
            "aoe_score": 0.0,
            "block_score": 0.0,
            "scaling_score": 0.0,
            "draw_score": 0.0,
            "energy_score": 0.0,
            "discard_score": 0.0,
            "exhaust_score": 0.0,
            "generated_score": 0.0,
            "dead_draw_rate": 0.0,
            "status_burden": 0.0,
            "first_cycle_time": 0.0,
            "later_cycle_time": 0.0,
            "upgrade_density": 0.0,
        }
    size = sum(int(card.get("count", 1) or 1) for card in cards)
    cost_sum = sum(_num(card.get("cost", card.get("card_cost", 0))) * int(card.get("count", 1) or 1) for card in cards)
    weighted_tags: Counter[str] = Counter()
    type_counts: Counter[str] = Counter()
    upgrades = 0
    draw_bonus = 0.0
    for card in cards:
        count = int(card.get("count", 1) or 1)
        weighted_tags.update({tag: count for tag in card_tags(card)})
        type_counts[str(card.get("type", "")).lower()] += count
        upgrade_level = _num(card.get("upgrade_level", card.get("upgrade", 0)))
        upgrades += count * int(upgrade_level > 0)
        draw_bonus += _num(card.get("draw", card.get("draw_count", 0))) * count
    denom = float(max(size, 1))
    def ratio(tag: str) -> float:
        return weighted_tags[tag] / denom
    dead = weighted_tags["status"] + weighted_tags["curse"] + weighted_tags["quest"]
    return {
        "deck_size": float(size),
        "average_cost": cost_sum / denom,
        "attack_ratio": (type_counts["attack"] + weighted_tags["attack"]) / max(denom * 2.0, 1.0),
        "skill_ratio": type_counts["skill"] / denom,
        "power_ratio": (type_counts["power"] + weighted_tags["power"]) / max(denom * 2.0, 1.0),
        "frontload_score": min(1.0, ratio("frontload") + ratio("attack")),
        "aoe_score": min(1.0, ratio("aoe")),
        "block_score": min(1.0, ratio("block") + ratio("mitigation")),
        "scaling_score": min(1.0, ratio("scaling")),
        "draw_score": min(1.0, ratio("draw") + draw_bonus / max(denom * 2.0, 1.0)),
        "energy_score": min(1.0, ratio("energy")),
        "discard_score": min(1.0, ratio("discard")),
        "exhaust_score": min(1.0, ratio("exhaust")),
        "generated_score": min(1.0, ratio("generated")),
        "dead_draw_rate": min(1.0, dead / denom),
        "status_burden": min(1.0, (weighted_tags["status"] + weighted_tags["curse"]) / denom),
        "first_cycle_time": max(1.0, (size - draw_bonus) / 5.0),
        "later_cycle_time": max(1.0, (size - draw_bonus - weighted_tags["exhaust"]) / 5.0),
        "upgrade_density": min(1.0, upgrades / denom),
    }


@dataclass(frozen=True)
class DeckFeatureEncoder:
    """Deterministic public-state encoder; no seed/future fields are accepted."""

    schema_version: str = "deck-feature-v1"

    def encode(self, state: Mapping[str, Any]) -> dict[str, Any]:
        validate_public_payload(state)
        cards = [card for card in _as_list(state.get("deck_public")) if isinstance(card, Mapping)]
        cards = sorted(cards, key=lambda card: (card_semantic_id(card), str(card.get("instance_id", ""))))
        card_tokens = [_token(card, index) for index, card in enumerate(cards)]
        relics = [dict(x) for x in _as_list(state.get("relic_public")) if isinstance(x, Mapping)]
        powers = [dict(x) for x in _as_list(state.get("power_public")) if isinstance(x, Mapping)]
        potions = [dict(x) for x in _as_list(state.get("potion_public")) if isinstance(x, Mapping)]
        health = _health(cards)
        hp = _num(state.get("hp"))
        max_hp = _num(state.get("max_hp"))
        return {
            "schema_version": self.schema_version,
            "context": {
                "character": str(state.get("character", "Unknown")),
                "act": int(state.get("act", 0) or 0),
                "floor": int(state.get("floor", 0) or 0),
                "ascension": int(state.get("ascension", 0) or 0),
                "hp_ratio": hp / max_hp if max_hp > 0 else 0.0,
                "hp": hp,
                "max_hp": max_hp,
                "gold": int(state.get("gold", 0) or 0),
                "potion_count": len(potions),
                "relic_count": len(relics),
                "power_count": len(powers),
                "enemy_count": int((state.get("visible_encounter_profile") or {}).get("enemy_count", 0) or 0),
            },
            "card_tokens": card_tokens,
            "card_counts": dict(sorted(_card_count(cards).items())),
            "relic_tokens": sorted(relics, key=lambda x: str(x.get("semantic_id", x.get("id", "OOV")))),
            "power_tokens": sorted(powers, key=lambda x: str(x.get("semantic_id", x.get("id", "OOV")))),
            "potion_tokens": sorted(potions, key=lambda x: str(x.get("semantic_id", x.get("id", "OOV")))),
            "deck_health": health,
            "visible_map_graph": state.get("visible_map_graph") or {},
            "visible_encounter_profile": state.get("visible_encounter_profile") or {},
        }


def encode_public_state(state: Mapping[str, Any]) -> dict[str, Any]:
    return DeckFeatureEncoder().encode(state)
