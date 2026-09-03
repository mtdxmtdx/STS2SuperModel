"""Deterministic public-only combat feature encoder."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable


STATE_SCALAR_NAMES = (
    "hp_ratio", "block_per_max_hp", "energy_ratio", "energy", "max_energy",
    "round", "act", "floor", "ascension", "hand_count", "draw_count",
    "discard_count", "exhaust_count", "enemy_count", "total_enemy_hp_ratio",
    "incoming_damage_per_max_hp",
)
CANDIDATE_SCALAR_NAMES = (
    "effective_energy_cost", "is_play_card", "is_use_potion", "is_end_turn",
    "has_target", "preview_damage", "preview_block", "legal",
)
ENEMY_SCALAR_NAMES = (
    "hp_ratio", "block_per_max_hp", "intends_attack", "is_hittable",
    "is_minion", "is_primary", "intent_damage_per_max_hp", "intent_count",
)


@dataclass(frozen=True)
class TokenVocabulary:
    tokens: tuple[str, ...]

    @classmethod
    def build(cls, rows: Iterable[dict[str, Any]]) -> "TokenVocabulary":
        values = {"<PAD>", "<UNK>"}
        for row in rows:
            values.update(iter_tokens(row))
        return cls(("<PAD>", "<UNK>", *sorted(values - {"<PAD>", "<UNK>"})))

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "TokenVocabulary":
        return cls(tuple(str(item) for item in value["tokens"]))

    def to_dict(self) -> dict[str, Any]:
        return {"tokens": list(self.tokens)}

    @property
    def ids(self) -> dict[str, int]:
        return {token: index for index, token in enumerate(self.tokens)}


def _id(value: Any) -> str:
    return str(value or "UNKNOWN").upper()


def _card_token(model_id: Any, upgraded: bool = False) -> str:
    return f"card:{_id(model_id)}:{1 if upgraded else 0}"


def _intent_damage(enemy: dict[str, Any]) -> float:
    total = 0.0
    for intent in enemy.get("intents") or []:
        damage = float(intent.get("damage") or 0)
        hits = float(intent.get("hits") or intent.get("times") or 1)
        total += damage * hits
    return total


def iter_tokens(row: dict[str, Any]) -> Iterable[str]:
    state = row.get("public_state") or {}
    yield f"character:{_id(row.get('character'))}"
    yield f"room:{_id((state.get('context') or {}).get('room_type'))}"
    for card in state.get("hand") or []:
        yield _card_token(card.get("id"), bool(card.get("upgraded")))
    for zone in ("draw", "discard", "exhaust"):
        for card in state.get(f"{zone}_pile_multiset") or []:
            yield _card_token(card.get("model_id"), bool(card.get("upgraded")))
    for relic in (state.get("player") or {}).get("relics") or []:
        yield f"relic:{_id(relic.get('id'))}"
    for power in state.get("player_powers") or []:
        yield f"power:{_id(power.get('id'))}"
    for enemy in state.get("enemies") or []:
        yield f"enemy:{_id(enemy.get('name') or enemy.get('instance_id'))}"
        for intent in enemy.get("intents") or []:
            yield f"intent:{_id(intent.get('type'))}"
        for power in enemy.get("powers") or []:
            yield f"power:{_id(power.get('id'))}"
    for action in row.get("legal_actions") or []:
        yield f"action:{_id(action.get('kind'))}"
        if action.get("source_model_id"):
            yield _card_token(action.get("source_model_id"))
        if action.get("target_id"):
            yield f"target:{_id(str(action['target_id']).rsplit(':', 1)[0])}"


class CombatFeatureEncoder:
    """Produces order-invariant set inputs and candidate-aligned action inputs."""

    def __init__(self, vocabulary: TokenVocabulary) -> None:
        self.vocabulary = vocabulary
        self._ids = vocabulary.ids

    def token_id(self, token: str) -> int:
        return self._ids.get(token, 1)

    def encode(self, row: dict[str, Any]) -> dict[str, Any]:
        state = row.get("public_state") or {}
        player = state.get("player") or {}
        enemies = sorted(state.get("enemies") or [], key=lambda item: str(item.get("instance_id") or item.get("name")))
        max_hp = max(float(player.get("max_hp") or 1), 1.0)
        max_energy = max(float(state.get("max_energy") or 1), 1.0)
        incoming = sum(_intent_damage(enemy) for enemy in enemies)
        total_enemy_ratio = sum(float(enemy.get("hp") or 0) / max(float(enemy.get("max_hp") or 1), 1.0) for enemy in enemies)
        state_numeric = [
            float(player.get("hp") or 0) / max_hp,
            float(player.get("block") or 0) / max_hp,
            float(state.get("energy") or 0) / max_energy,
            float(state.get("energy") or 0) / 10.0,
            float(state.get("max_energy") or 0) / 10.0,
            float(state.get("round") or row.get("round") or 0) / 20.0,
            float(row.get("act") or 0) / 3.0,
            float(row.get("floor") or 0) / 60.0,
            float(row.get("ascension") or 0) / 20.0,
            len(state.get("hand") or []) / 10.0,
            float(state.get("draw_pile_count") or 0) / 50.0,
            float(state.get("discard_pile_count") or 0) / 50.0,
            float(state.get("exhaust_pile_count") or 0) / 50.0,
            len(enemies) / 6.0,
            total_enemy_ratio / max(len(enemies), 1),
            incoming / max_hp,
        ]
        weighted_tokens: list[tuple[str, float]] = [
            (f"character:{_id(row.get('character'))}", 1.0),
            (f"room:{_id((state.get('context') or {}).get('room_type'))}", 1.0),
        ]
        for card in state.get("hand") or []:
            weighted_tokens.append((_card_token(card.get("id"), bool(card.get("upgraded"))), 1.0))
        for zone in ("draw", "discard", "exhaust"):
            for card in state.get(f"{zone}_pile_multiset") or []:
                weighted_tokens.append((_card_token(card.get("model_id"), bool(card.get("upgraded"))), float(card.get("count") or 0)))
        for relic in player.get("relics") or []:
            weighted_tokens.append((f"relic:{_id(relic.get('id'))}", 1.0))
        for power in state.get("player_powers") or []:
            weighted_tokens.append((f"power:{_id(power.get('id'))}", max(abs(float(power.get("amount") or 1)), 1.0)))
        weighted_tokens.sort()

        enemy_ids: list[int] = []
        enemy_numeric: list[list[float]] = []
        for enemy in enemies:
            enemy_max_hp = max(float(enemy.get("max_hp") or 1), 1.0)
            enemy_ids.append(self.token_id(f"enemy:{_id(enemy.get('name') or enemy.get('instance_id'))}"))
            enemy_numeric.append([
                float(enemy.get("hp") or 0) / enemy_max_hp,
                float(enemy.get("block") or 0) / enemy_max_hp,
                float(bool(enemy.get("intends_attack"))),
                float(bool(enemy.get("is_hittable", True))),
                float(bool(enemy.get("is_minion"))),
                float(bool(enemy.get("is_primary_enemy"))),
                _intent_damage(enemy) / enemy_max_hp,
                len(enemy.get("intents") or []) / 4.0,
            ])

        hand_by_instance = {str(card.get("instance_id")): card for card in state.get("hand") or []}
        candidate_ids: list[list[int]] = []
        candidate_numeric: list[list[float]] = []
        action_ids: list[str] = []
        for action in row.get("legal_actions") or []:
            kind = str(action.get("kind") or "Unknown")
            source_model = action.get("source_model_id")
            target_id = action.get("target_id")
            source_card = hand_by_instance.get(str(action.get("source_instance_id")), {})
            stats = source_card.get("stats") or {}
            candidate_ids.append([
                self.token_id(f"action:{_id(kind)}"),
                self.token_id(_card_token(source_model)) if source_model else 0,
                self.token_id(f"target:{_id(str(target_id).rsplit(':', 1)[0])}") if target_id else 0,
            ])
            candidate_numeric.append([
                float(action.get("effective_energy_cost") or 0) / 10.0,
                float(kind == "PlayCard"),
                float(kind == "UsePotion"),
                float(kind == "EndTurn"),
                float(bool(target_id)),
                float(stats.get("damage") or 0) / 100.0,
                float(stats.get("block") or 0) / 100.0,
                float(bool(action.get("legal", True))),
            ])
            action_ids.append(str(action.get("action_id")))
        return {
            "state_numeric": state_numeric,
            "state_token_ids": [self.token_id(token) for token, _ in weighted_tokens],
            "state_token_weights": [weight for _, weight in weighted_tokens],
            "enemy_token_ids": enemy_ids,
            "enemy_numeric": enemy_numeric,
            "candidate_token_ids": candidate_ids,
            "candidate_numeric": candidate_numeric,
            "action_ids": action_ids,
        }
