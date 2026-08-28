#!/usr/bin/env python3
"""Normalize CLI trace JSONL into versioned training decision records.

Only rows that contain a public observation become decision records. Teacher
payloads are attached from the following teacher snapshot row for the same
trace. Missing teacher labels are explicitly marked Uncalculable.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
}

GENERATOR_CONFIG = {
    "name": "trace_to_training",
    "schema_version": 1,
    "trace_schema": 1,
    "teacher_pairing": "public.post_state_hash==teacher.pre_state_hash",
    "missing_teacher_confidence": "Uncalculable",
    "teacher_without_label_confidence": "LowConfidence",
    "action_identity": "stable_instance_id",
}


def stable_id(*parts: object) -> str:
    raw = "|".join(str(part or "") for part in parts)
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()[:24]


def config_hash(config: dict) -> str:
    encoded = json.dumps(config, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


GENERATOR_CONFIG_HASH = config_hash(GENERATOR_CONFIG)


def observation_key(observation: object) -> str:
    """Hash a public observation independently of trace command bookkeeping."""
    encoded = json.dumps(observation, sort_keys=True, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


def validate_lock(row: dict, line_no: int) -> None:
    for key, expected in LOCK.items():
        if row.get(key) != expected:
            raise ValueError(f"line {line_no}: {key}={row.get(key)!r}, expected {expected!r}")
    if row.get("trace_schema") != 1:
        raise ValueError(f"line {line_no}: unsupported trace_schema={row.get('trace_schema')!r}")


def action_candidates(observation: dict) -> list[dict]:
    candidates: list[dict] = []
    for card in observation.get("hand", []) or []:
        if "index" not in card:
            continue
        instance = card.get("instance_id")
        if not instance:
            raise ValueError(f"card at hand index {card['index']} is missing stable instance_id")
        candidates.append({
            "kind": "PlayCard",
            "action_id": f"play:{card.get('id', card.get('name', 'UNKNOWN'))}:{instance}",
            "source_model_id": card.get("id"),
            "source_instance_id": instance,
            "effective_energy_cost": card.get("cost", 0),
            "legal": bool(card.get("can_play", False)),
            "restriction": None if card.get("can_play", False) else "engine_reported_not_playable",
        })
    for potion in observation.get("player", {}).get("potions", []) or []:
        if "index" not in potion:
            continue
        instance = potion.get("instance_id")
        if not instance:
            raise ValueError(f"potion at slot {potion['index']} is missing stable instance_id")
        candidates.append({
            "kind": "UsePotion",
            "action_id": f"potion:{potion.get('id', potion.get('name', 'UNKNOWN'))}:{instance}",
            "source_model_id": potion.get("id"),
            "source_instance_id": instance,
            "effective_energy_cost": 0,
            "legal": True,
        })
    candidates.append({
        "kind": "EndTurn",
        "action_id": "end_turn",
        "effective_energy_cost": 0,
        "legal": True,
    })
    return candidates


def validate_action_candidates(candidates: list[dict]) -> None:
    for index, candidate in enumerate(candidates):
        kind = candidate.get("kind")
        if kind in ("PlayCard", "UsePotion") and not candidate.get("source_instance_id"):
            raise ValueError(f"action candidate {index} ({kind}) is missing stable source_instance_id")
        if kind == "Choice":
            if not candidate.get("choice_id"):
                raise ValueError(f"action candidate {index} (Choice) is missing stable choice_id")
            if not isinstance(candidate.get("selected_card_instance_ids"), list):
                raise ValueError(f"action candidate {index} (Choice) is missing selected_card_instance_ids")


def normalize(rows: list[dict]) -> list[dict]:
    teacher_by_observation: dict[str, list[tuple[int, dict]]] = {}
    teacher_by_pre_hash: dict[str, list[tuple[int, dict]]] = {}
    for index, row in enumerate(rows):
        if row.get("teacher_snapshot") is None:
            continue
        public_observation = row.get("public_observation")
        if isinstance(public_observation, dict):
            teacher_by_observation.setdefault(observation_key(public_observation), []).append((index, row))
        pre_hash = row.get("pre_state_hash")
        if pre_hash:
            teacher_by_pre_hash.setdefault(str(pre_hash), []).append((index, row))
    records: list[dict] = []
    for line_no, row in enumerate(rows, 1):
        validate_lock(row, line_no)
        trace_id = row.get("trace_id", "unknown")
        if row.get("observation_view") == "teacher":
            continue
        public = row.get("public_observation")
        if not isinstance(public, dict):
            continue
        teacher_row = None
        public_key = observation_key(public)
        candidates = teacher_by_observation.get(public_key, [])
        row_index = line_no - 1
        if candidates:
            # Prefer a teacher query at or after this public decision point;
            # this is the ordering emitted by the teacher matrix collector.
            teacher_row = min(candidates, key=lambda item: (item[0] < row_index, abs(item[0] - row_index)))[1]
        if teacher_row is None:
            legacy = teacher_by_pre_hash.get(row.get("post_state_hash", ""), [])
            if legacy:
                teacher_row = min(legacy, key=lambda item: abs(item[0] - row_index))[1]
        teacher_hash = stable_id(trace_id, row.get("post_state_hash"), "teacher-missing")
        confidence = "Uncalculable"
        if teacher_row is not None:
            teacher_hash = stable_id(trace_id, teacher_row.get("post_state_hash"), json.dumps(teacher_row.get("teacher_snapshot"), sort_keys=True))
            teacher_snapshot = teacher_row.get("teacher_snapshot") or {}
            if teacher_snapshot.get("available") is True and teacher_snapshot.get("rng_raw_words_exposed") is False:
                confidence = "LowConfidence"
        legal_actions = public.get("action_candidates") or action_candidates(public)
        validate_action_candidates(legal_actions)
        # Training records embed a schema-complete public state.  Some CLI
        # event snapshots carry legal actions at the trace level only.
        if "action_candidates" not in public:
            public["action_candidates"] = legal_actions
        record = {
            "record_id": stable_id(trace_id, row.get("step"), row.get("post_state_hash")),
            "schema_version": 1,
            "trace_schema": 1,
            **LOCK,
            "simulator_version": row.get("simulator_version", "unknown"),
            "scorer_version": row.get("scorer_version", "unknown"),
            "semantic_database_version": row.get("semantic_database_version", "unknown"),
            "feature_schema_version": row.get("feature_schema_version", "unknown"),
            "model_version": row.get("model_version", "none"),
            "generator_config_hash": GENERATOR_CONFIG_HASH,
            "episode_id": trace_id,
            "character": public.get("player", {}).get("name", "unknown"),
            "ascension": public.get("context", {}).get("ascension", 0),
            "act": public.get("context", {}).get("act", 0),
            "floor": public.get("context", {}).get("floor", 0),
            "combat_id": stable_id(trace_id, public.get("context", {}).get("floor"), "combat"),
            "round": public.get("round", 0),
            "state_hash_public": row.get("post_state_hash"),
            "state_hash_teacher": teacher_hash,
            "public_state": public,
            "teacher_state_reference": teacher_row.get("post_state_hash") if teacher_row else None,
            "teacher_snapshot": teacher_row.get("teacher_snapshot") if teacher_row else None,
            "legal_actions": legal_actions,
            "teacher_best_actions": [],
            "action_values": {},
            "confidence": confidence,
            "search_complete": False,
            "risk_events": ["teacher_snapshot_missing"] if teacher_row is None else ["teacher_label_missing"],
            "provenance": {"trace_id": trace_id, "trace_step": row.get("step")},
        }
        records.append(record)
    return records


def convert(input_path: Path, output_path: Path) -> int:
    with input_path.open(encoding="utf-8") as handle:
        rows = [json.loads(line) for line in handle if line.strip()]
    records = normalize(rows)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
    return len(records)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(convert(args.input, args.output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
