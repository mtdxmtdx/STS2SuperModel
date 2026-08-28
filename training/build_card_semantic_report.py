#!/usr/bin/env python3
"""Build v0.111 card semantic coverage statistics from generated artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def variants_by_id(cards: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for card in cards:
        result[card["id"]] = card | {"upgraded": False}
        upgraded = card.get("upgraded")
        if upgraded:
            result[upgraded["id"]] = {
                "id": upgraded["id"], "character": card.get("character"),
                "type": card.get("type"), "rarity": card.get("rarity"),
                "description": upgraded.get("description", ""), "upgraded": True,
                "multiplayer_only": card.get("multiplayer_only", False),
            }
    return result


def count_by(items: list[dict[str, Any]], key: str) -> dict[str, int]:
    return dict(sorted(Counter(str(item.get(key) or "unknown") for item in items).items()))


def build(cards_path: Path, semantics_path: Path) -> dict[str, Any]:
    cards_doc = load(cards_path)
    semantics = load(semantics_path)
    cards = cards_doc["cards"]
    variants = semantics["variants"]
    metadata = variants_by_id(cards)
    rows = []
    for variant in variants:
        card = metadata.get(variant["id"], {})
        rows.append({**variant, **{k: card.get(k) for k in ("character", "type", "rarity", "multiplayer_only")}})
    single = [row for row in rows if not row.get("multiplayer_only", False)]

    def summary(items: list[dict[str, Any]]) -> dict[str, Any]:
        return {
            "variants": len(items),
            "fully_structured": sum(bool(row["is_fully_structured"]) for row in items),
            "immediately_executable": sum(bool(row["is_immediately_executable"]) for row in items),
            "simulator_executable": sum(bool(row["is_simulator_executable"]) for row in items),
            "runtime_handler_resolvable": sum(bool(row["runtime_handler_resolvable"]) for row in items),
            "unparsed_clauses": sum(len(row.get("unparsed_clauses") or []) for row in items),
            "cards_by_character": count_by(items, "character"),
            "cards_by_type": count_by(items, "type"),
        }

    categories = {
        "exhaust": lambda row: "消耗" in row.get("source_text", "") or any(op.get("id") in ("消耗", "UP_TO", "STATUS_ALL_PILES") for op in row.get("operations", [])),
        "ethereal": lambda row: "虚无" in row.get("source_text", ""),
        "retain": lambda row: "保留" in row.get("source_text", ""),
        "random": lambda row: "随机" in row.get("source_text", "") or any(op.get("random_source") for op in row.get("operations", [])),
        "choice": lambda row: any(op.get("kind") in ("select_card", "exhaust_cards") for op in row.get("operations", [])),
        "power_or_relic_trigger": lambda row: any(op.get("trigger") or op.get("timing") not in (None, "immediate") for op in row.get("operations", [])),
    }
    category_stats = {
        name: {"all_variants": sum(predicate(row) for row in rows),
               "single_player_variants": sum(predicate(row) for row in single)}
        for name, predicate in categories.items()
    }
    return {
        "schema_version": 1,
        "game_version": semantics.get("game_version", cards_doc.get("game_version")),
        "source": {"cards": str(cards_path), "cards_sha256": sha256(cards_path),
                   "semantics": str(semantics_path), "semantics_sha256": sha256(semantics_path)},
        "base_cards": len(cards),
        "upgraded_variants": sum(card.get("upgraded") is not None for card in cards),
        "all": summary(rows),
        "single_player_combat_scope": summary(single),
        "multiplayer_only_variants": len(rows) - len(single),
        "semantic_categories": category_stats,
        "top_unparsed_clauses_all": Counter(
            clause for row in rows for clause in row.get("unparsed_clauses", [])
        ).most_common(25),
        "top_unparsed_clauses_single_player": Counter(
            clause for row in single for clause in row.get("unparsed_clauses", [])
        ).most_common(25),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cards", type=Path, required=True)
    parser.add_argument("--semantics", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = build(args.cards, args.semantics)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "variants": report["all"]["variants"],
                      "single_player": report["single_player_combat_scope"]["variants"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
