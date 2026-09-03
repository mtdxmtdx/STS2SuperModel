#!/usr/bin/env python3
"""Materialize unordered public pile identities from version-locked audit traces.

The source teacher snapshot is used only to recover card identities that the
game UI exposes publicly. Output piles are sorted canonically, and all RNG
state/order information is discarded before the normal NOSL adapter sees it.
"""

from __future__ import annotations

import argparse
import copy
import json
from collections import Counter
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "training" / "collectors"))
from snapshot_adapter import build_nosl_belief_state  # noqa: E402


def model_id(card: dict) -> str:
    return str(card.get("id", card.get("model_id", "UNKNOWN"))).removeprefix("CARD.")


def semantic_multiset(values: list[dict]) -> list[dict]:
    counts = Counter(
        (model_id(value), bool(value.get("upgraded", False)))
        for value in values
        if isinstance(value, dict)
    )
    return [
        {"model_id": card_id, "upgraded": upgraded, "count": count}
        for (card_id, upgraded), count in sorted(counts.items())
    ]


def materialize(row: dict) -> dict:
    output = copy.deepcopy(row)
    public = output.get("public_state") or {}
    audit = output.get("teacher_snapshot") or {}
    if any(not isinstance(audit.get(zone), list)
           for zone in ("draw_pile", "discard_pile", "exhaust_pile")):
        output["public_state"] = public
        output["state_hash_public_legacy"] = output.get("state_hash_public")
        output["state_hash_public"] = build_nosl_belief_state(public).belief_signature
        output["public_pile_materialization"] = {
            "source": "audit_snapshot_unavailable",
            "canonical_order": None,
            "rng_fields_copied": False,
            "schema_version": 1,
        }
        return output

    for zone in ("draw_pile", "discard_pile", "exhaust_pile"):
        values = [value for value in audit.get(zone, []) if isinstance(value, dict)]
        public.pop(zone, None)
        public[f"{zone}_multiset"] = semantic_multiset(values)
        public[f"{zone}_count"] = len(values)

    output["public_state"] = public
    output["state_hash_public_legacy"] = output.get("state_hash_public")
    output["state_hash_public"] = build_nosl_belief_state(public).belief_signature
    output["public_pile_materialization"] = {
        "source": "teacher_snapshot_public_zone_identities_only",
        "canonical_order": "model_id,upgraded",
        "rng_fields_copied": False,
        "schema_version": 1,
    }
    return output


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.input.read_text(encoding="utf-8").splitlines() if line.strip()]
    converted = [materialize(row) for row in rows]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":"))
                                      for row in converted) + "\n", encoding="utf-8")
    print(json.dumps({"rows": len(converted), "output": str(args.output)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
