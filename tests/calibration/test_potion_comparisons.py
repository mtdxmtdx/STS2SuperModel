"""Ledger/uncertainty regressions; native evidence is a separate opt-in command."""
import copy
import importlib.util
import math
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("verify_potion_comparisons", ROOT / "tools/verify_potion_comparisons.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def pair_record():
    case = m.declared_cases()[0]
    root = {"status": "player_decision", "observation": {"startHp": 8},
            "actions": [{"kind": "play", "slot": 0}, {"kind": "potion", "slot": 0}]}
    case.update(public_root=root, baseline_action_index=0, potion_action_index=1, hp_saved_support=[0, 8])
    def outcome(use):
        return {"terminalKind": "Win", "playerAlive": True, "hpAtCombatStart": 8,
                "hpAfterSettlement": 8 if use else 3, "maxHpStart": 70, "maxHpAfterSettlement": 70,
                "settlementComplete": True, "settlementProfileId": m.ENDPOINT, "continuationPolicyId": m.BASELINE,
                "inventorySnapshotsComplete": True, "inventoryStart": [{"resourceId": "FirePotion", "count": 1}],
                "inventoryEnd": [] if use else [{"resourceId": "FirePotion", "count": 1}],
                "permanentChanges": [], "permanentChangesComplete": True}
    return case, {"public_input": {"observation": root["observation"], "candidate_actions": root["actions"]},
                  "audit_only": {"sampler_seeds": case["evaluation_seeds"], "exploration_seeds": [],
                                 "n_independent_eval": 32, "independent_final_evaluation": True,
                                 "continuation_version": m.BASELINE, "teacher_version": "nosl-full-combat-teacher-v1:T0",
                                 "objective_calibrated": False, "label_endpoint": m.ENDPOINT,
                                 "outcome_samples": [{"action_index": index, "outcomes": [outcome(index == 1) for _ in range(32)]}
                                                     for index in (0, 1)]},
                  "targets": {"actions": [{"action_index": 0}, {"action_index": 1, "value": None, "masks": {"value": False}}]}}


class PotionComparisonsTests(unittest.TestCase):
    def test_nine_sample_is_not_exact_boundary_proof(self):
        result = m.interval_with_missing([9] * 32, 32, 0, 20, .05 / 20)
        self.assertEqual(result["sample_mean"], 9)
        self.assertLess(result["lower"], 9)
        self.assertGreater(result["upper"], 9)
        self.assertEqual(m.potion_reference_eligibility(m.Interval(result["lower"], result["upper"])).value, "unresolved")
        self.assertEqual(m.potion_reference_eligibility(m.Interval(9, 9)).value, "eligible_not_mandatory")

    def test_missing_worlds_expand_bounds_without_complete_case_mean(self):
        result = m.interval_with_missing([None] + [1] * 31, 32, 0, 1, .05)
        self.assertIsNone(result["sample_mean"])
        self.assertEqual(result["sample_mean_identification_interval"], [31 / 32, 1])
        self.assertEqual(result["allocated_worlds"], 32)
        self.assertEqual(result["missing_worlds"], 1)
        unknown = m.interval_with_missing([None] * 32, 32, 0, 8, .05)
        self.assertEqual((unknown["lower"], unknown["upper"]), (0, 8))
        for values, n, lo, hi, alpha in (([1], 32, 0, 1, .05), ([9], 1, 0, 8, .05), ([1], 1, 0, 1, math.nan)):
            with self.assertRaises(ValueError):
                m.interval_with_missing(values, n, lo, hi, alpha)

    def test_complete_pair_preserves_unpriced_utility_mask(self):
        case, record = pair_record()
        result = m.summarize_case(case, record, .05 / 20)
        self.assertEqual(result["estimates"]["hp_saved"]["sample_mean"], 5)
        self.assertEqual(result["general_reference_eligibility"], "ineligible")
        self.assertFalse(result["utility_comparison_mask"])
        self.assertIsNone(result["verified_rescue_need"])
        self.assertFalse(result["formal_labels_allowed"])

    def test_incomplete_outcome_keeps_fixed_denominator_and_is_not_death(self):
        case, record = pair_record()
        record["audit_only"]["outcome_samples"][0]["outcomes"][0] = {
            "terminalKind": "ComputeTruncated", "hpAtCombatStart": 8, "continuationPolicyId": m.BASELINE}
        result = m.summarize_case(case, record, .05 / 20)
        for key in ("hp_saved", "hold_death", "paired_rescue", "paired_harm"):
            self.assertEqual(result["estimates"][key]["missing_worlds"], 1)
            self.assertIsNone(result["estimates"][key]["sample_mean"])
        self.assertEqual(result["estimates"]["use_death"]["missing_worlds"], 0)

    def test_observed_rescue_below_general_gate_is_unknown_not_rejected(self):
        case, record = pair_record()
        for row in record["audit_only"]["outcome_samples"][0]["outcomes"]:
            row.update(terminalKind="Loss", playerAlive=False, hpAfterSettlement=0)
        result = m.summarize_case(case, record, .05 / 20)
        self.assertEqual(result["general_reference_eligibility"], "ineligible")
        self.assertEqual(result["estimates"]["paired_rescue"]["sample_mean"], 1)
        self.assertEqual(result["rescue_exception_eligibility"], "unresolved")
        self.assertIsNone(result["verified_rescue_need"])
        self.assertEqual(result["rescue_gate_if_independently_verified"], "eligible_not_mandatory")

    def test_omitted_reordered_or_foreign_world_ledger_rejected(self):
        for mutation in ("omit", "duplicate_action", "seed_order", "continuation", "root", "price"):
            case, record = pair_record()
            if mutation == "omit": record["audit_only"]["outcome_samples"][0]["outcomes"].pop()
            if mutation == "duplicate_action": record["audit_only"]["outcome_samples"][1]["action_index"] = 0
            if mutation == "seed_order": record["audit_only"]["sampler_seeds"] = list(reversed(case["evaluation_seeds"]))
            if mutation == "continuation": record["audit_only"]["continuation_version"] = "different"
            if mutation == "root": record["public_input"]["candidate_actions"] = []
            if mutation == "price": record["targets"]["actions"][1].update(value=0, masks={"value": True})
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                m.summarize_case(case, record, .05 / 20)

    def test_contradicted_support_or_inventory_fails_closed(self):
        for mutation in ("healing", "use_not_immediate", "hold_consumed"):
            case, record = pair_record()
            if mutation == "healing": record["audit_only"]["outcome_samples"][0]["outcomes"][0]["hpAfterSettlement"] = 9
            if mutation == "use_not_immediate": record["audit_only"]["outcome_samples"][1]["outcomes"][0]["hpAfterSettlement"] = 7
            if mutation == "hold_consumed": record["audit_only"]["outcome_samples"][0]["outcomes"][0]["inventoryEnd"] = []
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                m.summarize_case(case, record, .05 / 20)

    def test_plan_is_bounded_and_eval_seeds_are_disjoint(self):
        cases = m.declared_cases()
        seeds = [seed for case in cases for seed in case["evaluation_seeds"]]
        self.assertEqual(len(cases), 4)
        self.assertTrue(all(len(case["evaluation_seeds"]) == 32 for case in cases))
        self.assertEqual(len(set(seeds)), 128)
        self.assertFalse(set(seeds) & set(m.DIAGNOSTIC_SEEDS))
        case = copy.deepcopy(cases[0]); case["scenario"]["relics"] = ["MeatOnTheBone"]
        with self.assertRaisesRegex(ValueError, "unreviewed scenario"):
            m.validate_root(case, {})


if __name__ == "__main__":
    unittest.main()
