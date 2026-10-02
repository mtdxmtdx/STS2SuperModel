"""Analytical preference regressions; no simulated worlds or trained labels."""
import copy
from contextlib import redirect_stderr, redirect_stdout
from decimal import Decimal
from fractions import Fraction
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("check_preference_feedback", ROOT / "tools/check_preference_feedback.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class PreferenceFeedbackTests(unittest.TestCase):
    def setUp(self):
        self.feedback = m.read_json(m.FEEDBACK_PATH)
        self.profile = m.read_json(m.CANDIDATE_PATH)
        self.death, self.fire, self.rare = self.feedback["cases"]

    def test_exact_authored_questions_and_reply_are_preserved(self):
        self.assertEqual(self.feedback["transcript"], {
            "question_1": "初始70血、资源不变：A必胜并剩50血；B有99%概率获胜并剩70血、1%概率死亡。你更偏好哪个？",
            "question_2": "两种方案都必胜，本场后续没有更好的喝药时机，也不会自动补药：A用掉一瓶火焰药水，剩50血；B保留药水，剩38血。你选哪个？换成稀有／高价值药水时会改变吗？",
            "reply": "1偏好B 2偏好A，换成稀有药水可能偏向A",
        })
        self.assertEqual(self.feedback["date"], "2026-10-02")

    def test_candidate_matches_death_case_but_is_not_calibrated(self):
        evidence = m.report(self.feedback, self.profile)
        self.assertTrue(evidence["checks_passed"])
        self.assertEqual(evidence["death_risk"]["status"], "AGREES")
        self.assertEqual(evidence["death_risk"]["cost_A"]["exact"], "148/7")
        self.assertEqual(evidence["death_risk"]["cost_B"]["exact"], "271/25")
        self.assertEqual(evidence["death_risk"]["strict_K_upper_bound"]["exact"], "14212/7")
        self.assertFalse(evidence["candidate"]["calibrated"])
        self.assertEqual(evidence["formal_label_profiles_admitted"], [])
        self.assertFalse(evidence["formal_labels_allowed"])
        self.assertFalse(evidence["training_authorized_by_report"])
        self.assertFalse(evidence["unique_objective_identified"])
        self.assertEqual(evidence["hard_preference_case_count"], 2)
        self.assertEqual(evidence["numerically_evaluated_hard_case_count"], 1)

    def test_exact_probabilities_are_two_outcomes_not_a_hundred_worlds(self):
        rows = self.death["alternatives"]["B"]
        self.assertEqual(len(rows), 2)
        self.assertEqual([m.rational(row["probability"]) for row in rows], [Fraction(99, 100), Fraction(1, 100)])
        evidence = m.report(self.feedback, self.profile)
        self.assertEqual(evidence["simulator_worlds"], 0)
        self.assertFalse(evidence["probability_evidence"])

    def test_death_boundary_tie_is_not_strict_preference(self):
        self.profile["defeatCost"] = Fraction(14212, 7)
        evidence = m.report(self.feedback, self.profile)
        self.assertFalse(evidence["checks_passed"])
        self.assertEqual(evidence["death_risk"]["status"], "TIE_NOT_STRICT_PREFERENCE")
        self.assertEqual(evidence["death_risk"]["cost_A"], evidence["death_risk"]["cost_B"])

    def test_exact_death_boundary_neighbors_do_not_round_into_ties(self):
        boundary = Fraction(14212, 7)
        for offset, expected in ((Fraction(-1, 10**20), "AGREES"), (Fraction(1, 10**20), "REVERSED")):
            with self.subTest(offset=offset):
                self.profile["defeatCost"] = boundary + offset
                evidence = m.report(self.feedback, self.profile)
                self.assertEqual(evidence["death_risk"]["status"], expected)
                self.assertEqual(evidence["checks_passed"], expected == "AGREES")

    def test_death_inequality_holds_for_other_coefficients_without_identifying_one(self):
        for eta in (Fraction(0), Fraction(1, 5), Fraction(2), Fraction(10)):
            for delta, status in ((-1, "AGREES"), (0, "TIE_NOT_STRICT_PREFERENCE"), (1, "REVERSED")):
                with self.subTest(eta=eta, delta=delta):
                    self.profile.update(defeatCost=1930 + Fraction(3510, 7) * eta + delta,
                                        downsideCoefficient=eta)
                    evidence = m.report(self.feedback, self.profile)
                    self.assertEqual(evidence["death_risk"]["status"], status)

    def test_rounded_death_algebra_coefficient_is_rejected(self):
        self.death["constraint"]["downsideCoefficient_multiplier"] = "501.428571"
        with self.assertRaisesRegex(ValueError, "exact weighted algebra"):
            m.report(self.feedback, self.profile)

    def test_q2_missing_hp_stays_unresolved(self):
        self.assertIsNone(self.fire["initial_hp"])
        with self.assertRaises(m.UnresolvedCase):
            m.fire_value_upper_bound(None, Fraction(1, 5))
        with self.assertRaises(m.UnresolvedCase):
            m.expected_cost(None, self.fire["alternatives"]["A"], 1000, Fraction(1, 5), {"FirePotion": 10})
        evidence = m.report(self.feedback, self.profile)["fire_potion"]
        self.assertEqual(evidence["candidate_numerical_ranking"], "UNRESOLVED_INITIAL_HP_AND_INVENTORY_VALUE")
        self.assertIsNone(evidence["initial_hp"])
        self.assertIsNone(evidence["assigned_price"])

    def test_seventy_hp_is_an_explicit_illustration_only(self):
        illustration = m.report(self.feedback, self.profile)["fire_potion"]["illustration"]
        self.assertEqual(illustration["assumed_initial_hp"], 70)
        self.assertFalse(illustration["initial_hp_confirmed"])
        self.assertEqual(illustration["strict_value_upper_bound"]["exact"], "2412/175")
        self.fire["initial_hp"] = 70
        with self.assertRaisesRegex(ValueError, "missing initial HP"):
            m.report(self.feedback, self.profile)

    def test_fire_bound_accounts_for_partial_or_net_healing(self):
        for h0, expected in ((70, Fraction(2412, 175)), (60, Fraction(332, 25)),
                             (50, Fraction(1572, 125)), (44, Fraction(669, 55)),
                             (38, Fraction(12)), (20, Fraction(12)), (Fraction(1, 2), Fraction(12))):
            with self.subTest(h0=h0):
                bound = m.fire_value_upper_bound(h0, Fraction(1, 5))
                self.assertEqual(bound, expected)
                formula = 12 + Fraction(1, 5) / h0 * (max(h0 - 38, 0)**2 - max(h0 - 50, 0)**2)
                self.assertEqual(bound, formula)

    def test_fire_strict_boundary_and_neighbors(self):
        boundary = m.fire_value_upper_bound(70, Fraction(1, 5))
        for offset, expected in ((Fraction(-1, 10**20), "AGREES"), (0, "TIE_NOT_STRICT_PREFERENCE"),
                                 (Fraction(1, 10**20), "REVERSED")):
            with self.subTest(offset=offset):
                values = {"FirePotion": boundary + offset}  # test-only, never installed in the candidate
                costs = {arm: m.expected_cost(70, rows, 1000, Fraction(1, 5), values)
                         for arm, rows in self.fire["alternatives"].items()}
                self.assertEqual(m.preference_result(costs["A"], costs["B"], "A"), expected)

    def test_less_than_twelve_is_sufficient_not_necessary(self):
        for h0 in (Fraction(1, 2), 1, 20, 38, 44, 50, 70, 1000):
            for eta in (0, Fraction(1, 5), 100):
                self.assertLess(Fraction(11999, 1000), m.fire_value_upper_bound(h0, eta))
        self.assertLess(13, m.fire_value_upper_bound(70, Fraction(1, 5)))
        self.assertEqual(m.fire_value_upper_bound(70, 0), 12)

    def test_inventory_is_priced_once_and_not_implicitly_zero(self):
        arm = self.fire["alternatives"]["A"]
        with self.assertRaisesRegex(m.UnresolvedCase, "unpriced inventory"):
            m.expected_cost(70, arm, 1000, Fraction(1, 5))
        cost = m.expected_cost(70, arm, 1000, Fraction(1, 5), {"FirePotion": 10})
        self.assertEqual(cost - m.hp_cost(70, 50, Fraction(1, 5)), 10)
        self.assertEqual(m.expected_cost(70, self.fire["alternatives"]["B"], 1000, 0), 32)
        arm[0]["inventory_value_coefficients"]["FirePotion"] = 2
        with self.assertRaisesRegex(ValueError, "single inventory charge"):
            m.report(self.feedback, self.profile)

    def test_rare_potion_remains_tentative_without_a_hard_label_or_price(self):
        evidence = m.report(self.feedback, self.profile)["rare_potion"]
        self.assertEqual(evidence["status"], "TENTATIVE_ONLY")
        self.assertEqual(evidence["leaning"], "A")
        self.assertFalse(evidence["hard_constraint"])
        self.assertIsNone(evidence["hard_label"])
        self.assertIsNone(evidence["assigned_price"])
        for key, value in (("hard_label", "A"), ("assigned_price", 12), ("resource_id", "InventedPotion")):
            feedback = copy.deepcopy(self.feedback)
            feedback["cases"][2][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                m.report(feedback, self.profile)
        self.rare["preference"]["hard_constraint"] = True
        with self.assertRaisesRegex(ValueError, "tentative preference scopes"):
            m.report(self.feedback, self.profile)

    def test_invalid_profiles_and_formal_gate_changes_fail_closed(self):
        for key, value in (("id", "different-objective"), ("calibrated", True), ("defeatCost", 0),
                           ("defeatCost", True), ("downsideCoefficient", -1),
                           ("inventoryValues", {"FirePotion": 9}), ("permanentFutureValues", {"max_hp": 0})):
            profile = copy.deepcopy(self.profile)
            profile[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                m.report(self.feedback, profile)
        self.feedback["formal_label_profiles_admitted"] = [self.profile["id"]]
        with self.assertRaisesRegex(ValueError, "formal label"):
            m.report(self.feedback, self.profile)

    def test_unknown_probability_mass_is_not_renormalized(self):
        rows = copy.deepcopy(self.death["alternatives"]["B"])
        rows.pop()
        with self.assertRaisesRegex(ValueError, "never renormalize"):
            m.expected_cost(70, rows, 1000, Fraction(1, 5))
        rows[0]["probability"] = "0.999999999999999999999"
        with self.assertRaisesRegex(ValueError, "never renormalize"):
            m.expected_cost(70, rows, 1000, Fraction(1, 5))

    def test_invalid_terminal_or_probability_facts_fail_closed(self):
        for key, value in (("terminal_kind", "engine_error"), ("terminal_kind", "compute_truncated"),
                           ("terminal_hp", 0), ("probability", "0"), ("probability", "-1"),
                           ("probability", "101/100")):
            rows = copy.deepcopy(self.death["alternatives"]["A"])
            rows[0][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                m.expected_cost(70, rows, 1000, Fraction(1, 5))
        rows = copy.deepcopy(self.death["alternatives"]["B"])
        rows[1]["terminal_hp"] = 1
        with self.assertRaisesRegex(ValueError, "win/death HP"):
            m.expected_cost(70, rows, 1000, Fraction(1, 5))

    def test_exact_numbers_reject_binary_floats_and_nonfinite_values(self):
        for value in (0.2, True, "nan", "inf", Decimal("NaN"), Decimal("Infinity"), "1/0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                m.rational(value)
        self.assertEqual(m.rational(Decimal("0.2")), Fraction(1, 5))

    def test_feedback_cannot_claim_a_simulator_sample(self):
        self.feedback["simulator_worlds"] = 100
        with self.assertRaisesRegex(ValueError, "simulator probability evidence"):
            m.report(self.feedback, self.profile)

    def test_authored_distribution_drift_and_duplicate_cases_are_rejected(self):
        for key, value in (("probability", "98/100"), ("terminal_hp", 69),
                           ("inventory_value_coefficients", {"FirePotion": 1})):
            feedback = copy.deepcopy(self.feedback)
            feedback["cases"][0]["alternatives"]["B"][0][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "differ from the question"):
                m.report(feedback, self.profile)
        self.feedback["cases"].append(copy.deepcopy(self.rare))
        with self.assertRaisesRegex(ValueError, "duplicated feedback cases"):
            m.report(self.feedback, self.profile)

    def test_cli_reads_fixed_sources_and_writes_only_an_ignored_report(self):
        before = {path: path.read_bytes() for path in (m.CANDIDATE_PATH, m.FEEDBACK_PATH)}
        m.REPORT_DIR.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=m.REPORT_DIR) as directory:
            output = Path(directory) / "report.json"
            with redirect_stdout(io.StringIO()):
                self.assertEqual(m.main(["--output", str(output)]), 0)
            evidence = m.read_json(output)
            self.assertIn("configs/objective_profile.candidate.json", evidence["sources"])
            self.assertFalse(evidence["candidate"]["calibrated"])
        self.assertEqual(before, {path: path.read_bytes() for path in before})
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            m.main(["--output", str(m.CANDIDATE_PATH)])


if __name__ == "__main__":
    unittest.main()
