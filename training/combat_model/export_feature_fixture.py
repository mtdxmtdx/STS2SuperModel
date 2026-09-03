"""Emit a public-only fixture for independent C#/Python encoder parity."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from .dataset import load_rows
from .encoder import CombatFeatureEncoder, TokenVocabulary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    row = load_rows(args.data)[0]
    vocabulary = TokenVocabulary.from_dict(json.loads(args.vocabulary.read_text(encoding="utf-8")))
    encoded = CombatFeatureEncoder(vocabulary).encode(row)
    public_row = {
        key: row[key]
        for key in ("character", "ascension", "act", "floor", "round", "state_hash_public", "public_state", "legal_actions")
    }
    fixture = {
        "schema_version": 1,
        "feature_schema_version": "combat-feature-v1",
        "row": public_row,
        "vocabulary": vocabulary.to_dict(),
        "expected": encoded,
    }
    serialized = json.dumps(fixture, ensure_ascii=False, separators=(",", ":"))
    forbidden = ("teacher_snapshot", "rng_raw_words", "future_draw_order", "ordered_draw_pile")
    if any(name in serialized for name in forbidden):
        raise RuntimeError("feature fixture contains a forbidden hidden field")
    args.output.write_text(serialized + "\n", encoding="utf-8")
    print(json.dumps({"verdict": "pass", "state_hash_public": row["state_hash_public"], "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
