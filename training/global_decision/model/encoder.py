"""Deterministic state/candidate encoders for the reward-policy prototype."""

from __future__ import annotations

import hashlib
from typing import Any, Mapping, Sequence

import torch

from ..deck_features import KNOWN_ROLE_TAGS, DeckFeatureEncoder, card_tags


DECK_HEALTH_KEYS = (
    "deck_size", "average_cost", "attack_ratio", "skill_ratio", "power_ratio",
    "frontload_score", "aoe_score", "block_score", "scaling_score", "draw_score",
    "energy_score", "discard_score", "exhaust_score", "generated_score",
    "dead_draw_rate", "status_burden", "first_cycle_time", "later_cycle_time",
    "upgrade_density",
)
CONTEXT_KEYS = (
    "act", "floor", "ascension", "hp_ratio", "hp", "max_hp", "gold",
    "potion_count", "relic_count", "power_count", "enemy_count",
)
ROLE_TAGS = tuple(sorted(KNOWN_ROLE_TAGS))
ACTION_TYPES = ("reward", "reward_skip")


def _bucket(value: str, size: int) -> int:
    return int.from_bytes(hashlib.sha256(value.encode("utf-8")).digest()[:4], "big") % size


def _num(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


class GlobalRewardEncoder:
    """Encode a public state using the existing DeckFeatureEncoder output."""

    def __init__(self, card_hash_buckets: int = 64, relic_hash_buckets: int = 32) -> None:
        self.card_hash_buckets = card_hash_buckets
        self.relic_hash_buckets = relic_hash_buckets
        self._deck_encoder = DeckFeatureEncoder()
        self.state_dim = len(CONTEXT_KEYS) + len(DECK_HEALTH_KEYS) + card_hash_buckets + relic_hash_buckets

    def encode(self, state: Mapping[str, Any]) -> torch.Tensor:
        encoded = self._deck_encoder.encode(state)
        context = encoded["context"]
        health = encoded["deck_health"]
        values = [_num(context.get(key)) for key in CONTEXT_KEYS]
        values.extend(_num(health.get(key)) for key in DECK_HEALTH_KEYS)
        card_bag = [0.0] * self.card_hash_buckets
        for token in encoded["card_tokens"]:
            card_bag[_bucket(str(token["semantic_id"]), self.card_hash_buckets)] += float(token.get("count", 1))
        relic_bag = [0.0] * self.relic_hash_buckets
        for token in encoded["relic_tokens"]:
            relic_bag[_bucket(str(token.get("semantic_id", token.get("id", "OOV"))), self.relic_hash_buckets)] += 1.0
        values.extend(card_bag)
        values.extend(relic_bag)
        return torch.tensor(values, dtype=torch.float32)


class CandidateEncoder:
    """Encode one candidate without relying on its transient list index."""

    def __init__(self, semantic_hash_buckets: int = 64) -> None:
        self.semantic_hash_buckets = semantic_hash_buckets
        self.candidate_dim = semantic_hash_buckets + len(ROLE_TAGS) + len(ACTION_TYPES) + 5

    def encode(self, candidate: Mapping[str, Any]) -> torch.Tensor:
        semantic_id = str(candidate.get("semantic_id") or "OOV")
        vector = [0.0] * self.semantic_hash_buckets
        vector[_bucket(semantic_id, self.semantic_hash_buckets)] = 1.0
        tags = set(card_tags(candidate)) | {str(tag).lower() for tag in candidate.get("tags", [])}
        vector.extend(1.0 if tag in tags else 0.0 for tag in ROLE_TAGS)
        action_type = str(candidate.get("action_type", "reward")).lower()
        vector.extend(1.0 if action_type == item else 0.0 for item in ACTION_TYPES)
        vector.extend(
            [
                _num(candidate.get("cost", candidate.get("card_cost", 0))) / 3.0,
                float(bool(candidate.get("is_skip", False) or candidate.get("candidate_role") == "skip" or action_type == "reward_skip")),
                _num(candidate.get("upgrade_level", candidate.get("upgrade", 0))),
                float(bool(candidate.get("quest", False))),
                float(bool(candidate.get("enchantment_ids"))),
            ]
        )
        return torch.tensor(vector, dtype=torch.float32)

    def encode_many(self, candidates: Sequence[Mapping[str, Any]]) -> torch.Tensor:
        if not candidates:
            return torch.zeros((0, self.candidate_dim), dtype=torch.float32)
        return torch.stack([self.encode(candidate) for candidate in candidates])
