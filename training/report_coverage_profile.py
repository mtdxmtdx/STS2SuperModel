#!/usr/bin/env python3
"""Report combat-dataset coverage without treating sample count as coverage."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


STARTER_RELIC_IDS = {"BURNING_BLOOD", "RING_OF_THE_SNAKE"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def rows(path: Path) -> Iterable[dict[str, Any]]:
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                yield json.loads(line)


def profile(path: Path) -> dict[str, Any]:
    totals = Counter()
    reliable = Counter()
    floor_counts: Counter[int] = Counter()
    act_counts: Counter[int] = Counter()
    round_counts: Counter[int] = Counter()
    enemy_counts: Counter[int] = Counter()
    character_counts: Counter[str] = Counter()
    reliable_character_counts: Counter[str] = Counter()
    relic_id_counts: Counter[str] = Counter()
    nonstarter_episodes: set[str] = set()
    for row in rows(path):
        state = row.get("public_state") or {}
        player = state.get("player") or {}
        relics = player.get("relics") or []
        relic_ids = [str(relic.get("id") or "UNKNOWN") for relic in relics]
        potions = player.get("potions") or []
        enemies = state.get("enemies") or []
        floor = int(row.get("floor") or (state.get("context") or {}).get("floor") or 0)
        act = int(row.get("act") or (state.get("context") or {}).get("act") or 0)
        round_number = int(row.get("round") or state.get("round") or 0)
        is_reliable = row.get("confidence") == "Reliable"
        character = str(row.get("character") or player.get("name") or "Unknown")
        totals["rows"] += 1
        totals["starter_only"] += int(bool(relic_ids) and set(relic_ids) <= STARTER_RELIC_IDS)
        totals["no_potions"] += int(not potions)
        totals["has_potions"] += int(bool(potions))
        totals["nonstarter_relic_occurrences"] += sum(
            relic_id not in STARTER_RELIC_IDS for relic_id in relic_ids
        )
        has_nonstarter = any(relic_id not in STARTER_RELIC_IDS for relic_id in relic_ids)
        totals["rows_with_nonstarter_relic"] += int(has_nonstarter)
        if has_nonstarter:
            nonstarter_episodes.add(str(row.get("episode_id") or row.get("trace_id") or "Unknown"))
        totals["nonstarter_relic_count_ge_2"] += int(
            sum(relic_id not in STARTER_RELIC_IDS for relic_id in relic_ids) >= 2
        )
        totals["round_ge_8"] += int(round_number >= 8)
        totals["act_ge_2"] += int(act >= 2)
        totals["enemy_count_ge_3"] += int(len(enemies) >= 3)
        floor_counts[floor] += 1
        act_counts[act] += 1
        round_counts[round_number] += 1
        enemy_counts[len(enemies)] += 1
        character_counts[character] += 1
        relic_id_counts.update(relic_ids)
        if is_reliable:
            reliable_character_counts[character] += 1
            reliable["rows"] += 1
            reliable["round_ge_8"] += int(round_number >= 8)
            reliable["nonstarter_relic_count_ge_2"] += int(
                sum(relic_id not in STARTER_RELIC_IDS for relic_id in relic_ids) >= 2
            )
            reliable["has_potions"] += int(bool(potions))
            reliable["act_ge_2"] += int(act >= 2)
            reliable["enemy_count_ge_3"] += int(len(enemies) >= 3)

    def ratio(value: int, total: int) -> float:
        return value / max(total, 1)

    all_rows = totals["rows"]
    reliable_rows = reliable["rows"]
    return {
        "schema_version": 1,
        "source": str(path),
        "source_sha256": sha256(path),
        "row_count": all_rows,
        "reliable_row_count": reliable_rows,
        "all_rows": {
            "floor_distribution": {str(key): value for key, value in sorted(floor_counts.items())},
            "act_distribution": {str(key): value for key, value in sorted(act_counts.items())},
            "round_distribution": {str(key): value for key, value in sorted(round_counts.items())},
            "enemy_count_distribution": {str(key): value for key, value in sorted(enemy_counts.items())},
            "character_distribution": {
                key: {"rows": value, "ratio": ratio(value, all_rows)}
                for key, value in sorted(character_counts.items())
            },
            "starter_only_rows": totals["starter_only"],
            "starter_only_ratio": ratio(totals["starter_only"], all_rows),
            "nonstarter_relic_occurrences": totals["nonstarter_relic_occurrences"],
            "rows_with_nonstarter_relic": totals["rows_with_nonstarter_relic"],
            "episodes_with_nonstarter_relic": len(nonstarter_episodes),
            "no_potion_rows": totals["no_potions"],
            "no_potion_ratio": ratio(totals["no_potions"], all_rows),
            "enemy_count_ge_3_rows": totals["enemy_count_ge_3"],
            "enemy_count_ge_3_ratio": ratio(totals["enemy_count_ge_3"], all_rows),
            "relic_id_counts": dict(sorted(relic_id_counts.items())),
        },
        "reliable_coverage": {
            key: {
                "rows": reliable[key],
                "ratio": ratio(reliable[key], reliable_rows),
            }
            for key in (
                "round_ge_8",
                "nonstarter_relic_count_ge_2",
                "has_potions",
                "act_ge_2",
                "enemy_count_ge_3",
            )
        },
        "reliable_character_distribution": {
            key: {"rows": value, "ratio": ratio(value, reliable_rows)}
            for key, value in sorted(reliable_character_counts.items())
        },
        "proposed_acceptance_thresholds": {
            "status": "pending_human_confirmation",
            "round_ge_8_reliable_ratio": 0.20,
            "nonstarter_relic_count_ge_2_reliable_ratio": 0.30,
            "has_potions_reliable_ratio": 0.20,
            "act_ge_2_reliable_ratio": 0.25,
            "enemy_count_ge_3_reliable_ratio": 0.35,
        },
        "version_lock": {
            "game_version": "v0.111.0",
            "game_commit": "41cef1ea",
            "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0",
            "trace_schema": 1,
            "feature_schema_version": "combat-feature-v1",
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = profile(args.input)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "verdict": "pass",
        "rows": result["row_count"],
        "reliable": result["reliable_row_count"],
        "output": str(args.output),
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
