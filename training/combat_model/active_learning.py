"""Rank public states for the next NOSL collection round."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch

from .dataset import CombatDataset, collate_samples, load_rows
from .encoder import CombatFeatureEncoder, TokenVocabulary
from .model import CombatPolicyValueModel


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--top", type=int, default=500)
    parser.add_argument("--batch-size", type=int, default=256)
    args = parser.parse_args()
    rows = load_rows(args.data, reliable_only=True)
    vocabulary = TokenVocabulary.from_dict(json.loads(args.vocabulary.read_text(encoding="utf-8")))
    encoder = CombatFeatureEncoder(vocabulary)
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    model = CombatPolicyValueModel(checkpoint["vocab_size"], checkpoint["embedding_dim"], checkpoint["hidden_dim"])
    model.load_state_dict(checkpoint["state_dict"])
    model.eval()
    scored: list[dict[str, object]] = []
    dataset = CombatDataset(rows, encoder)
    with torch.no_grad():
        for start in range(0, len(dataset), args.batch_size):
            samples = [dataset[index] for index in range(start, min(start + args.batch_size, len(dataset)))]
            batch = collate_samples(samples)
            logits, _, _ = model(
                batch["state_numeric"], batch["state_token_ids"], batch["state_token_weights"],
                batch["enemy_token_ids"], batch["enemy_numeric"], batch["enemy_mask"],
                batch["candidate_token_ids"], batch["candidate_numeric"], batch["legal_mask"],
            )
            for offset, row in enumerate(samples):
                count = int(batch["legal_mask"][offset].sum())
                predicted = int(logits[offset, :count].argmax())
                target = batch["policy_target"][offset, :count]
                best = int(target.argmax()) if count else -1
                known = batch["objective_mask"][offset, :count, 0]
                balanced = batch["objective_values"][offset, :count, 0]
                regret = float(max(0.0, (balanced[known].max() - balanced[predicted]).item())) if count and bool(known[predicted]) and bool(known.any()) else 0.0
                disagreement = predicted != best
                scored.append({
                    "state_hash_public": row["state_hash_public"],
                    "episode_id": rows[start + offset]["episode_id"],
                    "predicted_action": row["action_ids"][predicted] if count else None,
                    "teacher_action": row["action_ids"][best] if count and best >= 0 else None,
                    "disagreement": disagreement,
                    "regret": regret,
                    "priority": (2.0 if disagreement else 0.0) + regret,
                })
    scored.sort(key=lambda item: (-float(item["priority"]), str(item["state_hash_public"])))
    report = {
        "schema_version": 1,
        "source": str(args.data),
        "rows_scored": len(scored),
        "disagreement_count": sum(bool(item["disagreement"]) for item in scored),
        "nonzero_regret_count": sum(float(item["regret"]) > 0 for item in scored),
        "selection_policy": "descending (2 * top1_disagreement + known_balanced_regret), state_hash_public tie-break",
        "candidates": scored[: max(0, args.top)],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("rows_scored", "disagreement_count", "nonzero_regret_count", "selection_policy")}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
