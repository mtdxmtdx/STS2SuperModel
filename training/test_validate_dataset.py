#!/usr/bin/env python3
"""Unit tests for DatasetValidator testing valid real data and rejecting invalid fixtures."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from validate_dataset import DatasetValidator, validate_all

DATA_DIR = Path(__file__).parent.parent / "data"
MANIFEST_PATH = DATA_DIR / "p0-combat-action-manifest.json"
TRAINING_PATH = DATA_DIR / "p0-combat-action-training.jsonl"
TRACE_PATH = DATA_DIR / "p0-combat-action-trace.jsonl"
REPORT_OUTPUT = DATA_DIR / "p0-schema-validation-report.json"


class TestValidateDataset(unittest.TestCase):

    def test_real_p0_dataset_passes_100_percent(self):
        """Validates that current P0 production artifacts pass with 0 errors."""
        report = validate_all(
            manifest_path=MANIFEST_PATH,
            training_path=TRAINING_PATH,
            trace_path=TRACE_PATH,
            output_report_path=REPORT_OUTPUT,
        )
        self.assertEqual(report["validation_status"], "passed")
        self.assertEqual(report["error_count"], 0)
        self.assertEqual(report["public_leakage_count"], 0)
        self.assertGreater(report["total_rows_checked"], 5)

    def test_reject_version_lock_mismatch(self):
        """1. Rejects mixed game commit / version metadata."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "trace_id": "test", "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "WRONG_COMMIT",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "step": 0, "decision": "play",
                "post_state_hash": "A" * 64, "status": "ok"
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_trace_file(path)
            errors = [e for e in validator.errors if e.error_type == "version_lock_mismatch"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_public_privacy_leak_run_seed(self):
        """2. Rejects public view containing run_seed."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "record_id": "rec1", "schema_version": 1, "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "episode_id": "ep1", "character": "Ironclad",
                "ascension": 0, "act": 1, "floor": 1, "combat_id": "c1", "round": 1,
                "state_hash_public": "h1", "state_hash_teacher": "h2",
                "public_state": {"run_seed": "12345678", "hand": []},
                "legal_actions": [], "teacher_best_actions": [], "action_values": {},
                "confidence": "LowConfidence", "search_complete": False
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_training_file(path)
            self.assertGreater(validator.public_leakage_count, 0)
            errors = [e for e in validator.errors if e.error_type == "public_privacy_leak"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_public_privacy_leak_raw_rng_words(self):
        """3. Rejects public view containing raw RNG state words."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "record_id": "rec1", "schema_version": 1, "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "episode_id": "ep1", "character": "Ironclad",
                "ascension": 0, "act": 1, "floor": 1, "combat_id": "c1", "round": 1,
                "state_hash_public": "h1", "state_hash_teacher": "h2",
                "public_state": {"rng_raw_words": [1, 2, 3, 4]},
                "legal_actions": [], "teacher_best_actions": [], "action_values": {},
                "confidence": "LowConfidence", "search_complete": False
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_training_file(path)
            self.assertGreater(validator.public_leakage_count, 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_duplicate_action_id(self):
        """4. Rejects duplicate action_id in candidates."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "record_id": "rec1", "schema_version": 1, "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "episode_id": "ep1", "character": "Ironclad",
                "ascension": 0, "act": 1, "floor": 1, "combat_id": "c1", "round": 1,
                "state_hash_public": "h1", "state_hash_teacher": "h2",
                "public_state": {},
                "legal_actions": [
                    {"kind": "PlayCard", "action_id": "play:same_id", "source_instance_id": "card:1", "legal": True},
                    {"kind": "PlayCard", "action_id": "play:same_id", "source_instance_id": "card:2", "legal": True},
                ],
                "teacher_best_actions": [], "action_values": {},
                "confidence": "LowConfidence", "search_complete": False
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_training_file(path)
            errors = [e for e in validator.errors if e.error_type == "duplicate_action_id"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_missing_source_instance_id_on_play_card(self):
        """5. Rejects PlayCard without source_instance_id."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "record_id": "rec1", "schema_version": 1, "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "episode_id": "ep1", "character": "Ironclad",
                "ascension": 0, "act": 1, "floor": 1, "combat_id": "c1", "round": 1,
                "state_hash_public": "h1", "state_hash_teacher": "h2",
                "public_state": {},
                "legal_actions": [
                    {"kind": "PlayCard", "action_id": "play:strike", "legal": True}
                ],
                "teacher_best_actions": [], "action_values": {},
                "confidence": "LowConfidence", "search_complete": False
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_training_file(path)
            errors = [e for e in validator.errors if e.error_type == "missing_required_field"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_invalid_hash_format(self):
        """6. Rejects invalid hex-64 post_state_hash."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "trace_id": "test", "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "step": 0, "decision": "play",
                "post_state_hash": "NOT_A_VALID_64_HEX_HASH", "status": "ok"
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_trace_file(path)
            errors = [e for e in validator.errors if e.error_type == "invalid_hash_format"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_json_syntax_error(self):
        """7. Rejects malformed JSON syntax."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write("{\ninvalid json\n")
            path = Path(tf.name)

        try:
            validator.validate_trace_file(path)
            errors = [e for e in validator.errors if e.error_type in ("invalid_json", "truncated_json_line")]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_teacher_raw_rng_words_leak(self):
        """8. Rejects teacher snapshot with rng_raw_words_exposed=True."""
        validator = DatasetValidator()
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False, suffix=".jsonl") as tf:
            tf.write(json.dumps({
                "trace_id": "test", "trace_schema": 1,
                "game_version": "v0.111.0", "game_commit": "41cef1ea",
                "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
                "cli_protocol_version": "0.2.0", "step": 0, "decision": "play",
                "post_state_hash": "B" * 64, "status": "ok",
                "teacher_snapshot": {"available": True, "rng_raw_words_exposed": True}
            }) + "\n")
            path = Path(tf.name)

        try:
            validator.validate_trace_file(path)
            errors = [e for e in validator.errors if e.error_type == "raw_rng_words_leak"]
            self.assertGreater(len(errors), 0)
        finally:
            path.unlink(missing_ok=True)

    def test_reject_missing_extended_version_metadata(self):
        validator = DatasetValidator()
        row = json.loads(TRAINING_PATH.read_text(encoding="utf-8").splitlines()[0])
        del row["semantic_database_version"]
        path = Path(__file__).parent / "_invalid_version_metadata.jsonl"
        try:
            path.write_text(json.dumps(row) + "\n", encoding="utf-8")
            validator.validate_training_file(path)
            self.assertTrue(any(
                error.path == "semantic_database_version"
                for error in validator.errors
            ))
        finally:
            path.unlink(missing_ok=True)

    def test_reject_unstructured_power_and_relic(self):
        validator = DatasetValidator()
        row = json.loads(TRAINING_PATH.read_text(encoding="utf-8").splitlines()[0])
        row["public_state"]["enemies"][0]["powers"] = [{"id": "VULNERABLE_POWER", "amount": 2}]
        del row["public_state"]["player"]["relics"][0]["counter"]
        path = Path(__file__).parent / "_invalid_combat_objects.jsonl"
        try:
            path.write_text(json.dumps(row) + "\n", encoding="utf-8")
            validator.validate_training_file(path)
            schema_errors = [error for error in validator.errors if error.error_type == "schema_validation_error"]
            self.assertTrue(any("owner_id" in error.message for error in schema_errors))
            self.assertTrue(any("counter" in error.message for error in schema_errors))
        finally:
            path.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
