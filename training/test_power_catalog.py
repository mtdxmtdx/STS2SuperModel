#!/usr/bin/env python3
"""Unit tests for Power Tier 0 Catalog & Coverage."""

import json
import unittest
from pathlib import Path

from export_power_catalog import (
    CATALOG_PATH,
    COVERAGE_PATH,
    README_PATH,
    SCHEMA_PATH,
    load_catalog,
    load_coverage,
    validate_catalog,
)


class TestPowerCatalog(unittest.TestCase):

    def test_catalog_files_exist(self):
        self.assertTrue(CATALOG_PATH.exists())
        self.assertTrue(COVERAGE_PATH.exists())
        self.assertTrue(SCHEMA_PATH.exists())
        self.assertTrue(README_PATH.exists())

    def test_catalog_validation_passes(self):
        self.assertTrue(validate_catalog())

    def test_power_coverage_consistency(self):
        catalog = load_catalog()
        coverage = load_coverage()
        summary = coverage["summary"]

        self.assertEqual(len(catalog["powers"]), summary["total_powers"])
        self.assertEqual(summary["cataloged_count"], summary["total_powers"])
        self.assertEqual(summary["il_inspected_count"], 0)
        # Zero-mismatch v0.111 CLI/Core behavior probes; see P0_VERIFICATION.md
        # and P1_POWER_VERIFICATION.md (9 P0 powers + 11 P1 powers).
        self.assertEqual(summary["runtime_probed_count"], 20)
        self.assertEqual(summary["simulator_supported_count"], 20)
        self.assertGreater(summary["simulator_declared_count"], 50)

        # Check critical powers are simulator_supported
        power_map = {p["stable_id"]: p for p in catalog["powers"]}
        self.assertIn("STRENGTH", power_map)
        self.assertEqual(power_map["STRENGTH"]["simulator_support"], "simulator_supported")
        self.assertEqual(power_map["STRENGTH"]["evidence"], "LiveObserved")
        self.assertIn("VIGOR", power_map)
        self.assertEqual(power_map["VIGOR"]["simulator_support"], "simulator_supported")
        self.assertIn("DEXTERITY", power_map)
        self.assertEqual(power_map["DEXTERITY"]["simulator_support"], "simulator_supported")
        self.assertIn("VULNERABLE", power_map)
        self.assertEqual(power_map["VULNERABLE"]["simulator_support"], "simulator_supported")
        self.assertEqual(power_map["VULNERABLE"]["evidence"], "LiveObserved")
        self.assertIn("BARRICADE", power_map)
        self.assertEqual(power_map["BARRICADE"]["simulator_support"], "simulator_supported")
        for power_id in ("WEAK", "DEMON_FORM", "RUPTURE", "AFTERIMAGE"):
            self.assertEqual(power_map[power_id]["simulator_support"], "simulator_supported")
            self.assertEqual(power_map[power_id]["evidence"], "LiveObserved")

        # P1 promoted powers (data/P1_POWER_VERIFICATION.md).
        for power_id in ("THORNS", "ACCURACY", "PLATING", "POISON", "PANACHE", "RAGE", "FLAME_BARRIER", "CORRUPTION", "INFINITE_BLADES", "ENVENOM", "BUFFER"):
            self.assertIn(power_id, power_map)
            self.assertEqual(power_map[power_id]["simulator_support"], "simulator_supported")
            self.assertEqual(power_map[power_id]["evidence"], "LiveObserved")


if __name__ == "__main__":
    unittest.main()
