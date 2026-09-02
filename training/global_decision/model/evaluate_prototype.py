"""Evaluate ranking, legal masking, and repeatability of the reward prototype."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any, Iterable

import torch

from .candidate_scorer import GlobalRewardCandidateScorer
from .dataset import GlobalRewardDataset, load_rows, split_name
from .encoder import CandidateEncoder, GlobalRewardEncoder


def evaluate_model(rows: list[dict[str, Any]], model: GlobalRewardCandidateScorer, state_encoder: GlobalRewardEncoder, candidate_encoder: CandidateEncoder, split: str = "all") -> dict[str, Any]:
    selected = [row for row in rows if split == "all" or split_name(str(row["scenario_id"])) == split]
    dataset = GlobalRewardDataset(selected, state_encoder, candidate_encoder)
    top1 = 0
    pair_total = 0
    pair_correct = 0
    mask_violations = 0
    skip_present = 0
    skip_selected = 0
    confidence: list[float] = []
    digest = hashlib.sha256()
    model.eval()
    with torch.no_grad():
        for sample in dataset.samples:
            out = model.rank(sample["state"], sample["candidates"], sample["legal_mask"])
            scores = out["scores"][0]
            legal = sample["legal_mask"]
            pred = int(out["selected_index"][0])
            teacher = int(torch.argmax(sample["target_scores"].masked_fill(~legal, torch.finfo(torch.float32).min)))
            top1 += int(pred == teacher)
            legal_indices = [i for i, value in enumerate(legal.tolist()) if value]
            mask_violations += int(pred not in legal_indices)
            if any(sample["candidate_ids"][i].endswith(":skip") or "skip" in sample["candidate_ids"][i].lower() for i in range(len(legal))):
                skip_present += 1
                skip_selected += int(sample["candidate_ids"][pred].endswith(":skip") or "skip" in sample["candidate_ids"][pred].lower())
            for i in legal_indices:
                for j in legal_indices:
                    if sample["target_scores"][i] == sample["target_scores"][j]:
                        continue
                    pair_total += 1
                    pair_correct += int((scores[i] > scores[j]) == (sample["target_scores"][i] > sample["target_scores"][j]))
            confidence.extend(float(x) for x in out["confidence"][0][legal].tolist())
            digest.update(sample["scenario_id"].encode("utf-8"))
            digest.update(bytes([pred]))
    n = len(dataset)
    return {
        "split": split,
        "rows": n,
        "top1_accuracy": top1 / max(n, 1),
        "pairwise_accuracy": pair_correct / max(pair_total, 1),
        "mask_violations": mask_violations,
        "skip_present_rows": skip_present,
        "skip_selected_rows": skip_selected,
        "mean_confidence": sum(confidence) / max(len(confidence), 1),
        "prediction_digest": digest.hexdigest(),
        "label_source": "EstimatedByHeuristic",
        "reliable": False,
    }


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path("data/global_prototype/global-synthetic-act1-v0.jsonl"))
    parser.add_argument("--checkpoint", type=Path, default=Path("data/global_prototype/model_smoke/global-reward-prototype.pt"))
    parser.add_argument("--split", choices=("all", "train", "validation", "test"), default="all")
    args = parser.parse_args(list(argv) if argv is not None else None)
    rows = load_rows(args.data)
    state_encoder, candidate_encoder = GlobalRewardEncoder(), CandidateEncoder()
    payload = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = GlobalRewardCandidateScorer(payload["state_dim"], payload["candidate_dim"], payload["hidden_dim"])
    model.load_state_dict(payload["state_dict"])
    result = evaluate_model(rows, model, state_encoder, candidate_encoder, args.split)
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
