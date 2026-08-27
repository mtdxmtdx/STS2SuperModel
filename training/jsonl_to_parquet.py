#!/usr/bin/env python3
"""Convert version-gated TrainingDecisionRecord JSONL into bounded Parquet shards."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from typing import Any, Iterator

from build_dataset_manifest import EXPECTED, VERSION_METADATA, digest


EMPTY_OBJECT_SENTINEL = "__sts2_empty_object__"


def arrow_safe(value: Any) -> Any:
    if isinstance(value, dict):
        if not value:
            return {EMPTY_OBJECT_SENTINEL: True}
        return {key: arrow_safe(item) for key, item in value.items()}
    if isinstance(value, list):
        return [arrow_safe(item) for item in value]
    return value


def restore_empty_objects(value: Any) -> Any:
    if isinstance(value, dict):
        if value == {EMPTY_OBJECT_SENTINEL: True}:
            return {}
        return {key: restore_empty_objects(item) for key, item in value.items()}
    if isinstance(value, list):
        return [restore_empty_objects(item) for item in value]
    return value


def canonical_round_trip(value: Any) -> Any:
    """Treat Arrow's missing-struct-field nulls as equivalent to omitted JSON keys."""
    if isinstance(value, dict):
        return {
            key: canonical_round_trip(item)
            for key, item in value.items()
            if item is not None
        }
    if isinstance(value, list):
        return [canonical_round_trip(item) for item in value]
    return value


def require_pyarrow():
    try:
        import pyarrow as pa
        import pyarrow.parquet as pq
    except ImportError as exc:
        raise RuntimeError(
            "PyArrow is required for Parquet output. Install training/requirements-training.txt."
        ) from exc
    return pa, pq


def batches(path: Path, shard_rows: int) -> Iterator[list[dict[str, Any]]]:
    batch: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"{path}:{line_no}: invalid JSON: {exc}") from exc
            batch.append(row)
            if len(batch) == shard_rows:
                yield batch
                batch = []
    if batch:
        yield batch


def metadata_for(rows: list[dict[str, Any]], path: Path, first_line: int) -> dict[str, Any]:
    keys = (*EXPECTED.keys(), "trace_schema", "schema_version", *VERSION_METADATA, "generator_config_hash")
    first = {key: rows[0].get(key) for key in keys}
    missing = [key for key, value in first.items() if value in (None, "")]
    if missing:
        raise ValueError(f"{path}:{first_line}: missing metadata: {', '.join(missing)}")
    for offset, row in enumerate(rows):
        current = {key: row.get(key) for key in keys}
        if current != first:
            changed = [key for key in keys if current[key] != first[key]]
            raise ValueError(f"{path}:{first_line + offset}: mixed metadata: {', '.join(changed)}")
    for key, expected in EXPECTED.items():
        if first[key] != expected:
            raise ValueError(f"{path}:{first_line}: {key}={first[key]!r}, expected {expected!r}")
    return first


def convert(input_path: Path, output_dir: Path, shard_rows: int = 10_000) -> dict[str, Any]:
    if not 10_000 <= shard_rows <= 50_000:
        raise ValueError("shard_rows must be between 10000 and 50000")
    pa, pq = require_pyarrow()
    output_dir.mkdir(parents=True, exist_ok=True)
    source_sha256 = digest(input_path)
    shards: list[dict[str, Any]] = []
    expected_schema = None
    expected_metadata = None
    row_offset = 1

    for index, rows in enumerate(batches(input_path, shard_rows)):
        current_metadata = metadata_for(rows, input_path, row_offset)
        if expected_metadata is None:
            expected_metadata = current_metadata
        elif current_metadata != expected_metadata:
            raise ValueError(f"{input_path}:{row_offset}: metadata differs from previous shard")

        table = pa.Table.from_pylist([arrow_safe(row) for row in rows])
        if expected_schema is None:
            expected_schema = table.schema
        elif not table.schema.equals(expected_schema, check_metadata=False):
            raise ValueError(f"{input_path}:{row_offset}: Arrow schema differs from first shard")

        final_path = output_dir / f"part-{index:05d}.parquet"
        temp_path = final_path.with_suffix(".parquet.tmp")
        temp_path.unlink(missing_ok=True)
        try:
            pq.write_table(table, temp_path, compression="zstd")
            read_back = pq.read_table(temp_path)
            restored = [restore_empty_objects(row) for row in read_back.to_pylist()]
            if (
                read_back.num_rows != len(rows)
                or canonical_round_trip(restored) != canonical_round_trip(rows)
            ):
                raise RuntimeError(f"Parquet round-trip mismatch for {final_path.name}")
        except Exception:
            temp_path.unlink(missing_ok=True)
            raise
        os.replace(temp_path, final_path)
        shards.append({
            "path": final_path.name,
            "row_count": len(rows),
            "byte_count": final_path.stat().st_size,
            "sha256": digest(final_path),
        })
        row_offset += len(rows)

    config = {"shard_rows": shard_rows, "compression": "zstd", "schema_version": 1}
    config_hash = hashlib.sha256(
        json.dumps(config, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest().upper()
    manifest = {
        "schema_version": 1,
        "storage_format": "parquet",
        "source": str(input_path),
        "source_sha256": source_sha256,
        "generator_config": config,
        "generator_config_hash": config_hash,
        "row_count": sum(item["row_count"] for item in shards),
        "shard_count": len(shards),
        "shards": shards,
        "version_metadata": expected_metadata or {},
    }
    (output_dir / "parquet-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--shard-rows", type=int, default=10_000)
    args = parser.parse_args()
    print(json.dumps(convert(args.input, args.output_dir, args.shard_rows), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
