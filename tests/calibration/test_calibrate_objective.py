"""Mutation regressions for the offline M3 evidence tool, not simulated game tests."""
import copy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("calibrate_objective", ROOT / "tools/calibrate_objective.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def outcome():
    return {
        "terminalKind": "Win", "playerAlive": True, "hpAtCombatStart": 60, "hpAfterSettlement": 57,
        "maxHpStart": 70, "maxHpAfterSettlement": 70, "settlementComplete": True,
        "settlementProfileId": "test-only-endpoint", "continuationPolicyId": "test-only-policy",
        "inventorySnapshotsComplete": True, "inventoryStart": [], "inventoryEnd": [],
        "resourceEvents": [], "resourceProvenanceComplete": False,
        "permanentChangesComplete": True, "permanentChanges": [],
        "hpEventDiagnosticsComplete": False, "cumulativeHpDamage": 3, "healingReceived": None,
        "specifiedFinishSuccess": None, "earnedBonus": None, "deadlineMet": None,
        "persistentAssetsAtStartJson": '{"maxHp":70}', "persistentAssetsAfterSettlementJson": '{"maxHp":70}',
    }


def record():
    masks = {key: True for key in ("value", "win_probability", "death_probability", "expected_final_hp", "hp_distribution", "potion_net_change")}
    return {
        "public_input": {"observation": {"startHp": 60, "hp": 60, "potions": []}, "candidate_actions": [{"kind": "end_turn"}]},
        "targets": {"actions": [{"action_index": 0, "allocated_worlds": 4, "completed_worlds": 4,
            "truncated_worlds": 0, "error_worlds": 0, "other_worlds": 0, "quality": "complete", "masks": masks,
            "value": -3.03, "win_probability": 1.0, "death_probability": 0.0, "expected_final_hp": 57.0,
            "potion_net_change": 0.0, "hp_distribution": [{"hp": 57, "probability": 1.0}]}]},
        "audit_only": {"source_run_group": "fixture-run", "source_combat_id": "fixture-combat", "generation_source_index": 0,
            "versions": {"objective": "nosl_silent_a10_terminal_v4_candidate"}, "objective_calibrated": False,
            "objective_version": "nosl_silent_a10_terminal_v4_candidate", "n_independent_eval": 4,
            "sampler_seeds": [11, 12, 13, 14], "exploration_seeds": [],
            "outcome_samples": [{"action_index": 0, "outcomes": [outcome() for _ in range(4)]}]},
    }


class CalibrationToolTests(unittest.TestCase):
    def check_rejected(self, row, name):
        result = m.audit_records([row])
        self.assertFalse(result["audit_passed"])
        self.assertTrue(any(name in x["check"] for x in result["failures_first_50"]))

    def test_exact_synthetic_evidence_does_not_calibrate_profile(self):
        evidence = m.report()
        self.assertEqual(evidence["boundary_check_count"], 10)
        self.assertFalse(evidence["candidate"]["calibrated"])
        self.assertFalse(evidence["formal_labels_allowed"])
        self.assertAlmostEqual(evidence["identified_family_constraints"]["eta_strict_upper"], 150 / 13)
        self.assertLess(evidence["fixed_sample_success_illustrations"][0]["hoeffding_lower"], .8)

    def test_valid_saved_facts_reconcile_without_synthetic_game_claim(self):
        result = m.audit_records([record()])
        self.assertTrue(result["audit_passed"])
        self.assertEqual(result["counts"]["outcomes"], 4)
        self.assertEqual(result["counts"]["reconciled_utility_actions"], 1)
        self.assertEqual(result["counts"]["bonus_fields_all_present"], 0)
        self.assertFalse(result["formal_labels_allowed"])

    def test_omitted_world_is_not_renormalized(self):
        row = record(); row["audit_only"]["outcome_samples"][0]["outcomes"].pop()
        self.check_rejected(row, "allocated_world_conservation")

    def test_engine_error_does_not_count_as_death_or_terminal_hp(self):
        row = record(); row["audit_only"]["outcome_samples"][0]["outcomes"][0]["terminalKind"] = "EngineError"
        self.check_rejected(row, "utility_mask_mismatch")
        self.assertIsNone(m.outcome_facts({"terminalKind": "EngineError"})["cost"])

    def test_malformed_terminal_hp_is_rejected(self):
        row = record(); row["audit_only"]["outcome_samples"][0]["outcomes"][0]["hpAfterSettlement"] = 0
        self.check_rejected(row, "invalid_outcome")

    def test_combat_start_anchor_cannot_move(self):
        row = record(); row["audit_only"]["outcome_samples"][0]["outcomes"][0]["hpAtCombatStart"] = 57
        self.check_rejected(row, "combat_start_anchor_changed")

    def test_unknown_equal_inventory_cancels_only_on_win(self):
        o = outcome(); o["inventoryStart"] = o["inventoryEnd"] = [{"resourceId": "UnknownPotion", "count": 1}]
        self.assertAlmostEqual(m.outcome_facts(o)["cost"], 3.03)
        o.update(terminalKind="Loss", playerAlive=False, hpAfterSettlement=0)
        self.assertIsNone(m.outcome_facts(o)["cost"])

    def test_unpriced_consumption_cannot_become_zero_utility(self):
        row = record()
        for o in row["audit_only"]["outcome_samples"][0]["outcomes"]:
            o["inventoryStart"] = [{"resourceId": "UnknownPotion", "count": 1}]
        self.check_rejected(row, "utility_mask_mismatch")
        row["targets"]["actions"][0].update(value=None, quality="objective_value_unresolved", potion_net_change=-1)
        row["targets"]["actions"][0]["masks"]["value"] = False
        self.assertTrue(m.audit_records([row])["audit_passed"])

    def test_generated_then_consumed_resource_events_are_not_a_second_charge(self):
        o = outcome()
        o["resourceEvents"] = [{"kind": "consumed", "resourceId": "X", "quantity": 1}, {"kind": "generated", "resourceId": "X", "quantity": 1}]
        self.assertAlmostEqual(m.outcome_facts(o)["cost"], 3.03)

    def test_complete_hp_diagnostics_require_adjustment_and_reconciliation_without_changing_cost(self):
        o = outcome()
        o.update(hpEventDiagnosticsComplete=True, cumulativeHpDamage=5, healingReceived=4, otherHpAdjustment=-2)
        self.assertAlmostEqual(m.outcome_facts(o)["cost"], 3.03)
        del o["otherHpAdjustment"]
        with self.assertRaisesRegex(ValueError, "missing or invalid complete HP diagnostics"):
            m.outcome_facts(o)
        o["otherHpAdjustment"] = 0
        with self.assertRaisesRegex(ValueError, "do not reconcile"):
            m.outcome_facts(o)

    def test_permanent_reward_opportunity_has_no_invented_price(self):
        o = outcome(); o["permanentChanges"] = [{"kind": "earned_extra_reward_opportunity:GoldReward", "amount": 1}]
        self.assertIsNone(m.outcome_facts(o)["cost"])

    def test_missing_ledger_flags_and_max_hp_change_fail_closed(self):
        o = outcome(); o["inventorySnapshotsComplete"] = False
        self.assertIsNone(m.outcome_facts(o)["cost"])
        o = outcome(); o["permanentChangesComplete"] = False
        self.assertIsNone(m.outcome_facts(o)["cost"])
        o = outcome(); o["maxHpAfterSettlement"] = 71
        self.assertIn("permanent_change_not_recorded:max_hp", m.outcome_facts(o)["unresolved"])

    def test_hp_and_death_targets_must_match_raw_facts(self):
        row = record(); row["targets"]["actions"][0]["expected_final_hp"] = 60
        self.check_rejected(row, "terminal_target_mismatch:expected_final_hp")
        row = record(); row["targets"]["actions"][0]["death_probability"] = 1
        self.check_rejected(row, "terminal_target_mismatch:death_probability")

    def test_fabricated_value_rejected_even_if_every_outcome_complete(self):
        row = record(); row["targets"]["actions"][0]["value"] = 0
        self.check_rejected(row, "terminal_utility_mismatch")

    def test_failed_attempts_are_retained_separately(self):
        result = m.audit_records([record()], [{"status": "accepted"}, {"status": "failed_attempt", "reason": "worker_response_deadline"}])
        self.assertTrue(result["audit_passed"])
        self.assertEqual(result["attempt_statuses"]["failed_attempt"], 1)
        self.assertEqual(result["terminal_kinds"], {"Win": 4})

    def test_result_quality_and_hp_distribution_are_reconciled(self):
        row = record(); row["targets"]["actions"][0]["quality"] = "objective_value_unresolved"
        self.check_rejected(row, "result_quality_mismatch")
        row = record(); row["targets"]["actions"][0]["hp_distribution"] = [{"hp": 57, "probability": .5}]
        self.check_rejected(row, "terminal_hp_distribution_mismatch")

    def test_seed_overlap_and_fake_calibrated_flag_are_rejected(self):
        row = record(); row["audit_only"]["exploration_seeds"] = [11]
        self.check_rejected(row, "exploration_eval_overlap")
        row = record(); row["audit_only"]["objective_calibrated"] = True
        self.check_rejected(row, "candidate_marked_calibrated")


if __name__ == "__main__":
    unittest.main()
