#!/usr/bin/env python3
"""Machine-readable dataset quality gate for STS2 SuperModel artifacts.

Runs the row-level validator (validate_dataset.DatasetValidator), the
dataset-level split/manifest gates (split_dataset.verify_split_dir), and
source/shard SHA-256 + Parquet integrity verification, then emits a single
JSON verdict:

    data/dataset-quality-gate.json

with the shape:

    {dataset_path, dataset_kind, generated_at, version_lock, row_counts,
     split_violations, public_leakage_count, stable_id_missing, label_stats,
     duplicate_states, malformed_lines, verdict, failures}

Exit code is 0 on "pass" and 1 on "fail".
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).parent))

from validate_dataset import DatasetValidator  # noqa: E402
from split_dataset import try_resolve_recorded_path, verify_split_dir  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent

VERSION_LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol": "0.2.0",
    "trace_schema": 1,
    "training_schema": 1,
}


def sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def failure(file: str, line: int, path: str, message: str, kind: str) -> Dict[str, Any]:
    return {"file": file, "line": line, "path": path, "error": message, "type": kind}


def verify_parquet_manifest(manifest_path: Path) -> List[Dict[str, Any]]:
    """Verify source digest, generator config hash, and every Parquet shard.

    Shard checks cover existence, byte count, recorded sha256, readable
    footer/rows (catches truncated or corrupt files) and recorded row_count.
    """
    failures: List[Dict[str, Any]] = []
    location = str(manifest_path)
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        return [failure(location, 0, "", f"Parquet manifest is not valid JSON: {exc}", "invalid_json")]

    source = manifest.get("source")
    if isinstance(source, str) and source:
        source_path = try_resolve_recorded_path(
            source,
            (manifest_path.parent, manifest_path.parent.parent,
             manifest_path.parent.parent / "training", REPO_ROOT),
        )
        if source_path is None:
            failures.append(failure(location, 0, "source", f"Source file not found: {source}", "source_not_found"))
        elif manifest.get("source_sha256"):
            actual = sha256_file(source_path)
            if actual != manifest["source_sha256"]:
                failures.append(failure(
                    location, 0, "source_sha256",
                    f"Source SHA-256 mismatch for {source_path}: recorded "
                    f"{manifest['source_sha256']}, actual {actual}",
                    "source_sha_mismatch"))

    config = manifest.get("generator_config")
    recorded_hash = manifest.get("generator_config_hash")
    if isinstance(config, dict) and recorded_hash:
        encoded = json.dumps(config, sort_keys=True, separators=(",", ":")).encode("utf-8")
        recomputed = hashlib.sha256(encoded).hexdigest().upper()
        if recomputed != recorded_hash:
            failures.append(failure(
                location, 0, "generator_config_hash",
                f"generator_config_hash mismatch: recorded {recorded_hash}, recomputed {recomputed} from generator_config",
                "generator_config_hash_mismatch"))
    if not recorded_hash:
        failures.append(failure(location, 0, "generator_config_hash",
                                "Missing generator_config_hash in storage manifest",
                                "missing_generator_config_hash"))

    shards = manifest.get("shards")
    if not isinstance(shards, list) or not shards:
        failures.append(failure(location, 0, "shards", "Storage manifest has no shards", "no_shards"))
        return failures

    pyarrow_ok = True
    try:
        import pyarrow.parquet as pq  # noqa: F401
    except ImportError:
        pyarrow_ok = False

    total_rows = 0
    for index, shard in enumerate(shards):
        name = shard.get("path", f"shard[{index}]")
        shard_path = (manifest_path.parent / str(name)).resolve()
        if not shard_path.exists():
            failures.append(failure(location, 0, f"shards[{index}]", f"Shard file missing: {shard_path}", "shard_not_found"))
            continue
        expected_bytes = shard.get("byte_count")
        actual_bytes = shard_path.stat().st_size
        if expected_bytes is not None and expected_bytes != actual_bytes:
            failures.append(failure(
                location, 0, f"shards[{index}].byte_count",
                f"Shard byte_count mismatch for {name}: recorded {expected_bytes}, actual {actual_bytes}",
                "shard_byte_count_mismatch"))
        expected_sha = shard.get("sha256")
        actual_sha = sha256_file(shard_path)
        if expected_sha != actual_sha:
            failures.append(failure(
                location, 0, f"shards[{index}].sha256",
                f"Shard sha256 mismatch for {name}: recorded {expected_sha}, actual {actual_sha}",
                "shard_sha256_mismatch"))
        if pyarrow_ok:
            try:
                table = pq.read_table(str(shard_path))
                rows = table.num_rows
            except Exception as exc:  # ArrowInvalid/OSError => truncated/corrupt footer
                failures.append(failure(
                    location, 0, f"shards[{index}]",
                    f"Parquet shard unreadable (truncated or corrupt): {name}: {type(exc).__name__}: {exc}",
                    "parquet_truncated_or_corrupt"))
            else:
                total_rows += rows
                expected_rows = shard.get("row_count")
                if expected_rows is not None and expected_rows != rows:
                    failures.append(failure(
                        location, 0, f"shards[{index}].row_count",
                        f"Shard row_count mismatch for {name}: recorded {expected_rows}, parquet contains {rows}",
                        "shard_row_count_mismatch"))
        else:
            failures.append(failure(
                location, 0, "pyarrow",
                "PyArrow unavailable; shard readability (truncation) check skipped",
                "pyarrow_unavailable"))

    recorded_total = manifest.get("row_count")
    if recorded_total is not None and pyarrow_ok and total_rows != recorded_total:
        failures.append(failure(
            location, 0, "row_count",
            f"Manifest row_count={recorded_total} but shards contain {total_rows} readable rows",
            "shard_row_total_mismatch"))
    return failures


def build_report(
    dataset_path: Path,
    dataset_kind: str,
    training_path: Optional[Path] = None,
    trace_path: Optional[Path] = None,
    manifest_path: Optional[Path] = None,
    parquet_manifest_path: Optional[Path] = None,
    split_dir: Optional[Path] = None,
) -> Dict[str, Any]:
    def abs_input(path: Optional[Path]) -> Optional[Path]:
        """Anchor relative inputs to this repository, independent of CWD."""
        if path is None:
            return None
        return path if path.is_absolute() else (REPO_ROOT / path)

    def rel(path: Optional[Path]) -> Optional[str]:
        if path is None:
            return None
        resolved = Path(path).resolve()
        try:
            return resolved.relative_to(REPO_ROOT).as_posix()
        except ValueError:
            return resolved.as_posix()

    dataset_path = abs_input(dataset_path) or dataset_path
    training_path = abs_input(training_path)
    trace_path = abs_input(trace_path)
    manifest_path = abs_input(manifest_path)
    parquet_manifest_path = abs_input(parquet_manifest_path)
    split_dir = abs_input(split_dir)

    failures: List[Dict[str, Any]] = []
    validator = DatasetValidator()
    metadata_checks: Dict[str, Any] = {}

    if trace_path is not None:
        validator.validate_trace_file(trace_path)
    if training_path is not None:
        validator.validate_training_file(training_path)
    if manifest_path is not None:
        manifest_obj = validator.validate_manifest_file(manifest_path)
        if training_path is not None:
            mismatches = validator.cross_check_manifest_counts(manifest_obj, str(manifest_path))
            metadata_checks["manifest_count_cross_check"] = "ok" if mismatches == 0 else f"{mismatches} mismatches"
        source_hashes = manifest_obj.get("source_hashes") or []
        verified_sources = []
        if training_path is not None and training_path.exists():
            training_digest = sha256_file(training_path)
            verified_sources.append({
                "file": rel(training_path),
                "sha256": training_digest,
                "listed_in_manifest": training_digest in source_hashes,
            })
            if training_digest not in source_hashes:
                failures.append(failure(
                    str(manifest_path), 0, "source_hashes",
                    f"Training file digest {training_digest} ({rel(training_path)}) is absent from manifest source_hashes",
                    "source_sha_mismatch"))
        metadata_checks["sources"] = verified_sources

    if parquet_manifest_path is not None:
        parquet_failures = verify_parquet_manifest(parquet_manifest_path)
        failures.extend(parquet_failures)
        metadata_checks["parquet_manifest"] = {
            "file": rel(parquet_manifest_path),
            "status": "ok" if not parquet_failures else f"{len(parquet_failures)} failures",
        }

    split_violations: List[str] = []
    if split_dir is not None:
        split_violations, split_stats = verify_split_dir(split_dir)
        metadata_checks["splits"] = {"dir": rel(split_dir), "stats": split_stats}
        for violation in split_violations:
            failures.append(failure(rel(split_dir), 0, "splits", violation, "split_violation"))

    errors = validator.errors
    failures.extend(
        {
            "file": rel(Path(e.file)),
            "line": e.line_no,
            "path": e.path,
            "error": e.message,
            "type": e.error_type,
        }
        for e in sorted(errors, key=lambda item: (item.file, item.line_no))
    )

    stats = validator.stats
    labels = validator.stats.labels
    report: Dict[str, Any] = {
        "dataset_path": rel(dataset_path),
        "dataset_kind": dataset_kind,
        "generated_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "version_lock": dict(VERSION_LOCK),
        "row_counts": {
            "trace_rows": stats.trace_rows,
            "training_rows": stats.training_rows,
            "episodes": len(stats.episodes),
            "states_unique": len(stats.state_hashes),
            "actions_total": stats.actions_seen,
        },
        "split_violations": split_violations,
        "public_leakage_count": validator.public_leakage_count,
        "stable_id_missing": validator.stable_id_missing,
        "label_stats": {
            "reliable": labels["Reliable"],
            "estimated": labels["Estimated"],
            "low_confidence": labels["LowConfidence"],
            "uncalculable": labels["Uncalculable"],
            "unknown": sum(count for level, count in labels.items()
                           if level not in ("Reliable", "Estimated", "LowConfidence", "Uncalculable")),
            "empty_teacher_best_actions": sum(validator.empty_teacher_categories.values()),
            "empty_teacher_categories": dict(sorted(validator.empty_teacher_categories.items())),
        },
        "duplicate_states": {
            "warning_count": sum(1 for d in validator.duplicate_combo_warnings if d["severity"] == "warning"),
            "error_count": sum(1 for d in validator.duplicate_combo_warnings if d["severity"] == "error"),
            "entries": validator.duplicate_combo_warnings,
            "classification_legend": {
                "warning_identical_duplicate": "same hash + same combo + same labels: redundant, merge recommended",
                "error_conflicting_labels": "same hash + same combo but differing labels/confidence: dataset corruption",
            },
        },
        "malformed_lines": [
            {"file": rel(Path(e.file)), "line": e.line_no, "error": e.message, "kind": e.error_type}
            for e in errors
            if e.error_type in ("truncated_json_line", "invalid_json")
        ],
        "metadata_checks": metadata_checks,
        "warnings": [
            {"file": rel(Path(w.file)), "line": w.line_no, "path": w.path, "message": w.message, "type": w.error_type}
            for w in validator.warnings
        ],
        "verdict": "pass" if not failures else "fail",
        "failures": failures,
        "checked_files": sorted(rel(Path(item)) for item in validator.checked_files if item),
    }
    return report


def write_report(report: Dict[str, Any], output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset-path", required=True, type=Path,
                        help="Primary dataset artifact this gate run is about")
    parser.add_argument("--dataset-kind", required=True, choices=("tool_smoke", "teacher"))
    parser.add_argument("--training", type=Path, help="Training decision record JSONL")
    parser.add_argument("--trace", type=Path, help="Raw trace JSONL")
    parser.add_argument("--manifest", type=Path, help="Dataset manifest JSON")
    parser.add_argument("--parquet-manifest", type=Path, dest="parquet_manifest",
                        help="Parquet storage manifest (parquet-manifest.json)")
    parser.add_argument("--split-dir", type=Path, dest="split_dir", help="Directory with split outputs")
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "data" / "dataset-quality-gate.json",
                        help="Output report path (relative paths anchor to the repository root)")
    args = parser.parse_args(argv)
    if not args.output.is_absolute():
        args.output = REPO_ROOT / args.output

    report = build_report(
        dataset_path=args.dataset_path,
        dataset_kind=args.dataset_kind,
        training_path=args.training,
        trace_path=args.trace,
        manifest_path=args.manifest,
        parquet_manifest_path=args.parquet_manifest,
        split_dir=args.split_dir,
    )
    write_report(report, args.output)
    summary = (
        f"{report['verdict'].upper()}: {len(report['failures'])} failures, "
        f"{report['public_leakage_count']} leaks, {report['stable_id_missing']} missing stable ids, "
        f"{len(report['malformed_lines'])} malformed lines -> {args.output}"
    )
    print(summary)
    print(json.dumps({key: report[key] for key in ("verdict", "row_counts", "label_stats")}, ensure_ascii=False, indent=2))
    return 0 if report["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
