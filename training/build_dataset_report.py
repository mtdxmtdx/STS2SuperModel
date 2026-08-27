#!/usr/bin/env python3
"""Build deterministic, streaming P0 dataset quality statistics."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


VERSION_KEYS = (
    "game_version",
    "game_commit",
    "assembly_sha256",
    "cli_protocol_version",
    "simulator_version",
    "semantic_database_version",
    "scorer_version",
    "feature_schema_version",
    "model_version",
    "generator_config_hash",
)


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def ids(items: Any, key: str = "id") -> Iterable[str]:
    if not isinstance(items, list):
        return ()
    return (str(item[key]) for item in items if isinstance(item, dict) and item.get(key))


def build(input_path: Path, output_path: Path) -> dict[str, Any]:
    rows = actions = paired_teacher = 0
    episodes: set[str] = set()
    combats: set[str] = set()
    confidence = Counter()
    action_kinds = Counter()
    characters = Counter()
    ascensions = Counter()
    cards = Counter()
    potions = Counter()
    relics = Counter()
    powers = Counter()
    enemies = Counter()
    restrictions = Counter()
    risk_events = Counter()
    missing_metadata = Counter()
    metadata: dict[str, Any] | None = None

    with input_path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            rows += 1
            current = {key: row.get(key) for key in VERSION_KEYS}
            for key, value in current.items():
                if value in (None, ""):
                    missing_metadata[key] += 1
            if metadata is None:
                metadata = current
            elif current != metadata:
                changed = [key for key in VERSION_KEYS if current[key] != metadata[key]]
                raise ValueError(f"{input_path}:{line_no}: mixed metadata: {', '.join(changed)}")

            episodes.add(str(row.get("episode_id", "unknown")))
            combats.add(str(row.get("combat_id", "unknown")))
            confidence[str(row.get("confidence", "Unknown"))] += 1
            characters[str(row.get("character", "unknown"))] += 1
            ascensions[str(row.get("ascension", "unknown"))] += 1
            if row.get("teacher_state_reference"):
                paired_teacher += 1

            public = row.get("public_state") or {}
            cards.update(ids(public.get("hand")))
            player = public.get("player") or {}
            potions.update(ids(player.get("potions")))
            relics.update(ids(player.get("relics")))
            powers.update(ids(player.get("powers")))
            for enemy in public.get("enemies", []) or []:
                if not isinstance(enemy, dict):
                    continue
                if enemy.get("id") or enemy.get("name"):
                    enemies[str(enemy.get("id") or enemy.get("name"))] += 1
                powers.update(ids(enemy.get("powers")))

            for action in row.get("legal_actions", []) or []:
                if not isinstance(action, dict):
                    continue
                actions += 1
                action_kinds[str(action.get("kind", "Unknown"))] += 1
                if action.get("restriction"):
                    restrictions[str(action["restriction"])] += 1
            risk_events.update(str(event) for event in row.get("risk_events", []) or [])

    report = {
        "schema_version": 1,
        "report_kind": "p0_tooling_smoke",
        "source": str(input_path),
        "source_sha256": digest(input_path),
        "row_count": rows,
        "action_count": actions,
        "episode_count": len(episodes),
        "combat_count": len(combats),
        "teacher_pair_count": paired_teacher,
        "teacher_pair_rate": paired_teacher / rows if rows else 0.0,
        "metadata": metadata or {},
        "missing_metadata": dict(sorted(missing_metadata.items())),
        "confidence_counts": dict(sorted(confidence.items())),
        "action_kind_counts": dict(sorted(action_kinds.items())),
        "character_counts": dict(sorted(characters.items())),
        "ascension_counts": dict(sorted(ascensions.items())),
        "coverage": {
            "card_ids": dict(sorted(cards.items())),
            "potion_ids": dict(sorted(potions.items())),
            "relic_ids": dict(sorted(relics.items())),
            "power_ids": dict(sorted(powers.items())),
            "enemy_ids": dict(sorted(enemies.items())),
        },
        "restriction_counts": dict(sorted(restrictions.items())),
        "risk_event_counts": dict(sorted(risk_events.items())),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(build(args.input, args.output), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
