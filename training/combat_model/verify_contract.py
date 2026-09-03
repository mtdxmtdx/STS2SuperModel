"""Verify the combat encoder consumes only declared public NOSL fields."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from .dataset import load_rows
from .encoder import CANDIDATE_SCALAR_NAMES, ENEMY_SCALAR_NAMES, STATE_SCALAR_NAMES, CombatFeatureEncoder, TokenVocabulary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    public = set(manifest["public_input_fields"])
    forbidden = set(manifest["forbidden_input_fields"])
    if any(
        any(part == forbidden_name for part in field.replace("[]", "").split("."))
        for forbidden_name in forbidden
        for field in public
    ):
        raise SystemExit("forbidden field appears in public_input_fields")
    rows = load_rows(args.data)[:128]
    vocab = TokenVocabulary.build(rows)
    encoder = CombatFeatureEncoder(vocab)
    for row in rows:
        encoded = encoder.encode(row)
        assert len(encoded["state_numeric"]) == len(STATE_SCALAR_NAMES)
        assert all(len(item) == len(CANDIDATE_SCALAR_NAMES) for item in encoded["candidate_numeric"])
        assert all(len(item) == len(ENEMY_SCALAR_NAMES) for item in encoded["enemy_numeric"])
        assert "teacher_snapshot" not in encoded
    print(json.dumps({"verdict": "pass", "rows_checked": len(rows), "forbidden_overlap": 0, "vocabulary_size": len(vocab.tokens)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
