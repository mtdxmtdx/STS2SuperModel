#!/usr/bin/env python3
"""Verify immutable holdout definitions and exclude their episodes from train/validation."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _episodes(path: Path) -> set[str]:
    result: set[str] = set()
    with path.open(encoding="utf-8") as stream:
        for line_no, line in enumerate(stream, start=1):
            if not line.strip():
                continue
            row = json.loads(line)
            episode = row.get("episode_id") or row.get("trace_id")
            if not episode:
                raise ValueError(f"{path}:{line_no}: missing episode_id/trace_id")
            result.add(str(episode))
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--split-dir", required=True, type=Path)
    parser.add_argument("--holdout", required=True, action="append", type=Path)
    parser.add_argument("--expected-sha256", required=True, action="append")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if len(args.holdout) != len(args.expected_sha256):
        parser.error("--holdout and --expected-sha256 counts must match")

    split_episodes = {
        name: _episodes(args.split_dir / f"{name}.jsonl")
        for name in ("train", "validation", "test", "challenge")
    }
    checks: list[dict[str, Any]] = []
    failures: list[str] = []
    banned_union: set[str] = set()
    for path, expected_hash in zip(args.holdout, args.expected_sha256, strict=True):
        payload = json.loads(path.read_text(encoding="utf-8"))
        actual_hash = _sha256(path)
        frozen_test = {str(value) for value in payload.get("test_episode_ids") or []}
        frozen_challenge = {str(value) for value in payload.get("challenge_episode_ids") or []}
        banned = frozen_test | frozen_challenge
        banned_union |= banned
        leaked_train = sorted(banned & split_episodes["train"])
        leaked_validation = sorted(banned & split_episodes["validation"])
        missing_test = sorted(frozen_test - split_episodes["test"])
        missing_challenge = sorted(frozen_challenge - split_episodes["challenge"])
        item_failures: list[str] = []
        if actual_hash != expected_hash.upper():
            item_failures.append("holdout_sha256_mismatch")
        if leaked_train:
            item_failures.append("frozen_members_in_train")
        if leaked_validation:
            item_failures.append("frozen_members_in_validation")
        if missing_test:
            item_failures.append("frozen_test_members_missing")
        if missing_challenge:
            item_failures.append("frozen_challenge_members_missing")
        failures.extend(f"{payload.get('holdout_id', path.name)}:{failure}" for failure in item_failures)
        checks.append({
            "holdout_id": payload.get("holdout_id"),
            "path": str(path.resolve()),
            "sha256": actual_hash,
            "expected_sha256": expected_hash.upper(),
            "test_episode_count": len(frozen_test),
            "challenge_episode_count": len(frozen_challenge),
            "leaked_train": leaked_train,
            "leaked_validation": leaked_validation,
            "missing_test": missing_test,
            "missing_challenge": missing_challenge,
            "verdict": "pass" if not item_failures else "fail",
        })

    overlaps: list[dict[str, Any]] = []
    for left_index, left in enumerate(checks):
        left_payload = json.loads(Path(left["path"]).read_text(encoding="utf-8"))
        left_ids = set(left_payload.get("test_episode_ids") or []) | set(left_payload.get("challenge_episode_ids") or [])
        for right in checks[left_index + 1:]:
            right_payload = json.loads(Path(right["path"]).read_text(encoding="utf-8"))
            right_ids = set(right_payload.get("test_episode_ids") or []) | set(right_payload.get("challenge_episode_ids") or [])
            overlap = sorted(left_ids & right_ids)
            overlaps.append({
                "left": left["holdout_id"],
                "right": right["holdout_id"],
                "episode_count": len(overlap),
                "sample": overlap[:10],
                "note": "reported only; specialist holdouts are not required to be mutually exclusive",
            })

    result = {
        "schema_version": 1,
        "verdict": "pass" if not failures else "fail",
        "split_dir": str(args.split_dir.resolve()),
        "split_episode_counts": {key: len(value) for key, value in split_episodes.items()},
        "frozen_episode_union_count": len(banned_union),
        "checks": checks,
        "specialist_holdout_overlaps": overlaps,
        "failures": failures,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": result["verdict"], "failures": failures, "output": str(args.output)}))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
