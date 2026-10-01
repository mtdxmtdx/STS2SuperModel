"""Exact synthetic contract checks; none are simulator integration tests."""
import unittest
from contracts.preference_contract import (
    Eligibility, Interval, ObjectiveProfile, ResultKind, TerminalOutcome,
    expected_terminal_cost, extra_benefit_eligibility, potion_reference_eligibility,
)


def win(hp, p=1.0, inventory=0.0, bonus=0.0):
    return TerminalOutcome(p, ResultKind.WIN, hp, inventory, bonus)


class PreferenceContractTests(unittest.TestCase):
    def test_01_user_accepts_lower_mean_lottery(self):
        fixed = expected_terminal_cost(60, [win(52)])
        lottery = expected_terminal_cost(60, [win(60, .9), win(30, .1)])
        self.assertLess(lottery, fixed)

    def test_02_equal_mean_prefers_stable(self):
        fixed = expected_terminal_cost(60, [win(57)])
        lottery = expected_terminal_cost(60, [win(60, .9), win(30, .1)])
        self.assertLess(fixed, lottery)

    def test_03_safe_no_loss_beats_faster_hp_loss(self):
        self.assertLess(expected_terminal_cost(60, [win(60)]),
                        expected_terminal_cost(60, [win(55)]))

    def test_04_whole_combat_five_beats_twelve(self):
        self.assertLess(expected_terminal_cost(60, [win(55)]),
                        expected_terminal_cost(60, [win(48)]))

    def test_05_net_healing_can_be_beneficial(self):
        self.assertLess(expected_terminal_cost(40, [win(45)]),
                        expected_terminal_cost(40, [win(40)]))

    def test_06_equal_terminal_results_have_equal_primary_score(self):
        # Turn count is intentionally absent; a separate final tie-break may use it.
        self.assertEqual(expected_terminal_cost(60, [win(58)]),
                         expected_terminal_cost(60, [win(58)]))

    def test_07_defeat_is_not_dropped(self):
        risky = [win(20, .9), TerminalOutcome(.1, ResultKind.LOSS, 0)]
        self.assertGreater(expected_terminal_cost(20, risky),
                           expected_terminal_cost(20, [win(12)]))

    def test_08_inventory_cost_is_applied_once(self):
        no_cost = expected_terminal_cost(60, [win(57)])
        with_cost = expected_terminal_cost(60, [win(57, inventory=9)])
        self.assertAlmostEqual(with_cost - no_cost, 9)

    def test_09_net_heal_not_clamped_to_zero(self):
        self.assertLess(expected_terminal_cost(40, [win(50)]), 0)

    def test_10_potion_seven_is_below_general_reference(self):
        self.assertEqual(potion_reference_eligibility(Interval(7, 7)), Eligibility.INELIGIBLE)

    def test_11_potion_nine_is_eligible_not_mandatory(self):
        self.assertEqual(potion_reference_eligibility(Interval(9, 9)), Eligibility.ELIGIBLE)

    def test_12_potion_can_rescue_below_nine(self):
        self.assertEqual(potion_reference_eligibility(Interval(6, 6), verified_rescue_need=True),
                         Eligibility.ELIGIBLE)

    def test_13_uncertain_potion_value_stays_unresolved(self):
        self.assertEqual(potion_reference_eligibility(Interval(8, 10)), Eligibility.UNRESOLVED)

    def test_14_higher_potion_reference_is_explicit(self):
        self.assertEqual(potion_reference_eligibility(Interval(10, 10), reference_hp=12),
                         Eligibility.INELIGIBLE)

    def test_15_bonus_exact_five_eighty_is_eligible(self):
        self.assertEqual(extra_benefit_eligibility(Interval(5, 5), Interval(.8, .8), True),
                         Eligibility.ELIGIBLE)

    def test_16_bonus_over_five_fails(self):
        self.assertEqual(extra_benefit_eligibility(Interval(5.1, 5.1), Interval(.9, .9), True),
                         Eligibility.INELIGIBLE)

    def test_17_bonus_below_eighty_fails(self):
        self.assertEqual(extra_benefit_eligibility(Interval(4, 4), Interval(.79, .79), True),
                         Eligibility.INELIGIBLE)

    def test_18_bonus_uncertain_threshold_not_fabricated(self):
        self.assertEqual(extra_benefit_eligibility(Interval(4, 6), Interval(.76, .84), True),
                         Eligibility.UNRESOLVED)

    def test_19_bonus_not_a_per_trajectory_cap(self):
        # An exact expected extra loss of 2 can come from 0*.9 + 20*.1.
        self.assertEqual(extra_benefit_eligibility(Interval(2, 2), Interval(.9, .9), True),
                         Eligibility.ELIGIBLE)

    def test_20_bonus_cannot_override_safety(self):
        self.assertEqual(extra_benefit_eligibility(Interval(1, 1), Interval(.95, .95), False),
                         Eligibility.INELIGIBLE)

    def test_21_unknown_safety_remains_unknown(self):
        self.assertEqual(extra_benefit_eligibility(Interval(1, 1), Interval(.95, .95), None),
                         Eligibility.UNRESOLVED)

    def test_22_truncation_is_not_a_terminal_loss(self):
        with self.assertRaises(ValueError):
            expected_terminal_cost(60, [TerminalOutcome(1, ResultKind.COMPUTE_TRUNCATED, 60)])

    def test_23_engine_error_is_not_a_game_result(self):
        with self.assertRaises(ValueError):
            expected_terminal_cost(60, [TerminalOutcome(1, ResultKind.ENGINE_ERROR, 60)])

    def test_24_cannot_normalize_away_missing_mass(self):
        with self.assertRaises(ValueError):
            expected_terminal_cost(60, [win(60, .9)])

    def test_25_nonfinite_fields_are_rejected(self):
        with self.assertRaises(ValueError):
            win(float('nan'))

    def test_26_provisional_profile_blocks_production(self):
        with self.assertRaises(RuntimeError):
            ObjectiveProfile().assert_production_ready()

    def test_27_invalid_intervals_are_rejected(self):
        with self.assertRaises(ValueError):
            Interval(2, 1)

    def test_28_invalid_probability_is_rejected(self):
        with self.assertRaises(ValueError):
            win(40, 1.1)

    def test_29_empty_distribution_is_rejected(self):
        with self.assertRaises(ValueError):
            expected_terminal_cost(60, [])

    def test_30_loss_does_not_keep_future_reward(self):
        base = TerminalOutcome(1, ResultKind.LOSS, 0, permanent_future_value=0)
        fake = TerminalOutcome(1, ResultKind.LOSS, 0, permanent_future_value=100)
        self.assertEqual(expected_terminal_cost(60, [base]), expected_terminal_cost(60, [fake]))


if __name__ == '__main__':
    unittest.main(verbosity=2)
