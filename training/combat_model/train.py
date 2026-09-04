"""Train the deterministic NOSL combat policy/value/risk network."""

from __future__ import annotations

import argparse
import hashlib
import json
import random
from collections import Counter
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader

from .dataset import CombatDataset, collate_samples, load_rows
from .encoder import CombatFeatureEncoder, TokenVocabulary
from .model import CombatPolicyValueModel


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def move(batch: dict[str, Any], device: torch.device) -> dict[str, Any]:
    return {key: value.to(device) if isinstance(value, torch.Tensor) else value for key, value in batch.items()}


def losses(outputs: tuple[torch.Tensor, torch.Tensor, torch.Tensor], batch: dict[str, Any]) -> tuple[torch.Tensor, dict[str, float]]:
    logits, values, risk_logits = outputs
    policy = -(batch["policy_target"] * torch.log_softmax(logits, dim=-1)).sum(dim=-1).mean()
    value_raw = F.smooth_l1_loss(values, batch["objective_values"], reduction="none")
    value_mask = batch["objective_mask"].to(value_raw.dtype)
    value = (value_raw * value_mask).sum() / value_mask.sum().clamp_min(1.0)
    legal = batch["legal_mask"].to(risk_logits.dtype)
    state_risk = (risk_logits * legal).sum(dim=-1) / legal.sum(dim=-1).clamp_min(1.0)
    risk = F.binary_cross_entropy_with_logits(state_risk, batch["death_target"])
    total = policy + 0.2 * value + 0.1 * risk
    return total, {"policy_loss": float(policy.detach()), "value_loss": float(value.detach()), "risk_loss": float(risk.detach())}


@torch.no_grad()
def evaluate(model: CombatPolicyValueModel, loader: DataLoader, device: torch.device) -> dict[str, Any]:
    model.eval()
    rows = correct = 0
    ndcg_sum = regret_sum = 0.0
    regret_count = 0
    policy_loss_sum = 0.0
    total_loss = 0.0
    character_rows: Counter[str] = Counter()
    character_correct: Counter[str] = Counter()
    for raw in loader:
        batch = move(raw, device)
        outputs = model(
            batch["state_numeric"], batch["state_token_ids"], batch["state_token_weights"],
            batch["enemy_token_ids"], batch["enemy_numeric"], batch["enemy_mask"],
            batch["candidate_token_ids"], batch["candidate_numeric"], batch["legal_mask"],
        )
        loss, _ = losses(outputs, batch)
        logits = outputs[0]
        selected = logits.argmax(dim=-1)
        policy_loss_sum += float((-(batch["policy_target"] * torch.log_softmax(logits, dim=-1)).sum(dim=-1)).sum())
        total_loss += float(loss) * logits.shape[0]
        for index in range(logits.shape[0]):
            valid_count = int(batch["legal_mask"][index].sum())
            chosen = int(selected[index])
            target = batch["policy_target"][index, :valid_count]
            if float(target[chosen]) > 0:
                correct += 1
                character_correct[raw["character"][index]] += 1
            character_rows[raw["character"][index]] += 1
            ranking = torch.argsort(logits[index, :valid_count], descending=True)[:3]
            gains = (target[ranking] > 0).to(torch.float32)
            discounts = 1.0 / torch.log2(torch.arange(2, 2 + len(ranking), device=device, dtype=torch.float32))
            dcg = float((gains * discounts).sum())
            ideal_count = min(int((target > 0).sum()), 3)
            idcg = float(discounts[:ideal_count].sum()) if ideal_count else 1.0
            ndcg_sum += dcg / idcg
            known = batch["objective_mask"][index, :valid_count, 0]
            if bool(known[chosen]) and bool(known.any()):
                balanced = batch["objective_values"][index, :valid_count, 0]
                regret_sum += max(0.0, float(balanced[known].max() - balanced[chosen]))
                regret_count += 1
            rows += 1
    return {
        "loss": total_loss / max(rows, 1),
        "policy_loss": policy_loss_sum / max(rows, 1),
        "top1": correct / max(rows, 1),
        "ndcg_at_3": ndcg_sum / max(rows, 1),
        "regret": regret_sum / max(regret_count, 1),
        "regret_fixed": regret_sum / max(rows, 1),
        "regret_coverage": regret_count / max(rows, 1),
        "rows": float(rows),
        "top1_by_character": {
            character: character_correct[character] / count
            for character, count in sorted(character_rows.items())
        },
    }


def character_distribution(rows: list[dict[str, Any]]) -> dict[str, dict[str, float | int]]:
    counts = Counter(str(row.get("character") or "Unknown") for row in rows)
    total = sum(counts.values())
    return {
        character: {"rows": count, "ratio": count / max(total, 1)}
        for character, count in sorted(counts.items())
    }


def make_loader(rows: list[dict[str, Any]], encoder: CombatFeatureEncoder, batch_size: int, *, shuffle: bool, seed: int) -> DataLoader:
    generator = torch.Generator().manual_seed(seed)
    return DataLoader(CombatDataset(rows, encoder), batch_size=batch_size, shuffle=shuffle,
                      generator=generator if shuffle else None, collate_fn=collate_samples, num_workers=0)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--split-dir", required=True, type=Path)
    parser.add_argument("--feature-manifest", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--patience", type=int, default=12)
    parser.add_argument("--batch-size", type=int, default=128)
    parser.add_argument("--embedding-dim", type=int, default=64)
    parser.add_argument("--hidden-dim", type=int, default=128)
    parser.add_argument("--learning-rate", type=float, default=1e-3)
    parser.add_argument("--seed", type=int, default=20260903)
    parser.add_argument("--threads", type=int, default=16)
    parser.add_argument("--model-id", default="combat-nosl-policy-value-v1")
    parser.add_argument("--model-stage", default="pilot")
    parser.add_argument("--character-balance-note", default="Distribution is measured from all labelled source rows.")
    args = parser.parse_args()
    random.seed(args.seed)
    torch.manual_seed(args.seed)
    torch.set_num_threads(args.threads)
    torch.use_deterministic_algorithms(True)
    device = torch.device("cpu")
    train_rows = load_rows(args.split_dir / "train.jsonl")
    validation_rows = load_rows(args.split_dir / "validation.jsonl")
    test_rows = load_rows(args.split_dir / "test.jsonl")
    challenge_rows = load_rows(args.split_dir / "challenge.jsonl")
    all_source_rows = []
    for split in ("train", "validation", "test", "challenge"):
        all_source_rows.extend(load_rows(args.split_dir / f"{split}.jsonl", reliable_only=False))
    vocabulary = TokenVocabulary.build(train_rows)
    encoder = CombatFeatureEncoder(vocabulary)
    train_loader = make_loader(train_rows, encoder, args.batch_size, shuffle=True, seed=args.seed)
    validation_loader = make_loader(validation_rows, encoder, args.batch_size, shuffle=False, seed=args.seed)
    test_loader = make_loader(test_rows, encoder, args.batch_size, shuffle=False, seed=args.seed)
    challenge_loader = make_loader(challenge_rows, encoder, args.batch_size, shuffle=False, seed=args.seed)
    model = CombatPolicyValueModel(len(vocabulary.tokens), args.embedding_dim, args.hidden_dim).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate)
    scheduler = torch.optim.lr_scheduler.ReduceLROnPlateau(optimizer, mode="min", factor=0.5, patience=1)
    history: list[dict[str, Any]] = []
    best_state: dict[str, torch.Tensor] | None = None
    best_selection: tuple[float, float, float, float] | None = None
    best_epoch = 0
    stale = 0
    for epoch in range(1, args.epochs + 1):
        model.train()
        running = 0.0
        seen = 0
        for raw in train_loader:
            batch = move(raw, device)
            optimizer.zero_grad(set_to_none=True)
            outputs = model(
                batch["state_numeric"], batch["state_token_ids"], batch["state_token_weights"],
                batch["enemy_token_ids"], batch["enemy_numeric"], batch["enemy_mask"],
                batch["candidate_token_ids"], batch["candidate_numeric"], batch["legal_mask"],
            )
            loss, _ = losses(outputs, batch)
            loss.backward()
            optimizer.step()
            running += float(loss.detach()) * len(raw["state_hash_public"])
            seen += len(raw["state_hash_public"])
        metrics = evaluate(model, validation_loader, device)
        # Policy loss is normalized by the fixed number of validation rows.
        # The old scheduler/selection path used conditional regret whose
        # denominator changed with action-value coverage between epochs.
        scheduler.step(metrics["policy_loss"])
        history.append({"epoch": epoch, "train_loss": running / max(seen, 1), "learning_rate": optimizer.param_groups[0]["lr"], **{f"validation_{k}": v for k, v in metrics.items()}})
        # Model selection is driven first by top-1 over the fixed validation
        # row count, then NDCG, fixed-denominator regret, and policy loss.
        selection = (
            metrics["top1"],
            metrics["ndcg_at_3"],
            -metrics["regret_fixed"],
            -metrics["policy_loss"],
        )
        improved = best_selection is None or selection > best_selection
        if improved:
            best_selection = selection
            best_epoch = epoch
            best_state = deepcopy(model.state_dict())
            stale = 0
        else:
            stale += 1
            if stale >= args.patience:
                break
    if best_state is None:
        raise RuntimeError("training produced no checkpoint")
    model.load_state_dict(best_state)
    validation_metrics = evaluate(model, validation_loader, device)
    test_metrics = evaluate(model, test_loader, device)
    challenge_metrics = evaluate(model, challenge_loader, device)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    vocabulary_path = args.out_dir / "vocabulary.json"
    vocabulary_path.write_text(json.dumps(vocabulary.to_dict(), indent=2) + "\n", encoding="utf-8")
    checkpoint_path = args.out_dir / "combat-nosl-best.pt"
    torch.save({
        "state_dict": model.state_dict(), "vocab_size": len(vocabulary.tokens),
        "embedding_dim": args.embedding_dim, "hidden_dim": args.hidden_dim,
        "seed": args.seed, "feature_schema_version": "combat-feature-v1",
    }, checkpoint_path)
    (args.out_dir / "training-history.json").write_text(json.dumps(history, indent=2) + "\n", encoding="utf-8")
    manifest = {
        "model_id": args.model_id,
        "model_stage": args.model_stage,
        "feature_schema_version": "combat-feature-v1",
        "feature_manifest": str(args.feature_manifest),
        "feature_manifest_sha256": sha256(args.feature_manifest),
        "checkpoint": str(checkpoint_path),
        "checkpoint_sha256": sha256(checkpoint_path),
        "vocabulary": str(vocabulary_path),
        "vocabulary_sha256": sha256(vocabulary_path),
        "reliable_training_only": True,
        "training_character_distribution": character_distribution(all_source_rows),
        "supervised_training_character_distribution": character_distribution(train_rows),
        "character_balance_note": args.character_balance_note,
        "training_rows": len(train_rows),
        "validation_rows": len(validation_rows),
        "test_rows": len(test_rows),
        "challenge_rows": len(challenge_rows),
        "parameter_count": sum(parameter.numel() for parameter in model.parameters()),
        "seed": args.seed,
        "threads": args.threads,
        "epochs_configured": args.epochs,
        "epochs_completed": len(history),
        "best_epoch": best_epoch,
        "selection_criterion": "fixed_denominator_validation_top1_then_ndcg_at_3_then_regret_fixed_then_policy_loss",
        "selection_denominator": "validation_rows",
        "early_stopping_patience": args.patience,
        "early_stopping_triggered": len(history) < args.epochs,
        "validation_regret": validation_metrics["regret"],
        "validation_policy_loss": validation_metrics["policy_loss"],
        "validation_regret_fixed": validation_metrics["regret_fixed"],
        "validation_regret_coverage": validation_metrics["regret_coverage"],
        "validation_top1": validation_metrics["top1"],
        "validation_ndcg_at_3": validation_metrics["ndcg_at_3"],
        "validation_top1_by_character": validation_metrics["top1_by_character"],
        "test_regret": test_metrics["regret"],
        "test_policy_loss": test_metrics["policy_loss"],
        "test_regret_fixed": test_metrics["regret_fixed"],
        "test_regret_coverage": test_metrics["regret_coverage"],
        "test_top1": test_metrics["top1"],
        "test_ndcg_at_3": test_metrics["ndcg_at_3"],
        "test_top1_by_character": test_metrics["top1_by_character"],
        "challenge_regret": challenge_metrics["regret"],
        "challenge_policy_loss": challenge_metrics["policy_loss"],
        "challenge_regret_fixed": challenge_metrics["regret_fixed"],
        "challenge_regret_coverage": challenge_metrics["regret_coverage"],
        "challenge_top1": challenge_metrics["top1"],
        "challenge_ndcg_at_3": challenge_metrics["ndcg_at_3"],
        "challenge_top1_by_character": challenge_metrics["top1_by_character"],
        "torch_version": torch.__version__,
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "forbidden_inputs": json.loads(args.feature_manifest.read_text(encoding="utf-8"))["forbidden_input_fields"],
    }
    manifest_path = args.out_dir / "combat-nosl-model-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(manifest_path), "epochs": len(history), "validation": validation_metrics, "test": test_metrics, "challenge": challenge_metrics}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
