"""Extract structured per-floor data from a Slay the Spire 2 .run history file."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def as_list(value: Any) -> list[Any]:
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def object_list(value: Any) -> list[dict[str, Any]]:
    return [item for item in as_list(value) if isinstance(item, dict)]


def id_list(value: Any) -> list[str]:
    result: list[str] = []
    for item in as_list(value):
        if isinstance(item, dict) and item.get("id") is not None:
            result.append(str(item["id"]))
        elif item is not None:
            result.append(str(item))
    return result


def card_choices(value: Any) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for item in object_list(value):
        card = item.get("card") or {}
        result.append(
            {
                "id": card.get("id"),
                "was_picked": bool(item.get("was_picked", False)),
                "floor_added_to_deck": card.get("floor_added_to_deck"),
            }
        )
    return result


def relic_choices(value: Any) -> list[dict[str, Any]]:
    return [
        {"choice": item.get("choice"), "was_picked": bool(item.get("was_picked", False))}
        for item in object_list(value)
    ]


def potion_choices(value: Any) -> list[dict[str, Any]]:
    return [
        {"choice": item.get("choice"), "was_picked": bool(item.get("was_picked", False))}
        for item in object_list(value)
    ]


def ancient_choices(value: Any) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for item in object_list(value):
        title = item.get("title") or {}
        result.append(
            {
                "text_key": item.get("TextKey"),
                "title_key": title.get("key"),
                "was_chosen": bool(item.get("was_chosen", False)),
            }
        )
    return result


def event_choice_keys(value: Any) -> list[str]:
    result: list[str] = []
    for item in as_list(value):
        if isinstance(item, dict):
            title = item.get("title") or {}
            key = title.get("key")
            result.append(str(key if key is not None else item))
        elif item is not None:
            result.append(str(item))
    return result


def flatten_pipe(values: Any) -> str:
    return "|".join(str(value) for value in as_list(values) if value is not None)


def extract(data: dict[str, Any], source: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    floors: list[dict[str, Any]] = []
    flat: list[dict[str, Any]] = []
    histories = as_list(data.get("map_point_history"))

    for act_index, act_history in enumerate(histories, start=1):
        for floor_index, point in enumerate(as_list(act_history), start=1):
            if not isinstance(point, dict):
                continue
            stats_items = object_list(point.get("player_stats"))
            stats = stats_items[0] if stats_items else {}
            rooms = []
            for room in object_list(point.get("rooms")):
                rooms.append(
                    {
                        "model_id": room.get("model_id"),
                        "room_type": room.get("room_type"),
                        "monster_ids": room.get("monster_ids") or [],
                        "turns_taken": room.get("turns_taken", 0),
                    }
                )

            decisions = {
                "event_choices": stats.get("event_choices") or [],
                "ancient_choices": ancient_choices(stats.get("ancient_choice")),
                "rest_site_choices": stats.get("rest_site_choices") or [],
                "card_choices": card_choices(stats.get("card_choices")),
                "potion_choices": potion_choices(stats.get("potion_choices")),
                "relic_choices": relic_choices(stats.get("relic_choices")),
            }
            changes = {
                "cards_gained": id_list(stats.get("cards_gained")),
                "cards_removed": id_list(stats.get("cards_removed")),
                "cards_transformed": id_list(stats.get("cards_transformed")),
                "upgraded_cards": stats.get("upgraded_cards") or [],
                "bought_relics": stats.get("bought_relics") or [],
                "potion_used": stats.get("potion_used") or [],
                "potion_discarded": stats.get("potion_discarded") or [],
            }
            state = {
                "hp": stats.get("current_hp"),
                "max_hp": stats.get("max_hp"),
                "gold": stats.get("current_gold"),
                "damage_taken": stats.get("damage_taken", 0),
                "hp_healed": stats.get("hp_healed", 0),
                "gold_gained": stats.get("gold_gained", 0),
                "gold_lost": stats.get("gold_lost", 0),
                "gold_spent": stats.get("gold_spent", 0),
                "gold_stolen": stats.get("gold_stolen", 0),
                "max_hp_gained": stats.get("max_hp_gained", 0),
                "max_hp_lost": stats.get("max_hp_lost", 0),
                "decisions": decisions,
                "changes": changes,
            }
            floor = {
                "act": act_index,
                "floor": floor_index,
                "map_point_type": point.get("map_point_type"),
                "rooms": rooms,
                "state": state,
                "raw_player_stats": stats,
            }
            floors.append(floor)
            flat.append(
                {
                    "act": act_index,
                    "floor": floor_index,
                    "map_point_type": point.get("map_point_type"),
                    "room_model_ids": flatten_pipe([room.get("model_id") for room in rooms]),
                    "room_types": flatten_pipe([room.get("room_type") for room in rooms]),
                    "monster_ids": flatten_pipe([mid for room in rooms for mid in room["monster_ids"]]),
                    "hp": state["hp"],
                    "max_hp": state["max_hp"],
                    "gold": state["gold"],
                    "damage_taken": state["damage_taken"],
                    "hp_healed": state["hp_healed"],
                    "gold_gained": state["gold_gained"],
                    "gold_lost": state["gold_lost"],
                    "gold_spent": state["gold_spent"],
                    "cards_gained": flatten_pipe(changes["cards_gained"]),
                    "cards_removed": flatten_pipe(changes["cards_removed"]),
                    "cards_transformed": flatten_pipe(changes["cards_transformed"]),
                    "upgraded_cards": flatten_pipe(changes["upgraded_cards"]),
                    "relics_picked": flatten_pipe([
                        item["choice"] for item in decisions["relic_choices"]
                        if item["was_picked"] and item["choice"]
                    ]),
                    "potions_picked": flatten_pipe([
                        item["choice"] for item in decisions["potion_choices"]
                        if item["was_picked"] and item["choice"]
                    ]),
                    "event_choices": flatten_pipe(event_choice_keys(stats.get("event_choices"))),
                    "ancient_chosen": flatten_pipe(
                        item["text_key"] for item in decisions["ancient_choices"]
                        if item["was_chosen"] and item["text_key"]
                    ),
                    "rest_site_choices": flatten_pipe(decisions["rest_site_choices"]),
                    "turns_taken": sum(int(room.get("turns_taken", 0) or 0) for room in rooms),
                }
            )

    map_type_counts: dict[str, int] = {}
    room_type_counts: dict[str, int] = {}
    for row in flat:
        map_type = str(row["map_point_type"])
        map_type_counts[map_type] = map_type_counts.get(map_type, 0) + 1
        for room_type in filter(None, row["room_types"].split("|")):
            room_type_counts[room_type] = room_type_counts.get(room_type, 0) + 1

    def total(field: str) -> int:
        return sum(int(row[field] or 0) for row in flat)

    summary = {
        "act_count": len(histories),
        "visited_map_points": len(floors),
        "floor_counts": [len(as_list(act)) for act in histories],
        "room_count": sum(len(floor["rooms"]) for floor in floors),
        "map_point_type_counts": dict(sorted(map_type_counts.items())),
        "room_type_counts": dict(sorted(room_type_counts.items())),
        "total_damage_taken": total("damage_taken"),
        "total_hp_healed": total("hp_healed"),
        "total_gold_gained": total("gold_gained"),
        "total_gold_spent": total("gold_spent"),
        "card_choice_count": sum(len(floor["state"]["decisions"]["card_choices"]) for floor in floors),
        "cards_picked_count": sum(
            sum(1 for item in floor["state"]["decisions"]["card_choices"] if item["was_picked"])
            for floor in floors
        ),
        "relic_choice_count": sum(len(floor["state"]["decisions"]["relic_choices"]) for floor in floors),
        "event_choice_count": sum(len(floor["state"]["decisions"]["event_choices"]) for floor in floors),
        "ancient_choice_count": sum(len(floor["state"]["decisions"]["ancient_choices"]) for floor in floors),
    }
    result = {
        "schema_version": "run-history-extract-v1",
        "source": {
            "path": str(source),
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
            "bytes": source.stat().st_size,
        },
        "extracted_at_utc": datetime.now(timezone.utc).isoformat(),
        "run": {
            key: data.get(key)
            for key in (
                "schema_version", "build_id", "seed", "ascension", "game_mode", "acts",
                "modifiers", "platform_type", "start_time", "run_time", "win", "was_abandoned",
                "killed_by_encounter", "killed_by_event",
            )
        },
        "final_player": (data.get("players") or [None])[0],
        "summary": summary,
        "acts": [
            {"act": index, "floor_count": len(as_list(act)), "floors": [floor for floor in floors if floor["act"] == index]}
            for index, act in enumerate(histories, start=1)
        ],
    }
    return result, flat


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--json-output", type=Path, required=True)
    parser.add_argument("--csv-output", type=Path, required=True)
    args = parser.parse_args()

    data = json.loads(args.input.read_text(encoding="utf-8"))
    result, rows = extract(data, args.input)
    args.json_output.parent.mkdir(parents=True, exist_ok=True)
    args.csv_output.parent.mkdir(parents=True, exist_ok=True)
    args.json_output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    with args.csv_output.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]) if rows else [])
        writer.writeheader()
        writer.writerows(rows)
    print(json.dumps({"json_output": str(args.json_output), "csv_output": str(args.csv_output), "summary": result["summary"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
