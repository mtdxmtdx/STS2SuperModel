"""Policy-abstention boundary tests, using controlled outputs and no fitting."""
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.experimental_policy import ExperimentalPolicyGuard, GUARD_VERSION
from nosl.inference import Inference
from nosl.schema import load_config
from test_student import fixture, fixture_v2


class FixedOutputs(torch.nn.Module):
    """Synthetic output fixture, not learned/calibrated/promoted weights."""

    def __init__(self, bins, preferred=0):
        super().__init__()
        self.bins, self.preferred, self.inputs = bins, preferred, []

    def forward(self, public):
        self.inputs.append(deepcopy(public))
        values = torch.arange(len(public["candidate_actions"]), dtype=torch.float32)
        values[self.preferred] += 100
        return {"value": values,
                "ranking_score": values.masked_fill(~torch.tensor(public["legal_mask"]), -torch.inf),
                "win_probability": torch.zeros_like(values), "death_probability": torch.zeros_like(values),
                "expected_final_hp": torch.ones_like(values) * .55,
                "hp_distribution": torch.zeros(len(values), self.bins),
                "potion_net_change": torch.ones_like(values) * -99}


def add_potion(public, kind="potion", *, legal=True):
    public["candidate_actions"].append({"revision": 0, "kind": kind, "slot": 0,
                                        "target": 0 if kind == "potion" else -1, "selection": None})
    public["legal_mask"].append(legal)
    return len(public["candidate_actions"]) - 1


class ExperimentalPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")

    def runner(self, *, preferred=0, experimental=True, diagnostics=False):
        model = FixedOutputs(self.config["hp_bins"], preferred)
        inference = Inference(self.config, model,
                              {"trained": True, "status": "EXPERIMENTAL_UNPROMOTED", "calibrated": False,
                               "continuation_policy_id": "synthetic-output-test"},
                              allow_experimental=experimental)
        return ExperimentalPolicyGuard(inference, include_diagnostics=diagnostics)

    def assert_abstains(self, result):
        self.assertEqual(result["status"], "POLICY_INAPPLICABLE")
        self.assertIsNone(result["selected_action"])
        self.assertIsNone(result["selected_index"])
        self.assertEqual(result["predictions"], [])
        self.assertFalse(result["applicability_guard"]["certifies_applicability"])

    def test_legal_use_or_discard_blocks_even_when_raw_winner_preserves_potion(self):
        for kind in ("potion", "discard_potion"):
            with self.subTest(kind=kind):
                public = fixture()["public_input"]
                index = add_potion(public, kind)
                runner = self.runner(preferred=0)
                self.assertEqual(runner.inference.predict(public)["selected_index"], 0)
                result = runner.predict(public)
                self.assert_abstains(result)
                self.assertEqual(result["applicability_guard"]["blockers"][0]["action_indices"], [index])
                self.assertNotIn("diagnostics", result)

    def test_raw_potion_winner_is_never_exposed_as_diagnostic_selection(self):
        public = fixture()["public_input"]
        index = add_potion(public)
        runner = self.runner(preferred=index, diagnostics=True)
        raw = runner.inference.predict(public)
        self.assertEqual(raw["selected_action"]["kind"], "potion")
        result = runner.predict(public)
        self.assert_abstains(result)
        diagnostics = result["diagnostics"]
        self.assertEqual(diagnostics["predictions"], raw["predictions"])
        self.assertEqual(diagnostics["hp_bin_values"], raw["hp_bin_values"])
        self.assertEqual(diagnostics["prediction_semantics"], raw["prediction_semantics"])
        self.assertFalse(diagnostics["usable_as_policy"])
        self.assertNotIn("selected_action", diagnostics)
        self.assertNotIn("selected_index", diagnostics)
        # The frozen diagnostic runner itself is still unchanged/replayable.
        self.assertEqual(runner.inference.predict(public), raw)

    def test_illegal_potions_do_not_block_or_get_filtered(self):
        public = fixture()["public_input"]
        add_potion(public, legal=False)
        add_potion(public, "discard_potion", legal=False)
        original = deepcopy(public)
        runner = self.runner(diagnostics=True)
        result = runner.predict(public)
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["selected_action"], public["candidate_actions"][0])
        self.assertEqual(result["applicability_guard"]["blockers"], [])
        self.assertEqual(runner.inference.model.inputs, [original])
        self.assertEqual(public, original)
        rows = result["diagnostics"]["predictions"]
        self.assertEqual([row["action_index"] for row in rows], list(range(5)))
        self.assertEqual([row["legal"] for row in rows], public["legal_mask"])
        self.assertIsNone(rows[-1]["score"])

    def test_complete_candidates_and_indices_retained_for_blocked_decision(self):
        public = fixture()["public_input"]
        add_potion(public, legal=False)
        index = add_potion(public, "discard_potion")
        original = deepcopy(public)
        runner = self.runner(diagnostics=True)
        result = runner.predict({"decision_status": "player_decision", "public_input": public})
        self.assert_abstains(result)
        self.assertEqual(result["applicability_guard"]["blockers"][0]["action_indices"], [index])
        self.assertEqual(runner.inference.model.inputs, [original])
        self.assertEqual(public, original)
        self.assertEqual(len(result["diagnostics"]["predictions"]), len(public["candidate_actions"]))

    def test_inventory_and_predicted_potion_loss_do_not_invent_a_public_blocker(self):
        public = fixture()["public_input"]
        runner = self.runner(diagnostics=True)
        result = runner.predict(public)
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["selected_index"], 0)
        self.assertEqual(result["predictions"], [])
        self.assertEqual(result["applicability_guard"],
                         {"version": GUARD_VERSION, "blockers": [], "certifies_applicability": False})
        self.assertIn("unverified", result["reason"])
        self.assertEqual(result["diagnostics"]["predictions"][0]["potion_net_change"], -99)

    def test_default_unpromoted_rejection_cannot_be_bypassed_by_diagnostics(self):
        public = fixture()["public_input"]
        add_potion(public)
        runner = self.runner(experimental=False, diagnostics=True)
        result = runner.predict(public)
        self.assertEqual(result["status"], "MODEL_UNVALIDATED")
        self.assertIsNone(result["selected_action"])
        self.assertNotIn("diagnostics", result)
        self.assertEqual(runner.inference.model.inputs, [])

    def test_terminal_waiting_and_schema_rejections_preserve_base_behavior(self):
        runner = self.runner(diagnostics=True)
        public = fixture()["public_input"]
        invalid = deepcopy(public)
        invalid["legal_mask"].append(True)
        incomplete = deepcopy(public)
        incomplete["history_complete"] = False
        inputs = [{"decision_status": status, "public_input": None} for status in ("terminal", "waiting")]
        inputs += [invalid, incomplete, {"decision_status": "terminal", "public_input": public},
                   {"decision_status": "card_choice", "public_input": public}]
        for value in inputs:
            with self.subTest(value=value):
                expected = runner.inference.predict(value)
                result = runner.predict(value)
                self.assertEqual(result, expected)
                self.assertIsNone(result["selected_action"])
                self.assertNotIn("diagnostics", result)
        self.assertEqual(runner.inference.model.inputs, [])

    def test_no_legal_candidate_is_not_terminal_or_waiting(self):
        runner = self.runner()
        for empty in (True, False):
            public = fixture()["public_input"]
            public["candidate_actions"] = [] if empty else public["candidate_actions"]
            public["legal_mask"] = [False] * len(public["candidate_actions"])
            self.assertEqual(runner.predict(public)["status"], "NO_DECISION")
            self.assertEqual(runner.predict({"decision_status": "player_decision", "public_input": public})["status"], "INVALID_INPUT")
        self.assertEqual(runner.inference.model.inputs, [])

    def test_valid_card_choice_preserves_candidates_and_experimental_status(self):
        public = fixture()["public_input"]
        public["observation"]["choice"] = {"source": "Prepared", "min": 1, "max": 1, "cancelable": False,
                                             "candidates": public["observation"]["hand"]}
        public["candidate_actions"] = [{"revision": 0, "kind": "choose", "slot": -1, "target": -1,
                                         "selection": [index]} for index in range(2)]
        public["legal_mask"] = [True, True]
        runner = self.runner(preferred=1)
        result = runner.predict({"decision_status": "card_choice", "public_input": public})
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["selected_action"], public["candidate_actions"][1])
        self.assertEqual(runner.inference.model.inputs, [public])
        self.assertFalse(result["applicability_guard"]["certifies_applicability"])

    def test_observed_gold_gain_or_spend_conservatively_abstains(self):
        for gold in (98, 100):
            with self.subTest(gold=gold):
                public = fixture_v2()["public_input"]
                public["observation"]["gold"] = gold
                result = self.runner().predict(public)
                self.assert_abstains(result)
                blocker = result["applicability_guard"]["blockers"][0]
                self.assertEqual(blocker["code"], "UNPRICED_OBSERVED_GOLD_CHANGE")
                self.assertIn("absolute utility", blocker["reason"])

    def test_unchanged_gold_and_relic_presence_do_not_block(self):
        result = self.runner().predict(fixture_v2()["public_input"])
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["applicability_guard"]["blockers"], [])

    def test_past_potion_use_or_discard_blocks_after_bottle_gone(self):
        events = [{"kind": "potion_used", "detail": "SwiftPotion"}]
        events += [{"kind": "action", "detail": json.dumps(
            {"revision": 0, "kind": kind, "slot": 0, "target": -1, "selection": None})}
            for kind in ("potion", "discard_potion")]
        for event in events:
            with self.subTest(event=event):
                public = fixture()["public_input"]
                public["observation"]["potions"] = [None, None]
                public["observation"]["history"].append(event)
                result = self.runner(diagnostics=True).predict(public)
                self.assert_abstains(result)
                self.assertEqual(result["applicability_guard"]["blockers"][0]["code"], "UNPRICED_PUBLIC_POTION_HISTORY")
                self.assertEqual(result["applicability_guard"]["blockers"][0]["history_event_indices"], [2])
                self.assertEqual(len(result["diagnostics"]["predictions"]), 3)

    def test_pending_choice_abstains_after_accepted_potion_action_before_completion(self):
        public = fixture_v2()["public_input"]
        public["observation"]["potions"] = ["GamblersBrew", None]
        # Public use may be suspended at a choice before potion_used is emitted.
        public["observation"]["history"].append({"kind": "action", "detail": json.dumps(
            {"revision": 0, "kind": "potion", "slot": 0, "target": -1, "selection": None})})
        public["observation"]["choice"] = {"source": "GamblersBrew", "min": 0, "max": 2, "cancelable": False,
                                             "candidates": public["observation"]["hand"],
                                             "candidateOrder": "public", "bundles": None}
        public["candidate_actions"] = [{"revision": 1, "kind": "choose", "slot": -1,
                                         "target": -1, "selection": [0]}]
        public["legal_mask"] = [True]
        runner = self.runner(diagnostics=True)
        result = runner.predict({"decision_status": "card_choice", "public_input": public})
        self.assert_abstains(result)
        self.assertEqual(result["applicability_guard"]["blockers"][0]["code"], "UNPRICED_PUBLIC_POTION_HISTORY")
        self.assertEqual(runner.inference.model.inputs, [public])

    def test_duplicate_use_evidence_or_present_inventory_does_not_invent_net_change(self):
        public = fixture()["public_input"]
        public["observation"]["history"] += [
            {"kind": "action", "detail": json.dumps(
                {"revision": 0, "kind": "potion", "slot": 0, "target": 0, "selection": None})},
            {"kind": "potion_used", "detail": "FirePotion"},
        ]
        # A current bottle cannot prove cancellation against unobserved initial
        # inventory; the two events also cannot prove two consumed bottles.
        result = self.runner().predict(public)
        self.assert_abstains(result)
        blockers = result["applicability_guard"]["blockers"]
        self.assertEqual(len(blockers), 1)
        self.assertEqual(blockers[0]["history_event_indices"], [2, 3])
        self.assertEqual(set(blockers[0]), {"code", "history_event_indices", "reason"})

    def test_ordinary_action_history_does_not_trigger_potion_history_blocker(self):
        public = fixture()["public_input"]
        public["observation"]["history"] += [
            {"kind": "action", "detail": json.dumps(public["candidate_actions"][i])} for i in (0, 2)]
        result = self.runner().predict(public)
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["applicability_guard"]["blockers"], [])

    def test_promoted_status_is_outside_this_guard_scope(self):
        runner = self.runner(diagnostics=True)
        runner.inference.manifest.update(status="PROMOTED", calibrated=True)
        result = runner.predict(fixture()["public_input"])
        self.assertEqual(result["status"], "POLICY_INAPPLICABLE")
        self.assertIsNone(result["selected_action"])
        self.assertNotIn("diagnostics", result)

    def test_jsonl_schema_mode_handles_controls_and_bad_json(self):
        lines = [json.dumps({"decision_status": "terminal", "public_input": None}),
                 json.dumps({"decision_status": "waiting", "public_input": None}),
                 json.dumps(fixture()["public_input"]), "bad json"]
        process = subprocess.run([sys.executable, "-m", "nosl.experimental_policy", "--config",
                                  str(ROOT / "configs/student.pilot.json"), "--include-diagnostics"],
                                 input="\n".join(lines) + "\n", text=True, capture_output=True,
                                 cwd=ROOT, env={"PYTHONPATH": str(ROOT / "python")})
        self.assertEqual(process.returncode, 0, process.stderr)
        results = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertEqual([row["status"] for row in results], ["TERMINAL", "WAITING", "MODEL_UNTRAINED", "INVALID_INPUT"])
        self.assertTrue(all(row["selected_action"] is None for row in results))
        self.assertTrue(all("diagnostics" not in row for row in results))


if __name__ == "__main__":
    unittest.main()
