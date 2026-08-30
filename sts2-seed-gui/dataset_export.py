"""Deterministic group split for global behavior JSONL exports.

The splitter keeps one run/episode/branch group in exactly one split.  It is
deliberately dependency-free; Parquet conversion can be added by the training
repository without changing the annotation records.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


def _group_key(record: dict[str, Any]) -> str:
    context = str(record.get("run_context_hash") or "missing-context")
    episode = str(record.get("episode_id") or "missing-episode")
    seed = str(record.get("run_seed") or (record.get("context") or {}).get("run_seed") or "missing-seed")
    branch = str(record.get("branch_id") or "main")
    return "|".join((context, episode, seed, branch))


def _split_for_group(group: str, train: float, validation: float) -> str:
    value = int(hashlib.sha256(group.encode("utf-8")).hexdigest()[:16], 16) / float(16**16)
    if value < train:
        return "train"
    if value < train + validation:
        return "validation"
    return "test"


def split_records(records: list[dict[str, Any]], train: float = 0.8, validation: float = 0.1) -> dict[str, list[dict[str, Any]]]:
    if train < 0 or validation < 0 or train + validation > 1:
        raise ValueError("train and validation fractions must be non-negative and sum to at most 1")
    result: dict[str, list[dict[str, Any]]] = {"train": [], "validation": [], "test": []}
    for record in records:
        item = dict(record)
        item["split_group"] = _group_key(record)
        item["split"] = _split_for_group(item["split_group"], train, validation)
        result[item["split"]].append(item)
    return result


def export_dataset(input_path: str | Path, output_dir: str | Path, train: float = 0.8, validation: float = 0.1) -> dict[str, Any]:
    source = Path(input_path)
    output = Path(output_dir)
    output.mkdir(parents=True, exist_ok=True)
    records = [json.loads(line) for line in source.read_text(encoding="utf-8").splitlines() if line.strip()]
    if any(not isinstance(item, dict) for item in records):
        raise ValueError("every JSONL row must be an object")
    splits = split_records(records, train, validation)
    paths: dict[str, str] = {}
    for name, rows in splits.items():
        path = output / f"global_behavior.{name}.jsonl"
        path.write_text("".join(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n" for row in rows), encoding="utf-8")
        paths[name] = str(path)
    groups = {name: {_group_key(row) for row in rows} for name, rows in splits.items()}
    overlap = sorted((groups["train"] & groups["validation"]) | (groups["train"] & groups["test"]) | (groups["validation"] & groups["test"]))
    manifest = {
        "manifest_version": "global-behavior-split-manifest-v1",
        "input_path": str(source),
        "input_sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
        "row_count": len(records),
        "split_fractions": {"train": train, "validation": validation, "test": 1 - train - validation},
        "split_counts": {name: len(rows) for name, rows in splits.items()},
        "split_group_counts": {name: len(groups[name]) for name in groups},
        "group_overlap_count": len(overlap),
        "group_overlap": overlap,
        "decision_type_counts": {name: dict(Counter(str(row.get("decision_type", "unknown")) for row in rows)) for name, rows in splits.items()},
        "paths": paths,
    }
    manifest_path = output / "global_behavior.split.manifest.json"
    manifest["manifest_path"] = str(manifest_path)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--train", type=float, default=0.8)
    parser.add_argument("--validation", type=float, default=0.1)
    args = parser.parse_args()
    print(json.dumps(export_dataset(args.input, args.output_dir, args.train, args.validation), ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
