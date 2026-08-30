"""Convert global-behavior JSONL to a stable Parquet table.

Nested state/action fields are stored as canonical JSON strings so one malformed
or optional nested object cannot change the Arrow schema between shards. Install
``training/requirements-training.txt`` in the export environment first.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def _json_value(value: Any) -> Any:
    if isinstance(value, (dict, list)) or value is None:
        return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) if value is not None else None
    if isinstance(value, (str, int, float, bool)):
        return value
    return str(value)


def _load(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for line_no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        value = json.loads(line)
        if not isinstance(value, dict):
            raise ValueError(f"{path}:{line_no}: row must be a JSON object")
        rows.append(value)
    return rows


def convert(input_path: str | Path, output_path: str | Path) -> dict[str, Any]:
    source = Path(input_path)
    target = Path(output_path)
    rows = _load(source)
    try:
        import pyarrow as pa
        import pyarrow.parquet as pq
    except ImportError as exc:
        raise RuntimeError("PyArrow is required; install training/requirements-training.txt") from exc
    keys = sorted({key for row in rows for key in row})
    columns = {key: [_json_value(row.get(key)) for row in rows] for key in keys}
    table = pa.Table.from_pydict(columns)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_suffix(target.suffix + ".tmp")
    temporary.unlink(missing_ok=True)
    try:
        pq.write_table(table, temporary, compression="zstd")
        read_back = pq.read_table(temporary)
        if read_back.num_rows != len(rows):
            raise RuntimeError(f"Parquet row count mismatch: expected {len(rows)}, got {read_back.num_rows}")
        temporary.replace(target)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise
    manifest = {
        "manifest_version": "global-behavior-parquet-manifest-v1",
        "storage_format": "parquet",
        "source": str(source),
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
        "path": str(target),
        "sha256": hashlib.sha256(target.read_bytes()).hexdigest().upper(),
        "row_count": len(rows),
        "column_count": len(keys),
        "columns": keys,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    manifest_path = target.with_name(target.stem + ".manifest.json")
    manifest["manifest_path"] = str(manifest_path)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(json.dumps(convert(args.input, args.output), ensure_ascii=False, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
