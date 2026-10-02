"""Dry allocation and synthetic-worker tests; never start a simulator or trainer."""
from argparse import Namespace
from collections import Counter, defaultdict
from contextlib import redirect_stdout
from copy import deepcopy
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from test_generate_pilot_data import m, ROOT, fixture, outcomes, SimulatedCrash
import prepare_dataset as preparing
import report_data_stage as reporting


class BalancedAllocationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = m.source_catalog(ROOT)
        cls.recipe = m.balanced_recipe(cls.catalog, "dry-balanced")

    def test_finite_prefix_cross_tabs_match_reviewed_support(self):
        cells, categories = defaultdict(set), Counter()
        expected = {1250: (500, 125, 125), 2500: (688, 250, 250),
                    7500: (688, 256, 750), 11720: (688, 256, 1172)}
        for index in range(11720):
            setup, category, allocation = m.balanced_scenario(index, self.recipe, "dry-balanced")
            categories[category] += 1
            cells[category].add((allocation["primary_id"], setup["enemy"], allocation["primary_upgrade"]))
            if index + 1 in expected:
                self.assertEqual(expected[index + 1], tuple(len(cells[key + "_constructed"])
                    for key in ("single_card", "potion", "relic")))
                self.assertEqual([4 * ((index + 1) // 10)] * 2 + [(index + 1) // 10] * 2,
                    [categories[key + "_constructed"] for key in ("starter", "single_card", "potion", "relic")])

    def test_every_primary_rotates_four_enemies_and_card_eight_combinations(self):
        visits = defaultdict(list)
        for index in range(11720):
            _, category, allocation = m.balanced_scenario(index, self.recipe, "dry-balanced")
            visits[(category, allocation["primary_id"])].append((allocation["enemy"], allocation["primary_upgrade"]))
        for (category, primary), values in visits.items():
            self.assertEqual(set(m.ENEMIES), {enemy for enemy, _ in values[:4]}, (category, primary))
            if category == "single_card_constructed": self.assertEqual(8, len(set(values[:8])), primary)

    def test_supporting_cycles_use_every_card_and_both_upgrades(self):
        for category, slot in (("potion", 8), ("relic", 9)):
            visits = defaultdict(list)
            for ordinal in range(172):
                setup, _, allocation = m.balanced_scenario(10 * ordinal + slot, self.recipe, "dry-balanced")
                visits[allocation["supporting_card"]].append(allocation["supporting_upgrade"])
                self.assertEqual(setup["deck"][0], allocation["supporting_card"] + "+" * allocation["supporting_upgrade"])
            self.assertEqual(set(self.catalog["cards"]), set(visits))
            self.assertTrue(all(set(values) == {0, 1} for values in visits.values()))
        self.assertNotEqual(self.recipe["permutations"]["potion_support"], self.recipe["permutations"]["relic_support"])

    def test_named_domains_do_not_couple_hp_to_upgrades_or_setup(self):
        baseline = m.balanced_scenario(14, self.recipe, "dry-balanced")
        real_hash = m.domain_hash
        def changed_hp(seed, domain, value):
            return "0" * 64 if domain in ("player-hp", "low-player-hp") else real_hash(seed, domain, value)
        with patch.object(m, "domain_hash", side_effect=changed_hp):
            changed = m.balanced_scenario(14, self.recipe, "dry-balanced")
        self.assertEqual(1, changed[0]["hp"])
        self.assertEqual({k: v for k, v in baseline[0].items() if k != "hp"}, {k: v for k, v in changed[0].items() if k != "hp"})
        self.assertEqual(baseline[1:], changed[1:])
        for index in range(100):
            setup, _, _ = m.balanced_scenario(index, self.recipe, "dry-balanced")
            self.assertTrue(1 <= setup["hp"] <= 12 if index % 7 == 0 else 12 <= setup["hp"] <= 70)
            self.assertTrue(12 <= setup["enemyHp"] <= 65)

    def test_catalog_shape_is_explicit_and_catalog_input_order_irrelevant(self):
        reversed_catalog = deepcopy(self.catalog)
        for key in ("cards", "potions", "relics"): reversed_catalog[key].reverse()
        other = m.balanced_recipe(reversed_catalog, "dry-balanced")
        self.assertEqual(self.recipe["permutations"], other["permutations"])
        for change in ("count", "upgrade", "missing"):
            bad = deepcopy(self.catalog)
            if change == "count": bad["cards"].pop()
            elif change == "upgrade": bad["max_upgrade"][bad["cards"][0]] = 2
            else: bad["max_upgrade"].pop(bad["cards"][0])
            with self.assertRaisesRegex(ValueError, "86_cards_with_max_upgrade_one"):
                m.balanced_recipe(bad, "dry-balanced")

    def test_stateless_one_seven_way_and_reverse_order_replay(self):
        reference = {i: m.balanced_scenario(i // 4, self.recipe, "dry-balanced") for i in range(68, 68 + 160)}
        partitioned = {}
        for shard in reversed(range(7)):
            recipe = m.balanced_recipe(self.catalog, "dry-balanced")
            for index in reversed(range(68, 68 + 160)):
                if index % 7 == shard: partitioned[index] = m.balanced_scenario(index // 4, recipe, "dry-balanced")
        self.assertEqual(reference, partitioned)
        self.assertEqual(m.scenario(17, self.catalog, "dry-balanced", "public-phase-v1", m.BALANCED_RECIPE), reference[68][:2])


class BalancedRunTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        runtime = patch.object(m, "runtime_fingerprints", return_value={"mock-worker": "synthetic"})
        runtime.start(); self.addCleanup(runtime.stop)

    def args(self, name, start=17, count=2, shard=0, shards=1, **overrides):
        return Namespace(**dict(repo=ROOT, output=self.root / name, teacher="T0", worlds=4, exploration_worlds=8,
            max_decisions=200, tree_depth=4, mode="pilot", seed_prefix="synthetic-balanced", roots_per_battle=4,
            root_policy="public-phase-v1", max_source_decisions=160, shard_id=shard, shard_count=shards,
            target_roots=1, max_attempts=None, timeout=90, max_worker_mib=768,
            recipe_version=m.BALANCED_RECIPE, attempted_battle_start=start, attempted_battle_count=count) | overrides)

    def worker(self, failures=(), crash=None):
        class FakeWorker:
            commands = []
            teacher_indices = []
            def __init__(self, *args): self.slot = 0
            def request(self, command):
                type(self).commands.append(deepcopy(command))
                if command["op"] in ("reset", "continue"):
                    self.slot = 0 if command["op"] == "reset" else self.slot + 1
                    row = fixture()
                    row["public_input"]["observation"]["turn"] = self.slot + 1
                    return {"status": "card_choice" if self.slot == 3 else "player_decision",
                            "observation": row["public_input"]["observation"], "actions": row["public_input"]["candidate_actions"]}
                if command["op"] != "teacher_record": raise AssertionError(command)
                index = 4 * int(command["sourceRun"].rsplit(":", 1)[1]) + self.slot
                type(self).teacher_indices.append(index)
                if index == crash: raise SimulatedCrash()
                if index in failures: raise TimeoutError("worker_response_deadline")
                row = fixture(index % 1000)
                for action in row["targets"]["actions"]:
                    action.update(allocated_worlds=4, completed_worlds=4)
                audit = row["audit_only"]
                audit.update(source_run_group=command["sourceRun"], source_combat_id=command["sourceCombat"],
                             branch_family=command["branchFamily"], n_independent_eval=4, n_exploration=0)
                audit["versions"]["observation_schema"] = row["public_input"]["observation"]["schema"]
                audit["continuation_version"] = m.LEGACY_CONTINUATION
                audit["versions"]["continuation"] = m.LEGACY_CONTINUATION
                audit["costs"].update(worlds_allocated=8, worlds_completed=8)
                audit["outcome_samples"] = outcomes(row)
                return row
            def close(self): pass
        return FakeWorker

    def run_mock(self, args, worker=None):
        with patch.object(m, "Worker", worker or self.worker()), redirect_stdout(io.StringIO()):
            m.run_locked(args)
        return list(m.existing_rows(args.output / "attempts.jsonl"))

    def test_exact_low_and_nonzero_blocks_never_stop_at_success_quota(self):
        for start in (0, 1, 17, 10001):
            args = self.args(str(start), start=start, max_attempts=8)
            journals = self.run_mock(args)
            self.assertEqual(list(range(start * 4, (start + 2) * 4)), [a["source_index"] for a in journals])
            self.assertEqual(8, len(journals))
            self.assertEqual({m.requested_phase("public-phase-v1", i) for i in range(4)}, {a["requested_source_phase"] for a in journals})
            progress = json.loads((args.output / "progress.json").read_text())
            self.assertEqual((8, 8, "attempted_block_complete"), (progress["attempts"], progress["planned_phase_requests_this_shard"], progress["status"]))
            self.assertGreater(progress["effective_full_candidate_roots"], args.target_roots)
            self.assertEqual(journals, self.run_mock(args))

    def test_partition_union_and_restart_have_identical_scenarios_seeds_and_source_ids(self):
        whole_args = self.args("whole", count=4)
        whole_worker = self.worker()
        whole = self.run_mock(whole_args, whole_worker)
        combined, commands = [], []
        for shard in reversed(range(7)):
            args = self.args(f"shard-{shard}", count=4, shard=shard, shards=7)
            worker = self.worker()
            combined.extend(self.run_mock(args, worker))
            self.run_mock(args, worker)
            commands.extend(c for c in worker.commands if c["op"] == "teacher_record")
        stable_attempt = lambda a: {k: a[k] for k in ("source_index", "source_battle_index", "source_step", "scenario", "allocation", "requested_source_phase", "generation_config_sha256")}
        self.assertEqual([stable_attempt(a) for a in whole], [stable_attempt(a) for a in sorted(combined, key=lambda a: a["source_index"])])
        self.assertEqual(sorted(m.canonical(c) for c in whole_worker.commands if c["op"] == "teacher_record"), sorted(m.canonical(c) for c in commands))

    def test_failed_and_interrupted_attempts_remain_in_block_and_world_budget(self):
        args = self.args("interrupted", max_attempts=8)
        with self.assertRaises(SimulatedCrash): self.run_mock(args, self.worker(failures={69}, crash=70))
        remaining_worker = self.worker()
        journals = self.run_mock(args, remaining_worker)
        self.assertEqual(list(range(68, 76)), [a["source_index"] for a in journals])
        self.assertEqual([71, 72, 73, 74, 75], remaining_worker.teacher_indices)
        self.assertEqual("failed_attempt", journals[1]["status"])
        self.assertEqual("interrupted_attempt", journals[2]["status"])
        self.assertEqual(8, journals[1]["uncommitted_requested_action_worlds"])
        self.assertEqual(8, journals[2]["uncommitted_requested_action_worlds"])
        self.assertIsNone(journals[2]["elapsed_seconds"])
        self.assertEqual(8, json.loads((args.output / "progress.json").read_text())["attempts"])
        self.assertFalse((args.output / "inflight_attempt.json").exists())

    def test_saved_success_journal_crash_does_not_repeat_source(self):
        args = self.args("journal-crash")
        append = m.append_json
        def fail_first_journal(path, value):
            if path.name == "attempts.jsonl": raise OSError("mock journal failure")
            append(path, value)
        with patch.object(m, "append_json", side_effect=fail_first_journal):
            with self.assertRaisesRegex(OSError, "mock journal failure"): self.run_mock(args)
        worker = self.worker()
        journals = self.run_mock(args, worker)
        self.assertEqual(list(range(69, 76)), worker.teacher_indices)
        self.assertEqual(8, len(journals))
        self.assertTrue(journals[0]["recovered"])

    def test_safe_pause_after_quota_hit_resumes_remaining_predeclared_requests(self):
        args = self.args("pause")
        finish = m.finish_attempt
        def pause_after_first(*call_args, **kwargs):
            finish(*call_args, **kwargs)
            (args.output / "PAUSE_REQUESTED").touch()
        with patch.object(m, "finish_attempt", side_effect=pause_after_first):
            self.assertEqual(1, len(self.run_mock(args)))
        progress = json.loads((args.output / "progress.json").read_text())
        self.assertEqual((1, 1, "paused_at_safe_boundary"),
                         (progress["attempts"], progress["effective_full_candidate_roots"], progress["status"]))
        (args.output / "PAUSE_REQUESTED").unlink()
        worker = self.worker()
        self.assertEqual(8, len(self.run_mock(args, worker)))
        self.assertEqual(list(range(69, 76)), worker.teacher_indices)

    def test_block_and_worker_budget_are_locked_but_disjoint_blocks_share_label_identity(self):
        first = self.args("first")
        self.run_mock(first)
        first_config = m.saved_config(first.output)
        second = self.args("second", start=19)
        self.run_mock(second)
        self.assertEqual(m.generation_identity_config(first_config), m.generation_identity_config(m.saved_config(second.output)))
        for override in ({"attempted_battle_start": 19}, {"attempted_battle_count": 3}, {"timeout": 91}, {"max_worker_mib": 769}):
            with self.assertRaisesRegex(ValueError, "Generation version/config changed"):
                self.run_mock(self.args("first", **override))
        self.assertEqual(first_config["recipe_metadata"]["catalog"], m.source_catalog(ROOT))
        self.assertEqual(first_config["teacher_options"], {"mode": "T0", "evaluationSeeds": [100001, 100002, 100003, 100004],
            "explorationSeeds": [], "maxDecisions": 200, "treeDepth": 4, "formalLabels": False})

    def test_invalid_or_insufficient_blocks_are_rejected_before_worker(self):
        for override in ({"attempted_battle_start": -1}, {"attempted_battle_start": None}, {"attempted_battle_count": 0},
                         {"attempted_battle_count": None}, {"max_attempts": 7}, {"root_policy": "opening-prefix-v1"},
                         {"roots_per_battle": 3}, {"teacher": "T1"}):
            with patch.object(m, "Worker") as worker, self.assertRaises(ValueError):
                m.run_locked(self.args("invalid", **override))
            worker.assert_not_called()

    def test_empty_modulo_partition_is_complete_without_worker(self):
        args = self.args("empty", start=0, count=1, shard=6, shards=7)
        with patch.object(m, "Worker") as worker:
            self.assertEqual([], self.run_mock(args, worker))
        worker.assert_not_called()
        self.assertEqual(0, json.loads((args.output / "progress.json").read_text())["attempts"])
        self.assertTrue(reporting.report_stage([args.output])["integrity"]["passed"])

    def test_all_failures_complete_the_declared_block_without_usable_roots(self):
        args = self.args("all-failures")
        journals = self.run_mock(args, self.worker(failures=set(range(68, 76))))
        self.assertEqual(8, len(journals))
        self.assertEqual({"failed_attempt"}, {a["status"] for a in journals})
        progress = json.loads((args.output / "progress.json").read_text())
        self.assertEqual((0, "attempted_block_complete"), (progress["unique_roots"], progress["status"]))
        report = reporting.report_stage([args.output])
        self.assertTrue(report["integrity"]["passed"], report["integrity"])
        self.assertEqual(8, report["counts"]["journal_failed_attempt"])

    def test_recovery_and_report_reject_indices_outside_declared_block(self):
        args = self.args("outside")
        self.run_mock(args)
        config = m.saved_config(args.output)
        for index in (67, 76):
            with self.assertRaisesRegex(ValueError, "source_outside_attempted_battle_block"):
                m.checked_source_index(index, config)
            with self.assertRaisesRegex(reporting.ReportError, "record_outside_attempted_battle_block"):
                reporting.source_index({"generation_source_index": index}, config)

    def test_legacy_recipe_cannot_resume_old_recipe_bytes_under_changed_source(self):
        args = self.args("old", recipe_version=m.LEGACY_RECIPE, attempted_battle_start=None,
                         attempted_battle_count=None, max_attempts=4)
        self.run_mock(args)
        config_path = args.output / "generation_config.json"
        config = json.loads(config_path.read_text())
        config["generator_sha256"] = "old-source-fingerprint"
        m.atomic_json(config_path, config)
        with self.assertRaisesRegex(ValueError, "Generation version/config changed"):
            self.run_mock(args)

    def test_report_and_preparation_accept_blocks_but_reject_recipe_mixing(self):
        a, b = self.args("a"), self.args("b", start=19)
        self.run_mock(a); self.run_mock(b)
        report = reporting.report_stage([a.output, b.output], verify_outcomes=True)
        self.assertTrue(report["integrity"]["passed"], report["integrity"])
        self.assertEqual(16, report["counts"]["valid_decision_rows"])
        self.assertEqual(2, len(report["configuration"]["attempted_battle_blocks"]))
        rows = list(m.existing_rows(a.output / "decisions.jsonl")) + list(m.existing_rows(b.output / "decisions.jsonl"))
        pipeline = preparing.read_json(ROOT / "configs/data_pipeline.v1.json")
        _, prepared, rejected = preparing.prepare(rows, pipeline, "pilot")
        self.assertFalse(rejected)
        self.assertEqual(16, prepared["usable_new_roots_before_holdout_exclusion"])
        old = self.args("legacy", recipe_version=m.LEGACY_RECIPE, attempted_battle_start=None,
                        attempted_battle_count=None, max_attempts=4)
        self.run_mock(old)
        with self.assertRaisesRegex(reporting.ReportError, "incompatible_generation_configs"):
            reporting.report_stage([a.output, old.output])
        old_rows = list(m.existing_rows(old.output / "decisions.jsonl"))
        _, _, rejected = preparing.prepare(old_rows, pipeline, "pilot", prepared["split_state"])
        self.assertEqual("append_record_versions_mismatch", rejected[0]["reason"])

    def test_overlap_keeps_source_ids_and_global_duplicate_detection(self):
        a, b = self.args("a"), self.args("b", start=18)
        self.run_mock(a); self.run_mock(b)
        left = {r["audit_only"]["generation_source_index"]: r for r in m.existing_rows(a.output / "decisions.jsonl")}
        right = {r["audit_only"]["generation_source_index"]: r for r in m.existing_rows(b.output / "decisions.jsonl")}
        for index in left.keys() & right.keys():
            for key in ("source_run_group", "source_combat_id", "branch_family", "generation_config_sha256", "scenario_recipe"):
                self.assertEqual(left[index]["audit_only"][key], right[index]["audit_only"][key])
        report = reporting.report_stage([a.output, b.output])
        self.assertFalse(report["integrity"]["passed"])
        self.assertEqual(4, report["counts"]["invalid_decision_rows"])
        self.assertEqual(4, report["counts"]["invalid_attempt_rows"])


if __name__ == "__main__": unittest.main()
