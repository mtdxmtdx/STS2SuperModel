"""Synthetic evaluator orchestration tests; no learned weights or fitting."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import evaluate_pilot_policy as evaluation
from test_student import fixture


def packet():
    public = fixture()["public_input"]
    return {"status": "player_decision", "observation": public["observation"], "actions": public["candidate_actions"]}


def terminal(before, action, hp=55, result="win"):
    return {"result": result, "startHp": 60, "finalHp": hp, "startMaxHp": 70, "finalMaxHp": 70,
            "potions": before["observation"]["potions"], "events": before["observation"]["history"] + [{"kind": "action", "detail": json.dumps(action)}],
            "boundary": evaluation.ENDPOINT, "rewardSelectionsMade": 0}


class FakeWorker:
    def __init__(self, replies): self.replies, self.commands = list(replies), []
    def request(self, command):
        self.commands.append(deepcopy(command))
        result = self.replies.pop(0)
        if isinstance(result, Exception): raise result
        return deepcopy(result)


class FakeStudent:
    def __init__(self, action, status="EXPERIMENTAL_UNCALIBRATED"):
        self.action, self.status, self.inputs = action, status, []
    def predict(self, public):
        self.inputs.append(deepcopy(public))
        return {"status": self.status, "selected_action": self.action, "predictions": [{"expected_final_hp": 999999, "win_probability": 1}]}


class ClosedLoopEvaluationTests(unittest.TestCase):
    def guard(self, decisions=10):
        return evaluation.Guard({"max_decisions": decisions, "max_battle_seconds": 30, "max_job_seconds": 60,
                                 "max_rss_mib": 2048, "max_trace_mib": 10})

    def source(self):
        return {"source_battle_id": "synthetic-source", "category": "low_hp", "scenario": {"seed": "NEVER_MODEL_INPUT", "enemy": "TwigSlimeS", "hp": 60}}

    def test_student_receives_only_public_and_reports_actual_settlement(self):
        before = packet(); selected = before["actions"][0]
        worker = FakeWorker([before, {"status": "terminal_settled", "observation": None, "actions": []}, terminal(before, selected, hp=7)])
        student = FakeStudent(selected)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "trace.jsonl"
            result = evaluation.rollout(worker, self.source(), "student", self.guard(), path, student)
            self.assertEqual(result["status"], "WIN")
            self.assertEqual(result["terminal"]["finalHp"], 7)
            self.assertEqual(result["net_hp_loss"], 53)
            self.assertEqual(set(student.inputs[0]), {"schema_version", "observation", "history_complete", "controller_context", "candidate_actions", "legal_mask"})
            self.assertNotIn("NEVER_MODEL_INPUT", json.dumps(student.inputs))
            self.assertEqual(worker.commands[1], {"op": "step", "action": selected})
            traces = [json.loads(line) for line in path.read_text().splitlines()]
            self.assertEqual(traces[1]["selected_action"], selected)
            self.assertEqual(traces[-1]["facts"]["finalHp"], 7)
            self.assertIsNone(result["resources"]["final_gold"])

    def test_baseline_recovers_exact_action_from_public_events(self):
        before = packet(); selected = before["actions"][1]
        worker = FakeWorker([before, {"status": "terminal_settled", "observation": None, "actions": []}, terminal(before, selected)])
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "trace.jsonl"
            result = evaluation.rollout(worker, self.source(), "baseline", self.guard(), path)
            self.assertEqual(result["status"], "WIN")
            self.assertTrue(result["selected_action_trace_complete"])
            self.assertEqual(worker.commands[1], {"op": "continue"})
            traces = [json.loads(line) for line in path.read_text().splitlines()]
            self.assertEqual(next(row for row in traces if row["kind"] == "public_action_selected")["selected_action"], selected)

    def test_student_rejection_never_falls_back_to_baseline(self):
        before = packet(); worker = FakeWorker([before]); student = FakeStudent(None, "UNSUPPORTED")
        with tempfile.TemporaryDirectory() as temp:
            result = evaluation.rollout(worker, self.source(), "student", self.guard(), Path(temp) / "trace", student)
            self.assertEqual(result["status"], "POLICY_REJECTED")
            self.assertEqual(len(worker.commands), 1)
            self.assertIsNone(result["terminal"])

    def test_non_candidate_action_is_policy_error_not_game_loss(self):
        before = packet(); worker = FakeWorker([before]); student = FakeStudent({"kind": "invented"})
        with tempfile.TemporaryDirectory() as temp:
            result = evaluation.rollout(worker, self.source(), "student", self.guard(), Path(temp) / "trace", student)
            self.assertEqual(result["status"], "POLICY_ERROR")
            self.assertIsNone(result["terminal"])

    def test_engine_failure_and_missing_action_trace_are_retained(self):
        worker = FakeWorker([packet(), {"status": "invalid_operation", "message": "synthetic failure"}])
        with tempfile.TemporaryDirectory() as temp:
            result = evaluation.rollout(worker, self.source(), "baseline", self.guard(), Path(temp) / "trace")
            self.assertEqual(result["status"], "ENGINE_ERROR")
            self.assertFalse(result["selected_action_trace_complete"])
            self.assertIsNone(result["terminal"])
            traces = [json.loads(line) for line in (Path(temp) / "trace").read_text().splitlines()]
            self.assertEqual(next(row for row in traces if row["kind"] == "public_response")["packet"]["message"], "synthetic failure")

    def test_decision_limit_is_compute_truncation(self):
        before = packet(); after = deepcopy(before)
        action = before["actions"][0]
        after["observation"]["history"].append({"kind": "action", "detail": json.dumps(action)})
        worker = FakeWorker([before, after])
        with tempfile.TemporaryDirectory() as temp:
            result = evaluation.rollout(worker, self.source(), "baseline", self.guard(decisions=1), Path(temp) / "trace")
            self.assertEqual(result["status"], "COMPUTE_TRUNCATED")
            self.assertEqual(result["reason"], "decision_limit")
            self.assertEqual(result["decisions"], 1)

    def test_resource_guard_classifies_rss_budget(self):
        with patch.object(evaluation, "rss_mib", return_value=4096):
            with self.assertRaisesRegex(evaluation.BudgetExceeded, "rss"):
                self.guard().check()

    def test_wrong_settlement_boundary_is_not_a_game_result(self):
        before = packet(); facts = terminal(before, before["actions"][0]); facts["rewardSelectionsMade"] = 1
        worker = FakeWorker([before, {"status": "terminal_settled", "observation": None, "actions": []}, facts])
        with tempfile.TemporaryDirectory() as temp:
            result = evaluation.rollout(worker, self.source(), "student", self.guard(), Path(temp) / "trace", FakeStudent(before["actions"][0]))
            self.assertEqual(result["status"], "ENGINE_ERROR")
            self.assertIsNone(result["terminal"])

    def test_public_packet_cannot_contain_audit_or_seed(self):
        before = packet(); before["private_seed"] = "forbidden"
        with self.assertRaises(ValueError): evaluation.public_input(before)

    def test_summary_keeps_unresolved_source_denominator_and_pairing(self):
        source = self.source(); sources = [source, {**source, "source_battle_id": "second"}]
        plan = {"source_battle_count": 2, "scenarios": sources}
        def result(policy, status, hp):
            return {"source_battle_id": source["source_battle_id"], "policy": policy, "status": status,
                    "terminal": {"finalHp": hp}, "actual_death": hp == 0, "net_hp_loss": 60 - hp, "resources": {"potion_net_count": 0}}
        rows = [result("baseline", "WIN", 50), result("student", "LOSS", 0),
                {"source_battle_id": "second", "policy": "student", "status": "COMPUTE_TRUNCATED", "terminal": None}]
        report = evaluation.summarize(plan, rows, ["baseline", "student"])
        self.assertEqual(report["policies"]["baseline"]["wins_over_all_declared_sources"], .5)
        self.assertEqual(report["policies"]["student"]["actual_deaths"], 1)
        self.assertEqual(report["policies"]["student"]["unresolved_or_failed_sources"], 1)
        self.assertEqual(report["paired_comparisons"][0]["student_minus_baseline_final_hp"], -50)
        self.assertFalse(report["paired_comparisons"][1]["both_completed"])

    def test_scenarios_include_low_hp_hard_and_combinations_without_search(self):
        first = evaluation.declared_cases(); second = evaluation.declared_cases()
        self.assertEqual(first, second)
        self.assertTrue(any("low_hp" in category for _, category, _ in first))
        self.assertTrue(any("hard" in category for _, category, _ in first))
        self.assertTrue(any("combination" in category for _, category, _ in first))

    def test_freeze_is_read_only_about_outcomes_and_checksum_detects_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "plan"
            with patch.object(evaluation, "runtime_identity", return_value={"synthetic.dll": "test"}), patch.object(evaluation, "Worker", side_effect=AssertionError("freeze cannot execute simulator")):
                plan = evaluation.freeze(ROOT, output, smoke_only=True, seeds_per_case=1)
                self.assertEqual(evaluation.load_plan(output, ROOT), plan)
                self.assertFalse(plan["seed_holdout_checked"])
                changed = deepcopy(plan); changed["scenarios"][0]["scenario"]["seed"] = "different"
                (output / "plan.json").write_text(json.dumps(changed))
                with self.assertRaisesRegex(ValueError, "checksum"): evaluation.load_plan(output, ROOT)

    def test_paired_rollouts_use_same_declared_source_seed(self):
        before = packet(); action = before["actions"][0]
        workers = [FakeWorker([before, {"status": "terminal_settled", "observation": None, "actions": []}, terminal(before, action)]) for _ in range(2)]
        with tempfile.TemporaryDirectory() as temp:
            evaluation.rollout(workers[0], self.source(), "baseline", self.guard(), Path(temp) / "baseline")
            evaluation.rollout(workers[1], self.source(), "student", self.guard(), Path(temp) / "student", FakeStudent(action))
        self.assertEqual(workers[0].commands[0], workers[1].commands[0])

    def test_freeze_detects_actual_corpus_seed_overlap_without_model_or_outcomes(self):
        sys.path.insert(0, str(ROOT / "python"))
        import nosl.data
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); corpus = root / "audit.jsonl"
            corpus.write_text(json.dumps({"audit_only": {"scenario_recipe": {"seed": "fixed:starter:0"}}, "targets": "NOT_READ"}) + "\n")
            with patch.object(nosl.data, "prepared_paths", return_value=([corpus], "frozen-corpus")):
                with self.assertRaisesRegex(ValueError, "evaluation seed occurs"):
                    evaluation.freeze(ROOT, root / "plan", prepared=root, seed_prefix="fixed", seeds_per_case=1)
            self.assertFalse((root / "plan").exists())

    def test_full_freeze_records_unseen_initial_combinations_from_audit_only(self):
        import nosl.data
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); corpus = root / "audit.jsonl"
            corpus.write_text(json.dumps({"audit_only": {"scenario_recipe": {"seed": "train-only", "deck": ["StrikeSilent"]}}, "targets": "NOT_READ"}) + "\n")
            with patch.object(nosl.data, "prepared_paths", return_value=([corpus], "frozen-corpus")), patch.object(evaluation, "runtime_identity", return_value={"synthetic.dll": "test"}):
                plan = evaluation.freeze(ROOT, root / "plan", prepared=root, seeds_per_case=1)
            self.assertTrue(plan["seed_holdout_checked"])
            self.assertEqual(plan["source_battle_count"], 8)
            self.assertTrue(any(row["initial_composition_unseen_in_frozen_corpus"] and "combination" in row["category"] for row in plan["scenarios"]))
            self.assertEqual(plan["prepared_manifest_sha256"], "frozen-corpus")

    def test_limits_reject_nonfinite_or_expanded_jobs(self):
        limits = self.guard().limits
        with self.assertRaises(ValueError): evaluation.validate_limits({**limits, "max_decisions": 1000000})
        with self.assertRaises(ValueError): evaluation.validate_limits({**limits, "max_job_seconds": float("inf")})
