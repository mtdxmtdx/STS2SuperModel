#!/usr/bin/env python3
"""Build a version-gated manifest from newline-delimited training records.

The builder intentionally uses only the standard library so it can run before
PyArrow is installed. It rejects mixed game/CLI/schema metadata instead of
silently combining incompatible traces.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


EXPECTED = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
}
VERSION_METADATA = (
    "simulator_version",
    "scorer_version",
    "semantic_database_version",
    "feature_schema_version",
    "model_version",
)


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest().upper()


def build(input_paths: list[Path], output: Path, schema_version: int = 1) -> dict:
    metadata: dict[str, str] | None = None
    rows = states = actions = 0
    confidence = Counter()
    source_hashes: list[str] = []
    seen_states: set[str] = set()

    for path in input_paths:
        source_hashes.append(digest(path))
        with path.open(encoding="utf-8") as f:
            for line_no, line in enumerate(f, 1):
                if not line.strip():
                    continue
                record = json.loads(line)
                required_keys = (
                    *EXPECTED.keys(),
                    "trace_schema",
                    "schema_version",
                    *VERSION_METADATA,
                    "generator_config_hash",
                )
                missing = [key for key in required_keys if record.get(key) in (None, "")]
                if missing:
                    raise ValueError(f"{path}:{line_no}: missing required version metadata: {', '.join(missing)}")
                current = {
                    key: record.get(key)
                    for key in (*EXPECTED.keys(), "trace_schema", "schema_version", *VERSION_METADATA, "generator_config_hash")
                    if record.get(key) is not None
                }
                for key, expected in EXPECTED.items():
                    if key in current and current[key] != expected:
                        raise ValueError(f"{path}:{line_no}: {key}={current[key]!r} does not match {expected!r}")
                if record.get("trace_schema") != 1:
                    raise ValueError(f"{path}:{line_no}: unsupported trace_schema={record.get('trace_schema')!r}")
                if metadata is None:
                    metadata = current
                else:
                    for key, value in current.items():
                        if metadata.get(key) not in (None, value):
                            raise ValueError(f"mixed metadata for {key}: {metadata[key]!r} vs {value!r}")
                rows += 1
                state_hash = record.get("state_hash_public") or record.get("public_state_hash") or record.get("post_state_hash")
                if state_hash:
                    seen_states.add(state_hash)
                actions += len(record.get("legal_actions", []) or [])
                level = record.get("confidence") or record.get("label_quality") or "Unknown"
                confidence[str(level)] += 1

    metadata = metadata or {}
    result = {
        "dataset_id": output.stem,
        "schema_version": schema_version,
        "game_version": metadata.get("game_version", EXPECTED["game_version"]),
        "game_commit": metadata.get("game_commit", EXPECTED["game_commit"]),
        "assembly_sha256": metadata.get("assembly_sha256", EXPECTED["assembly_sha256"]),
        "cli_protocol_version": metadata.get("cli_protocol_version", EXPECTED["cli_protocol_version"]),
        "simulator_version": metadata.get("simulator_version", "unknown"),
        "scorer_version": metadata.get("scorer_version", "unknown"),
        "semantic_database_version": metadata.get("semantic_database_version", "unknown"),
        "feature_schema_version": metadata.get("feature_schema_version", "unknown"),
        "model_version": metadata.get("model_version", "none"),
        "generator_config_hash": metadata.get("generator_config_hash", "unknown"),
        "feature_config_hash": metadata.get("generator_config_hash", "unknown"),
        "split_policy": "episode_or_seed_group",
        "row_count": rows,
        "state_count": len(seen_states),
        "action_count": actions,
        "reliable_count": confidence.get("Reliable", 0),
        "estimated_count": confidence.get("Estimated", 0),
        "uncalculable_count": confidence.get("Uncalculable", 0),
        "confidence_counts": dict(sorted(confidence.items())),
        "source_hashes": sorted(source_hashes),
        "created_at_utc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", action="append", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.input, args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
