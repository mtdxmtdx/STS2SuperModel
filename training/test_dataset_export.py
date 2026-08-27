#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

from build_dataset_report import build
from jsonl_to_parquet import (
    arrow_safe,
    canonical_round_trip,
    convert,
    require_pyarrow,
    restore_empty_objects,
)
from trace_to_training import GENERATOR_CONFIG_HASH


DATA_DIR = Path(__file__).parent.parent / "data"
TRAINING = DATA_DIR / "p0-combat-action-training.jsonl"


class DatasetExportTests(unittest.TestCase):
    def test_generator_config_hash_is_concrete(self):
        self.assertRegex(GENERATOR_CONFIG_HASH, r"^[0-9A-F]{64}$")

    def test_empty_object_encoding_is_reversible(self):
        value = {"action_values": {}, "nested": [{"value": {}}]}
        self.assertEqual(restore_empty_objects(arrow_safe(value)), value)

    def test_arrow_null_fields_are_canonicalized(self):
        self.assertEqual(
            canonical_round_trip({"present": 1, "optional": None}),
            canonical_round_trip({"present": 1}),
        )

    def test_quality_report_is_deterministic(self):
        directory = Path(__file__).parent / "_dataset_export_test"
        directory.mkdir(exist_ok=True)
        try:
            first = directory / "first.json"
            second = directory / "second.json"
            report = build(TRAINING, first)
            build(TRAINING, second)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            self.assertEqual(report["row_count"], 3)
            self.assertEqual(report["action_count"], 17)
            self.assertEqual(report["missing_metadata"], {})
        finally:
            for path in directory.glob("*"):
                path.unlink()
            directory.rmdir()

    @unittest.skipUnless(
        importlib.util.find_spec("pyarrow") is not None,
        "PyArrow is not installed",
    )
    def test_parquet_round_trip(self):
        require_pyarrow()
        directory = Path(__file__).parent / "_parquet_export_test"
        directory.mkdir(exist_ok=True)
        try:
            manifest = convert(TRAINING, directory, 10_000)
            self.assertEqual(manifest["row_count"], 3)
            self.assertEqual(manifest["shard_count"], 1)
            self.assertRegex(manifest["shards"][0]["sha256"], r"^[0-9A-F]{64}$")
        finally:
            for path in directory.glob("*"):
                path.unlink()
            directory.rmdir()


if __name__ == "__main__":
    unittest.main()
