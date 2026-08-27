#!/usr/bin/env python3
"""Deterministically split normalized training JSONL by episode/trace group."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import defaultdict
from pathlib import Path


SPLITS = ("train", "validation", "test", "challenge")
VERSION_KEYS = (
    "game_version",
    "game_commit",
    "assembly_sha256",
    "cli_protocol_version",
    "simulator_version",
    "semantic_database_version",
    "scorer_version",
    "feature_schema_version",
    "model_version",
    "generator_config_hash",
)


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def split_for(group: str) -> str:
    bucket = int.from_bytes(hashlib.sha256(group.encode("utf-8")).digest()[:4], "big") % 100
    if bucket < 10:
        return "challenge"
    if bucket < 30:
        return "test"
    if bucket < 50:
        return "validation"
    return "train"


def split(input_path: Path, output_dir: Path) -> dict:
    groups: dict[str, list[dict]] = defaultdict(list)
    metadata: dict[str, object] | None = None
    with input_path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            current = {key: row.get(key) for key in VERSION_KEYS}
            missing = [key for key, value in current.items() if value in (None, "")]
            if missing:
                raise ValueError(f"{input_path}:{line_no}: missing metadata: {', '.join(missing)}")
            if metadata is None:
                metadata = current
            elif current != metadata:
                changed = [key for key in VERSION_KEYS if current[key] != metadata[key]]
                raise ValueError(f"{input_path}:{line_no}: mixed metadata: {', '.join(changed)}")
            group = str(row.get("episode_id") or row.get("trace_id") or row.get("provenance", {}).get("trace_id") or f"row-{line_no}")
            groups[group].append(row)

    rows_by_split: dict[str, list[dict]] = {name: [] for name in SPLITS}
    groups_by_split: dict[str, list[str]] = {name: [] for name in SPLITS}
    for group in sorted(groups):
        name = split_for(group)
        groups_by_split[name].append(group)
        rows_by_split[name].extend(groups[group])

    output_dir.mkdir(parents=True, exist_ok=True)
    manifests = {}
    for name in SPLITS:
        path = output_dir / f"{name}.jsonl"
        with path.open("w", encoding="utf-8") as handle:
            for row in rows_by_split[name]:
                handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
        manifests[name] = {
            "split": name,
            "row_count": len(rows_by_split[name]),
            "group_count": len(groups_by_split[name]),
            "groups_sha256": hashlib.sha256("\n".join(groups_by_split[name]).encode("utf-8")).hexdigest().upper(),
            "source": str(input_path),
            "source_sha256": digest(input_path),
            "output_sha256": digest(path),
            "byte_count": path.stat().st_size,
            "storage_format": "jsonl",
            **(metadata or {}),
        }
        (output_dir / f"{name}.manifest.json").write_text(json.dumps(manifests[name], indent=2) + "\n", encoding="utf-8")
    summary = {
        "schema_version": 1,
        "source": str(input_path),
        "source_sha256": digest(input_path),
        "split_policy": "sha256(episode_id|trace_id)%100: challenge<10, test<30, validation<50, train>=50",
        "total_rows": sum(len(rows) for rows in rows_by_split.values()),
        "total_groups": len(groups),
        "splits": manifests,
        **(metadata or {}),
    }
    (output_dir / "split-manifest.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return summary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(split(args.input, args.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
