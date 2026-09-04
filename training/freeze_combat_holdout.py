#!/usr/bin/env python3
"""Freeze the current test/challenge episode membership as a versioned holdout."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


VERSION_LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
    "feature_schema_version": "combat-feature-v1",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def read_split(path: Path) -> tuple[list[str], int, int, dict[str, dict[str, float | int]]]:
    episodes: set[str] = set()
    counts: Counter[str] = Counter()
    rows = reliable = 0
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if not line.strip():
                continue
            row: dict[str, Any] = json.loads(line)
            episode = row.get("episode_id") or row.get("trace_id")
            if not episode:
                raise ValueError(f"{path}: row missing episode_id/trace_id")
            episodes.add(str(episode))
            counts[str(row.get("character") or "Unknown")] += 1
            rows += 1
            reliable += int(row.get("confidence") == "Reliable")
    distribution = {
        character: {"rows": count, "ratio": count / max(rows, 1)}
        for character, count in sorted(counts.items())
    }
    return sorted(episodes), rows, reliable, distribution


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--split-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--frozen-at-utc", required=True)
    parser.add_argument("--holdout-id", default="holdout-core-v1")
    parser.add_argument(
        "--expected-source-sha256",
        default="93F9FCD4BF504FB737806E7A5074CA65FBDF802959A5917700121FD41DCF9AA3",
    )
    args = parser.parse_args()
    expected_sha = args.expected_source_sha256.upper()
    actual_sha = sha256(args.source)
    if actual_sha != expected_sha:
        raise ValueError(f"source SHA-256 mismatch: expected {expected_sha}, actual {actual_sha}")
    test_ids, test_rows, test_reliable, test_distribution = read_split(args.split_dir / "test.jsonl")
    challenge_ids, challenge_rows, challenge_reliable, challenge_distribution = read_split(
        args.split_dir / "challenge.jsonl"
    )
    overlap = sorted(set(test_ids) & set(challenge_ids))
    if overlap:
        raise ValueError(f"test/challenge episode overlap: {overlap[:3]}")
    for name, distribution in (("test", test_distribution), ("challenge", challenge_distribution)):
        for character in ("The Ironclad", "The Silent"):
            ratio = float((distribution.get(character) or {}).get("ratio") or 0.0)
            if not 0.45 <= ratio <= 0.55:
                raise ValueError(
                    f"{name} character balance outside [0.45, 0.55]: {character}={ratio}"
                )
    payload = {
        "schema_version": 1,
        "holdout_id": args.holdout_id,
        "frozen_at_utc": args.frozen_at_utc,
        "source": args.source.name,
        "source_sha256": actual_sha,
        "selection_rule": (
            "all episode_id values from the source split tree's existing test and challenge files; "
            "IDs sorted by ordinal Unicode code point; accepted without resampling because each split's "
            "Ironclad/Silent row ratios are within [0.45,0.55]"
        ),
        "coverage_profile": {
            "act": [1],
            "floor": [1],
            "round_max": 4,
            "relics": "starter_only_dominant",
            "potions": "none_dominant",
            "characters": ["The Ironclad", "The Silent"],
        },
        "test_episode_ids": test_ids,
        "challenge_episode_ids": challenge_ids,
        "test_episode_count": len(test_ids),
        "challenge_episode_count": len(challenge_ids),
        "test_row_count": test_rows,
        "challenge_row_count": challenge_rows,
        "test_reliable_row_count": test_reliable,
        "challenge_reliable_row_count": challenge_reliable,
        "character_distribution": {
            "test": test_distribution,
            "challenge": challenge_distribution,
        },
        "version_lock": VERSION_LOCK,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "verdict": "pass",
        "test_rows": test_rows,
        "challenge_rows": challenge_rows,
        "test_episodes": len(test_ids),
        "challenge_episodes": len(challenge_ids),
        "output": str(args.output),
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
