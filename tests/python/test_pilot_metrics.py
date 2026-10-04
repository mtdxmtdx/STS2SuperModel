"""Pilot diagnostics tests only: no optimizer step or fitting is performed."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.model import Student
from nosl.schema import HEADS, SchemaError, load_config
from nosl.train import (atomic_save, before_fit_validation_reference, checkpoint_state,
                        evaluate, restore_checkpoint, seed_everything)
from test_student import fixture


class FixedPredictions:
    def __init__(self, config, scores):
        self.config, self.scores = config, scores

    def eval(self): return self

    def __call__(self, public):
        count = len(public["candidate_actions"])
        values = torch.tensor(self.scores[:count], dtype=torch.float32) / 100
        legal = torch.tensor(public["legal_mask"], dtype=torch.bool)
        return {"value": values, "ranking_score": values.masked_fill(~legal, -torch.inf),
                "win_probability": torch.zeros(count), "death_probability": torch.zeros(count),
                "expected_final_hp": torch.zeros(count), "potion_net_change": torch.zeros(count),
                "hp_distribution": torch.zeros(count, self.config["hp_bins"]), "legal_mask": legal}


def dataset(*records):
    return SimpleNamespace(records=list(records))


def means_record(means):
    record = fixture()
    record["targets"]["pairwise"] = []
    record["targets"]["equivalent_action_set"] = []
    for row, value in zip(record["targets"]["actions"], means): row["value"] = value
    return record


class PilotMetricsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")

    def test_value_mae_and_empirical_regret_use_teacher_utility_units(self):
        record = means_record([0., -5., -10.])
        result = evaluate(FixedPredictions(self.config, [-2., 0., -3.]), dataset(record))
        self.assertAlmostEqual(result["value_mae"]["value"], 14 / 3, places=5)
        self.assertEqual(result["value_mae"]["count"], 3)
        self.assertEqual(result["empirical_teacher_mean_regret"], {"value": 5., "count": 1})
        self.assertEqual(result["empirical_teacher_best_action_agreement"], {"value": 0., "count": 1})
        self.assertEqual(result["pairwise_agreement"]["count"], 0)
        semantics = result["empirical_teacher_ranking_semantics"]
        self.assertFalse(semantics["statistically_certified"])
        self.assertFalse(semantics["student_rollout_win_rate_measured"])

    def test_tied_empirical_best_actions_are_not_arbitrarily_wrong(self):
        result = evaluate(FixedPredictions(self.config, [0., 2., -3.]), dataset(means_record([0., 0., -10.])))
        self.assertEqual(result["empirical_teacher_mean_regret"]["value"], 0.)
        self.assertEqual(result["empirical_teacher_best_action_agreement"]["value"], 1.)
        self.assertEqual(result["equivalent_set_agreement"]["count"], 0)

    def test_missing_value_candidate_excludes_whole_root_from_empirical_ranking(self):
        record = means_record([0., -5., -10.])
        row = record["targets"]["actions"][2]
        row["masks"]["value"], row["value"], row["quality"] = False, None, "objective_value_unresolved"
        result = evaluate(FixedPredictions(self.config, [1., 2., 9.]), dataset(record))
        self.assertEqual(result["value_mae"]["count"], 2)
        self.assertEqual(result["empirical_teacher_mean_regret"], {"value": None, "count": 0})
        self.assertEqual(result["empirical_teacher_best_action_agreement"], {"value": None, "count": 0})
        self.assertEqual(result["empirical_teacher_ranking_semantics"]["incomplete_value_roots_skipped"], 1)

    def test_illegal_candidates_are_excluded_without_hiding_legal_missing_labels(self):
        record = means_record([0., -5., -10.])
        record["public_input"]["legal_mask"][2] = False
        row = record["targets"]["actions"][2]
        row["quality"] = "unresolved"
        row["masks"] = {head: False for head in HEADS}
        row.update({head: None for head in HEADS})
        result = evaluate(FixedPredictions(self.config, [2., 0., 999.]), dataset(record))
        self.assertEqual(result["empirical_teacher_mean_regret"], {"value": 0., "count": 1})
        self.assertEqual(result["value_mae"]["count"], 2)

    def test_single_action_roots_do_not_inflate_best_action_agreement(self):
        record = means_record([0., -5., -10.])
        record["public_input"]["candidate_actions"] = record["public_input"]["candidate_actions"][:1]
        record["public_input"]["legal_mask"] = [True]
        record["targets"]["actions"] = record["targets"]["actions"][:1]
        result = evaluate(FixedPredictions(self.config, [2.]), dataset(record))
        self.assertEqual(result["empirical_teacher_best_action_agreement"]["count"], 0)
        self.assertEqual(result["empirical_teacher_ranking_semantics"]["single_action_roots_skipped"], 1)
        self.assertEqual(result["value_mae"]["count"], 1)

    def test_decision_metrics_are_root_means_and_pair_metric_stays_separate(self):
        first, second = means_record([0., -5., -10.]), means_record([0., 0., -10.])
        first["targets"]["pairwise"] = [{"preferred": 0, "other": 1, "weight": .5}]
        result = evaluate(FixedPredictions(self.config, [-2., 0., -3.]), dataset(first, second))
        self.assertEqual(result["empirical_teacher_mean_regret"], {"value": 2.5, "count": 2})
        self.assertEqual(result["empirical_teacher_best_action_agreement"], {"value": .5, "count": 2})
        self.assertEqual(result["pairwise_agreement"], {"value": 0., "count": 1})
        self.assertEqual(result["evaluation_roots"], 2)

    def test_initial_reference_evaluates_only_validation_once(self):
        model, validation, trace = object(), object(), []
        initial = {"value_mae": {"value": 12., "count": 3}}
        with patch("nosl.train.evaluate", return_value=initial) as evaluate_mock:
            reference = before_fit_validation_reference(model, validation, trace, resume=False)
            evaluate_mock.assert_called_once_with(model, validation)
        self.assertEqual(reference, initial)
        self.assertEqual(trace[0]["optimizer_steps"], 0)
        self.assertEqual(trace[0]["phase"], "before_fit_random_initialization")
        trace.append({"epoch": 1, "optimizer_steps": 1, "validation": {"value_mae": {"value": 3., "count": 3}}})
        with patch("nosl.train.evaluate", side_effect=AssertionError("resume must not recompute baseline")):
            self.assertEqual(before_fit_validation_reference(model, validation, trace, resume=True), initial)

    def test_missing_or_duplicate_resume_reference_is_not_relabelled_as_random(self):
        for trace in ([], [{"phase": "before_fit_random_initialization", "epoch": 1, "optimizer_steps": 0, "validation": {}}],
                      [{"phase": "before_fit_random_initialization", "epoch": 0, "optimizer_steps": 0, "validation": {}}] * 2):
            with self.subTest(trace=trace), patch("nosl.train.evaluate", side_effect=AssertionError("no reevaluation")):
                with self.assertRaises(SchemaError): before_fit_validation_reference(None, None, trace, resume=True)

    def test_initial_reference_survives_zero_step_checkpoint_and_rng_restore(self):
        seed_everything(1729)
        model = Student(self.config)
        optimizer = torch.optim.AdamW(model.parameters(), lr=self.config["learning_rate"])
        trace = []
        before_fit_validation_reference(model, dataset(means_record([0., -5., -10.])), trace, resume=False)
        frozen, budget = {"identity": "unit-test-only"}, {"max_steps": 1, "max_epochs": 1, "max_roots": 1}
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "untrained-reference.pt"
            state = checkpoint_state(model, optimizer, frozen, budget, 0, 0, 0, [], trace)
            atomic_save(state, path)
            loaded = restore_checkpoint(path, model, optimizer, frozen, budget)
            self.assertEqual(loaded["optimizer_steps"], 0)
            self.assertEqual(loaded["trace"], trace)
            with patch("nosl.train.evaluate", side_effect=AssertionError("baseline must be preserved")):
                self.assertEqual(before_fit_validation_reference(model, None, loaded["trace"], resume=True), trace[0]["validation"])
        # No backward(), optimizer.step(), pilot_train(), or learned weights here.


if __name__ == "__main__":
    unittest.main()
