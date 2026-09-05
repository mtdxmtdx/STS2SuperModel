from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "training" / "run_joint_coverage_gate.py"
FEATURE_MANIFEST = ROOT / "data" / "combat_model" / "combat-feature-manifest.json"
TEST_OUTPUT = ROOT / "training" / "test-output" / "joint-coverage-gate"


def _row(index: int, *, potion: bool, multi_relic: bool, character: str) -> dict:
    relics = [{"id": "BURNING_BLOOD" if character == "The Ironclad" else "RING_OF_THE_SNAKE"}]
    if multi_relic:
        relics += [{"id": "DATA_DISK"}, {"id": "GORGET"}]
    return {
        "game_version": "v0.111.0",
        "game_commit": "41cef1ea",
        "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        "cli_protocol_version": "0.2.0",
        "trace_schema": 1,
        "feature_schema_version": "1",
        "episode_id": f"episode-{index}",
        "state_hash_public": f"state-{index}",
        "character": character,
        "confidence": "Reliable",
        "public_state": {
            "round": 1,
            "enemies": [{}],
            "player": {
                "name": character,
                "potions": [{"id": "FIRE_POTION"}] if potion else [],
                "relics": relics,
            },
        },
    }


def _run(case: str, rows: list[dict], *, expected_hash: str | None = None) -> subprocess.CompletedProcess[str]:
    case_dir = TEST_OUTPUT / case
    case_dir.mkdir(parents=True, exist_ok=True)
    dataset = case_dir / "data.jsonl"
    dataset.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    actual = hashlib.sha256(dataset.read_bytes()).hexdigest().upper()
    return subprocess.run(
        [
            sys.executable,
            str(SCRIPT),
            "--dataset-path",
            str(dataset),
            "--expected-sha256",
            expected_hash or actual,
            "--feature-manifest",
            str(FEATURE_MANIFEST),
            "--output",
            str(case_dir / "gate.json"),
        ],
        text=True,
        capture_output=True,
        check=False,
    )


def _boundary_rows() -> list[dict]:
    return [
        _row(
            index,
            potion=index < 4,
            multi_relic=index < 6,
            character="The Ironclad" if index < 10 else "The Silent",
        )
        for index in range(20)
    ]


def test_exact_integer_boundaries_pass() -> None:
    result = _run("boundary", _boundary_rows())
    assert result.returncode == 0, result.stdout + result.stderr


def test_potion_below_twenty_percent_exits_nonzero() -> None:
    rows = _boundary_rows()
    rows[3]["public_state"]["player"]["potions"] = []
    result = _run("potion-low", rows)
    assert result.returncode != 0
    assert "potion_coverage_below_20_percent" in result.stdout


def test_multi_relic_below_thirty_percent_exits_nonzero() -> None:
    rows = _boundary_rows()
    rows[5]["public_state"]["player"]["relics"] = [{"id": "BURNING_BLOOD"}]
    result = _run("relic-low", rows)
    assert result.returncode != 0
    assert "multi_relic_coverage_below_30_percent" in result.stdout


def test_character_out_of_range_exits_nonzero() -> None:
    rows = _boundary_rows()
    for index in (10, 11):
        rows[index]["character"] = "The Ironclad"
        rows[index]["public_state"]["player"]["name"] = "The Ironclad"
    result = _run("character-range", rows)
    assert result.returncode != 0
    assert "reliable_character_ratio_out_of_range" in result.stdout


def test_zero_reliable_exits_nonzero() -> None:
    rows = _boundary_rows()
    for row in rows:
        row["confidence"] = "Estimated"
    result = _run("zero-reliable", rows)
    assert result.returncode != 0
    assert "zero_reliable_rows" in result.stdout


def test_stale_hash_exits_nonzero() -> None:
    result = _run("stale-hash", _boundary_rows(), expected_hash="0" * 64)
    assert result.returncode != 0
    assert "dataset_sha256_mismatch" in result.stdout
