"""Dataset helpers for heuristic-labelled global reward records."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any, Iterable, Mapping

import torch
from torch.utils.data import Dataset

from .encoder import CandidateEncoder, GlobalRewardEncoder


def split_name(scenario_id: str) -> str:
    bucket = int.from_bytes(hashlib.sha256(scenario_id.encode("utf-8")).digest()[:4], "big") % 100
    if bucket < 80:
        return "train"
    if bucket < 90:
        return "validation"
    return "test"


def candidate_with_offer(row: Mapping[str, Any], index: int) -> dict[str, Any]:
    candidates = row["offer_snapshot"]["candidates"]
    candidate = dict(candidates[index])
    offers = row["state_public"].get("visible_offers", [])
    if index < len(offers):
        offer = offers[index]
        if isinstance(offer, Mapping):
            merged = dict(offer)
            merged.update(candidate)
            candidate = merged
    candidate["is_skip"] = candidate.get("candidate_role") == "skip" or candidate.get("action_type") == "reward_skip"
    return candidate


def load_rows(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            row = json.loads(line)
            if row.get("label_source") != "EstimatedByHeuristic" or row.get("quality") != "EstimatedByHeuristic":
                raise ValueError("prototype model accepts EstimatedByHeuristic rows only")
            rows.append(row)
    return rows


class GlobalRewardDataset(Dataset[dict[str, Any]]):
    def __init__(self, rows: Iterable[Mapping[str, Any]], state_encoder: GlobalRewardEncoder | None = None, candidate_encoder: CandidateEncoder | None = None) -> None:
        self.rows = list(rows)
        self.state_encoder = state_encoder or GlobalRewardEncoder()
        self.candidate_encoder = candidate_encoder or CandidateEncoder()
        self.samples: list[dict[str, Any]] = []
        for row in self.rows:
            candidates = [candidate_with_offer(row, i) for i in range(len(row["offer_snapshot"]["candidates"]))]
            labels = row["labels"]
            target_scores = [float(labels[candidate["action_id"]]["score"]) for candidate in candidates]
            legal_mask = [bool(candidate.get("legal", True)) for candidate in candidates]
            self.samples.append(
                {
                    "scenario_id": row["scenario_id"],
                    "state": self.state_encoder.encode(row["state_public"]),
                    "candidates": self.candidate_encoder.encode_many(candidates),
                    "target_scores": torch.tensor(target_scores, dtype=torch.float32),
                    "legal_mask": torch.tensor(legal_mask, dtype=torch.bool),
                    "candidate_ids": [candidate["action_id"] for candidate in candidates],
                }
            )

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, index: int) -> dict[str, Any]:
        return self.samples[index]


def collate_samples(samples: list[dict[str, Any]]) -> dict[str, Any]:
    if not samples:
        raise ValueError("empty batch")
    max_candidates = max(sample["candidates"].shape[0] for sample in samples)
    state = torch.stack([sample["state"] for sample in samples])
    candidate_dim = samples[0]["candidates"].shape[1]
    candidates = torch.zeros((len(samples), max_candidates, candidate_dim), dtype=torch.float32)
    targets = torch.zeros((len(samples), max_candidates), dtype=torch.float32)
    masks = torch.zeros((len(samples), max_candidates), dtype=torch.bool)
    scenario_ids: list[str] = []
    candidate_ids: list[list[str]] = []
    for row_index, sample in enumerate(samples):
        count = sample["candidates"].shape[0]
        candidates[row_index, :count] = sample["candidates"]
        targets[row_index, :count] = sample["target_scores"]
        masks[row_index, :count] = sample["legal_mask"]
        scenario_ids.append(sample["scenario_id"])
        candidate_ids.append(sample["candidate_ids"])
    return {"state": state, "candidates": candidates, "target_scores": targets, "legal_mask": masks, "scenario_ids": scenario_ids, "candidate_ids": candidate_ids}
