from __future__ import annotations

import json
import shutil
import uuid
from contextlib import contextmanager
from pathlib import Path

import pytest

from training.combat_model.holdout import load_frozen_splits
from training.combat_model.train import load_training_rows


def _row(episode: str, state: str, confidence: str = "Reliable") -> dict:
    return {
        "episode_id": episode,
        "state_hash_public": state,
        "confidence": confidence,
        "character": "The Ironclad",
    }


def _write(path: Path, rows: list[dict]) -> None:
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")


@contextmanager
def _fixture():
    root = Path(__file__).parent / "test-output" / f"holdout-{uuid.uuid4().hex}"
    split_dir = root / "splits"
    split_dir.mkdir(parents=True)
    _write(split_dir / "train.jsonl", [_row("train-1", "state-train")])
    _write(split_dir / "validation.jsonl", [_row("validation-1", "state-validation")])
    _write(split_dir / "test.jsonl", [_row("test-1", "state-test")])
    _write(split_dir / "challenge.jsonl", [_row("challenge-1", "state-challenge")])
    holdout = root / "holdout.json"
    holdout.write_text(json.dumps({
        "holdout_id": "holdout-core-v1",
        "test_episode_ids": ["test-1"],
        "challenge_episode_ids": ["challenge-1"],
    }), encoding="utf-8")
    try:
        yield split_dir, holdout
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_frozen_holdout_keeps_test_and_challenge_membership() -> None:
    with _fixture() as (split_dir, holdout):
        result = load_frozen_splits(split_dir, holdout)
        assert [row["episode_id"] for row in result["test"]] == ["test-1"]
        assert [row["episode_id"] for row in result["challenge"]] == ["challenge-1"]
        assert not ({row["episode_id"] for row in result["train"]} & {"test-1", "challenge-1"})
        assert not ({row["episode_id"] for row in result["validation"]} & {"test-1", "challenge-1"})
        train, validation, test, challenge, all_rows = load_training_rows(split_dir, holdout)
        assert len(all_rows) == 4
        assert [row["episode_id"] for row in test] == ["test-1"]
        assert [row["episode_id"] for row in challenge] == ["challenge-1"]


def test_frozen_holdout_rejects_test_episode_added_to_train() -> None:
    with _fixture() as (split_dir, holdout):
        with (split_dir / "train.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(_row("test-1", "state-leak")) + "\n")
        with pytest.raises(ValueError, match="frozen test episode 'test-1' appears in 'train'"):
            load_frozen_splits(split_dir, holdout)
