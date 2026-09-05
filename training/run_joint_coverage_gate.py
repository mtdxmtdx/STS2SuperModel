#!/usr/bin/env python3
"""Validate the Line-D joint Reliable coverage contract from its JSONL source."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


GAME_VERSION = "v0.111.0"
GAME_COMMIT = "41cef1ea"
ASSEMBLY_SHA256 = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
CLI_PROTOCOL_VERSION = "0.2.0"
TRACE_SCHEMA = 1
FEATURE_SCHEMA_VERSION = "1"
FEATURE_CONTRACT = "combat-feature-v1"
STARTER_RELIC_IDS = {"BURNING_BLOOD", "RING_OF_THE_SNAKE"}
EXPECTED_CHARACTERS = ("The Ironclad", "The Silent")


def _held_potions(player: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    for potion in player.get("potions") or []:
        if not isinstance(potion, dict):
            continue
        potion_id = str(potion.get("id") or "").strip().upper()
        if potion_id and potion_id not in {"EMPTY", "NONE"}:
            result.append(potion)
    return result


def _nonstarter_relic_count(player: dict[str, Any]) -> int:
    count = 0
    for relic in player.get("relics") or []:
        if not isinstance(relic, dict):
            continue
        relic_id = str(relic.get("id") or "").strip().upper()
        if relic_id and relic_id not in STARTER_RELIC_IDS:
            count += 1
    return count


def evaluate(dataset_path: Path, expected_sha256: str, feature_manifest: Path) -> dict[str, Any]:
    digest = hashlib.sha256()
    all_characters: Counter[str] = Counter()
    reliable_characters: Counter[str] = Counter()
    version_mismatch_counts: Counter[str] = Counter()
    malformed_lines: list[dict[str, Any]] = []
    rows = reliable = potion_rows = multi_relic_rows = 0
    round_ge_8 = enemy_count_ge_3 = act_ge_2 = 0

    with dataset_path.open("rb") as stream:
        for line_no, raw in enumerate(stream, start=1):
            digest.update(raw)
            if not raw.strip():
                continue
            try:
                row = json.loads(raw)
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                malformed_lines.append({"line": line_no, "error": str(exc)})
                continue
            rows += 1
            state = row.get("public_state") or {}
            player = state.get("player") or {}
            character = str(row.get("character") or player.get("name") or "Unknown")
            all_characters[character] += 1
            expected_versions = {
                "game_version": GAME_VERSION,
                "game_commit": GAME_COMMIT,
                "assembly_sha256": ASSEMBLY_SHA256,
                "cli_protocol_version": CLI_PROTOCOL_VERSION,
                "trace_schema": TRACE_SCHEMA,
                "feature_schema_version": FEATURE_SCHEMA_VERSION,
            }
            for field, expected in expected_versions.items():
                if row.get(field) != expected:
                    version_mismatch_counts[field] += 1
            if row.get("confidence") != "Reliable":
                continue
            reliable += 1
            reliable_characters[character] += 1
            potion_rows += int(bool(_held_potions(player)))
            multi_relic_rows += int(_nonstarter_relic_count(player) >= 2)
            round_ge_8 += int(int(row.get("round") or state.get("round") or 0) >= 8)
            enemy_count_ge_3 += int(len(state.get("enemies") or []) >= 3)
            act_ge_2 += int(int(row.get("act") or (state.get("context") or {}).get("act") or 0) >= 2)

    actual_sha256 = digest.hexdigest().upper()
    expected_sha256 = expected_sha256.upper()
    feature_payload = json.loads(feature_manifest.read_text(encoding="utf-8"))
    feature_sha256 = hashlib.sha256(feature_manifest.read_bytes()).hexdigest().upper()
    failures: list[str] = []
    if actual_sha256 != expected_sha256:
        failures.append("dataset_sha256_mismatch")
    if feature_payload.get("feature_schema_version") != FEATURE_CONTRACT:
        failures.append("feature_contract_mismatch")
    if malformed_lines:
        failures.append("malformed_lines")
    if version_mismatch_counts:
        failures.append("version_lock_mismatch")
    if reliable == 0:
        failures.append("zero_reliable_rows")
    else:
        if 5 * potion_rows < reliable:
            failures.append("potion_coverage_below_20_percent")
        if 10 * multi_relic_rows < 3 * reliable:
            failures.append("multi_relic_coverage_below_30_percent")
        if set(reliable_characters) != set(EXPECTED_CHARACTERS):
            failures.append("reliable_character_set_mismatch")
        for character in EXPECTED_CHARACTERS:
            count = reliable_characters[character]
            if 20 * count < 9 * reliable or 20 * count > 11 * reliable:
                failures.append(f"reliable_character_ratio_out_of_range:{character}")

    def ratio(value: int, denominator: int) -> float:
        return value / denominator if denominator else 0.0

    return {
        "schema_version": 1,
        "verdict": "pass" if not failures else "fail",
        "dataset_path": str(dataset_path.resolve()),
        "dataset_sha256": actual_sha256,
        "expected_dataset_sha256": expected_sha256,
        "row_count": rows,
        "reliable_denominator": reliable,
        "reliable_with_potions": potion_rows,
        "reliable_with_potions_ratio": ratio(potion_rows, reliable),
        "reliable_with_two_nonstarter_relics": multi_relic_rows,
        "reliable_with_two_nonstarter_relics_ratio": ratio(multi_relic_rows, reliable),
        "integer_checks": {
            "five_times_p_ge_n": 5 * potion_rows >= reliable and reliable > 0,
            "ten_times_g_ge_three_n": 10 * multi_relic_rows >= 3 * reliable and reliable > 0,
        },
        "reliable_character_distribution": {
            character: {
                "rows": reliable_characters[character],
                "ratio": ratio(reliable_characters[character], reliable),
            }
            for character in sorted(reliable_characters)
        },
        "all_row_character_distribution": {
            character: {"rows": count, "ratio": ratio(count, rows)}
            for character, count in sorted(all_characters.items())
        },
        "soft_slices": {
            "round_ge_8": {"rows": round_ge_8, "ratio": ratio(round_ge_8, reliable)},
            "enemy_count_ge_3": {"rows": enemy_count_ge_3, "ratio": ratio(enemy_count_ge_3, reliable)},
            "act_ge_2": {"rows": act_ge_2, "ratio": ratio(act_ge_2, reliable)},
        },
        "malformed_lines": malformed_lines[:20],
        "version_mismatch_counts": dict(sorted(version_mismatch_counts.items())),
        "version_lock": {
            "game_version": GAME_VERSION,
            "game_commit": GAME_COMMIT,
            "assembly_sha256": ASSEMBLY_SHA256,
            "cli_protocol_version": CLI_PROTOCOL_VERSION,
            "trace_schema": TRACE_SCHEMA,
            "feature_schema_version": FEATURE_SCHEMA_VERSION,
            "feature_contract": FEATURE_CONTRACT,
            "feature_manifest": str(feature_manifest.resolve()),
            "feature_manifest_sha256": feature_sha256,
        },
        "failures": failures,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset-path", required=True, type=Path)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--feature-manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = evaluate(args.dataset_path, args.expected_sha256, args.feature_manifest)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "verdict": result["verdict"],
        "N": result["reliable_denominator"],
        "P": result["reliable_with_potions"],
        "G": result["reliable_with_two_nonstarter_relics"],
        "failures": result["failures"],
        "output": str(args.output),
    }, ensure_ascii=False))
    return 0 if result["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
