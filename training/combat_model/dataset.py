"""Dataset and padding for the NOSL combat model."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import torch
from torch.utils.data import Dataset

from .encoder import CombatFeatureEncoder


OBJECTIVES = ("Balanced", "HighestDamage", "MinimumLoss")


def load_rows(path: Path, *, reliable_only: bool = True) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if not line.strip():
                continue
            row = json.loads(line)
            if not reliable_only or row.get("confidence") == "Reliable":
                rows.append(row)
    return rows


class CombatDataset(Dataset[dict[str, Any]]):
    def __init__(self, rows: list[dict[str, Any]], encoder: CombatFeatureEncoder) -> None:
        self.rows = rows
        self.encoder = encoder

    def __len__(self) -> int:
        return len(self.rows)

    def __getitem__(self, index: int) -> dict[str, Any]:
        row = self.rows[index]
        encoded = self.encoder.encode(row)
        action_ids = encoded["action_ids"]
        best = set(row.get("teacher_best_actions") or [])
        policy = [float(action_id in best) for action_id in action_ids]
        total = sum(policy)
        if total:
            policy = [value / total for value in policy]
        objective_values: list[list[float]] = []
        objective_mask: list[list[bool]] = []
        objectives = row.get("objectives") or {}
        for action_id in action_ids:
            values: list[float] = []
            masks: list[bool] = []
            for name in OBJECTIVES:
                value = ((objectives.get(name) or {}).get("action_values") or {}).get(action_id)
                masks.append(value is not None)
                values.append(float(value or 0.0))
            objective_values.append(values)
            objective_mask.append(masks)
        encoded.update({
            "policy_target": policy,
            "objective_values": objective_values,
            "objective_mask": objective_mask,
            "death_target": float(row.get("death_probability") or 0.0),
            "state_hash_public": str(row.get("state_hash_public")),
            "character": str(row.get("character") or "Unknown"),
        })
        return encoded


def collate_samples(samples: list[dict[str, Any]]) -> dict[str, Any]:
    batch = len(samples)
    max_state_tokens = max(len(item["state_token_ids"]) for item in samples)
    max_enemies = max(max(len(item["enemy_token_ids"]), 1) for item in samples)
    max_actions = max(len(item["action_ids"]) for item in samples)
    state_numeric = torch.tensor([item["state_numeric"] for item in samples], dtype=torch.float32)
    state_ids = torch.zeros((batch, max_state_tokens), dtype=torch.long)
    state_weights = torch.zeros((batch, max_state_tokens), dtype=torch.float32)
    enemy_ids = torch.zeros((batch, max_enemies), dtype=torch.long)
    enemy_numeric = torch.zeros((batch, max_enemies, 8), dtype=torch.float32)
    enemy_mask = torch.zeros((batch, max_enemies), dtype=torch.bool)
    candidate_ids = torch.zeros((batch, max_actions, 3), dtype=torch.long)
    candidate_numeric = torch.zeros((batch, max_actions, 8), dtype=torch.float32)
    legal_mask = torch.zeros((batch, max_actions), dtype=torch.bool)
    policy_target = torch.zeros((batch, max_actions), dtype=torch.float32)
    objective_values = torch.zeros((batch, max_actions, 3), dtype=torch.float32)
    objective_mask = torch.zeros((batch, max_actions, 3), dtype=torch.bool)
    death_target = torch.tensor([item["death_target"] for item in samples], dtype=torch.float32)
    for row_index, item in enumerate(samples):
        st = len(item["state_token_ids"])
        state_ids[row_index, :st] = torch.tensor(item["state_token_ids"], dtype=torch.long)
        state_weights[row_index, :st] = torch.tensor(item["state_token_weights"], dtype=torch.float32)
        en = len(item["enemy_token_ids"])
        if en:
            enemy_ids[row_index, :en] = torch.tensor(item["enemy_token_ids"], dtype=torch.long)
            enemy_numeric[row_index, :en] = torch.tensor(item["enemy_numeric"], dtype=torch.float32)
            enemy_mask[row_index, :en] = True
        ac = len(item["action_ids"])
        candidate_ids[row_index, :ac] = torch.tensor(item["candidate_token_ids"], dtype=torch.long)
        candidate_numeric[row_index, :ac] = torch.tensor(item["candidate_numeric"], dtype=torch.float32)
        legal_mask[row_index, :ac] = True
        policy_target[row_index, :ac] = torch.tensor(item["policy_target"], dtype=torch.float32)
        objective_values[row_index, :ac] = torch.tensor(item["objective_values"], dtype=torch.float32)
        objective_mask[row_index, :ac] = torch.tensor(item["objective_mask"], dtype=torch.bool)
    return {
        "state_numeric": state_numeric,
        "state_token_ids": state_ids,
        "state_token_weights": state_weights,
        "enemy_token_ids": enemy_ids,
        "enemy_numeric": enemy_numeric,
        "enemy_mask": enemy_mask,
        "candidate_token_ids": candidate_ids,
        "candidate_numeric": candidate_numeric,
        "legal_mask": legal_mask,
        "policy_target": policy_target,
        "objective_values": objective_values,
        "objective_mask": objective_mask,
        "death_target": death_target,
        "action_ids": [item["action_ids"] for item in samples],
        "state_hash_public": [item["state_hash_public"] for item in samples],
        "character": [item["character"] for item in samples],
    }
