#!/usr/bin/env python3
"""Streaming schema and integrity validator for STS2 SuperModel dataset artifacts.

Validates trace files, training decision records, and dataset manifests
against P0 Schema v1 rules using only standard library modules.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from jsonschema import Draft202012Validator

LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "simulator_version": "cli-v0111-headless",
    "scorer_version": "not-applicable",
    "semantic_database_version": "game-runtime-v0111",
    "feature_schema_version": "1",
    "model_version": "none",
}

SCHEMA_DIR = Path(__file__).parent / "schemas"
SCHEMA_FILES = {
    "trace": "trace-schema-v1.json",
    "public": "public-state-schema-v1.json",
    "teacher": "teacher-state-schema-v1.json",
    "training": "training-decision-record-v1.json",
    "manifest": "dataset-manifest-v1.json",
}

FORBIDDEN_PUBLIC_KEYS = {
    "run_seed",
    "rng_raw_words",
    "raw_rng_words",
    "rng_words",
    "future_draw_order",
    "future_draw_identities",
    "teacher_only_state",
    "seed",
}

HEX64_PATTERN = re.compile(r"^[0-9A-Fa-f]{64}$")


@dataclass
class ValidationError:
    file: str
    line_no: int
    path: str
    message: str
    error_type: str


class DatasetValidator:
    def __init__(self):
        self.errors: List[ValidationError] = []
        self.checked_files: List[str] = []
        self.rows_checked: int = 0
        self.public_leakage_count: int = 0
        self._schemas = {
            name: json.loads((SCHEMA_DIR / filename).read_text(encoding="utf-8"))
            for name, filename in SCHEMA_FILES.items()
        }
        # Public and teacher payloads are validated independently so their
        # internal #/$defs references retain the correct schema root.
        self._schemas["trace"] = self._without_external_refs(self._schemas["trace"])
        self._schemas["training"] = self._without_external_refs(self._schemas["training"])
        self._validators = {
            name: Draft202012Validator(schema)
            for name, schema in self._schemas.items()
        }

    @staticmethod
    def _without_external_refs(value: Any) -> Any:
        if isinstance(value, dict):
            ref = value.get("$ref")
            if isinstance(ref, str) and not ref.startswith("#"):
                return {}
            return {key: DatasetValidator._without_external_refs(item) for key, item in value.items()}
        if isinstance(value, list):
            return [DatasetValidator._without_external_refs(item) for item in value]
        return copy.deepcopy(value)

    def _check_schema(self, schema_name: str, value: Any, file: str, line_no: int, prefix: str = "") -> None:
        for error in sorted(self._validators[schema_name].iter_errors(value), key=lambda item: list(item.absolute_path)):
            suffix = "".join(f"[{part}]" if isinstance(part, int) else f".{part}" for part in error.absolute_path)
            path = f"{prefix}{suffix}".lstrip(".")
            self.add_error(file, line_no, path, error.message, "schema_validation_error")

    def add_error(self, file: str, line_no: int, path: str, message: str, error_type: str) -> None:
        self.errors.append(ValidationError(
            file=str(file),
            line_no=line_no,
            path=path,
            message=message,
            error_type=error_type,
        ))

    def _check_version_lock(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        for key, expected in LOCK.items():
            val = row.get(key)
            if val != expected:
                self.add_error(
                    file, line_no, key,
                    f"Version lock mismatch: {key}={val!r}, expected {expected!r}",
                    "version_lock_mismatch"
                )

    def _check_public_privacy(self, obj: Any, file: str, line_no: int, current_path: str = "public_state") -> None:
        if isinstance(obj, dict):
            for k, v in obj.items():
                p = f"{current_path}.{k}"
                if k.lower() in FORBIDDEN_PUBLIC_KEYS:
                    self.public_leakage_count += 1
                    self.add_error(
                        file, line_no, p,
                        f"Forbidden public field detected: {k!r} with value {v!r}",
                        "public_privacy_leak"
                    )
                self._check_public_privacy(v, file, line_no, p)
        elif isinstance(obj, list):
            for idx, item in enumerate(obj):
                self._check_public_privacy(item, file, line_no, f"{current_path}[{idx}]")

    def _check_action_candidates(self, candidates: Any, file: str, line_no: int, path: str) -> None:
        if not isinstance(candidates, list):
            self.add_error(file, line_no, path, "Action candidates must be a list", "invalid_type")
            return

        seen_action_ids = set()
        for idx, cand in enumerate(candidates):
            cand_path = f"{path}[{idx}]"
            if not isinstance(cand, dict):
                self.add_error(file, line_no, cand_path, "Candidate must be an object", "invalid_type")
                continue

            action_id = cand.get("action_id")
            kind = cand.get("kind")
            legal = cand.get("legal")

            if not action_id or not isinstance(action_id, str):
                self.add_error(file, line_no, f"{cand_path}.action_id", "Missing or empty action_id", "missing_required_field")
            elif action_id in seen_action_ids:
                self.add_error(file, line_no, f"{cand_path}.action_id", f"Duplicate action_id: {action_id!r}", "duplicate_action_id")
            else:
                seen_action_ids.add(action_id)

            if kind not in ("PlayCard", "UsePotion", "Choice", "EndTurn"):
                self.add_error(file, line_no, f"{cand_path}.kind", f"Invalid action kind: {kind!r}", "invalid_value")

            if kind in ("PlayCard", "UsePotion"):
                src_inst = cand.get("source_instance_id")
                if not src_inst or not isinstance(src_inst, str):
                    self.add_error(file, line_no, f"{cand_path}.source_instance_id", f"{kind} requires non-empty source_instance_id", "missing_required_field")
            if kind == "Choice":
                if not cand.get("choice_id"):
                    self.add_error(file, line_no, f"{cand_path}.choice_id", "Choice requires non-empty choice_id", "missing_required_field")
                if not isinstance(cand.get("selected_card_instance_ids"), list):
                    self.add_error(file, line_no, f"{cand_path}.selected_card_instance_ids", "Choice requires selected_card_instance_ids", "missing_required_field")

            if not isinstance(legal, bool):
                self.add_error(file, line_no, f"{cand_path}.legal", "legal field must be boolean", "invalid_type")

    def validate_trace_file(self, path: Path) -> int:
        """Validate raw trace JSONL file."""
        self.checked_files.append(str(path))
        count = 0
        if not path.exists():
            self.add_error(str(path), 0, "", "File does not exist", "file_not_found")
            return 0

        with path.open(encoding="utf-8") as f:
            for line_no, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                count += 1
                self.rows_checked += 1
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as e:
                    self.add_error(str(path), line_no, "", f"Invalid JSON: {e}", "json_syntax_error")
                    continue

                if not isinstance(row, dict):
                    self.add_error(str(path), line_no, "", "Row must be a JSON object", "invalid_type")
                    continue

                self._check_version_lock(row, str(path), line_no)
                self._check_schema("trace", row, str(path), line_no)
                if row.get("trace_schema") != 1:
                    self.add_error(str(path), line_no, "trace_schema", f"trace_schema must be 1, got {row.get('trace_schema')!r}", "invalid_value")

                post_hash = row.get("post_state_hash")
                if not post_hash or not HEX64_PATTERN.match(str(post_hash)):
                    self.add_error(str(path), line_no, "post_state_hash", f"Invalid post_state_hash format: {post_hash!r}", "invalid_hash_format")

                # Public observation checks
                public_obs = row.get("public_observation")
                if public_obs is not None:
                    self._check_schema("public", public_obs, str(path), line_no, "public_observation")
                    self._check_public_privacy(public_obs, str(path), line_no, "public_observation")
                    if isinstance(public_obs, dict) and "action_candidates" in public_obs:
                        self._check_action_candidates(public_obs["action_candidates"], str(path), line_no, "public_observation.action_candidates")

                # Teacher snapshot checks
                teacher_snap = row.get("teacher_snapshot")
                if teacher_snap is not None and isinstance(teacher_snap, dict):
                    self._check_schema("teacher", teacher_snap, str(path), line_no, "teacher_snapshot")
                    if teacher_snap.get("rng_raw_words_exposed") is not False:
                        self.add_error(
                            str(path), line_no, "teacher_snapshot.rng_raw_words_exposed",
                            f"rng_raw_words_exposed must be False, got {teacher_snap.get('rng_raw_words_exposed')!r}",
                            "raw_rng_words_leak"
                        )

        return count

    def validate_training_file(self, path: Path) -> int:
        """Validate training decision record JSONL file."""
        self.checked_files.append(str(path))
        count = 0
        if not path.exists():
            self.add_error(str(path), 0, "", "File does not exist", "file_not_found")
            return 0

        with path.open(encoding="utf-8") as f:
            for line_no, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                count += 1
                self.rows_checked += 1
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as e:
                    self.add_error(str(path), line_no, "", f"Invalid JSON: {e}", "json_syntax_error")
                    continue

                if not isinstance(row, dict):
                    self.add_error(str(path), line_no, "", "Row must be a JSON object", "invalid_type")
                    continue

                self._check_version_lock(row, str(path), line_no)
                self._check_schema("training", row, str(path), line_no)
                if row.get("schema_version") != 1:
                    self.add_error(str(path), line_no, "schema_version", f"schema_version must be 1, got {row.get('schema_version')!r}", "invalid_value")

                for req in ("record_id", "episode_id", "character", "combat_id", "state_hash_public", "state_hash_teacher"):
                    if not row.get(req):
                        self.add_error(str(path), line_no, req, f"Missing required field {req!r}", "missing_required_field")

                confidence = row.get("confidence")
                if confidence not in ("Reliable", "Estimated", "LowConfidence", "Uncalculable"):
                    self.add_error(str(path), line_no, "confidence", f"Invalid confidence: {confidence!r}", "invalid_value")

                # Public state privacy check
                public_state = row.get("public_state")
                if public_state is not None:
                    self._check_schema("public", public_state, str(path), line_no, "public_state")
                    self._check_public_privacy(public_state, str(path), line_no, "public_state")

                # Legal actions check
                legal_actions = row.get("legal_actions")
                self._check_action_candidates(legal_actions, str(path), line_no, "legal_actions")

        return count

    def validate_manifest_file(self, path: Path) -> Dict[str, Any]:
        """Validate dataset manifest JSON file."""
        self.checked_files.append(str(path))
        if not path.exists():
            self.add_error(str(path), 0, "", "File does not exist", "file_not_found")
            return {}

        try:
            with path.open(encoding="utf-8") as f:
                manifest = json.load(f)
        except json.JSONDecodeError as e:
            self.add_error(str(path), 1, "", f"Invalid JSON: {e}", "json_syntax_error")
            return {}

        if not isinstance(manifest, dict):
            self.add_error(str(path), 1, "", "Manifest must be a JSON object", "invalid_type")
            return {}

        self._check_version_lock(manifest, str(path), 1)
        self._check_schema("manifest", manifest, str(path), 1)
        if manifest.get("schema_version") != 1:
            self.add_error(str(path), 1, "schema_version", "schema_version must be 1", "invalid_value")

        for req in ("dataset_id", "split_policy", "row_count", "state_count", "action_count", "source_hashes", "created_at_utc"):
            if req not in manifest:
                self.add_error(str(path), 1, req, f"Missing required manifest field {req!r}", "missing_required_field")

        source_hashes = manifest.get("source_hashes", [])
        if not isinstance(source_hashes, list) or len(source_hashes) == 0:
            self.add_error(str(path), 1, "source_hashes", "source_hashes must be a non-empty list", "invalid_value")
        else:
            for idx, h in enumerate(source_hashes):
                if not HEX64_PATTERN.match(str(h)):
                    self.add_error(str(path), 1, f"source_hashes[{idx}]", f"Invalid SHA-256 hash: {h!r}", "invalid_hash_format")

        return manifest

    def get_report(self) -> Dict[str, Any]:
        return {
            "schema_version": 1,
            "game_version": LOCK["game_version"],
            "game_commit": LOCK["game_commit"],
            "assembly_sha256": LOCK["assembly_sha256"],
            "cli_protocol_version": LOCK["cli_protocol_version"],
            "validation_status": "passed" if len(self.errors) == 0 else "failed",
            "total_files_checked": len(self.checked_files),
            "total_rows_checked": self.rows_checked,
            "public_leakage_count": self.public_leakage_count,
            "error_count": len(self.errors),
            "errors": [asdict(e) for e in sorted(self.errors, key=lambda x: (x.file, x.line_no, x.path))],
            "checked_files": sorted(self.checked_files),
        }


def validate_all(
    manifest_path: Optional[Path] = None,
    training_path: Optional[Path] = None,
    trace_path: Optional[Path] = None,
    output_report_path: Optional[Path] = None,
) -> Dict[str, Any]:
    validator = DatasetValidator()
    if manifest_path:
        validator.validate_manifest_file(manifest_path)
    if training_path:
        validator.validate_training_file(training_path)
    if trace_path:
        validator.validate_trace_file(trace_path)

    report = validator.get_report()
    if output_report_path:
        output_report_path.parent.mkdir(parents=True, exist_ok=True)
        with output_report_path.open("w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, ensure_ascii=False)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate STS2 SuperModel datasets against Schema v1")
    parser.add_argument("--manifest", type=Path, help="Path to manifest JSON")
    parser.add_argument("--training", type=Path, help="Path to training JSONL")
    parser.add_argument("--trace", type=Path, help="Path to trace JSONL")
    parser.add_argument("--output", type=Path, help="Path to write validation report JSON")
    args = parser.parse_args()

    report = validate_all(args.manifest, args.training, args.trace, args.output)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["validation_status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
