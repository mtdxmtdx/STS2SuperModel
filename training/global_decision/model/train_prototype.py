"""Train the heuristic-labelled global reward policy prototype."""

from __future__ import annotations

import argparse
import hashlib
import json
import random
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import torch
from torch import nn
from torch.utils.data import DataLoader

from .candidate_scorer import GlobalRewardCandidateScorer
from .dataset import GlobalRewardDataset, collate_samples, load_rows, split_name
from .encoder import CandidateEncoder, GlobalRewardEncoder


def _masked_listwise_loss(scores: torch.Tensor, targets: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
    temperature = 0.25
    safe_targets = targets.masked_fill(~mask, torch.finfo(targets.dtype).min)
    teacher = torch.softmax(safe_targets / temperature, dim=-1)
    log_probs = torch.log_softmax(scores.masked_fill(~mask, torch.finfo(scores.dtype).min), dim=-1)
    return -(teacher * log_probs).sum(dim=-1).mean()


def _masked_mse(scores: torch.Tensor, targets: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
    return ((scores - targets).pow(2) * mask.to(scores.dtype)).sum() / mask.sum().clamp_min(1)


def train_model(dataset: GlobalRewardDataset, *, epochs: int = 12, batch_size: int = 64,
                hidden_dim: int = 128, lr: float = 1e-3, seed: int = 20260831) -> tuple[GlobalRewardCandidateScorer, list[dict[str, float]]]:
    random.seed(seed)
    torch.manual_seed(seed)
    model = GlobalRewardCandidateScorer(dataset.state_encoder.state_dim, dataset.candidate_encoder.candidate_dim, hidden_dim)
    optimizer = torch.optim.AdamW(model.parameters(), lr=lr)
    loader = DataLoader(dataset, batch_size=batch_size, shuffle=False, collate_fn=collate_samples)
    history: list[dict[str, float]] = []
    model.train()
    for epoch in range(epochs):
        total = 0.0
        count = 0
        for batch in loader:
            optimizer.zero_grad(set_to_none=True)
            out = model(batch["state"], batch["candidates"], batch["legal_mask"])
            loss = _masked_listwise_loss(out["scores"], batch["target_scores"], batch["legal_mask"]) + 0.25 * _masked_mse(out["scores"], batch["target_scores"], batch["legal_mask"])
            loss.backward()
            optimizer.step()
            total += float(loss.detach()) * len(batch["scenario_ids"])
            count += len(batch["scenario_ids"])
        history.append({"epoch": float(epoch + 1), "loss": total / max(count, 1)})
    return model, history


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _write_manifest(path: Path, *, data_path: Path, rows: list[dict[str, Any]], train_rows: int, history: list[dict[str, float]], model: nn.Module, seed: int, hidden_dim: int) -> None:
    counts = {name: sum(split_name(str(row["scenario_id"])) == name for row in rows) for name in ("train", "validation", "test")}
    manifest = {
        "model_id": "global-reward-policy-prototype-v0",
        "model_stage": "prototype",
        "stage": "prototype",
        "label_source": "EstimatedByHeuristic",
        "reliable": False,
        "reliable_count": 0,
        "data_path": str(data_path),
        "data_sha256": _sha256(data_path),
        "row_count": len(rows),
        "training_row_count": train_rows,
        "split_counts": counts,
        "parameter_count": sum(p.numel() for p in model.parameters()),
        "hidden_dim": hidden_dim,
        "torch_version": torch.__version__,
        "device": "cpu",
        "seed": seed,
        "epochs": len(history),
        "initial_loss": history[0]["loss"],
        "final_loss": history[-1]["loss"],
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "forbidden_inputs": ["seed", "rng_state", "future_draw_order", "teacher_snapshot"],
    }
    path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path("data/global_prototype/global-synthetic-act1-v0.jsonl"))
    parser.add_argument("--out-dir", type=Path, default=Path("data/global_prototype/model_smoke"))
    parser.add_argument("--epochs", type=int, default=12)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--hidden-dim", type=int, default=128)
    parser.add_argument("--seed", type=int, default=20260831)
    args = parser.parse_args(list(argv) if argv is not None else None)
    rows = load_rows(args.data)
    enc = GlobalRewardEncoder()
    cand = CandidateEncoder()
    train_rows = [row for row in rows if split_name(str(row["scenario_id"])) == "train"]
    dataset = GlobalRewardDataset(train_rows, enc, cand)
    model, history = train_model(dataset, epochs=args.epochs, batch_size=args.batch_size, hidden_dim=args.hidden_dim, seed=args.seed)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    checkpoint = args.out_dir / "global-reward-prototype.pt"
    torch.save({"state_dict": model.state_dict(), "state_dim": enc.state_dim, "candidate_dim": cand.candidate_dim, "hidden_dim": args.hidden_dim, "seed": args.seed}, checkpoint)
    (args.out_dir / "training-history.json").write_text(json.dumps(history, indent=2) + "\n", encoding="utf-8")
    _write_manifest(args.out_dir / "global-reward-prototype-manifest.json", data_path=args.data, rows=rows, train_rows=len(train_rows), history=history, model=model, seed=args.seed, hidden_dim=args.hidden_dim)
    print(json.dumps({"rows": len(rows), "training_rows": len(train_rows), "split_counts": {s: sum(split_name(str(r["scenario_id"])) == s for r in rows) for s in ("train", "validation", "test")}, "final_loss": history[-1]["loss"], "checkpoint": str(checkpoint)}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
