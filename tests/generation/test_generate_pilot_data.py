from copy import deepcopy
from argparse import Namespace
from contextlib import redirect_stdout
import io
import sys
from unittest.mock import patch
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("generate_pilot_data", ROOT / "tools/generate_pilot_data.py")
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "tests/data"))
sys.path.insert(0, str(ROOT / "python"))
from test_prepare_dataset import fixture
from test_report_data_stage import outcomes
from nosl.data import validate_record
from nosl.schema import load_config
from prepare_dataset import provenance_components, public_digest


class SimulatedCrash(BaseException):
    pass


class GenerationTests(unittest.TestCase):
    def test_recipe_reproducible_and_declares_constructed_category(self):
        catalog = {"cards": ["DaggerThrow"], "potions": ["FirePotion"], "relics": ["MeatOnTheBone"]}
        for index in range(20):
            first = m.scenario(index, catalog, "test-seed")
            self.assertEqual(first, m.scenario(index, catalog, "test-seed"))
            self.assertIn("constructed", first[1])
            self.assertIn("seed", first[0])
        self.assertEqual(m.scenario(8, catalog, "x")[0]["potions"], ["FirePotion"])
        self.assertEqual(m.scenario(9, catalog, "x")[0]["relics"], ["MeatOnTheBone"])

    def test_false_or_missing_masks_do_not_count_effective_root(self):
        action = {"allocated_worlds": 2, "completed_worlds": 2,
                  "masks": {"value": True, "win_probability": True, "expected_final_hp": True}}
        record = {"targets": {"actions": [action]}}
        self.assertEqual(m.usable_counts(record), (True, True))
        action["masks"]["value"] = False
        self.assertEqual(m.usable_counts(record), (True, False))
        action["masks"]["win_probability"] = False
        self.assertEqual(m.usable_counts(record), (False, False))
        self.assertEqual(m.usable_counts({}), (False, False))

    def test_append_is_readable_and_partial_tail_is_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "corpus.jsonl"
            m.append_json(path, {"row": 1}); m.append_json(path, {"row": 2})
            self.assertEqual(list(m.existing_rows(path)), [{"row": 1}, {"row": 2}])
            with path.open("ab") as f: f.write(b'{"unfinished":')
            self.assertEqual(list(m.existing_rows(path)), [{"row": 1}, {"row": 2}])
            backups = list(Path(tmp).glob("*.partial.*"))
            self.assertEqual(len(backups), 1)
            self.assertEqual(backups[0].read_bytes(), b'{"unfinished":')

    def test_corruption_inside_complete_rows_is_not_dropped(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "corpus.jsonl"
            path.write_text('{"row":1}\ncorrupt\n')
            with self.assertRaises(json.JSONDecodeError): list(m.existing_rows(path))

    def test_lossless_raw_outcomes_are_separate_checksummed_members(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            raw = [{"action_index": 0, "outcomes": [{"terminalKind": "Win", "hpAfterSettlement": 61}]}]
            first = {"audit_only": {"outcome_samples": raw}}
            second = {"audit_only": {"outcome_samples": raw}}
            m.archive_outcomes(root, 0, first); m.archive_outcomes(root, 1, second)
            self.assertNotIn("outcome_samples", first["audit_only"])
            self.assertEqual(m.read_outcomes(root, first["audit_only"]["outcome_samples_ref"]), raw)
            self.assertEqual(m.read_outcomes(root, second["audit_only"]["outcome_samples_ref"]), raw)
            bad = dict(first["audit_only"]["outcome_samples_ref"], sha256="0" * 64)
            with self.assertRaises(ValueError): m.read_outcomes(root, bad)
            bad = dict(first["audit_only"]["outcome_samples_ref"], path="../outside.gz")
            with self.assertRaises(ValueError): m.read_outcomes(root, bad)

    def test_public_phase_snapshot_reaches_later_turn_using_only_source_actions(self):
        class FakeWorker:
            def __init__(self): self.commands = []; self.packets = iter([
                {"status": "player_decision", "observation": {"turn": 1}},
                {"status": "player_decision", "observation": {"turn": 1}},
                {"status": "player_decision", "observation": {"turn": 2}}])
            def request(self, command): self.commands.append(command); return next(self.packets)
        worker = FakeWorker()
        packet, index, phase = m.collect_source_snapshot(worker, {"seed": "private"}, "public-phase-v1", 1, 10)
        self.assertEqual((packet["observation"]["turn"], index, phase), (2, 2, "first_player_turn_2"))
        self.assertEqual([c["op"] for c in worker.commands], ["reset", "continue", "continue"])

    def test_choice_phase_stops_at_first_public_choice_and_never_peeks_future(self):
        class FakeWorker:
            def __init__(self): self.calls = 0
            def request(self, command):
                self.calls += 1
                if self.calls > 1: raise AssertionError("peeked beyond selected public root")
                return {"status": "card_choice", "observation": {"turn": 1}}
        worker = FakeWorker()
        packet, index, phase = m.collect_source_snapshot(worker, {}, "public-phase-v1", 3, 10)
        self.assertEqual((packet["status"], index, phase), ("card_choice", 0, "first_pending_choice"))

    def test_unavailable_phase_and_source_budget_are_not_game_losses(self):
        class Ended:
            def request(self, command): return {"status": "terminal_settled"}
        with self.assertRaisesRegex(ValueError, "terminal_before_requested_phase"):
            m.collect_source_snapshot(Ended(), {}, "public-phase-v1", 2, 3)
        class Loop:
            def request(self, command): return {"status": "player_decision", "observation": {"turn": 1}}
        with self.assertRaisesRegex(ValueError, "source_decision_budget_exhausted"):
            m.collect_source_snapshot(Loop(), {}, "public-phase-v1", 2, 3)

    def test_phase_policy_and_low_hp_recipes_are_explicit_and_reproducible(self):
        self.assertEqual([m.requested_phase("public-phase-v1", i) for i in range(4)],
                         ["opening", "first_player_turn_2", "first_player_turn_3", "first_pending_choice"])
        with self.assertRaises(ValueError): m.requested_phase("public-phase-v1", 4)
        catalog = {"cards": ["DaggerThrow"], "potions": ["FirePotion"], "relics": ["MeatOnTheBone"]}
        for index in range(0, 100, 7):
            first = m.scenario(index, catalog, "public-phase-test", "public-phase-v1")
            self.assertEqual(first, m.scenario(index, catalog, "public-phase-test", "public-phase-v1"))
            self.assertTrue(1 <= first[0]["hp"] <= 12)

    def test_atomic_progress_replaces_whole_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "progress.json"
            m.atomic_json(path, {"n": 1}); m.atomic_json(path, {"n": 2})
            self.assertEqual(json.loads(path.read_text()), {"n": 2})
            self.assertFalse(path.with_suffix(".json.tmp").exists())


class RecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.student = load_config(ROOT / "configs/student.pilot.json")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        # These are mock-worker tests; no compiled simulator is needed or loaded.
        runtime = patch.object(m, "runtime_fingerprints", return_value={"mock-worker": "synthetic"})
        runtime.start(); self.addCleanup(runtime.stop)
        self.output = Path(self.temp.name)
        recipe = Path(m.__file__).read_bytes()
        self.config = {"version": m.VERSION, "generator_sha256": m.hashlib.sha256(recipe).hexdigest(),
                       "roots_per_battle": 3, "data_mode": "pilot", "seed_prefix": "recovery-test",
                       "execution_partition": {"shard_id": 0, "shard_count": 1},
                       "teacher_options": {"mode": "T0", "evaluationSeeds": list(range(8))}}
        m.atomic_json(self.output / "generation_config.json", self.config)
        (self.output / "generation_recipe.py").write_bytes(recipe)

    def row(self, index=0):
        row = fixture(index)
        audit = row["audit_only"]
        label_hash = m.sha({k: v for k, v in self.config.items() if k != "execution_partition"})
        audit.update(generation_source_index=index, generation_source_step=index % 3,
                     generation_version=self.config["version"], source_category="unit_constructed",
                     scenario_recipe={"seed": "unit", "enemy": "TwigSlimeS"}, generation_config_sha256=label_hash)
        audit["versions"]["generation_config"] = label_hash
        audit["outcome_samples"] = outcomes(row)
        return row

    def intent(self, index=0):
        return {"source_index": index, "source_battle_index": index // 3, "source_step": index % 3,
                "source_category": "unit_constructed", "scenario": {"seed": "unit"},
                "status": "started", "requested_source_phase": "opening", "generation_version": m.VERSION,
                "planned_evaluation_worlds": 8, "teacher_request_may_have_started": False,
                "requested_action_worlds": None, "elapsed_seconds": None, "timing_status": "unknown"}

    def reconcile(self):
        return m.reconcile_corpus(self.output, self.config, lambda row: validate_record(row, self.student))

    def rows(self, name):
        return list(m.existing_rows(self.output / name))

    def ready(self, row=None):
        row = row or self.row()
        attempt = self.intent(row["audit_only"]["generation_source_index"])
        m.begin_attempt(self.output, self.config, attempt)
        attempt.update(teacher_request_may_have_started=True, requested_action_worlds=16)
        with patch.object(m.time, "monotonic", return_value=12.):
            m.checkpoint_attempt(self.output, self.config, attempt, "teacher_requested", started=10.)
            m.archive_outcomes(self.output, attempt["source_index"], row)
            m.checkpoint_attempt(self.output, self.config, attempt, "record_ready", started=10., record=row)
        return attempt, row

    def test_tiny_first_partial_line_is_preserved(self):
        path = self.output / "decisions.jsonl"
        path.write_bytes(b'{"partial":')
        state = self.reconcile()
        self.assertEqual([], self.rows("decisions.jsonl"))
        self.assertEqual(1, len(state["preserved_partial_tails"]))
        self.assertEqual(b'{"partial":', Path(state["preserved_partial_tails"][0]).read_bytes())

    def test_large_first_partial_line_is_preserved_without_tail_limit(self):
        path = self.output / "decisions.jsonl"
        original = b'{"partial":"' + b'x' * (2 * 1024 * 1024)
        path.write_bytes(original)
        state = self.reconcile()
        self.assertEqual(original, Path(state["preserved_partial_tails"][0]).read_bytes())
        self.assertEqual(b"", path.read_bytes())

    def test_crash_during_source_has_no_requested_teacher_worlds(self):
        m.begin_attempt(self.output, self.config, self.intent())
        state = self.reconcile()
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual("interrupted_attempt", journal["status"])
        self.assertEqual(0, journal["uncommitted_requested_action_worlds"])
        self.assertIsNone(journal["elapsed_seconds"])
        self.assertEqual("recovered_unknown", journal["timing_status"])
        self.assertEqual(0, state["complete_roots"])
        self.assertFalse((self.output / "inflight_attempt.json").exists())

    def test_crash_during_teacher_preserves_budget_and_partial_time(self):
        attempt = self.intent()
        m.begin_attempt(self.output, self.config, attempt)
        attempt.update(teacher_request_may_have_started=True, requested_action_worlds=16)
        with patch.object(m.time, "monotonic", return_value=12.5):
            m.checkpoint_attempt(self.output, self.config, attempt, "teacher_requested", started=10.)
        state = self.reconcile()
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual("interrupted_attempt", journal["status"])
        self.assertEqual(16, journal["uncommitted_requested_action_worlds"])
        self.assertIsNone(journal["elapsed_seconds"])
        self.assertEqual(2.5, journal["elapsed_seconds_observed"])
        self.assertEqual("recovered_partial", journal["timing_status"])
        self.assertEqual(0, state["decision_records"])
        self.assertEqual(0, self.reconcile()["recovered_journals"])
        self.assertEqual(1, len(self.rows("attempts.jsonl")))

    def test_crash_after_saved_result_before_row_materializes_only_saved_fact(self):
        attempt, row = self.ready()
        self.assertFalse((self.output / "decisions.jsonl").exists())
        state = self.reconcile()
        self.assertEqual(row, self.rows("decisions.jsonl")[0])
        self.assertEqual(1, state["complete_roots"])
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual("accepted", journal["status"])
        self.assertIsNone(journal["elapsed_seconds"])
        self.assertEqual(2., journal["elapsed_seconds_observed"])
        self.assertEqual(1, state["recovered_journals"])

    def test_crash_after_row_before_final_journal_has_unknown_total_time(self):
        _, row = self.ready()
        m.append_json(self.output / "decisions.jsonl", row)
        state = self.reconcile()
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual(1, state["decision_records"])
        self.assertIsNone(journal["elapsed_seconds"])
        self.assertEqual("recovered_partial", journal["timing_status"])
        self.assertEqual(16, journal["requested_action_worlds"])
        self.assertEqual(1, len(self.rows("decisions.jsonl")))

    def test_crash_with_partial_first_row_recovers_saved_full_record(self):
        _, row = self.ready()
        (self.output / "decisions.jsonl").write_bytes(b'{"public_input":')
        state = self.reconcile()
        self.assertEqual(row, self.rows("decisions.jsonl")[0])
        self.assertEqual(1, len(state["preserved_partial_tails"]))
        self.assertEqual(1, state["complete_roots"])

    def test_crash_after_measured_journal_ready_preserves_real_measurement(self):
        attempt, row = self.ready()
        m.append_json(self.output / "decisions.jsonl", row)
        attempt.update(status="accepted", public_input_digest=m.public_sha(row["public_input"]))
        append = m.append_json
        def crash_journal(path, value):
            if path.name == "attempts.jsonl": raise OSError("journal storage failure")
            append(path, value)
        with patch.object(m, "append_json", side_effect=crash_journal), patch.object(m.time, "monotonic", return_value=15.):
            with self.assertRaises(OSError): m.finish_attempt(self.output, self.config, attempt, 10., row)
        state = self.reconcile()
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual(5., journal["elapsed_seconds"])
        self.assertEqual("measured", journal["timing_status"])
        self.assertTrue(journal["recovered"])
        self.assertEqual(1, state["recovered_journals"])

    def test_crash_after_journal_before_clear_is_idempotent(self):
        attempt, row = self.ready()
        m.append_json(self.output / "decisions.jsonl", row)
        attempt.update(status="accepted", public_input_digest=m.public_sha(row["public_input"]))
        with patch.object(m, "clear_inflight", side_effect=SimulatedCrash), patch.object(m.time, "monotonic", return_value=15.):
            with self.assertRaises(SimulatedCrash): m.finish_attempt(self.output, self.config, attempt, 10., row)
        before = (self.output / "attempts.jsonl").read_bytes()
        state = self.reconcile()
        self.assertEqual(0, state["recovered_journals"])
        self.assertEqual(before, (self.output / "attempts.jsonl").read_bytes())
        self.assertFalse((self.output / "inflight_attempt.json").exists())

    def test_partial_first_journal_is_preserved_and_replayed(self):
        attempt, row = self.ready()
        m.append_json(self.output / "decisions.jsonl", row)
        attempt.update(status="accepted", public_input_digest=m.public_sha(row["public_input"]))
        def partial(path, value):
            path.write_bytes(b'{"source_index":')
            raise SimulatedCrash
        with patch.object(m, "append_json", side_effect=partial), patch.object(m.time, "monotonic", return_value=15.):
            with self.assertRaises(SimulatedCrash): m.finish_attempt(self.output, self.config, attempt, 10., row)
        state = self.reconcile()
        self.assertEqual(1, len(self.rows("attempts.jsonl")))
        self.assertEqual(5., self.rows("attempts.jsonl")[0]["elapsed_seconds"])
        self.assertEqual(1, len(state["preserved_partial_tails"]))

    def test_legacy_durable_row_gets_honest_unknown_timing_journal(self):
        row = self.row()
        m.archive_outcomes(self.output, 0, row)
        m.append_json(self.output / "decisions.jsonl", row)
        state = self.reconcile()
        journal = self.rows("attempts.jsonl")[0]
        self.assertEqual("legacy_durable_row_missing_journal", journal["recovery_reason"])
        self.assertIsNone(journal["elapsed_seconds"])
        self.assertIsNone(journal["elapsed_seconds_observed"])
        self.assertEqual("recovered_unknown", journal["timing_status"])
        self.assertEqual(16, journal["requested_action_worlds"])
        self.assertEqual(row["audit_only"]["scenario_recipe"], journal["scenario"])
        self.assertEqual(1, state["complete_roots"])
        self.assertEqual(0, self.reconcile()["recovered_journals"])

    def test_legacy_budget_missing_in_saved_config_stays_unknown(self):
        self.config["teacher_options"].pop("evaluationSeeds")
        m.atomic_json(self.output / "generation_config.json", self.config)
        row = self.row()
        m.archive_outcomes(self.output, 0, row)
        m.append_json(self.output / "decisions.jsonl", row)
        self.reconcile()
        self.assertIsNone(self.rows("attempts.jsonl")[0]["requested_action_worlds"])

    def test_corrupt_inflight_battle_or_budget_never_commits_or_clears(self):
        for field, value, message in (("source_battle_index", 99, "battle_identity"),
                                      ("requested_action_worlds", -1, "requested_world_budget"),
                                      ("elapsed_seconds_observed", -2., "attempt_elapsed")):
            with self.subTest(field=field):
                path = self.output / "inflight_attempt.json"
                if path.exists(): path.unlink()
                m.begin_attempt(self.output, self.config, self.intent())
                wal = json.loads(path.read_text())
                wal["attempt"][field] = value
                m.atomic_json(path, wal)
                before = path.read_bytes()
                with self.assertRaisesRegex(ValueError, message):
                    self.reconcile()
                self.assertEqual(before, path.read_bytes())
                self.assertFalse((self.output / "attempts.jsonl").exists())
                self.assertFalse((self.output / "decisions.jsonl").exists())

    def test_recovery_rejects_corrupt_saved_result_or_config(self):
        self.ready()
        path = self.output / "inflight_attempt.json"
        value = json.loads(path.read_text()); value["record_sha256"] = "wrong"
        m.atomic_json(path, value)
        with self.assertRaisesRegex(ValueError, "record_checksum"):
            self.reconcile()
        self.assertFalse((self.output / "decisions.jsonl").exists())

    def test_recovery_requires_valid_frozen_recipe_and_raw_outcomes(self):
        row = self.row()
        m.archive_outcomes(self.output, 0, row)
        row["audit_only"]["outcome_samples_ref"]["sha256"] = "wrong"
        m.append_json(self.output / "decisions.jsonl", row)
        with self.assertRaisesRegex(ValueError, "integrity_failure"):
            self.reconcile()
        self.assertFalse((self.output / "attempts.jsonl").exists())
        (self.output / "generation_recipe.py").write_text("tampered")
        with self.assertRaisesRegex(ValueError, "recipe_hash_mismatch"):
            self.reconcile()

    def test_accepted_journal_without_row_is_not_invented(self):
        m.append_json(self.output / "attempts.jsonl", dict(self.intent(), status="accepted", elapsed_seconds=2., timing_status="measured"))
        with self.assertRaisesRegex(ValueError, "missing_durable_decision"):
            self.reconcile()
        self.assertFalse((self.output / "decisions.jsonl").exists())

    def test_recover_only_obeys_live_generator_lock_and_never_starts_worker(self):
        row = self.row()
        m.archive_outcomes(self.output, 0, row)
        m.append_json(self.output / "decisions.jsonl", row)
        args = Namespace(output=self.output, repo=ROOT)
        with (self.output / "generator.lock").open("a") as lock:
            m.fcntl.flock(lock, m.fcntl.LOCK_EX | m.fcntl.LOCK_NB)
            with self.assertRaisesRegex(RuntimeError, "another_generator_owns"):
                m.recover_only(args)
        self.assertFalse((self.output / "attempts.jsonl").exists())
        with patch.object(m, "Worker", side_effect=AssertionError("recovery started a worker")), redirect_stdout(io.StringIO()):
            result = m.recover_only(args)
        self.assertEqual(1, result["recovered_journals"])
        self.assertEqual(m.PUBLIC_IDENTITY_SCHEME, result["public_identity_scheme"])
        self.assertIsNone(result["stored_generation_identity_scheme"])

    def test_duplicate_rows_preserve_cross_battle_transitive_bridge(self):
        rows = [self.row(index) for index in (0, 1, 3, 4)]
        rows[2]["public_input"] = deepcopy(rows[0]["public_input"])
        rows[2]["public_input"]["observation"]["block"] = 0.0
        for i, row in enumerate(rows):
            family = "A" if i < 2 else "B"
            row["audit_only"].update(source_run_group=family, source_combat_id=family, branch_family=family)
            m.archive_outcomes(self.output, row["audit_only"]["generation_source_index"], row)
            m.append_json(self.output / "decisions.jsonl", row)
        state = self.reconcile()
        self.assertEqual((4, 3, 3, 1), tuple(state[k] for k in ("decision_records", "unique_roots", "complete_roots", "duplicate_records")))
        saved = self.rows("decisions.jsonl")
        groups, _, _ = provenance_components(saved)
        self.assertEqual(1, len(set(groups)))
        self.assertTrue(self.rows("attempts.jsonl")[2]["duplicate_public_input"])
        self.assertEqual("accepted", self.rows("attempts.jsonl")[2]["status"])
        self.assertEqual(3, self.reconcile()["unique_roots"])
        self.assertEqual(4, len(self.rows("attempts.jsonl")))

    def test_public_digest_matches_m5_without_changing_config_hash(self):
        self.assertEqual(public_digest({"block": 0, "nested": [1., True, "1"]}), m.public_sha({"block": -0.0, "nested": [1, True, "1"]}))
        self.assertNotEqual(m.sha({"number": 1}), m.sha({"number": 1.0}))

    def run_args(self, output, target=1, attempts=3):
        return Namespace(repo=ROOT, output=output, teacher="T0", worlds=8, exploration_worlds=0,
            max_decisions=20, tree_depth=2, mode="pilot", seed_prefix="fake-worker", roots_per_battle=1,
            root_policy="opening-prefix-v1", max_source_decisions=20, shard_id=0, shard_count=1,
            target_roots=target, max_attempts=attempts, timeout=5, max_worker_mib=100)

    def fake_worker(self, duplicate_first=False, timeout=False):
        class FakeWorker:
            created, teacher_calls = 0, 0
            def __init__(self, *args): type(self).created += 1
            def request(self, command):
                if command["op"] == "reset":
                    row = fixture()
                    return {"status": "player_decision", "observation": row["public_input"]["observation"], "actions": row["public_input"]["candidate_actions"]}
                if command["op"] == "teacher_record":
                    if timeout: raise TimeoutError("worker_response_deadline")
                    number = type(self).teacher_calls
                    type(self).teacher_calls += 1
                    row = fixture(0 if duplicate_first and number == 1 else number)
                    row["audit_only"].update(source_run_group=command["sourceRun"], source_combat_id=command["sourceCombat"], branch_family=command["branchFamily"])
                    row["audit_only"]["outcome_samples"] = outcomes(row)
                    return row
                raise AssertionError(command)
            def close(self): pass
        return FakeWorker

    def test_mocked_worker_journal_failure_resume_repro_is_fixed(self):
        output = self.output / "actual-run"
        args = self.run_args(output)
        fake = self.fake_worker()
        append = m.append_json
        def fail_journal(path, value):
            if path.name == "attempts.jsonl": raise OSError("reproduced journal failure")
            append(path, value)
        catalog = {"cards": ["DaggerThrow"], "potions": ["FirePotion"], "relics": ["MeatOnTheBone"]}
        with patch.object(m, "source_catalog", return_value=catalog), patch.object(m, "Worker", fake), redirect_stdout(io.StringIO()):
            with patch.object(m, "append_json", side_effect=fail_journal):
                with self.assertRaisesRegex(OSError, "reproduced journal failure"):
                    m.run_locked(args)
            m.run_locked(args)
        self.assertEqual(1, fake.created)
        self.assertEqual(1, fake.teacher_calls)
        self.assertEqual(1, len(list(m.existing_rows(output / "decisions.jsonl"))))
        journal = list(m.existing_rows(output / "attempts.jsonl"))
        self.assertEqual(1, len(journal))
        self.assertTrue(journal[0]["recovered"])
        progress = json.loads((output / "progress.json").read_text())
        self.assertEqual("data_stage_already_complete", progress["status"])
        self.assertEqual(1, progress["recovered_journals"])
        config = json.loads((output / "generation_config.json").read_text())
        self.assertEqual(m.PUBLIC_IDENTITY_SCHEME, config["public_identity_scheme"])
        self.assertIn("public_identity.py", config["validator_files"])

    def test_mocked_worker_preserves_duplicate_whole_records_and_raw_refs(self):
        output = self.output / "duplicate-run"
        args = self.run_args(output, target=2)
        fake = self.fake_worker(duplicate_first=True)
        catalog = {"cards": ["DaggerThrow"], "potions": ["FirePotion"], "relics": ["MeatOnTheBone"]}
        with patch.object(m, "source_catalog", return_value=catalog), patch.object(m, "Worker", fake), redirect_stdout(io.StringIO()):
            m.run_locked(args)
            m.run_locked(args)
        records = list(m.existing_rows(output / "decisions.jsonl"))
        journals = list(m.existing_rows(output / "attempts.jsonl"))
        self.assertEqual(3, len(records))
        self.assertEqual(3, len(journals))
        self.assertTrue(records[1]["audit_only"]["duplicate_public_input"])
        self.assertTrue(journals[1]["duplicate_public_input"])
        self.assertEqual("accepted", journals[1]["status"])
        self.assertEqual(1, fake.created)
        self.assertTrue(all(m.read_outcomes(output, row["audit_only"]["outcome_samples_ref"]) for row in records))
        progress = json.loads((output / "progress.json").read_text())
        self.assertEqual(2, progress["unique_roots"])
        self.assertEqual(1, progress["duplicate_records_preserved"])

    def test_mocked_worker_timeout_records_unsuccessful_attempt_not_loss(self):
        output = self.output / "timeout-run"
        fake = self.fake_worker(timeout=True)
        catalog = {"cards": ["DaggerThrow"], "potions": ["FirePotion"], "relics": ["MeatOnTheBone"]}
        with patch.object(m, "source_catalog", return_value=catalog), patch.object(m, "Worker", fake), redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(RuntimeError, "attempt_budget_exhausted"):
                m.run_locked(self.run_args(output, attempts=1))
        self.assertFalse((output / "decisions.jsonl").exists())
        journal = list(m.existing_rows(output / "attempts.jsonl"))[0]
        self.assertEqual("failed_attempt", journal["status"])
        self.assertEqual(16, journal["uncommitted_requested_action_worlds"])
        self.assertGreaterEqual(journal["elapsed_seconds"], 0)
        self.assertNotIn("death_probability", journal)


if __name__ == "__main__": unittest.main()
