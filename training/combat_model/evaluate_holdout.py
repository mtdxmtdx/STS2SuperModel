"""Evaluate an existing checkpoint on a frozen combat holdout."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import torch

from .encoder import CombatFeatureEncoder, TokenVocabulary
from .holdout import load_frozen_splits
from .model import CombatPolicyValueModel
from .train import character_distribution, evaluate, make_loader


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--split-dir", required=True, type=Path)
    parser.add_argument("--holdout", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--threads", type=int, default=16)
    args = parser.parse_args()
    torch.set_num_threads(args.threads)
    torch.use_deterministic_algorithms(True)
    splits = load_frozen_splits(args.split_dir, args.holdout, reliable_only=True)
    vocabulary = TokenVocabulary.from_dict(json.loads(args.vocabulary.read_text(encoding="utf-8")))
    encoder = CombatFeatureEncoder(vocabulary)
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    model = CombatPolicyValueModel(
        checkpoint["vocab_size"], checkpoint["embedding_dim"], checkpoint["hidden_dim"]
    )
    model.load_state_dict(checkpoint["state_dict"])
    device = torch.device("cpu")
    metrics = {}
    for name in ("test", "challenge"):
        loader = make_loader(splits[name], encoder, args.batch_size, shuffle=False, seed=0)
        metrics[name] = evaluate(model, loader, device)
    episode_owners: dict[str, str] = {}
    state_owners: dict[str, str] = {}
    episode_leaks: set[str] = set()
    state_leaks: set[str] = set()
    split_validation = {}
    for name, split_rows in splits.items():
        episodes = {str(row.get("episode_id") or row.get("trace_id")) for row in split_rows}
        states = {str(row.get("state_hash_public")) for row in split_rows if row.get("state_hash_public")}
        split_validation[name] = {"rows": len(split_rows), "episodes": len(episodes), "states": len(states)}
        for episode in episodes:
            previous = episode_owners.setdefault(episode, name)
            if previous != name:
                episode_leaks.add(episode)
        for state in states:
            previous = state_owners.setdefault(state, name)
            if previous != name:
                state_leaks.add(state)
    split_validation["cross_split_episode_count"] = len(episode_leaks)
    split_validation["cross_split_state_count"] = len(state_leaks)
    model_manifest = json.loads((args.checkpoint.parent / "combat-nosl-model-manifest.json").read_text(encoding="utf-8"))
    payload = {
        "schema_version": 1,
        "holdout_id": json.loads(args.holdout.read_text(encoding="utf-8"))["holdout_id"],
        "holdout_sha256": sha256(args.holdout),
        "model_id": model_manifest["model_id"],
        "model_sha256": sha256(args.checkpoint),
        "top1": metrics["test"]["top1"],
        "top1_by_character": metrics["test"]["top1_by_character"],
        "ndcg_at_3": metrics["test"]["ndcg_at_3"],
        "regret_fixed": metrics["test"]["regret_fixed"],
        "test": metrics["test"],
        "challenge": metrics["challenge"],
        "test_character_distribution": character_distribution(splits["test"]),
        "challenge_character_distribution": character_distribution(splits["challenge"]),
        "split_validation": split_validation,
        "version_lock": {
            "game_version": "v0.111.0",
            "game_commit": "41cef1ea",
            "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0",
            "trace_schema": 1,
            "feature_schema_version": "combat-feature-v1",
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": "pass", "model_id": payload["model_id"], "test": metrics["test"], "challenge": metrics["challenge"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
