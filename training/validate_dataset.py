#!/usr/bin/env python3
"""Streaming schema and integrity validator for STS2 SuperModel dataset artifacts.

Validates trace files, training decision records, and dataset manifests
against P0 Schema v1 rules using only standard library modules (plus
jsonschema for the Draft 2020-12 schemas).

P1 data-quality additions over the P0 baseline:

- complete metadata presence checks (generator_config_hash included);
- manifest count cross-checks against streaming-observed label statistics;
- strengthened recursive public-view leakage scanning (run_seed, raw RNG
  words / four-state-word arrays, future draw identities, teacher-only pile
  orders, feature-list containers);
- stable action id gaps reported with file and line number;
- duplicate public_state_hash x action-combination detection with explicit
  warning/error classification;
- explicit category labels for empty teacher_best_actions records;
- truncated vs. invalid JSONL line disambiguation with file+line location.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
from collections import Counter
from dataclasses import asdict, dataclass, field
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

# Keys that must never appear anywhere inside the public view. The four RNG
# state words and teacher-only ordered piles are explicitly enumerated; seed
# material of any spelling is forbidden as well.
FORBIDDEN_PUBLIC_KEYS = {
    "run_seed",
    "run_seed_hash",
    "seed",
    "seed_group",
    "rng_raw_words",
    "raw_rng_words",
    "rng_words",
    "rng_state_words",
    "future_draw_order",
    "future_draw_identities",
    "draw_order",
    "teacher_only_state",
    "teacher_only_draw_pile",
    "teacher_only_discard_pile",
    "teacher_only_pile_order",
}

# Ordered card-identity pile listings belong to the teacher view only. The
# public view may carry *_count fields but never the identity lists.
PUBLIC_PILE_IDENTITY_KEYS = {"draw_pile", "discard_pile", "exhaust_pile"}

# Feature containers (if a producer embeds an explicit feature list) inherit
# the same forbidden-key rules as the public view: seeds never enter features.
FEATURE_CONTAINER_KEYS = {"features", "feature_vector", "feature_list"}

HEX64_PATTERN = re.compile(r"^[0-9A-Fa-f]{64}$")

ACTION_SIGNATURE_FIELDS = ("kind", "action_id", "source_instance_id", "target_id", "choice_id", "legal")

LABEL_VALUES = ("Reliable", "Estimated", "LowConfidence", "Uncalculable")
LABEL_QUALITY_VALUES = (
    "ExactComplete", "ExactWithKnownChance", "SampledWithConfidenceInterval",
    "BudgetBound", "EstimatedByHeuristic", "Uncalculable",
)


@dataclass
class ValidationError:
    file: str
    line_no: int
    path: str
    message: str
    error_type: str
    severity: str = "error"

    def locate(self) -> str:
        return f"{self.file}:{self.line_no}: {self.path}: {self.message} [{self.error_type}]"


@dataclass
class RowStats:
    """Streaming counters used for manifest cross-checking."""

    training_rows: int = 0
    trace_rows: int = 0
    actions_seen: int = 0
    labels: Counter = field(default_factory=Counter)
    episodes: set = field(default_factory=set)
    state_hashes: set = field(default_factory=set)
    episode_seed_groups: Dict[str, set] = field(default_factory=dict)


class DatasetValidator:
    def __init__(self):
        self.errors: List[ValidationError] = []
        self.warnings: List[ValidationError] = []
        self.checked_files: List[str] = []
        self.rows_checked: int = 0
        self.public_leakage_count: int = 0
        self.stable_id_missing: int = 0
        self.stats = RowStats()
        # (state_hash, action-signature) -> list[(file, line_no)]
        self._state_combos: Dict[Tuple[str, str], List[Tuple[str, int]]] = {}
        self._state_combo_labels: Dict[Tuple[str, str], List[str]] = {}
        self.duplicate_combo_warnings: List[Dict[str, Any]] = []
        self.empty_teacher_categories: Counter = Counter()
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

    def add_error(self, file: str, line_no: int, path: str, message: str, error_type: str,
                  severity: str = "error") -> None:
        entry = ValidationError(
            file=str(file),
            line_no=line_no,
            path=path,
            message=f"(line {line_no}) {message}",
            error_type=error_type,
            severity=severity,
        )
        if severity == "warning":
            self.warnings.append(entry)
        else:
            self.errors.append(entry)

    def _check_version_lock(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        for key, expected in LOCK.items():
            val = row.get(key)
            if val != expected:
                self.add_error(
                    file, line_no, key,
                    f"Version lock mismatch: {key}={val!r}, expected {expected!r}",
                    "version_lock_mismatch"
                )

    def _check_generator_config_hash(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        gch = row.get("generator_config_hash")
        if gch in (None, ""):
            self.add_error(
                file, line_no, "generator_config_hash",
                "generator_config_hash is required on every training record",
                "missing_generator_config_hash",
            )
        elif not isinstance(gch, str) or not HEX64_PATTERN.match(gch):
            self.add_error(
                file, line_no, "generator_config_hash",
                f"generator_config_hash must be a SHA-256 hex string, got {gch!r}",
                "invalid_generator_config_hash_format",
            )

    def _check_episode_seed_group_consistency(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        episode_id = row.get("episode_id") or "(missing_episode)"
        seed_group = row.get("seed_group", "(none)")
        known = self.stats.episode_seed_groups.setdefault(episode_id, set())
        known.add(seed_group)
        if len(known) > 1:
            self.add_error(
                file, line_no, "seed_group",
                f"Episode {episode_id!r} carries inconsistent seed groups: {sorted(map(str, known))}",
                "seed_group_inconsistent",
            )

    def _is_raw_word_array(self, value: Any) -> bool:
        """Heuristic: a bare array of exactly four integers is RNG state."""
        if not isinstance(value, list) or len(value) != 4:
            return False
        return all(isinstance(item, int) and not isinstance(item, bool) and 0 <= item <= 0xFFFFFFFF for item in value)

    def _check_public_privacy(self, obj: Any, file: str, line_no: int, current_path: str = "public_state") -> None:
        if isinstance(obj, dict):
            for k, v in obj.items():
                p = f"{current_path}.{k}"
                key_lower = k.lower() if isinstance(k, str) else k
                if key_lower in FORBIDDEN_PUBLIC_KEYS:
                    self.public_leakage_count += 1
                    self.add_error(
                        file, line_no, p,
                        f"Forbidden public field detected: {k!r} with value {v!r}",
                        "public_privacy_leak"
                    )
                elif key_lower in PUBLIC_PILE_IDENTITY_KEYS and isinstance(v, list) and v:
                    self.public_leakage_count += 1
                    self.add_error(
                        file, line_no, p,
                        f"Teacher-only ordered pile {k!r} leaked into public view "
                        f"({len(v)} identity entries)",
                        "public_pile_order_leak",
                    )
                elif key_lower in FEATURE_CONTAINER_KEYS and isinstance(v, dict):
                    for fk in v:
                        if (fk.lower() if isinstance(fk, str) else fk) in FORBIDDEN_PUBLIC_KEYS:
                            self.public_leakage_count += 1
                            self.add_error(
                                file, line_no, f"{p}.{fk}",
                                f"Seed/RNG material {fk!r} found inside feature container {k!r}",
                                "public_feature_leak",
                            )
                if self._is_raw_word_array(v):
                    self.public_leakage_count += 1
                    self.add_error(
                        file, line_no, p,
                        f"Public field {k!r} looks like raw RNG state words "
                        f"(array of exactly four integers): {v!r}",
                        "raw_rng_words_leak",
                    )
                self._check_public_privacy(v, file, line_no, p)
        elif isinstance(obj, list):
            for idx, item in enumerate(obj):
                self._check_public_privacy(item, file, line_no, f"{current_path}[{idx}]")

    def _action_signature(self, candidates: Any) -> str:
        rows = []
        if isinstance(candidates, list):
            for cand in candidates:
                if isinstance(cand, dict):
                    rows.append({key: cand.get(key) for key in ACTION_SIGNATURE_FIELDS})
        return json.dumps(rows, sort_keys=True, ensure_ascii=False)

    def _register_state_combo(self, row: Dict[str, Any], signature_source: Any,
                              file: str, line_no: int) -> None:
        state_hash = row.get("state_hash_public") or row.get("public_state_hash") or ""
        if not state_hash:
            return
        combo_key = (str(state_hash), self._action_signature(signature_source))
        label_fingerprint = json.dumps(
            [row.get("confidence"), row.get("teacher_best_actions"), row.get("risk_events")],
            sort_keys=True, ensure_ascii=False,
        )
        occurrences = self._state_combos.setdefault(combo_key, [])
        fingerprints = self._state_combo_labels.setdefault(combo_key, [])
        occurrences.append((str(file), line_no))
        is_new_label = label_fingerprint not in fingerprints
        fingerprints.append(label_fingerprint)
        if len(occurrences) == 1:
            return
        conflicting = len(set(fingerprints)) > 1
        entry = {
            "state_hash_public": combo_key[0],
            "occurrences": [
                {"file": occ_file, "line": occ_line}
                for occ_file, occ_line in occurrences
            ],
            "classification": "error_conflicting_labels" if conflicting else "warning_identical_duplicate",
        }
        if conflicting:
            self.add_error(
                file, line_no, "state_hash_public",
                f"Duplicate public_state_hash x action combination with conflicting labels at "
                f"{occurrences[0][0]}:{occurrences[0][1]} and {file}:{line_no}",
                "duplicate_state_conflict",
            )
            self.duplicate_combo_warnings.append(entry | {"severity": "error"})
        else:
            self.add_error(
                file, line_no, "state_hash_public",
                f"Identical public_state_hash x action combination repeated from "
                f"{occurrences[0][0]}:{occurrences[0][1]}; redundant rows should be merged",
                "duplicate_state_combo",
                severity="warning",
            )
            self.duplicate_combo_warnings.append(entry | {"severity": "warning"})

    def _classify_empty_teacher_best_actions(self, row: Dict[str, Any], file: str, line_no: int) -> Optional[str]:
        if row.get("teacher_best_actions"):
            return None
        confidence = row.get("confidence")
        risk_events = {str(evt) for evt in row.get("risk_events", []) or []}
        if confidence == "Uncalculable" or "teacher_snapshot_missing" in risk_events:
            category = "uncalculable_teacher_unavailable"
        elif confidence in ("LowConfidence", None) or "teacher_label_missing" in risk_events:
            category = "low_confidence_label_missing"
        else:
            category = "reliable_or_estimated_without_label"
            self.add_error(
                file, line_no, "teacher_best_actions",
                f"confidence={confidence!r} requires a non-empty policy label; empty "
                f"teacher_best_actions here would silently drop supervision",
                "reliable_without_teacher_label",
            )
        self.empty_teacher_categories[category] += 1
        return category

    def _check_policy_label_position(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        if row.get("confidence") == "Uncalculable" and row.get("teacher_best_actions"):
            self.add_error(
                file, line_no, "confidence",
                "Uncalculable record occupies the policy main-label position "
                "(non-empty teacher_best_actions); such rows must stay out of policy supervision",
                "uncalculable_policy_label",
            )

    def _check_label_quality(self, row: Dict[str, Any], file: str, line_no: int) -> None:
        quality = row.get("label_quality")
        if quality is None:
            return
        if quality not in LABEL_QUALITY_VALUES:
            self.add_error(file, line_no, "label_quality", f"Invalid label_quality: {quality!r}", "invalid_value")
            return
        confidence = row.get("confidence")
        actions = row.get("teacher_best_actions") or []
        complete = row.get("search_complete") is True
        if quality in ("ExactComplete", "ExactWithKnownChance") and (confidence != "Reliable" or not actions or not complete):
            self.add_error(file, line_no, "label_quality",
                           f"{quality} requires Reliable confidence, non-empty teacher_best_actions and search_complete=true",
                           "label_quality_contract")
        elif quality == "SampledWithConfidenceInterval" and confidence == "Reliable":
            self.add_error(file, line_no, "label_quality",
                           "SampledWithConfidenceInterval cannot be marked Reliable", "label_quality_contract")
        elif quality == "BudgetBound" and complete:
            self.add_error(file, line_no, "label_quality",
                           "BudgetBound requires search_complete=false", "label_quality_contract")
        elif quality == "EstimatedByHeuristic" and confidence == "Reliable":
            self.add_error(file, line_no, "label_quality",
                           "EstimatedByHeuristic cannot be marked Reliable", "label_quality_contract")
        elif quality == "Uncalculable" and actions:
            self.add_error(file, line_no, "label_quality",
                           "Uncalculable records must not contain teacher_best_actions", "label_quality_contract")

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

            if action_id in (None, ""):
                self.stable_id_missing += 1
                self.add_error(file, line_no, f"{cand_path}.action_id",
                               f"Missing or empty stable action id at candidate index {idx}", "missing_required_field")
            elif not isinstance(action_id, str):
                self.stable_id_missing += 1
                self.add_error(file, line_no, f"{cand_path}.action_id",
                               f"Stable action id must be a string, got {type(action_id).__name__}",
                               "invalid_type")
            elif action_id in seen_action_ids:
                self.add_error(file, line_no, f"{cand_path}.action_id", f"Duplicate action_id: {action_id!r}", "duplicate_action_id")
            else:
                seen_action_ids.add(action_id)

            if kind not in ("PlayCard", "UsePotion", "Choice", "EndTurn"):
                self.add_error(file, line_no, f"{cand_path}.kind", f"Invalid action kind: {kind!r}", "invalid_value")

            if kind in ("PlayCard", "UsePotion"):
                src_inst = cand.get("source_instance_id")
                if src_inst in (None, ""):
                    self.stable_id_missing += 1
                    self.add_error(file, line_no, f"{cand_path}.source_instance_id",
                                   f"{kind} requires non-empty source_instance_id at candidate index {idx}",
                                   "missing_required_field")
            if kind == "Choice":
                if not cand.get("choice_id"):
                    self.add_error(file, line_no, f"{cand_path}.choice_id", "Choice requires non-empty choice_id", "missing_required_field")
                if not isinstance(cand.get("selected_card_instance_ids"), list):
                    self.add_error(file, line_no, f"{cand_path}.selected_card_instance_ids", "Choice requires selected_card_instance_ids", "missing_required_field")

            if not isinstance(legal, bool):
                self.add_error(file, line_no, f"{cand_path}.legal", "legal field must be boolean", "invalid_type")

    def _iter_jsonl_rows(self, path: Path) -> Any:
        """Yield (line_no, parsed_row_or_None, decode_error_or_None).

        Distinguishes a truncated final line (no trailing newline, partial JSON)
        from ordinary invalid JSON elsewhere in the file.
        """
        raw_lines: List[bytes] = []
        with path.open("rb") as handle:
            for raw in handle:
                raw_lines.append(raw)
        ends_with_newline = bool(raw_lines) and raw_lines[-1].endswith(b"\n")
        last_line_no = len(raw_lines)
        for line_no, raw in enumerate(raw_lines, 1):
            text = raw.decode("utf-8", errors="replace").strip()
            if not text:
                continue
            try:
                yield line_no, json.loads(text), None
            except json.JSONDecodeError as exc:
                truncated = line_no == last_line_no and not ends_with_newline
                reason = (
                    f"truncated final line (no newline terminator, parser stopped at column "
                    f"{exc.colno}): {exc.msg}"
                    if truncated
                    else f"invalid JSON at column {exc.colno}: {exc.msg}"
                )
                error_type = "truncated_json_line" if truncated else "invalid_json"
                self.add_error(str(path), line_no, "", f"Unparseable JSONL row: {reason}", error_type)
                yield line_no, None, exc

    def validate_trace_file(self, path: Path) -> int:
        """Validate raw trace JSONL file."""
        self.checked_files.append(str(path))
        count = 0
        if not path.exists():
            self.add_error(str(path), 0, "", "File does not exist", "file_not_found")
            return 0

        for line_no, row, exc in self._iter_jsonl_rows(path):
            if exc is not None:
                continue
            count += 1
            self.rows_checked += 1
            self.stats.trace_rows += 1

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

        for line_no, row, exc in self._iter_jsonl_rows(path):
            if exc is not None:
                continue
            count += 1
            self.rows_checked += 1
            self.stats.training_rows += 1

            if not isinstance(row, dict):
                self.add_error(str(path), line_no, "", "Row must be a JSON object", "invalid_type")
                continue

            self._check_version_lock(row, str(path), line_no)
            self._check_generator_config_hash(row, str(path), line_no)
            self._check_schema("training", row, str(path), line_no)
            if row.get("schema_version") != 1:
                self.add_error(str(path), line_no, "schema_version", f"schema_version must be 1, got {row.get('schema_version')!r}", "invalid_value")

            for req in ("record_id", "episode_id", "character", "combat_id", "state_hash_public", "state_hash_teacher"):
                if not row.get(req):
                    self.add_error(str(path), line_no, req, f"Missing required field {req!r}", "missing_required_field")

            confidence = row.get("confidence")
            if confidence not in LABEL_VALUES:
                self.add_error(str(path), line_no, "confidence", f"Invalid confidence: {confidence!r}", "invalid_value")
            else:
                self.stats.labels[confidence] += 1

            if row.get("episode_id"):
                self.stats.episodes.add(row["episode_id"])
            state_hash = row.get("state_hash_public")
            if state_hash:
                self.stats.state_hashes.add(state_hash)
            self._check_episode_seed_group_consistency(row, str(path), line_no)

            legal_actions = row.get("legal_actions")
            self.stats.actions_seen += len(legal_actions) if isinstance(legal_actions, list) else 0

            # Public state privacy check
            public_state = row.get("public_state")
            if public_state is not None:
                self._check_schema("public", public_state, str(path), line_no, "public_state")
                self._check_public_privacy(public_state, str(path), line_no, "public_state")

            # Legal actions check
            self._check_action_candidates(legal_actions, str(path), line_no, "legal_actions")

            # Duplicate public_state_hash x action-combination detection
            self._register_state_combo(row, legal_actions, str(path), line_no)

            # Empty teacher label classification + policy-label position rule
            self._classify_empty_teacher_best_actions(row, str(path), line_no)
            self._check_policy_label_position(row, str(path), line_no)
            self._check_label_quality(row, str(path), line_no)

        return count

    def cross_check_manifest_counts(self, manifest: Dict[str, Any], manifest_file: str = "<manifest>") -> int:
        """Compare manifest counts against values observed while streaming.

        Must run after ``validate_training_file`` so stats are populated.
        Returns the number of mismatches recorded.
        """
        observed = {
            "row_count": self.stats.training_rows,
            "state_count": len(self.stats.state_hashes),
            "action_count": self.stats.actions_seen,
            "reliable_count": self.stats.labels["Reliable"],
            "estimated_count": self.stats.labels["Estimated"],
            "uncalculable_count": self.stats.labels["Uncalculable"],
        }
        mismatches = 0
        for key, actual in observed.items():
            expected = manifest.get(key)
            if expected is None:
                continue
            if expected != actual:
                mismatches += 1
                self.add_error(
                    manifest_file, 1, key,
                    f"Manifest {key}={expected!r} does not match observed count {actual!r}",
                    "manifest_count_mismatch",
                )
        manifest_counts = manifest.get("confidence_counts")
        if isinstance(manifest_counts, dict):
            for level in (*LABEL_VALUES, "Unknown"):
                expected = manifest_counts.get(level)
                if expected is None:
                    continue
                actual = self.stats.labels[level]
                if expected != actual:
                    mismatches += 1
                    self.add_error(
                        manifest_file, 1, f"confidence_counts.{level}",
                        f"Manifest confidence_counts[{level}]={expected!r} does not match observed {actual!r}",
                        "manifest_count_mismatch",
                    )
        return mismatches

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

        for req in ("dataset_id", "split_policy", "row_count", "state_count", "action_count",
                    "source_hashes", "created_at_utc", "simulator_version", "scorer_version",
                    "feature_schema_version", "model_version", "generator_config_hash",
                    "feature_config_hash"):
            if req not in manifest:
                self.add_error(str(path), 1, req, f"Missing required manifest field {req!r}", "missing_required_field")

        generator_hash = manifest.get("generator_config_hash")
        if generator_hash in (None, ""):
            self.add_error(str(path), 1, "generator_config_hash",
                           "generator_config_hash is required (datasets without a concrete generator config are rejected)",
                           "missing_generator_config_hash")
        elif not isinstance(generator_hash, str) or not HEX64_PATTERN.match(generator_hash):
            self.add_error(str(path), 1, "generator_config_hash",
                           f"generator_config_hash must be SHA-256 hex, got {generator_hash!r}",
                           "invalid_generator_config_hash_format")

        source_hashes = manifest.get("source_hashes", [])
        if not isinstance(source_hashes, list) or len(source_hashes) == 0:
            self.add_error(str(path), 1, "source_hashes", "source_hashes must be a non-empty list", "invalid_value")
        else:
            for idx, h in enumerate(source_hashes):
                if not HEX64_PATTERN.match(str(h)):
                    self.add_error(str(path), 1, f"source_hashes[{idx}]", f"Invalid SHA-256 hash: {h!r}", "invalid_hash_format")

        return manifest

    def get_report(self) -> Dict[str, Any]:
        malformed = [
            {"file": e.file, "line": e.line_no, "error": e.message, "kind": e.error_type}
            for e in self.errors
            if e.error_type in ("truncated_json_line", "invalid_json")
        ]
        reliable = self.stats.labels["Reliable"]
        estimated = self.stats.labels["Estimated"]
        uncalculable = self.stats.labels["Uncalculable"]
        low_confidence = self.stats.labels["LowConfidence"]
        unknown = sum(count for level, count in self.stats.labels.items()
                      if level not in LABEL_VALUES)
        empty_total = sum(self.empty_teacher_categories.values())
        dup_errors = [d for d in self.duplicate_combo_warnings if d["severity"] == "error"]
        dup_warnings = [d for d in self.duplicate_combo_warnings if d["severity"] == "warning"]
        report = {
            "schema_version": 1,
            "game_version": LOCK["game_version"],
            "game_commit": LOCK["game_commit"],
            "assembly_sha256": LOCK["assembly_sha256"],
            "cli_protocol_version": LOCK["cli_protocol_version"],
            "validation_status": "passed" if len(self.errors) == 0 else "failed",
            "total_files_checked": len(self.checked_files),
            "total_rows_checked": self.rows_checked,
            "row_counts": {
                "trace_rows": self.stats.trace_rows,
                "training_rows": self.stats.training_rows,
                "episodes": len(self.stats.episodes),
                "states_unique": len(self.stats.state_hashes),
                "actions_total": self.stats.actions_seen,
            },
            "label_stats": {
                "reliable": reliable,
                "estimated": estimated,
                "low_confidence": low_confidence,
                "uncalculable": uncalculable,
                "unknown": unknown,
                "empty_teacher_best_actions": empty_total,
                "empty_teacher_categories": dict(sorted(self.empty_teacher_categories.items())),
            },
            "stable_id_missing": self.stable_id_missing,
            "public_leakage_count": self.public_leakage_count,
            "duplicate_states": {
                "warning_count": len(dup_warnings),
                "error_count": len(dup_errors),
                "entries": self.duplicate_combo_warnings,
                "classification_legend": {
                    "warning_identical_duplicate": "same hash + same combo + same labels: redundant, merge recommended",
                    "error_conflicting_labels": "same hash + same combo but differing labels/confidence: dataset corruption",
                },
            },
            "malformed_lines": malformed,
            "warning_count": len(self.warnings),
            "warnings": [asdict(w) for w in self.warnings],
            "error_count": len(self.errors),
            "errors": [asdict(e) for e in sorted(self.errors, key=lambda x: (x.file, x.line_no, x.path))],
            "checked_files": sorted(self.checked_files),
        }
        return report


def validate_all(
    manifest_path: Optional[Path] = None,
    training_path: Optional[Path] = None,
    trace_path: Optional[Path] = None,
    output_report_path: Optional[Path] = None,
    cross_check_manifest: bool = True,
) -> Dict[str, Any]:
    validator = DatasetValidator()
    manifest_obj: Dict[str, Any] = {}
    manifest_path = Path(manifest_path) if manifest_path else None
    training_path = Path(training_path) if training_path else None
    trace_path = Path(trace_path) if trace_path else None
    if manifest_path:
        manifest_obj = validator.validate_manifest_file(manifest_path)
    if training_path:
        validator.validate_training_file(training_path)
    if trace_path:
        validator.validate_trace_file(trace_path)
    if manifest_path and training_path and cross_check_manifest:
        validator.cross_check_manifest_counts(manifest_obj, str(manifest_path))

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
