"""Frozen episode holdouts for comparable combat-model evaluation."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

from .dataset import load_rows


SPLITS = ("train", "validation", "test", "challenge")


def episode_id(row: dict[str, Any]) -> str:
    value = row.get("episode_id") or row.get("trace_id")
    if not value:
        raise ValueError("row is missing episode_id/trace_id")
    return str(value)


def load_holdout(path: Path) -> dict[str, Any]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    test = {str(value) for value in payload.get("test_episode_ids") or []}
    challenge = {str(value) for value in payload.get("challenge_episode_ids") or []}
    overlap = sorted(test & challenge)
    if overlap:
        raise ValueError(f"holdout episode lists overlap: {overlap[:3]}")
    if not test or not challenge:
        raise ValueError("holdout must contain non-empty test and challenge episode lists")
    return payload


def _validation_episode(value: str) -> bool:
    bucket = int.from_bytes(
        hashlib.sha256(f"holdout-train-validation-v1|{value}".encode("utf-8")).digest()[:4],
        "big",
    ) % 100
    return bucket < 30


def load_frozen_splits(
    split_dir: Path,
    holdout_path: Path,
    *,
    reliable_only: bool = True,
) -> dict[str, list[dict[str, Any]]]:
    """Load a split tree and enforce a frozen test/challenge episode contract.

    Any duplicate episode across input files is rejected before reassignment.
    Frozen episodes must still be present in their named source split, which
    makes accidentally copying a test episode into train a hard leakage error.
    Non-frozen episodes are deterministically assigned only to train/validation.
    """

    holdout = load_holdout(holdout_path)
    frozen_test = {str(value) for value in holdout["test_episode_ids"]}
    frozen_challenge = {str(value) for value in holdout["challenge_episode_ids"]}
    origins: dict[str, str] = {}
    rows: list[dict[str, Any]] = []
    for source_split in SPLITS:
        path = split_dir / f"{source_split}.jsonl"
        if not path.is_file():
            raise ValueError(f"missing split file: {path}")
        for row in load_rows(path, reliable_only=False):
            episode = episode_id(row)
            previous = origins.setdefault(episode, source_split)
            if previous != source_split:
                raise ValueError(
                    f"episode leakage in input split tree: {episode!r} appears in "
                    f"both {previous!r} and {source_split!r}"
                )
            if episode in frozen_test and source_split != "test":
                raise ValueError(
                    f"frozen test episode {episode!r} appears in {source_split!r}"
                )
            if episode in frozen_challenge and source_split != "challenge":
                raise ValueError(
                    f"frozen challenge episode {episode!r} appears in {source_split!r}"
                )
            rows.append(row)

    missing_test = sorted(frozen_test - origins.keys())
    missing_challenge = sorted(frozen_challenge - origins.keys())
    if missing_test or missing_challenge:
        raise ValueError(
            f"frozen episodes missing from split tree: "
            f"test={missing_test[:3]}, challenge={missing_challenge[:3]}"
        )

    result: dict[str, list[dict[str, Any]]] = {name: [] for name in SPLITS}
    state_owner: dict[str, str] = {}
    for row in rows:
        if reliable_only and row.get("confidence") != "Reliable":
            continue
        episode = episode_id(row)
        if episode in frozen_test:
            target = "test"
        elif episode in frozen_challenge:
            target = "challenge"
        else:
            target = "validation" if _validation_episode(episode) else "train"
        state_hash = str(row.get("state_hash_public") or "")
        if state_hash:
            previous = state_owner.setdefault(state_hash, target)
            if previous != target:
                raise ValueError(
                    f"state leakage after frozen partition: {state_hash!r} appears in "
                    f"both {previous!r} and {target!r}"
                )
        result[target].append(row)
    return result
