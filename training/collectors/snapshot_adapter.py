"""Loss-aware conversion of CLI teacher/public payloads to CombatSnapshot JSON.

The adapter preserves stable IDs and all observed containers.  Card semantics
are reconstructed from explicit effects when present, otherwise from the
runtime preview's damage/block stats.  Any lossy field is returned as a
warning so the caller can downgrade the label rather than claim Reliable.
"""

from __future__ import annotations

from collections import Counter
import hashlib
import json
from dataclasses import dataclass
from typing import Any


_NOSL_FORBIDDEN_KEYS = {
    "teacher_snapshot",
    "teacher_state",
    "rng_state",
    "rng_streams",
    "rng_state_words",
    "future_draw_order",
    "hidden_discard_order",
    "realized_hidden_outcome",
    "state_hash_teacher",
    "rng_counter",
}
_NOSL_ORDERLESS_CONTAINERS = {
    "deck", "draw_pile", "discard_pile", "exhaust_pile",
    "draw_pile_multiset", "discard_pile_multiset", "exhaust_pile_multiset",
}


def _pile_multiset(public: dict[str, Any], zone: str) -> Counter[tuple[str, bool]] | None:
    values = public.get(f"{zone}_multiset")
    if not isinstance(values, list):
        return None
    result: Counter[tuple[str, bool]] = Counter()
    for value in values:
        if not isinstance(value, dict):
            return None
        model_id = str(value.get("model_id", "")).removeprefix("CARD.")
        count = value.get("count")
        if not model_id or not isinstance(count, int) or isinstance(count, bool) or count < 0:
            return None
        result[(model_id, bool(value.get("upgraded", False)))] += count
    return result


def _public_signature_payload(value: Any, key: str | None = None) -> Any:
    """Canonicalize public JSON while discarding hidden/order-only fields."""
    if key is not None and key.lower() in _NOSL_FORBIDDEN_KEYS:
        return None
    if isinstance(value, dict):
        result = {}
        for child_key in sorted(value):
            if child_key.lower() in _NOSL_FORBIDDEN_KEYS:
                continue
            child = _public_signature_payload(value[child_key], child_key)
            if child is not None:
                result[child_key] = child
        return result
    if isinstance(value, list):
        values = [_public_signature_payload(item) for item in value]
        values = [item for item in values if item is not None]
        if key is not None and key.lower() in _NOSL_ORDERLESS_CONTAINERS:
            return sorted(values, key=lambda item: json.dumps(item, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
        return values
    return value


def _target(card: dict[str, Any]) -> str:
    target = str(card.get("target_type", "Self"))
    return "Enemy" if "Enemy" in target else "AllEnemies" if "All" in target else "Self"


def _effects(card: dict[str, Any]) -> tuple[list[dict[str, Any]], list[str]]:
    if isinstance(card.get("effects"), list):
        return card["effects"], []
    stats = card.get("stats") or {}
    effects: list[dict[str, Any]] = []
    warnings: list[str] = []
    model_id = _model_id(card).upper()
    if model_id == "BARRICADE":
        effects.append({
            "kind": "ApplyStatus", "amount": 1,
            "status_id": "BARRICADE", "duration": -1,
            "target_override": "Self",
        })
    if model_id == "ACROBATICS" and isinstance(stats.get("cards"), (int, float)) and stats["cards"] > 0:
        effects.append({"kind": "Draw", "amount": int(stats["cards"]), "target_override": "Self"})
        # The follow-up discard is a player choice.  Keeping it explicit lets
        # the simulator mark unresolved multi-card selection as Estimated or
        # Uncalculable instead of silently dropping the effect.
        effects.append({"kind": "DiscardCards", "amount": 1, "target_override": "Self"})
    if model_id == "BATTLE_TRANCE" and isinstance(stats.get("cards"), (int, float)) and stats["cards"] > 0:
        effects.append({"kind": "Draw", "amount": int(stats["cards"]), "target_override": "Self"})
        effects.append({
            "kind": "ApplyStatus", "amount": 1, "status_id": "CANNOT_DRAW",
            "duration": 1, "target_override": "Self",
        })
    if isinstance(stats.get("damage"), (int, float)) and stats["damage"]:
        if str(card.get("target_type", "")) == "RandomEnemy":
            repeat = stats.get("repeat", 1)
            effects.append({
                "kind": "RandomEnemyDamage", "amount": stats["damage"],
                "repeat": int(repeat) if isinstance(repeat, (int, float)) else 1,
            })
        else:
            effects.append({"kind": "Damage", "amount": stats["damage"], "target_override": "Enemy"})
    if isinstance(stats.get("block"), (int, float)) and stats["block"]:
        effects.append({"kind": "Block", "amount": stats["block"], "target_override": "Self"})
    # v0.111 power-card previews expose their dynamic values in stats rather
    # than as a generic damage/block field.  Reconstruct only the verified
    # trigger semantics used by the shadow simulator so public-only teacher
    # requests do not become spuriously Uncalculable.
    if isinstance(stats.get("afterimagepower"), (int, float)) and stats["afterimagepower"]:
        effects.append({
            "kind": "ApplyStatus", "amount": stats["afterimagepower"],
            "status_id": "TRIGGER_CARD_PLAYED_BLOCK", "duration": -1,
            "target_override": "Self",
        })
    if isinstance(stats.get("strengthpower"), (int, float)) and stats["strengthpower"]:
        status_id = "TURN_START_STRENGTH" if model_id == "DEMON_FORM" else "STRENGTH"
        effects.append({
            "kind": "ApplyStatus", "amount": stats["strengthpower"],
            "status_id": status_id, "duration": -1, "target_override": "Self",
        })
    if isinstance(stats.get("vulnerablepower"), (int, float)) and stats["vulnerablepower"]:
        effects.append({
            "kind": "ApplyStatus", "amount": stats["vulnerablepower"],
            "status_id": "VULNERABLE", "duration": int(stats["vulnerablepower"]),
            "is_debuff": True, "target_override": "Enemy",
        })
    if isinstance(stats.get("weakpower"), (int, float)) and stats["weakpower"]:
        effects.append({
            "kind": "ApplyStatus", "amount": stats["weakpower"],
            "status_id": "WEAK", "duration": int(stats["weakpower"]),
            "is_debuff": True, "target_override": "Enemy",
        })
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


def _model_id(card: dict[str, Any]) -> str:
    return str(card.get("id", card.get("model_id", "UNKNOWN"))).removeprefix("CARD.")


@dataclass(frozen=True)
class NoslBeliefState:
    """Public-information belief state used by the NOSL teacher.

    The object intentionally contains no concrete draw/discard order and no
    RNG state words.  The remaining card multiset is derived only from the
    public deck composition and the currently visible hand.
    """

    public_state: dict[str, Any]
    known_deck_composition: tuple[tuple[str, int], ...]
    known_hand: tuple[str, ...]
    visible_relics: tuple[dict[str, Any], ...]
    visible_powers: tuple[dict[str, Any], ...]
    remaining_card_multiset: tuple[tuple[str, int], ...]
    publicly_observable_history: dict[str, Any]
    random_effect_distributions: dict[str, Any]
    round_context: dict[str, Any]
    belief_signature: str

    @classmethod
    def from_public_observation(cls, public: dict[str, Any]) -> "NoslBeliefState":
        player = public.get("player") if isinstance(public.get("player"), dict) else {}
        deck = [c for c in player.get("deck", []) or [] if isinstance(c, dict)]
        hand = [c for c in public.get("hand", []) or [] if isinstance(c, dict)]
        deck_counts = Counter(_model_id(card) for card in deck)
        hand_ids = tuple(sorted(str(card.get("instance_id", "")) for card in hand))
        visible_relics = tuple(
            dict(item) for item in (player.get("relics") or public.get("relics") or [])
            if isinstance(item, dict)
        )
        raw_powers = list(player.get("powers") or public.get("player_powers") or public.get("powers") or [])
        for enemy in public.get("enemies", []) or []:
            if isinstance(enemy, dict):
                raw_powers.extend(enemy.get("powers") or [])
        visible_powers = tuple(dict(item) for item in raw_powers if isinstance(item, dict))
        hand_counts = Counter(_model_id(card) for card in hand)
        draw_multiset = _pile_multiset(public, "draw_pile")
        discard_multiset = _pile_multiset(public, "discard_pile")
        if draw_multiset is not None and discard_multiset is not None:
            remaining = Counter()
            for (model_id, _), count in (draw_multiset + discard_multiset).items():
                remaining[model_id] += count
        else:
            remaining = {
                model_id: count - hand_counts.get(model_id, 0)
                for model_id, count in deck_counts.items()
                if count - hand_counts.get(model_id, 0) > 0
            }
        known_deck = tuple(sorted(deck_counts.items()))
        remaining_multiset = tuple(sorted(remaining.items()))
        history = public.get("history_counters") or public.get("history") or {}
        if not isinstance(history, dict):
            history = {}
        context = public.get("context") or {}
        if not isinstance(context, dict):
            context = {}
        round_context = {
            "round": public.get("round", 0),
            "energy": public.get("energy", player.get("energy", 0)),
            "max_energy": public.get("max_energy", player.get("max_energy", 0)),
            "context": context,
        }
        distributions = public.get("random_effect_distributions") or {}
        if not isinstance(distributions, dict):
            distributions = {}
        canonical = {
            "known_deck_composition": known_deck,
            "known_hand": hand_ids,
            "visible_relics": visible_relics,
            "visible_powers": visible_powers,
            "remaining_card_multiset": remaining_multiset,
            "public_history": history,
            "random_effect_distributions": distributions,
            "round_context": round_context,
            "public_observation": _public_signature_payload(public),
            "schema_version": 1,
        }
        signature = hashlib.sha256(
            json.dumps(canonical, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        return cls(
            public_state=public,
            known_deck_composition=known_deck,
            known_hand=hand_ids,
            visible_relics=visible_relics,
            visible_powers=visible_powers,
            remaining_card_multiset=remaining_multiset,
            publicly_observable_history=history,
            random_effect_distributions=distributions,
            round_context=round_context,
            belief_signature=signature,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": 1,
            "known_deck_composition": [list(item) for item in self.known_deck_composition],
            "known_hand": list(self.known_hand),
            "visible_relics": [dict(item) for item in self.visible_relics],
            "visible_powers": [dict(item) for item in self.visible_powers],
            "remaining_card_multiset": [list(item) for item in self.remaining_card_multiset],
            "publicly_observable_history": self.publicly_observable_history,
            "random_effect_distributions": self.random_effect_distributions,
            "round_context": self.round_context,
            "belief_signature": self.belief_signature,
        }


def build_nosl_belief_state(public: dict[str, Any]) -> NoslBeliefState:
    """Build a NOSL belief exclusively from a CLI public observation."""
    if not isinstance(public, dict):
        raise ValueError("public observation must be an object")
    return NoslBeliefState.from_public_observation(public)


def rebuild_nosl_combat_snapshot(public: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    """Create a simulator input with hidden order and RNG state removed.

    This snapshot is intentionally not a replay snapshot.  The belief engine
    supplies chance outcomes separately; an empty ordered pile prevents an
    evaluator from accidentally consuming teacher-only future order.
    """
    belief = build_nosl_belief_state(public)
    player = public.get("player") if isinstance(public.get("player"), dict) else {}
    if not isinstance(player.get("deck"), list):
        warnings: list[str] = ["deck_composition_missing"]
    else:
        warnings = []
    hand = public.get("hand") or []
    hand_cards: list[dict[str, Any]] = []
    for value in hand:
        if not isinstance(value, dict):
            continue
        card, card_warnings = _card(value)
        hand_cards.append(card)
        warnings.extend(card_warnings)
    enemies = []
    for enemy in public.get("enemies", []) or []:
        if not isinstance(enemy, dict):
            continue
        intents = []
        for intent in enemy.get("intents", []) or []:
            if isinstance(intent, dict):
                intent_type = intent.get("type", "Unknown")
                effects: list[dict[str, Any]] = []
                # Shrinker Beetle's visible DebuffStrong intent is a verified
                # three-turn Shrink application; mirror the same semantic
                # mapping used by ShadowDiff without guessing other enemy AI.
                if (str(intent_type).lower() == "debuffstrong" and
                        "shrinker_beetle" in str(enemy.get("instance_id", "")).lower()):
                    effects.append({
                        "kind": "ApplyStatus", "amount": 1,
                        "status_id": "SHRINK", "duration": 3,
                        "is_debuff": True, "source_id": "SHRINKER_BEETLE",
                        "target_override": "Self",
                    })
                intents.append({
                    "type": intent_type,
                    "damage_per_hit": intent.get("damage", 0),
                    "hits": intent.get("hits", 1),
                    "effects": effects,
                })
        enemies.append({
            "id": enemy.get("instance_id", enemy.get("id", "enemy:unknown")),
            "name": enemy.get("name", "Unknown"),
            "hp": enemy.get("hp", 0), "max_hp": enemy.get("max_hp", enemy.get("hp", 0)),
            "block": enemy.get("block", 0), "statuses": enemy.get("statuses") or {}, "intents": intents,
        })
    if not enemies:
        warnings.append("enemy_state_missing")
    # Encode only the remaining card *multiset* as an unordered discard pool.
    # DeterministicSimulator will turn this pool into chance branches when a
    # draw crosses the unknown-shuffle boundary.  Synthetic IDs are stable and
    # contain no information about the real future order.
    hand_model_counts = Counter(_model_id(card) for card in hand if isinstance(card, dict))
    remaining_cards: list[dict[str, Any]] = []
    for value in sorted((card for card in player.get("deck", []) or [] if isinstance(card, dict)),
                        key=lambda card: (_model_id(card), str(card.get("instance_id", "")))):
        model_id = _model_id(value)
        if hand_model_counts.get(model_id, 0) > 0:
            hand_model_counts[model_id] -= 1
            continue
        synthetic = dict(value)
        synthetic["instance_id"] = f"belief:draw:{model_id}:{len(remaining_cards):03d}"
        remaining_cards.append(synthetic)
    unknown_pool: list[dict[str, Any]] = []
    for value in remaining_cards:
        card, card_warnings = _card(value)
        unknown_pool.append(card)
        warnings.extend(card_warnings)

    public_draw_count = public.get("draw_pile_count")
    public_discard_count = public.get("discard_pile_count")
    draw_count = public_draw_count if isinstance(public_draw_count, int) else None
    discard_count = public_discard_count if isinstance(public_discard_count, int) else None
    draw_transport: list[dict[str, Any]] = []
    public_discard: list[dict[str, Any]] = []
    public_exhaust: list[dict[str, Any]] = []
    unordered_draw_pool = False
    explicit_draw = public.get("draw_pile")
    explicit_discard = public.get("discard_pile")
    explicit_exhaust = public.get("exhaust_pile")
    draw_multiset = _pile_multiset(public, "draw_pile")
    discard_multiset = _pile_multiset(public, "discard_pile")
    exhaust_multiset = _pile_multiset(public, "exhaust_pile")

    def expand_multiset(values: Counter[tuple[str, bool]], zone: str) -> list[dict[str, Any]]:
        expanded: list[dict[str, Any]] = []
        templates = {
            (_model_id(card), bool(card.get("upgraded", False))): card
            for card in player.get("deck", []) or []
            if isinstance(card, dict)
        }
        for (model_id, upgraded), count in sorted(values.items()):
            for ordinal in range(count):
                source = dict(templates.get((model_id, upgraded), {
                    "id": model_id, "upgraded": upgraded,
                }))
                source["instance_id"] = f"belief:{zone}:{model_id}:{int(upgraded)}:{ordinal:03d}"
                card, card_warnings = _card(source)
                expanded.append(card)
                warnings.extend(card_warnings)
        return expanded

    if draw_multiset is not None and discard_multiset is not None and exhaust_multiset is not None:
        draw_transport = expand_multiset(draw_multiset, "draw")
        public_discard = expand_multiset(discard_multiset, "discard")
        public_exhaust = expand_multiset(exhaust_multiset, "exhaust")
        if draw_count is not None and draw_count != len(draw_transport):
            warnings.append("uncalculable_draw_pile_count_mismatch")
        if discard_count is not None and discard_count != len(public_discard):
            warnings.append("uncalculable_discard_pile_count_mismatch")
        exhaust_count = public.get("exhaust_pile_count")
        if isinstance(exhaust_count, int) and exhaust_count != len(public_exhaust):
            warnings.append("uncalculable_exhaust_pile_count_mismatch")
        unordered_draw_pool = len(draw_transport) > 0
    elif isinstance(explicit_draw, list) and isinstance(explicit_discard, list) and isinstance(explicit_exhaust, list):
        for value in sorted((item for item in explicit_draw if isinstance(item, dict)),
                            key=lambda card: (_model_id(card), str(card.get("instance_id", "")))):
            card, card_warnings = _card(value)
            draw_transport.append(card)
            warnings.extend(card_warnings)
        for value in sorted((item for item in explicit_discard if isinstance(item, dict)),
                            key=lambda card: (_model_id(card), str(card.get("instance_id", "")))):
            card, card_warnings = _card(value)
            public_discard.append(card)
            warnings.extend(card_warnings)
        for value in sorted((item for item in explicit_exhaust if isinstance(item, dict)),
                            key=lambda card: (_model_id(card), str(card.get("instance_id", "")))):
            card, card_warnings = _card(value)
            public_exhaust.append(card)
            warnings.extend(card_warnings)
        if draw_count is not None and draw_count != len(draw_transport):
            warnings.append("uncalculable_draw_pile_count_mismatch")
        if discard_count is not None and discard_count != len(public_discard):
            warnings.append("uncalculable_discard_pile_count_mismatch")
        unordered_draw_pool = len(draw_transport) > 0
    elif draw_count is None or discard_count is None:
        warnings.append("uncalculable_pile_counts_missing")
    elif draw_count + discard_count != len(unknown_pool):
        # Master-deck minus hand can still contain played Powers, exhausted
        # cards, or removed cards. Inventing a draw/discard assignment would
        # create illegal actions, so legacy rows without zone identities are
        # explicitly rejected for Reliable labels.
        warnings.append("uncalculable_pile_partition_missing")
    elif discard_count == 0:
        draw_transport = unknown_pool
        unordered_draw_pool = len(draw_transport) > 0
    elif draw_count == 0:
        public_discard = unknown_pool
    else:
        warnings.append("uncalculable_pile_partition_missing")
    snapshot = {
        "fingerprint": public.get("state_hash_public", public.get("fingerprint", "nosl-belief")),
        "belief_signature": belief.belief_signature,
        "player": {
            "hp": player.get("hp", 0), "max_hp": player.get("max_hp", player.get("hp", 0)),
            "block": player.get("block", 0), "energy": public.get("energy", player.get("energy", 0)),
            "max_energy": public.get("max_energy", player.get("max_energy", player.get("energy", 0))),
            "statuses": player.get("statuses") or {},
        },
        "enemies": enemies,
        "hand": hand_cards,
        # The marker tells MutableCombatState that this canonical array is an
        # unordered transport, not a future-order observation.
        "draw_pile": draw_transport,
        "discard_pile": public_discard,
        "exhaust_pile": public_exhaust,
        "potions": player.get("potions") or [],
        "rng_state": 0,
        "rng_streams": None,
        "round": public.get("round", 0), "is_boss": False,
        # Hidden order is represented by the public NOSL belief and masked RNG
        # availability. It is not a simulator restriction: adding it here
        # would downgrade every otherwise deterministic line to Estimated.
        "global_restrictions": ["nosl_unordered_draw_pool"] if unordered_draw_pool else [],
        "orbs": [], "orb_capacity": int(public.get("orb_slots", player.get("orb_slots", 0)) or 0),
        "relics": player.get("relics") or [], "powers": player.get("powers") or [],
        "view": "Public",
    }
    return snapshot, sorted(set(warnings))


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
