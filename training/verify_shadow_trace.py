#!/usr/bin/env python3
"""Compare an engine trace with a shadow trace without assuming identical JSON."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def load(path: Path) -> list[dict]:
    with path.open(encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def verify(engine: list[dict], shadow: list[dict], compare_hashes: bool = True) -> dict:
    mismatches = []

    def compare_observation(index: int, left: dict, right: dict) -> None:
        left_obs = left.get("public_observation") or {}
        right_obs = right.get("public_observation") or {}
        for path in (
            ("round",),
            ("energy",),
            ("max_energy",),
            ("player", "hp"),
            ("player", "block"),
            ("draw_pile_count",),
            ("discard_pile_count",),
        ):
            def get(root: dict):
                value = root
                for key in path:
                    if not isinstance(value, dict):
                        return None
                    value = value.get(key)
                return value
            if get(left_obs) != get(right_obs):
                mismatches.append({"step": index, "field": "public_observation." + ".".join(path), "engine": get(left_obs), "shadow": get(right_obs)})

        def ids(root: dict, key: str) -> list[str]:
            values = root.get(key, []) or []
            return [str(item.get("action_id") or item.get("instance_id") or item.get("id") or item.get("name")) for item in values if isinstance(item, dict)]
        for key in ("hand", "action_candidates"):
            if ids(left_obs, key) != ids(right_obs, key):
                mismatches.append({"step": index, "field": f"public_observation.{key}", "engine": ids(left_obs, key), "shadow": ids(right_obs, key)})
        for key in ("relics", "player_powers", "enemies"):
            if ids(left_obs.get("player", {}) if key == "relics" else left_obs, key) != ids(right_obs.get("player", {}) if key == "relics" else right_obs, key):
                mismatches.append({"step": index, "field": f"public_observation.{key}", "engine": ids(left_obs.get("player", {}) if key == "relics" else left_obs, key), "shadow": ids(right_obs.get("player", {}) if key == "relics" else right_obs, key)})

    for index, (left, right) in enumerate(zip(engine, shadow)):
        fields = ("step", "normalized_action_id", "pre_state_hash", "post_state_hash") if compare_hashes else ("step", "normalized_action_id")
        for field in fields:
            if left.get(field) != right.get(field):
                mismatches.append({"step": index, "field": field, "engine": left.get(field), "shadow": right.get(field)})
        if left.get("rng_before") != right.get("rng_before") or left.get("rng_after") != right.get("rng_after"):
            mismatches.append({"step": index, "field": "rng", "engine": [left.get("rng_before"), left.get("rng_after")], "shadow": [right.get("rng_before"), right.get("rng_after")]})
        compare_observation(index, left, right)
    if len(engine) != len(shadow):
        mismatches.append({"field": "row_count", "engine": len(engine), "shadow": len(shadow)})
    hash_mismatches = []
    for index, (left, right) in enumerate(zip(engine, shadow)):
        for field in ("pre_state_hash", "post_state_hash"):
            if left.get(field) != right.get(field):
                hash_mismatches.append({"step": index, "field": field, "engine": left.get(field), "shadow": right.get(field)})
    hash_comparable = compare_hashes
    return {
        "match": not mismatches,
        "projection_match": not [item for item in mismatches if item.get("field", "").startswith("public_observation.")],
        "hash_comparable": hash_comparable,
        "hash_match": (not hash_mismatches) if hash_comparable else None,
        "engine_rows": len(engine), "shadow_rows": len(shadow),
        "mismatches": mismatches,
        "hash_mismatches": hash_mismatches if not compare_hashes else [],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", required=True, type=Path)
    parser.add_argument("--shadow", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--projection-only", action="store_true", help="compare observable state without requiring identical full-state hashes")
    args = parser.parse_args()
    result = verify(load(args.engine), load(args.shadow), compare_hashes=not args.projection_only)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0 if result["match"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
