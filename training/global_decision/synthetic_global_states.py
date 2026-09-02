"""Deterministic, public-only Act 1 global prototype state generator."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import random
from typing import Any, Iterable, Mapping

from .contracts import GlobalActionCandidate, GlobalDatasetManifest, GlobalOfferSnapshot, GlobalRunStatePublic, canonical_json, stable_hash
from .reward_heuristic import rank_offer
from .stable_ids import action_id, card_instance_id, potion_instance_id, relic_instance_id


CARD_LIBRARY: tuple[dict[str, Any], ...] = (
    {"id": "STRIKE_R", "type": "Attack", "cost": 1, "tags": ["attack", "frontload"], "character": "both"},
    {"id": "DEFEND_R", "type": "Skill", "cost": 1, "tags": ["block", "mitigation"], "character": "both"},
    {"id": "BASH", "type": "Attack", "cost": 2, "tags": ["attack", "frontload"], "character": "Ironclad"},
    {"id": "POMMEL_STRIKE", "type": "Attack", "cost": 1, "tags": ["attack", "draw"], "character": "Ironclad"},
    {"id": "SHRUG_IT_OFF", "type": "Skill", "cost": 1, "tags": ["block", "draw"], "character": "Ironclad"},
    {"id": "INFLAME", "type": "Power", "cost": 1, "tags": ["scaling", "power"], "character": "Ironclad"},
    {"id": "TRUE_GRIT", "type": "Skill", "cost": 1, "tags": ["block", "exhaust"], "character": "Ironclad"},
    {"id": "WHIRLWIND", "type": "Attack", "cost": 1, "tags": ["attack", "aoe", "frontload"], "character": "Ironclad"},
    {"id": "NEUTRALIZE", "type": "Attack", "cost": 0, "tags": ["attack", "frontload", "mitigation"], "character": "Silent"},
    {"id": "SURVIVOR", "type": "Skill", "cost": 1, "tags": ["block", "discard", "select_discard"], "character": "Silent"},
    {"id": "BACKFLIP", "type": "Skill", "cost": 1, "tags": ["block", "draw"], "character": "Silent"},
    {"id": "DEADLY_POISON", "type": "Skill", "cost": 1, "tags": ["scaling", "status"], "character": "Silent"},
    {"id": "BLADE_DANCE", "type": "Skill", "cost": 1, "tags": ["attack", "aoe", "generated"], "character": "Silent"},
    {"id": "ACROBATICS", "type": "Skill", "cost": 1, "tags": ["draw", "discard", "select_discard"], "character": "Silent"},
    {"id": "FOOTWORK", "type": "Power", "cost": 1, "tags": ["scaling", "block", "power"], "character": "Silent"},
    {"id": "TACTICAL_DRAW", "type": "Skill", "cost": 0, "tags": ["draw", "energy"], "character": "both"},
    {"id": "EXHAUSTIVE_PLAN", "type": "Skill", "cost": 1, "tags": ["exhaust", "consume", "draw", "quest"], "character": "both"},
    {"id": "VOID_BURDEN", "type": "Skill", "cost": 2, "tags": ["void", "status", "curse"], "character": "both"},
    {"id": "SCOURING_WAVE", "type": "Attack", "cost": 2, "tags": ["attack", "aoe", "frontload"], "character": "both"},
    {"id": "RANDOM_DISCARD", "type": "Skill", "cost": 1, "tags": ["discard", "random_discard", "status"], "character": "both"},
    {"id": "CONSUMING_BOLT", "type": "Attack", "cost": 2, "tags": ["attack", "consume", "exhaust"], "character": "both"},
    {"id": "BATTLE_TRANCE", "type": "Skill", "cost": 0, "tags": ["draw", "energy"], "character": "Ironclad"},
)

RELIC_LIBRARY: tuple[str, ...] = ("BURNING_BLOOD", "RING_OF_THE_SNAKE", "BAG_OF_PREPARATION", "VAJRA", "AKABEKO")
POTION_LIBRARY: tuple[str, ...] = ("FIRE_POTION", "BLOCK_POTION", "ENERGY_POTION", "DEXTERITY_POTION")


def _rng_for(base_seed: int, index: int) -> random.Random:
    digest = hashlib.sha256(f"global-prototype:{base_seed}:{index}".encode("utf-8")).digest()
    return random.Random(int.from_bytes(digest[:8], "big"))


def _specs(character: str) -> list[dict[str, Any]]:
    return [dict(spec) for spec in CARD_LIBRARY if spec["character"] in {"both", character}]


def _deck(character: str, index: int, rng: random.Random) -> list[dict[str, Any]]:
    basics = ["STRIKE_R", "STRIKE_R", "STRIKE_R", "STRIKE_R", "DEFEND_R", "DEFEND_R", "DEFEND_R", "DEFEND_R"]
    if character == "Ironclad":
        basics.append("BASH")
        pool = ["POMMEL_STRIKE", "SHRUG_IT_OFF", "INFLAME", "TRUE_GRIT", "WHIRLWIND", "BATTLE_TRANCE"]
    else:
        basics.append("NEUTRALIZE")
        pool = ["SURVIVOR", "BACKFLIP", "DEADLY_POISON", "BLADE_DANCE", "ACROBATICS", "FOOTWORK"]
    extra_count = index % 8
    selected = [rng.choice(pool) for _ in range(extra_count)]
    names = basics + selected
    result: list[dict[str, Any]] = []
    ordinals: dict[str, int] = {}
    by_id = {spec["id"]: spec for spec in CARD_LIBRARY}
    for name in names:
        ordinal = ordinals.get(name, 0)
        ordinals[name] = ordinal + 1
        spec = by_id[name]
        result.append(
            {
                "card_instance_id": card_instance_id(name, ordinal),
                "semantic_id": name,
                "id": name,
                "upgrade_level": int((index + ordinal) % 5 == 0 and name not in {"STRIKE_R", "DEFEND_R"}),
                "enchantment_ids": [],
                "quest": "quest" in spec["tags"],
                "temporary": False,
                "generated": "generated" in spec["tags"],
                "tags": list(spec["tags"]),
                "cost": spec["cost"],
                "type": spec["type"].lower(),
            }
        )
    return result


def _visible_map(index: int, rng: random.Random) -> dict[str, Any]:
    branch = 2 + (index % 2)
    nodes = [{"id": "map:1:0:0", "row": 0, "col": 0, "type": "Start", "visible": True}]
    edges: list[list[str]] = []
    for col in range(branch):
        node_id = f"map:1:1:{col}"
        nodes.append({"id": node_id, "row": 1, "col": col, "type": "Combat" if col % 2 == 0 else "Event", "visible": True})
        edges.append(["map:1:0:0", node_id])
    return {"nodes": nodes, "edges": edges, "current": "map:1:0:0", "visible_rows": [0, 1], "randomized": bool(rng.randrange(2))}


def _offer(character: str, index: int, rng: random.Random) -> list[dict[str, Any]]:
    pool = _specs(character)
    rng.shuffle(pool)
    selected = pool[:3]
    return [
        {
            "offer_id": f"reward-{character.lower()}-{index:04d}-{slot}",
            "semantic_id": spec["id"],
            "id": spec["id"],
            "name": spec["id"],
            "type": spec["type"],
            "cost": spec["cost"],
            "rarity": "Common",
            "tags": list(spec["tags"]),
            "upgrade_level": 0,
            "enchantment_ids": [],
            "candidate_role": "offer",
            "index": slot,
        }
        for slot, spec in enumerate(selected)
    ]


def generate_row(index: int, *, base_seed: int = 20260831) -> dict[str, Any]:
    rng = _rng_for(base_seed, index)
    character = "Ironclad" if index % 2 == 0 else "Silent"
    ascension = (0, 5, 10)[index % 3]
    hp_ratio = (0.25, 0.55, 0.90)[index % 3]
    max_hp = 75.0 if character == "Ironclad" else 70.0
    hp = round(max_hp * hp_ratio, 3)
    gold = (35, 95, 185)[index % 3] + (index % 7) * 3
    deck = _deck(character, index, rng)
    relics = [{"relic_instance_id": relic_instance_id(name, 0), "semantic_id": name, "id": name, "counter": index % 3} for name in RELIC_LIBRARY[: index % 4]]
    potions = [{"potion_instance_id": potion_instance_id(name, slot), "semantic_id": name, "id": name, "slot": slot, "charges": 1} for slot, name in enumerate(POTION_LIBRARY[: index % 3])]
    visible_map = _visible_map(index, rng)
    encounter_profile = {
        "enemy_count": 1 if index % 4 < 3 else 3,
        "encounter_type": "single_normal" if index % 4 < 3 else "multi_normal",
        "known_from_public_history": True,
    }
    offers = _offer(character, index, rng)
    descriptors = [{"action_type": "reward", "semantic_id": item["semantic_id"], "offer_id": item["offer_id"]} for item in offers]
    descriptors.append({"action_type": "reward_skip", "semantic_id": None, "offer_id": None})
    snapshot_hash = stable_hash({"decision": "card_reward", "scenario": f"global-act1-{index:06d}", "candidates": descriptors})
    candidate_objects: list[GlobalActionCandidate] = []
    for slot, item in enumerate(offers):
        candidate_objects.append(
            GlobalActionCandidate(
                action_id=action_id("reward", snapshot_hash=snapshot_hash, offer_id=item["offer_id"]),
                action_type="reward",
                semantic_id=item["semantic_id"],
                transport_action="select_card_reward",
                transport_args={"index": slot},
                candidate_index=slot,
                offer_snapshot_hash=snapshot_hash,
                parent_decision_id=f"global-act1-{index:06d}",
                candidate_role="offer",
                source_confidence="synthetic",
            )
        )
    candidate_objects.append(
        GlobalActionCandidate(
            action_id=action_id("reward_skip"),
            action_type="reward_skip",
            semantic_id=None,
            transport_action="skip_card_reward",
            transport_args={},
            candidate_index=3,
            offer_snapshot_hash=snapshot_hash,
            parent_decision_id=f"global-act1-{index:06d}",
            candidate_role="skip",
            source_confidence="synthetic",
        )
    )
    action_dicts = [candidate.to_dict() | next((item for item in offers if item.get("offer_id") == candidate.action_id.split(":")[-1]), {}) for candidate in candidate_objects[:3]]
    action_dicts.append(candidate_objects[3].to_dict())
    public_base = {
        "schema_version": "global-public-v1",
        "character": character,
        "act": 1,
        "floor": index % 16,
        "current_node": "map:1:0:0",
        "current_room_type": "reward",
        "hp": hp,
        "max_hp": max_hp,
        "gold": gold,
        "ascension": ascension,
        "deck_public": deck,
        "relic_public": relics,
        "potion_public": potions,
        "visible_map_graph": visible_map,
        "visible_encounter_profile": encounter_profile,
        "visible_options": [],
        "visible_offers": offers + [{"action_type": "reward_skip", "candidate_role": "skip", "semantic_id": None}],
        "legal_actions": action_dicts,
        "public_history": [],
        "combat_summary": {"quality": "EstimatedByHeuristic", "source": "global-prototype-proxy"},
        "field_completeness": {"legal_actions_complete": True, "missing_fields": []},
        "provenance": {"source": "synthetic", "generator": "global-synthetic-v1", "future_masked": True},
        "legal_actions_complete": True,
    }
    public_base["state_public_hash"] = stable_hash(public_base)
    state = GlobalRunStatePublic.from_dict(public_base)
    snapshot = GlobalOfferSnapshot(
        offer_snapshot_hash=snapshot_hash,
        decision_type="card_reward",
        candidates=tuple(candidate_objects),
        candidate_order=tuple(candidate.action_id or "" for candidate in candidate_objects),
        visible_context_hash=stable_hash({"character": character, "act": 1, "floor": index % 16, "hp": hp, "gold": gold}),
        legal_actions_complete=True,
        source="synthetic",
    )
    ranked = rank_offer(state.to_dict(), [dict(item, action_id=candidate_objects[i].action_id) for i, item in enumerate(offers)] + [candidate_objects[3].to_dict()])
    labels = {item["action_id"]: {key: item[key] for key in ("score", "reason", "quality", "label_source", "rank")} for item in ranked}
    return {
        "schema_version": "global-prototype-v1",
        "stage": "prototype",
        "scenario_id": f"global-act1-{index:06d}",
        "state_public": state.to_dict(),
        "offer_snapshot": snapshot.to_dict(),
        "labels": labels,
        "label_source": "EstimatedByHeuristic",
        "quality": "EstimatedByHeuristic",
        "version": {
            "game_version": "v0.111.0",
            "game_branch": "beta",
            "cli_protocol_version": "0.2.0",
            "semantic_catalog_version": "runtime-v0.111",
            "feature_schema_version": "deck-feature-v1",
        },
    }


def generate_dataset(count: int = 1000, base_seed: int = 20260831) -> list[dict[str, Any]]:
    if count <= 0:
        raise ValueError("count must be positive")
    return [generate_row(index, base_seed=base_seed) for index in range(count)]


def write_dataset(rows: list[dict[str, Any]], output: Path, manifest_output: Path) -> dict[str, Any]:
    output.parent.mkdir(parents=True, exist_ok=True)
    lines = "".join(canonical_json(row) + "\n" for row in rows)
    output.write_text(lines, encoding="utf-8")
    action_count = sum(len(row["offer_snapshot"]["candidates"]) for row in rows)
    source_hash = stable_hash(CARD_LIBRARY)
    config_hash = stable_hash({"generator": "global-synthetic-v1", "count": len(rows)})
    manifest = GlobalDatasetManifest(
        dataset_id="global-prototype-act1-v0",
        schema_version="global-manifest-v1",
        stage="prototype",
        game_version="v0.111.0",
        game_branch="beta",
        game_commit="41cef1ea",
        assembly_sha256="0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        cli_protocol_version="0.2.0",
        simulator_version="global-synthetic-v1",
        semantic_catalog_version="runtime-v0.111",
        feature_schema_version="deck-feature-v1",
        model_version="global-prototype-v0",
        generator_config_hash=config_hash,
        feature_config_hash=stable_hash({"encoder": "deck-feature-v1"}),
        split_policy="scenario_id grouped; seed internal-only",
        row_count=len(rows),
        state_count=len(rows),
        action_count=action_count,
        reliable_count=0,
        estimated_count=action_count,
        uncalculable_count=0,
        source_hashes=(source_hash,),
        created_at_utc="2026-08-31T00:00:00Z",
        label_source="EstimatedByHeuristic",
        public_leakage_count=0,
        stable_id_missing=0,
        quality_counts={"EstimatedByHeuristic": action_count},
    )
    manifest_output.parent.mkdir(parents=True, exist_ok=True)
    manifest_output.write_text(canonical_json(manifest.to_dict()) + "\n", encoding="utf-8")
    return {"rows": len(rows), "actions": action_count, "output": str(output), "manifest": str(manifest_output)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--count", type=int, default=1000)
    parser.add_argument("--seed", type=int, default=20260831, help="internal deterministic generator seed; never emitted")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--manifest-output", type=Path, required=True)
    args = parser.parse_args()
    rows = generate_dataset(args.count, args.seed)
    print(json.dumps(write_dataset(rows, args.output, args.manifest_output), ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
