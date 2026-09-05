#!/usr/bin/env python3

from __future__ import annotations

import json
import shutil
import subprocess
import unittest
import uuid
from pathlib import Path

from verify_repeat_runs import expected_report_names


DATA_DIR = Path(__file__).parent.parent / "data"
REPORTS = tuple(sorted(path.name for path in DATA_DIR.glob("p0-csharp-*-diff-report*.json")))
LEGACY_DIR = DATA_DIR / "legacy-shadow-diff"
SHADOW_DIFF = DATA_DIR.parent / "training" / "ShadowDiff" / "bin" / "Release" / "net9.0" / "STS2BestChoice.ShadowDiff.exe"
TEST_OUTPUT_DIR = DATA_DIR / "test-output"


def load(name: str) -> dict:
    path = DATA_DIR / name
    if not path.exists():
        path = LEGACY_DIR / name
    return json.loads(path.read_text(encoding="utf-8"))


def run_shadow_diff(trace_name: str, ordinal: int = 0) -> dict:
    """Run the current ShadowDiff binary into a temporary report path.

    Checked-in reports are historical evidence and are intentionally not
    rewritten by unit tests. A fresh invocation catches regressions in report
    confidence/quality classification independently of those artifacts.
    """
    if not SHADOW_DIFF.exists():
        raise unittest.SkipTest(f"ShadowDiff binary is not built: {SHADOW_DIFF}")
    trace_path = DATA_DIR / trace_name
    if not trace_path.exists():
        raise AssertionError(f"missing trace fixture: {trace_path}")
    # Keep the subprocess output inside the writable checkout. Some Windows
    # runners deny a child process access to the per-user `%TEMP%` ACL even
    # though pytest itself can create a temporary directory there.
    TEST_OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    # Copy the immutable input before launching the child. Probe runners may
    # regenerate the canonical trace concurrently; an isolated copy keeps a
    # report test from observing a delete/replace halfway through ReadLines.
    # Keep the original basename so ShadowDiff's derived fixture identifier
    # remains meaningful in diagnostics.
    run_dir = TEST_OUTPUT_DIR / f"shadow-diff-test-{uuid.uuid4().hex}"
    run_dir.mkdir(parents=True, exist_ok=False)
    trace_copy = run_dir / trace_path.name
    report_path = run_dir / "report.json"
    try:
        shutil.copyfile(trace_path, trace_copy)
        completed = subprocess.run(
            [str(SHADOW_DIFF), str(trace_copy), str(report_path), str(ordinal)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
            timeout=120,
        )
        # ShadowDiff uses exit 1 for a valid differential mismatch. Tests that
        # expect a green report still assert report["match"] below; negative
        # gate tests must be able to inspect the emitted mismatch payload.
        if completed.returncode not in (0, 1):
            raise AssertionError(
                f"ShadowDiff failed ({completed.returncode}): {completed.stderr[-1000:]}"
            )
        try:
            return json.loads(completed.stdout)
        except json.JSONDecodeError as exc:
            raise AssertionError(f"ShadowDiff emitted invalid JSON: {completed.stdout[-1000:]}") from exc
    finally:
        report_path.unlink(missing_ok=True)
        trace_copy.unlink(missing_ok=True)
        run_dir.rmdir()


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

    def test_p1_report_has_trace_and_comparison_metadata(self):
        report = load("p1-csharp-rage-diff-report-0.json")
        self.assertEqual(report["fixture"], "p1-power-rage")
        self.assertEqual(report["seed"], "probe-p1-rage-seed")
        self.assertEqual(report["cli_protocol_version"], "0.2.0")
        self.assertEqual(report["trace_schema"], 1)
        self.assertEqual(report["projected_comparison_hash"], report["actual_comparison_hash"])
        self.assertEqual(report["mismatches"], [])
        # Repeat hashes are added by verify_repeat_runs after the raw report
        # is generated and are absent from a freshly regenerated checkout.
        if report.get("repeat_sha256") is not None:
            self.assertRegex(report["repeat_sha256"], r"^[0-9a-f]{64}$")

    def test_unknown_random_target_is_not_reliable(self):
        report = run_shadow_diff("p1-card-random-target-trace.jsonl")
        self.assertTrue(report["match"])
        self.assertEqual(report["confidence"], "Estimated")
        self.assertTrue(report["chance_present"])
        self.assertEqual(report["random_operator"], "CombatTargets")
        self.assertFalse(report["probability_known"])
        self.assertEqual(report["outcome_quality"], "Unknown")
        self.assertIsNone(report["probability_mass_covered"])
        self.assertEqual(report["comparison_scope"], "aggregate_count_only")
        self.assertEqual(report["identity_comparison"], "omitted")
        self.assertEqual(report["rng_consumption_vector"], {"CombatTargets": 3})

    def test_unknown_random_exhaust_is_not_reliable(self):
        report = run_shadow_diff("p1-card-random-exhaust-trace.jsonl")
        self.assertTrue(report["match"])
        self.assertEqual(report["confidence"], "Uncalculable")
        self.assertEqual(report["random_operator"], "CombatCardSelection")
        self.assertFalse(report["probability_known"])
        self.assertEqual(report["outcome_quality"], "Unknown")
        self.assertEqual(report["comparison_scope"], "aggregate_count_only")
        self.assertEqual(report["identity_comparison"], "omitted")
        self.assertEqual(report["rng_consumption_vector"], {"CombatCardSelection": 1})

    def test_unknown_shuffle_placeholder_is_not_reliable(self):
        report = run_shadow_diff("p1-card-ethereal-trace.jsonl")
        self.assertTrue(report["match"])
        self.assertIn(report["confidence"], {"Estimated", "Uncalculable"})
        self.assertTrue(report["chance_present"])
        self.assertEqual(report["random_operator"], "Shuffle")
        self.assertFalse(report["probability_known"])
        self.assertEqual(report["outcome_quality"], "Unknown")
        self.assertEqual(report["comparison_scope"], "aggregate_count_only")
        self.assertEqual(report["identity_comparison"], "omitted")
        self.assertEqual(report["rng_consumption_vector"], {"Shuffle": 3})

    def test_teacher_ordered_end_turn_is_observed_conditioned(self):
        # Art of War retains a draw-pile snapshot whose length matches the
        # pre-state at the end-turn boundary, so the projection really uses
        # the ordered teacher pile (unlike a stale snapshot after HAVOC).
        report = run_shadow_diff("p0-art-of-war-trace.jsonl", ordinal=1)
        self.assertTrue(report["match"])
        # The simulator may additionally report an Uncalculable shuffle risk;
        # either way the privileged ordered pile can never be Reliable NOSL.
        self.assertIn(report["confidence"], {"Estimated", "Uncalculable"})
        self.assertTrue(report["chance_present"])
        # No shuffle counter changed in this fixture; the uncertainty comes
        # from conditioning on the hidden ordered pile, so the operator is
        # explicitly None rather than an invented RNG source.
        self.assertEqual(report["random_operator"], "None")
        self.assertFalse(report["probability_known"])
        self.assertEqual(report["outcome_quality"], "Unknown")
        self.assertEqual(report["comparison_scope"], "observed_conditioned")
        self.assertEqual(report["identity_comparison"], "observed")

    def test_stale_teacher_pile_remains_aggregate_only(self):
        # HAVOC consumes the last draw-pile card before end_turn; the earlier
        # teacher snapshot is therefore stale and must not be treated as the
        # order used by the projection.
        report = run_shadow_diff("p1-card-auto-play-trace.jsonl", ordinal=1)
        self.assertTrue(report["match"])
        self.assertIn(report["confidence"], {"Estimated", "Uncalculable"})
        self.assertEqual(report["comparison_scope"], "aggregate_count_only")
        self.assertEqual(report["identity_comparison"], "omitted")
        self.assertEqual(report["outcome_quality"], "Unknown")
        self.assertFalse(report["probability_known"])

    def test_strict_deterministic_replay_remains_reliable(self):
        report = run_shadow_diff("p0-bash-trace.jsonl")
        self.assertTrue(report["match"])
        self.assertEqual(report["confidence"], "Reliable")
        self.assertFalse(report["chance_present"])
        self.assertEqual(report["random_operator"], "None")
        self.assertTrue(report["probability_known"])
        self.assertEqual(report["outcome_quality"], "Exact")
        self.assertEqual(report["probability_mass_covered"], 1)
        self.assertEqual(report["comparison_scope"], "strict_public_state")
        self.assertEqual(report["identity_comparison"], "compared")

    def test_strict_intent_preview_reapplies_enemy_strength(self):
        report = run_shadow_diff("p1-relic-brimstone-trace.jsonl", ordinal=0)
        self.assertTrue(report["match"])
        self.assertEqual(report["mismatch_count"], 0)
        intent = next(item for item in report["fields"] if item["field"].endswith(".intents"))
        self.assertEqual(intent["projected"], ["Attack:12:1"])
        self.assertEqual(intent["actual"], ["Attack:12:1"])

    def test_player_shrink_does_not_reduce_enemy_intent_preview(self):
        report = run_shadow_diff("p1-relic-hand-drill-trace.jsonl", ordinal=1)
        self.assertTrue(report["match"])
        intent = next(item for item in report["fields"] if item["field"].endswith(".intents"))
        self.assertEqual(intent["projected"], ["Attack:7:1"])
        self.assertEqual(intent["actual"], ["Attack:7:1"])

    def test_aggregate_end_turn_does_not_claim_future_intent_identity(self):
        report = run_shadow_diff("p1-relic-fake-happy-flower-trace.jsonl", ordinal=4)
        self.assertTrue(report["match"])
        self.assertEqual(report["comparison_scope"], "aggregate_count_only")
        self.assertNotIn(
            "enemy.enemy:SEAPUNK:1.public_ai.intent_history",
            {item["field"] for item in report["fields"]},
        )

    def test_enemy_ids_detect_real_summon_missing_from_shadow(self):
        report = run_shadow_diff("m3c-enemy-ids-summon-trace.jsonl", ordinal=2)
        fields = {item["field"]: item for item in report["fields"]}
        self.assertFalse(report["match"])
        self.assertIn("enemy.ids", fields)
        self.assertFalse(fields["enemy.ids"]["match"])
        self.assertEqual(len(fields["enemy.ids"]["projected"]), 3)
        self.assertEqual(len(fields["enemy.ids"]["actual"]), 4)
        self.assertIn("enemy:TWO_TAILED_RAT:4", fields["enemy.ids"]["actual"])

    def test_enemy_ids_detect_projected_extra_enemy(self):
        report = run_shadow_diff("m3c-enemy-ids-extra-shadow-trace.jsonl", ordinal=0)
        fields = {item["field"]: item for item in report["fields"]}
        self.assertFalse(report["match"])
        self.assertFalse(fields["enemy.ids"]["match"])
        self.assertIn("enemy:SYNTHETIC_EXTRA:999", fields["enemy.ids"]["projected"])
        self.assertNotIn("enemy:SYNTHETIC_EXTRA:999", fields["enemy.ids"]["actual"])

    def test_chance_quality_mirror_is_complete_and_consistent(self):
        report = run_shadow_diff("p1-card-random-target-trace.jsonl")
        quality = report["chance_quality"]
        for key in (
            "chance_present", "random_operator", "probability_known",
            "outcome_quality", "probability_mass_covered",
            "effective_sample_size", "confidence_interval_low",
            "confidence_interval_high", "branch_probability",
            "rng_consumption_vector", "branch_enumerated",
            "comparison_scope", "identity_comparison",
        ):
            self.assertIn(key, quality)
            self.assertEqual(quality[key], report[key] if key != "branch_probability" else report[key])

    def test_closeout_manifest_matches_registered_probes(self):
        # Manifest size tracks the registered probe matrix: 71 through the P1
        # power batch, then 91/111/121/127/136/144/148/149/153/159/165/167/171/179
        # through relic batches 1-14, 183 after relic batch 16 + the two R1
        # holds, and 212 after card batch C1 (29 card reports), plus six
        # current closeout reports, plus eight D2 relic hook witnesses.
        names = expected_report_names()
        self.assertEqual(len(names), 226)
        actual = {path.name for path in DATA_DIR.glob("p0-csharp-*-diff-report*.json")}
        # The 607-row card direct-matrix reports are a separate coverage
        # artifact and are audited by card-specific tests; they are not part
        # of the registered P0/P1 closeout matrix.
        actual |= {
            path.name for path in DATA_DIR.glob("p1-csharp-*-diff-report*.json")
            if "card-direct-" not in path.name
        }
        self.assertEqual(actual, names)


if __name__ == "__main__":
    unittest.main()
