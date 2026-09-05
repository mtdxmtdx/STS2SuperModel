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


def _summary(selected: list[dict[str, Any]]) -> tuple[list[str], int, int, dict[str, dict[str, float | int]]]:
    episodes: set[str] = set()
    counts: Counter[str] = Counter()
    rows = reliable = 0
    for row in selected:
        episode = row.get("episode_id") or row.get("trace_id")
        episodes.add(str(episode))
        counts[str(row.get("character") or "Unknown")] += 1
        rows += 1
        reliable += int(row.get("confidence") == "Reliable")
    distribution = {
        character: {"rows": count, "ratio": count / max(rows, 1)}
        for character, count in sorted(counts.items())
    }
    return sorted(episodes), rows, reliable, distribution


def read_split(
    path: Path,
    *,
    rebalance_by_character: bool = False,
    selection_salt: str = "",
) -> tuple[list[str], int, int, dict[str, dict[str, float | int]], list[dict[str, Any]]]:
    values: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if not line.strip():
                continue
            row: dict[str, Any] = json.loads(line)
            episode = row.get("episode_id") or row.get("trace_id")
            if not episode:
                raise ValueError(f"{path}: row missing episode_id/trace_id")
            values.append(row)
    if rebalance_by_character:
        grouped: dict[str, list[dict[str, Any]]] = {}
        for row in values:
            episode = str(row.get("episode_id") or row.get("trace_id"))
            grouped.setdefault(episode, []).append(row)
        by_character: dict[str, list[tuple[str, list[dict[str, Any]]]]] = {}
        for episode, episode_rows in grouped.items():
            characters = {str(row.get("character") or "Unknown") for row in episode_rows}
            if len(characters) != 1:
                raise ValueError(f"episode {episode!r} has mixed characters: {sorted(characters)}")
            by_character.setdefault(next(iter(characters)), []).append((episode, episode_rows))
        if set(by_character) != {"The Ironclad", "The Silent"}:
            raise ValueError(f"character rebalance requires exactly Ironclad/Silent: {sorted(by_character)}")
        row_totals = {key: sum(len(rows) for _, rows in groups) for key, groups in by_character.items()}
        minority = min(row_totals, key=row_totals.get)
        majority = max(row_totals, key=row_totals.get)
        selected = [row for _, group in by_character[minority] for row in group]
        target = row_totals[minority]
        current = 0
        ordered_majority = sorted(
            by_character[majority],
            key=lambda item: hashlib.sha256(
                f"{selection_salt}|{item[0]}".encode("utf-8")
            ).hexdigest(),
        )
        for _, group in ordered_majority:
            if current + len(group) <= target:
                selected.extend(group)
                current += len(group)
        values = selected
    ids, row_count, reliable, distribution = _summary(values)
    return ids, row_count, reliable, distribution, values


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--split-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--frozen-at-utc", required=True)
    parser.add_argument("--holdout-id", default="holdout-core-v1")
    parser.add_argument("--rebalance-by-character", action="store_true")
    parser.add_argument("--coverage-potions", help="Comma-separated potion IDs represented by this holdout")
    parser.add_argument("--coverage-relics", help="Comma-separated relic IDs represented by this holdout")
    parser.add_argument(
        "--expected-source-sha256",
        default="93F9FCD4BF504FB737806E7A5074CA65FBDF802959A5917700121FD41DCF9AA3",
    )
    args = parser.parse_args()
    expected_sha = args.expected_source_sha256.upper()
    actual_sha = sha256(args.source)
    if actual_sha != expected_sha:
        raise ValueError(f"source SHA-256 mismatch: expected {expected_sha}, actual {actual_sha}")
    test_ids, test_rows, test_reliable, test_distribution, test_values = read_split(
        args.split_dir / "test.jsonl",
        rebalance_by_character=args.rebalance_by_character,
        selection_salt=f"{args.holdout_id}|test",
    )
    challenge_ids, challenge_rows, challenge_reliable, challenge_distribution, challenge_values = read_split(
        args.split_dir / "challenge.jsonl",
        rebalance_by_character=args.rebalance_by_character,
        selection_salt=f"{args.holdout_id}|challenge",
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
            "episode groups selected deterministically by sha256(holdout_id|split|episode_id); all minority-character "
            "episodes retained and majority episodes greedily retained up to the minority row count; final IDs sorted "
            "by ordinal Unicode code point"
            if args.rebalance_by_character else
            "all episode_id values from the source split tree's existing test and challenge files; IDs sorted by "
            "ordinal Unicode code point; accepted without resampling because each split's Ironclad/Silent row ratios "
            "are within [0.45,0.55]"
        ),
        **({"rebalanced_by_character": True} if args.rebalance_by_character else {}),
        "coverage_profile": ({
            "act": sorted({int(row.get("act") or 0) for row in test_values + challenge_values}),
            "floor": sorted({int(row.get("floor") or 0) for row in test_values + challenge_values}),
            "round_min": min(int(row.get("round") or 0) for row in test_values + challenge_values),
            "round_max": max(int(row.get("round") or 0) for row in test_values + challenge_values),
            "relics": sorted(value.strip() for value in args.coverage_relics.split(",") if value.strip())
                if args.coverage_relics else "measured_by_dataset_coverage_profile",
            "potions": sorted(value.strip() for value in args.coverage_potions.split(",") if value.strip())
                if args.coverage_potions else "measured_by_dataset_coverage_profile",
            "characters": ["The Ironclad", "The Silent"],
        } if args.rebalance_by_character else {
            "act": [1],
            "floor": [1],
            "round_max": 4,
            "relics": "starter_only_dominant",
            "potions": "none_dominant",
            "characters": ["The Ironclad", "The Silent"],
        }),
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
