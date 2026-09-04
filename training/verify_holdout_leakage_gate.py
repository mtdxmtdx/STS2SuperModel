#!/usr/bin/env python3
"""Negative proof that a frozen test episode cannot also enter train."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

try:
    from training.combat_model.holdout import load_frozen_splits
except ModuleNotFoundError:  # direct ``python training/...py`` execution
    from combat_model.holdout import load_frozen_splits


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).parent / "test-output" / "holdout-leakage-negative"
    shutil.rmtree(root, ignore_errors=True)
    split_dir = root / "splits"
    split_dir.mkdir(parents=True)
    base = {"confidence": "Reliable", "character": "The Ironclad"}
    rows = {
        "train": [
            {**base, "episode_id": "train-1", "state_hash_public": "state-train"},
            {**base, "episode_id": "test-1", "state_hash_public": "state-injected-leak"},
        ],
        "validation": [{**base, "episode_id": "validation-1", "state_hash_public": "state-validation"}],
        "test": [{**base, "episode_id": "test-1", "state_hash_public": "state-test"}],
        "challenge": [{**base, "episode_id": "challenge-1", "state_hash_public": "state-challenge"}],
    }
    for name, values in rows.items():
        (split_dir / f"{name}.jsonl").write_text(
            "".join(json.dumps(value) + "\n" for value in values), encoding="utf-8"
        )
    holdout = root / "holdout.json"
    holdout.write_text(json.dumps({
        "holdout_id": "holdout-core-v1",
        "test_episode_ids": ["test-1"],
        "challenge_episode_ids": ["challenge-1"],
    }), encoding="utf-8")
    try:
        load_frozen_splits(split_dir, holdout)
    except ValueError as exc:
        message = str(exc)
        verdict = "pass" if "frozen test episode 'test-1' appears in 'train'" in message else "fail"
        payload = {
            "schema_version": 1,
            "verdict": verdict,
            "injection": "test episode test-1 duplicated into train.jsonl",
            "literal_error": message,
            "expected_exit_status": 1,
        }
    else:
        payload = {
            "schema_version": 1,
            "verdict": "fail",
            "injection": "test episode test-1 duplicated into train.jsonl",
            "literal_error": None,
            "expected_exit_status": 1,
        }
    finally:
        shutil.rmtree(root, ignore_errors=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, sort_keys=True))
    return 0 if payload["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
