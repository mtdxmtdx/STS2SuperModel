from __future__ import annotations

import hashlib
import json

from training.global_decision.contracts import canonical_json
from training.global_decision.synthetic_global_states import generate_dataset


def _digest(rows: list[dict]) -> str:
    text = "".join(canonical_json(row) + "\n" for row in rows).encode("utf-8")
    return hashlib.sha256(text).hexdigest()


def test_synthetic_small_coverage_and_public_only() -> None:
    rows = generate_dataset(32, 20260831)
    assert len(rows) == 32
    assert {row["state_public"]["character"] for row in rows} == {"Ironclad", "Silent"}
    assert {row["state_public"]["visible_encounter_profile"]["enemy_count"] for row in rows} == {1, 3}
    assert {row["quality"] for row in rows} == {"EstimatedByHeuristic"}
    for row in rows:
        assert len(row["offer_snapshot"]["candidates"]) == 4
        assert row["offer_snapshot"]["candidates"][-1]["action_id"] == "reward:skip"
        text = json.dumps(row, ensure_ascii=False).lower()
        assert '"seed"' not in text
        assert '"run_seed"' not in text
        assert '"future_draw_order"' not in text


def test_synthetic_1000_repeat_hash() -> None:
    first = generate_dataset(1000, 20260831)
    second = generate_dataset(1000, 20260831)
    assert _digest(first) == _digest(second)
    assert sum(len(row["offer_snapshot"]["candidates"]) for row in first) == 4000
    assert sum(row["quality"] == "Reliable" for row in first) == 0
