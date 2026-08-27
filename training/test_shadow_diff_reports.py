#!/usr/bin/env python3

from __future__ import annotations

import json
import unittest
from pathlib import Path


DATA_DIR = Path(__file__).parent.parent / "data"
REPORTS = tuple(sorted(path.name for path in DATA_DIR.glob("p0-csharp-*-diff-report*.json")))


def load(name: str) -> dict:
    return json.loads((DATA_DIR / name).read_text(encoding="utf-8"))


class ShadowDiffReportTests(unittest.TestCase):
    def test_all_current_p0_reports_match(self):
        for name in REPORTS:
            with self.subTest(name=name):
                report = load(name)
                self.assertTrue(report["match"])
                self.assertEqual(report["mismatch_count"], 0)
                self.assertEqual(report["game_version"], "v0.111.0")

    def test_power_transition_fields_are_compared(self):
        report = load("p0-csharp-bash-diff-report.json")
        fields = {item["field"]: item for item in report["fields"]}
        prefix = "power.enemy:SHRINKER_BEETLE:1.VULNERABLE_POWER"
        for suffix in ("present", "amount", "applier", "dynamic_vars", "counters"):
            self.assertIn(f"{prefix}.{suffix}", fields)
            self.assertTrue(fields[f"{prefix}.{suffix}"]["match"])

    def test_existing_power_affects_following_action(self):
        report = load("p0-csharp-bash-then-strike-diff-report.json")
        fields = {item["field"]: item for item in report["fields"]}
        damage_result = fields["enemy.enemy:SHRINKER_BEETLE:1.hp"]
        self.assertEqual(damage_result["projected"], 21)
        self.assertEqual(damage_result["actual"], 21)
        self.assertTrue(damage_result["match"])

    def test_relic_counter_and_dynamic_vars_are_compared(self):
        report = load("p0-csharp-nunchaku-diff-report.json")
        fields = {item["field"]: item for item in report["fields"]}
        self.assertEqual(fields["relic.NUNCHAKU.counter"]["projected"], 1)
        self.assertEqual(fields["relic.NUNCHAKU.counter"]["actual"], 1)
        self.assertTrue(fields["relic.NUNCHAKU.dynamic_vars"]["match"])

    def test_real_chance_trace_records_rng_consumption(self):
        trace_path = DATA_DIR / "p0-chance-entropic-trace.jsonl"
        rows = [json.loads(line) for line in trace_path.read_text(encoding="utf-8").splitlines() if line]
        action = next(row for row in rows if (row.get("normalized_action_id") or "").startswith("use_potion:"))
        branch = action["chance_branch"]
        self.assertTrue(action["produced_chance_branch"])
        self.assertTrue(branch["produced"])
        self.assertEqual(branch["kind"], "realized_rng_consumption")
        self.assertEqual(branch["rng_deltas"], {"CombatPotionGeneration": 6})
        self.assertEqual(branch["streams_changed"], ["CombatPotionGeneration"])
        self.assertFalse(branch["probability_known"])
        self.assertFalse(branch["branch_enumerated"])

    def test_power_trace_preserves_structured_identity_and_provenance(self):
        trace_path = DATA_DIR / "p0-demon-form-trace.jsonl"
        rows = [json.loads(line) for line in trace_path.read_text(encoding="utf-8").splitlines() if line]
        observed = []
        for row in rows:
            for view_name in ("public_observation", "teacher_snapshot"):
                view = row.get(view_name)
                if isinstance(view, dict):
                    observed.extend(view.get("player_powers") or [])
        demon = next(power for power in observed if power.get("id") == "DEMON_FORM_POWER")
        for field in (
            "id", "owner_id", "applier_id", "amount", "dynamic_vars",
            "internal_counters", "trigger_phases", "source", "source_version",
            "support", "evidence",
        ):
            self.assertIn(field, demon)
        self.assertEqual(demon["owner_id"], "player")
        self.assertEqual(demon["source_version"], "v0.111.0")
        self.assertEqual(demon["evidence"], "LiveObserved")


if __name__ == "__main__":
    unittest.main()
