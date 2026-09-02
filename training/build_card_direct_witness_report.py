#!/usr/bin/env python3
"""Build an auditable inventory for the per-card direct ShadowDiff sweep.

The matrix is an oracle inventory, not a training-label generator.  A row is
promoted to ``direct_reliable`` only when it is an actual combat-play row,
both the matrix and report say match/mismatch_count=0, the report is labelled
Reliable, and the referenced report/trace pass the v0.111 provenance checks.
Timeouts and runtime errors stay explicitly uncalculable; mismatches are
degraded.  No row is inferred from a neighbouring card or from a handler map.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"

VERSION_LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}


def sha256(path: Path) -> str | None:
    if not path.is_file():
        return None
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT.parent.resolve()).as_posix()
    except ValueError:
        return path.resolve().as_posix()


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def load_trace(path: Path) -> tuple[list[dict[str, Any]], str | None]:
    if not path.is_file():
        return [], None
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            rows.append(value)
    return rows, next((r.get("trace_id") for r in rows if r.get("trace_id")), None)


def trace_candidates(matrix_path: Path, variant_id: str) -> list[Path]:
    # traces3 and traces2 are byte-identical snapshots in the current sweep;
    # prefer the latest named directory and retain the selected hash in the
    # manifest so a future replacement cannot be silent.
    return [
        matrix_path.parent / name / f"{variant_id}.jsonl"
        for name in ("card-direct-traces4", "card-direct-traces3", "card-direct-traces2", "card-direct-traces")
    ]


def choose_trace(matrix_path: Path, variant_id: str, expected_trace_id: str | None) -> tuple[Path | None, list[dict[str, Any]], str | None, list[dict[str, Any]]]:
    alternatives: list[dict[str, Any]] = []
    fallback: tuple[Path | None, list[dict[str, Any]], str | None] = (None, [], None)
    for path in trace_candidates(matrix_path, variant_id):
        rows, trace_id = load_trace(path)
        if not rows:
            continue
        item = {"path": relative(path), "sha256": sha256(path), "trace_id": trace_id}
        alternatives.append(item)
        if fallback[0] is None:
            fallback = (path, rows, trace_id)
        if expected_trace_id and trace_id == expected_trace_id:
            return path, rows, trace_id, alternatives
    return fallback[0], fallback[1], fallback[2], alternatives


def action_rows(trace_rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        row for row in trace_rows
        if isinstance(row.get("normalized_action_id"), str)
        and (
            row["normalized_action_id"].startswith("play_card:")
            or row["normalized_action_id"].startswith("use_potion:")
            or row["normalized_action_id"].startswith("end_turn")
        )
        and (
            isinstance(row.get("public_observation"), dict)
            or (
                row["normalized_action_id"].startswith("play_card:")
                and row.get("decision") == "card_select"
            )
        )
        and row.get("post_state_hash")
    ]


def version_issues(value: dict[str, Any] | None, prefix: str) -> list[str]:
    if not isinstance(value, dict):
        return [f"{prefix}_metadata_missing"]
    return [
        f"{prefix}_version_mismatch:{key}"
        for key, expected in VERSION_LOCK.items()
        if value.get(key) != expected
    ]


def report_and_trace_record(
    row: dict[str, Any], matrix_path: Path, reports_dir: Path,
    repeat_hashes: dict[str, str] | None = None,
    repeat_pass: bool = False,
) -> dict[str, Any]:
    variant_id = row.get("variant_id")
    issues: list[str] = []
    if not isinstance(variant_id, str) or not variant_id:
        variant_id = str(variant_id or "<missing>")
        issues.append("variant_id_missing")

    report_name = row.get("report")
    report_path: Path | None = None
    report: dict[str, Any] | None = None
    if isinstance(report_name, str) and report_name:
        # Only basenames are accepted; a matrix must not redirect evidence to
        # an arbitrary path outside its data directory.
        if Path(report_name).name != report_name:
            issues.append("report_path_not_basename")
        else:
            report_path = reports_dir / report_name
            if report_path.is_file():
                try:
                    parsed = load_json(report_path)
                    if isinstance(parsed, dict):
                        report = parsed
                    else:
                        issues.append("report_not_object")
                except (OSError, json.JSONDecodeError):
                    issues.append("report_unparseable")
            else:
                issues.append("report_missing")
    else:
        issues.append("report_name_missing")

    expected_trace_id = report.get("trace_id") if report else None
    trace_path, trace_rows, trace_id, alternatives = choose_trace(
        matrix_path, variant_id, expected_trace_id)
    if trace_path is None:
        issues.append("trace_missing")
    issues.extend(version_issues(report, "report"))
    trace_meta = trace_rows[0] if trace_rows else None
    issues.extend(version_issues(trace_meta, "trace"))
    if report and trace_id and report.get("trace_id") != trace_id:
        issues.append("report_trace_id_mismatch")

    action_id = row.get("normalized_action_id")
    matched_actions = [
        action for action in action_rows(trace_rows)
        if action.get("normalized_action_id") == action_id
    ]
    if not matched_actions:
        issues.append("trace_action_not_found")
    if report and report.get("normalized_action_id") != action_id:
        issues.append("report_action_id_mismatch")
    if report and report.get("fixture") not in (variant_id, None):
        issues.append("report_fixture_variant_mismatch")
    if report and row.get("confidence") != report.get("confidence"):
        issues.append("row_report_confidence_mismatch")
    if report and row.get("match") != report.get("match"):
        issues.append("row_report_match_mismatch")
    if report and row.get("mismatch_count") != report.get("mismatch_count"):
        issues.append("row_report_mismatch_count_mismatch")
    if report and report.get("projected_comparison_hash") != report.get("actual_comparison_hash"):
        issues.append("report_comparison_hash_mismatch")

    decision = row.get("decision")
    error = row.get("error")
    exact_match = row.get("match") is True and row.get("mismatch_count") == 0
    report_exact = bool(
        report
        and report.get("match") is True
        and report.get("mismatch_count") == 0
        and report.get("mismatches") in ([], None)
        and report.get("projected_comparison_hash") == report.get("actual_comparison_hash")
    )
    strict_scope = bool(
        report
        and report.get("comparison_scope") == "strict_public_state"
        and report.get("identity_comparison") == "compared"
    )
    structural_ok = not any(
        issue.startswith(("report_", "trace_", "row_report_"))
        for issue in issues
    )
    direct_reliable = bool(
        decision == "combat_play"
        and not error
        and exact_match
        and report_exact
        and row.get("confidence") == "Reliable"
        and report.get("confidence") == "Reliable" if report else False
    ) and strict_scope and structural_ok
    report_digest = sha256(report_path) if report_path else None
    repeat_verified = bool(
        repeat_pass and report_name and report_digest and
        repeat_hashes and repeat_hashes.get(report_name) == report_digest
    )
    no_gameplay_report = bool(
        report
        and report.get("reason") == "no gameplay action with public post-state (choice/error-only trace)"
    )

    if no_gameplay_report and decision == "card_reward":
        status, quality = "fixture_not_entered_combat", "Uncalculable"
    elif no_gameplay_report and decision == "card_select":
        status, quality = "choice_fixture_required", "Uncalculable"
    elif no_gameplay_report and decision == "error":
        status, quality = "runtime_unavailable", "Uncalculable"
    elif error == "shadowdiff_timeout" and decision == "card_reward":
        status, quality = "fixture_not_entered_combat", "Uncalculable"
    elif error == "shadowdiff_timeout" and decision == "card_select":
        status, quality = "choice_fixture_required", "Uncalculable"
    elif error == "shadowdiff_timeout" and decision == "error":
        status, quality = "runtime_unavailable", "Uncalculable"
    elif error == "shadowdiff_timeout":
        status, quality = "runtime_timeout", "Uncalculable"
    elif error:
        status, quality = "runtime_error", "Uncalculable"
    elif decision != "combat_play":
        status, quality = "non_play_matched" if exact_match else "non_play", "Estimated"
    elif not exact_match or not report_exact:
        status, quality = "mismatch", "Estimated"
    elif direct_reliable:
        status, quality = "direct_reliable", "Reliable"
    elif row.get("confidence") == "Uncalculable":
        status, quality = "direct_uncalculable", "Uncalculable"
    else:
        status, quality = "direct_estimated", "Estimated"

    return {
        "witness_id": f"card-direct-matrix:{variant_id}",
        "variant_id": variant_id,
        "decision": decision,
        "normalized_action_id": action_id,
        "matrix_confidence": row.get("confidence"),
        "matrix_match": row.get("match"),
        "matrix_mismatch_count": row.get("mismatch_count"),
        "matrix_error": error,
        "report": report_name,
        "report_path": relative(report_path) if report_path else None,
        "report_sha256": report_digest,
        "trace_path": relative(trace_path) if trace_path else None,
        "trace_sha256": sha256(trace_path) if trace_path else None,
        "trace_id": trace_id,
        "trace_alternatives": alternatives,
        "report_confidence": report.get("confidence") if report else None,
        "comparison_scope": report.get("comparison_scope") if report else None,
        "identity_comparison": report.get("identity_comparison") if report else None,
        "engine_pre_state_hash": report.get("engine_pre_state_hash") if report else None,
        "engine_post_state_hash": report.get("engine_post_state_hash") if report else None,
        "projected_comparison_hash": report.get("projected_comparison_hash") if report else None,
        "actual_comparison_hash": report.get("actual_comparison_hash") if report else None,
        "shadowdiff_reliable": bool(row.get("confidence") == "Reliable"),
        "direct_witness_eligible": direct_reliable,
        "repeat_verified": repeat_verified,
        "main_reliable_eligible": bool(direct_reliable and repeat_verified),
        "status": status,
        "quality": quality,
        "issues": sorted(set(issues)),
    }


def build(matrix_path: Path, reports_dir: Path) -> dict[str, Any]:
    source_sha = sha256(matrix_path)
    raw = load_json(matrix_path)
    source_errors: list[str] = []
    if not isinstance(raw, list):
        source_errors.append("matrix_not_array")
        raw = []
    rows = [row for row in raw if isinstance(row, dict)]
    if len(rows) != len(raw):
        source_errors.append("non_object_matrix_row")
    repeat_hashes: dict[str, str] = {}
    repeat_pass = False
    repeat_path = reports_dir / "card-direct-repeat-verification.json"
    if repeat_path.is_file():
        try:
            repeat = load_json(repeat_path)
            comparison = repeat.get("comparison", {}) if isinstance(repeat, dict) else {}
            repeat_pass = comparison.get("verdict") == "pass"
            if repeat_pass and isinstance(repeat.get("second_report_sha256"), dict):
                repeat_hashes = {str(k): str(v) for k, v in repeat["second_report_sha256"].items()}
        except (OSError, json.JSONDecodeError):
            repeat_pass = False
    records = [report_and_trace_record(row, matrix_path, reports_dir, repeat_hashes, repeat_pass) for row in rows]
    records.sort(key=lambda value: (str(value.get("variant_id")), str(value.get("witness_id"))))
    counts = Counter(record["status"] for record in records)
    matched = [record for record in records if record["matrix_match"] is True and record["matrix_mismatch_count"] == 0]
    reliable_raw = [record for record in records if record["shadowdiff_reliable"]]
    timeout = [record for record in records if record["status"] == "runtime_timeout"]
    fixture_not_entered = [record for record in records if record["status"] == "fixture_not_entered_combat"]
    choice_required = [record for record in records if record["status"] == "choice_fixture_required"]
    runtime_unavailable = [record for record in records if record["status"] == "runtime_unavailable"]
    mismatch = [record for record in records if record["status"] == "mismatch"]
    runtime_error = [record for record in records if record["status"] == "runtime_error"]
    degraded = mismatch + runtime_error
    direct_reliable = [record for record in records if record["direct_witness_eligible"]]
    validation_errors = list(source_errors)
    ids = [record["variant_id"] for record in records]
    if len(ids) != len(set(ids)):
        validation_errors.append("duplicate_variant_id")
    manifest = {
        "schema_version": 1,
        "builder_version": "card-direct-witness-v1",
        "version_lock": VERSION_LOCK,
        "source": {
            "matrix": relative(matrix_path),
            "matrix_sha256": source_sha,
            "reports_dir": relative(reports_dir),
            "trace_roots": [relative(matrix_path.parent / name) for name in (
                "card-direct-traces4", "card-direct-traces3", "card-direct-traces2", "card-direct-traces")],
        },
        "quality_policy": {
            "direct_reliable": "combat_play + matrix/report match + mismatch_count=0 + Reliable + strict_public_state/identity comparison + valid v0.111 report/trace",
            "direct_estimated": "play row matched but report is Estimated or lacks strict Reliable eligibility",
            "mismatch": "report exists but match=false or mismatch_count>0; never Reliable",
            "fixture_not_entered_combat": "fixture ended at card_reward instead of a combat action; regenerate with the card's owning character/context",
            "choice_fixture_required": "card opened an interactive selection; regenerate with a stable select_cards/choice contract",
            "runtime_unavailable": "runtime rejected or could not instantiate the card; verify version/model availability before retrying",
            "runtime_timeout": "combat action existed but ShadowDiff timed out; Uncalculable",
            "runtime_error": "runtime/CLI error; Uncalculable and included in degraded aggregate",
            "non_play": "card_reward/card_select/error decision is not a direct combat witness even if a terminal report matches",
            "repeat_verified": "report hash must be present in card-direct-repeat-verification.json with verdict=pass; only then is main_reliable_eligible true",
        },
        "summary": {
            "matrix_rows": len(records),
            "matched_rows": len(matched),
            "shadowdiff_reliable_rows": len(reliable_raw),
            "direct_reliable_rows": len(direct_reliable),
            "direct_reliable_variants": len({r["variant_id"] for r in direct_reliable}),
            "repeat_verified_rows": sum(bool(r["repeat_verified"]) for r in records),
            "repeat_verified_direct_reliable_rows": sum(bool(r["main_reliable_eligible"]) for r in records),
            "matched_estimated_rows": counts["direct_estimated"],
            "matched_uncalculable_rows": counts["direct_uncalculable"],
            "matched_non_play_rows": counts["non_play_matched"],
            "report_present_rows": sum(bool(r["report_sha256"]) for r in records),
            "trace_present_rows": sum(bool(r["trace_sha256"]) for r in records),
            "timeout_rows": len(timeout),
            "fixture_not_entered_combat_rows": len(fixture_not_entered),
            "choice_fixture_required_rows": len(choice_required),
            "runtime_unavailable_rows": len(runtime_unavailable),
            "runtime_error_rows": len(runtime_error),
            "mismatch_rows": len(mismatch),
            "degraded_rows": len(degraded),
            "uncalculable_runtime_rows": len(timeout) + len(fixture_not_entered) + len(choice_required) + len(runtime_unavailable) + len(runtime_error),
            "status_counts": dict(sorted(counts.items())),
            "quality_counts": dict(sorted(Counter(r["quality"] for r in records).items())),
        },
        "reliable_variant_ids": sorted({r["variant_id"] for r in direct_reliable}),
        "main_reliable_variant_ids": sorted({r["variant_id"] for r in records if r["main_reliable_eligible"]}),
        "repeat_verification": {
            "path": relative(repeat_path),
            "verdict": "pass" if repeat_pass else "missing_or_fail",
            "hash_count": len(repeat_hashes),
        },
        "equivalence_proofs": [],
        "rows": records,
        "validation": {
            "verdict": "pass" if not validation_errors else "fail",
            "errors": sorted(set(validation_errors)),
        },
    }
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--matrix", type=Path, default=DATA / "card-direct-matrix-results.json")
    parser.add_argument("--reports", type=Path, default=DATA)
    parser.add_argument("--output", type=Path, default=DATA / "card-direct-witness-manifest.json")
    args = parser.parse_args()
    manifest = build(args.matrix, args.reports)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary = manifest["summary"]
    print(json.dumps({
        "validation": manifest["validation"]["verdict"],
        "matrix_rows": summary["matrix_rows"],
        "matched_rows": summary["matched_rows"],
        "direct_reliable_rows": summary["direct_reliable_rows"],
        "timeout_rows": summary["timeout_rows"],
        "degraded_rows": summary["degraded_rows"],
        "output": str(args.output),
    }, ensure_ascii=False))
    return 0 if manifest["validation"]["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
