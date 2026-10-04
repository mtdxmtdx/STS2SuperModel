"""Synthetic contract fixtures only; never game evidence or generated labels."""
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import prepare_dataset as data


def fixture(number=0):
    card = {"id": "StrikeSilent", "upgrade": 0, "cost": 1, "starCost": 0, "type": "Attack", "keywords": []}
    observation = {"schema": "nosl.public.v1", "startHp": 60, "ascension": 10, "turn": 1, "hp": 60, "maxHp": 70,
                   "block": 0, "energy": 3, "stars": 0, "hand": [card], "discard": [], "exhaust": [],
                   "unknownDraw": [{"card": card, "count": 1}], "knownDraw": [], "drawCount": 1,
                   "potions": [], "relics": [], "powers": [],
                   "enemies": [{"slot": 0, "id": "TwigSlimeS", "hp": 20 + number, "maxHp": 2000, "block": 0,
                                "powers": [], "intents": [{"kind": "Attack", "damage": 5, "repeats": 1}]}],
                   "history": [{"kind": "combat_started", "detail": "Silent:A10"}], "choice": None}
    actions = [{"revision": 0, "kind": "play", "slot": 0, "target": 0, "selection": None},
               {"revision": 0, "kind": "end_turn", "slot": -1, "target": -1, "selection": None}]
    public = {"schema_version": "nosl.student.public.v1", "observation": observation, "history_complete": True,
              "controller_context": {"status": "inactive"}, "candidate_actions": actions, "legal_mask": [True, True]}
    targets = []
    for index in range(2):
        hp = 60 - index * 5
        targets.append({"action_index": index, "quality": "complete", "masks": {head: True for head in data.HEADS},
                        "value": hp - 60, "win_probability": 1., "death_probability": 0., "expected_final_hp": hp,
                        "hp_distribution": [{"hp": hp, "probability": 1.}], "potion_net_change": 0.,
                        "allocated_worlds": 8, "completed_worlds": 8, "truncated_worlds": 0, "error_worlds": 0, "other_worlds": 0})
    return {"public_input": public, "targets": {"actions": targets, "pairwise": [], "equivalent_action_set": []},
            "audit_only": {"source_run_group": f"run-{number}", "source_combat_id": f"combat-{number}",
                           "branch_family": f"branch-{number}", "public_state_digest": f"digest-{number}",
                           "source_kind": "constructed", "engineering_smoke": True,
                           "versions": {"teacher": "T0-test", "continuation": "test-policy", "objective": "synthetic-contract",
                                        "simulator": "test-simulator", "public_schema": "nosl.student.public.v1"},
                           "n_exploration": 2, "n_independent_eval": 8, "n_unresolved": 0, "n_error": 0,
                           "independent_final_evaluation": True,
                           "costs": {"root_candidates": 2, "worlds_allocated": 16, "worlds_completed": 16,
                                     "rollout_decisions": 32, "elapsed_seconds": 2., "clone_seconds": .2,
                                     "settlement_seconds": .1, "peak_worker_memory_bytes": 1024}}}


def unresolved(record, index=1, *, error=False):
    row = record["targets"]["actions"][index]
    row["quality"] = "unresolved"
    row["completed_worlds"] = 7
    row["error_worlds" if error else "truncated_worlds"] = 1
    for head in data.HEADS:
        row[head], row["masks"][head] = None, False
    record["audit_only"]["n_error" if error else "n_unresolved"] = 1
    record["audit_only"]["costs"]["worlds_completed"] = 15
    return record


class DataPreparationTests(unittest.TestCase):
    def setUp(self):
        self.config = data.read_json(ROOT / "configs/data_pipeline.v1.json")
        self.student = data.load_student_config()

    def prepare(self, records, state=None, mode="pilot"):
        return data.prepare(records, self.config, mode, state, self.student)

    def assertRejected(self, record, reason):
        splits, report, rejected = self.prepare([record])
        self.assertEqual(0, sum(map(len, splits.values())))
        self.assertIn(reason, rejected[0]["reason"])
        self.assertEqual(record, rejected[0]["record"])

    def split_for(self, record):
        groups, _, _ = data.provenance_components([record])
        return data.choose_split(groups[0], self.config)

    def representative_splits(self):
        found = {}
        for number in range(300):
            record = fixture(number)
            found.setdefault(self.split_for(record), record)
            if len(found) == 3:
                return found
        self.fail("Fixture failed to produce three independent split groups")

    def test_complete_root_no_mutation(self):
        record = fixture()
        before = deepcopy(record)
        splits, report, rejected = self.prepare([record])
        self.assertEqual(1, report["full_candidate_complete_new_roots"])
        self.assertEqual(0, report["auxiliary_only_usable_new_roots"])
        self.assertEqual([], rejected)
        self.assertEqual(before, record)
        out = next(row for rows in splits.values() for row in rows)
        self.assertEqual(before["public_input"], out["public_input"])
        self.assertNotIn("prepared_public_input_digest", out["public_input"])

    def test_incomplete_candidate_kept_whole_with_null_masks(self):
        record = unresolved(fixture())
        splits, report, rejected = self.prepare([record])
        self.assertEqual([], rejected)
        self.assertEqual(0, report["full_candidate_complete_new_roots"])
        self.assertEqual(1, report["auxiliary_only_usable_new_roots"])
        self.assertEqual(1, report["retained_unresolved_actions"])
        out = next(row for rows in splits.values() for row in rows)
        self.assertEqual(2, len(out["targets"]["actions"]))
        self.assertIsNone(out["targets"]["actions"][1]["value"])

    def test_error_is_never_converted_to_loss(self):
        record = unresolved(fixture(), error=True)
        _, report, _ = self.prepare([record])
        self.assertEqual(1, report["throughput"]["error_action_worlds"])
        self.assertEqual(15 / 16, report["throughput"]["effective_sample_rate"])
        self.assertIsNone(record["targets"]["actions"][1]["death_probability"])

    def test_incomplete_point_target_rejected(self):
        record = unresolved(fixture())
        row = record["targets"]["actions"][1]
        row["value"], row["masks"]["value"] = 0., True
        self.assertRejected(record, "incomplete_worlds_have_point_targets")

    def test_unresolved_complete_accounting_cannot_enable_target(self):
        record = fixture()
        record["targets"]["actions"][0]["quality"] = "unresolved"
        self.assertRejected(record, "unresolved_action_has_point_targets")

    def test_masked_zero_rejected(self):
        record = fixture()
        record["targets"]["actions"][0]["masks"]["value"] = False
        self.assertRejected(record, "masked_target_must_be_null:value")

    def test_missing_candidate_rejects_whole_root(self):
        record = fixture()
        record["targets"]["actions"].pop()
        self.assertRejected(record, "candidate_target_coverage_incomplete")

    def test_reordered_action_targets_rejected(self):
        record = fixture()
        record["targets"]["actions"].reverse()
        self.assertRejected(record, "action_index_alignment_invalid")

    def test_world_accounting_conservation(self):
        record = fixture()
        record["targets"]["actions"][0]["completed_worlds"] = 7
        self.assertRejected(record, "world_count_conservation_failed")

    def test_top_level_error_accounting_consistent(self):
        record = unresolved(fixture(), error=True)
        record["audit_only"]["n_error"] = 0
        self.assertRejected(record, "audit_error_count_inconsistent")

    def test_unresolved_objective_auxiliary_targets_preserved(self):
        record = fixture()
        for row in record["targets"]["actions"]:
            row["quality"] = "objective_value_unresolved"
            row["masks"]["value"], row["value"] = False, None
        _, report, rejected = self.prepare([record])
        self.assertEqual([], rejected)
        self.assertEqual(1, report["all_candidate_trajectory_complete_new_roots"])
        self.assertEqual(1, report["auxiliary_only_usable_new_roots"])
        self.assertEqual(0, report["full_candidate_complete_new_roots"])

    def test_incomplete_root_cannot_claim_pairwise_win(self):
        record = unresolved(fixture())
        record["targets"]["pairwise"] = [{"preferred": 0, "other": 1, "weight": 1.}]
        self.assertRejected(record, "incomplete_root_has_strong_ranking")

    def test_unknown_metadata_cannot_enter_public(self):
        record = fixture()
        record["public_input"]["teacher_confidence"] = 1
        self.assertRejected(record, "public_input_fields_not_whitelisted")

    def test_nested_private_seed_rejected(self):
        record = fixture()
        record["public_input"]["observation"]["seed"] = 42
        self.assertRejected(record, "public_input_invalid")

    def test_natural_distribution_requires_evidence(self):
        record = fixture()
        record["audit_only"]["source_kind"] = "natural"
        self.assertRejected(record, "natural_source_requires_reachability_evidence")

    def test_seed_overlap_and_independence_guard(self):
        record = fixture()
        record["audit_only"].update(sampler_seeds=[1, 2], exploration_seeds=[2, 3])
        self.assertRejected(record, "exploration_evaluation_seed_overlap")
        record = fixture()
        record["audit_only"]["independent_final_evaluation"] = False
        self.assertRejected(record, "independent_eval_unverified")

    def test_transitive_run_combat_branch_and_digest_union(self):
        rows = [fixture(i) for i in range(5)]
        rows[1]["audit_only"]["source_run_group"] = rows[0]["audit_only"]["source_run_group"]
        rows[2]["audit_only"]["source_combat_id"] = rows[1]["audit_only"]["source_combat_id"]
        rows[3]["audit_only"]["branch_family"] = rows[2]["audit_only"]["branch_family"]
        rows[4]["audit_only"]["public_state_digest"] = rows[3]["audit_only"]["public_state_digest"]
        splits, report, rejected = self.prepare(rows)
        self.assertEqual([], rejected)
        self.assertEqual(1, len(report["split_state"]["components"]))
        self.assertEqual([5], [len(x) for x in splits.values() if x])

    def test_invalid_bridge_still_connects_valid_roots(self):
        a, bridge, b = fixture(1), fixture(2), fixture(3)
        bridge["audit_only"]["source_run_group"] = a["audit_only"]["source_run_group"]
        bridge["audit_only"]["branch_family"] = b["audit_only"]["branch_family"]
        bridge["targets"]["actions"].pop()
        splits, report, rejected = self.prepare([a, bridge, b])
        self.assertEqual(1, len(rejected))
        self.assertEqual([2], [len(x) for x in splits.values() if x])
        self.assertEqual(1, len(report["split_state"]["components"]))

    def test_exact_public_dedup_ignores_different_provenance(self):
        first, second = fixture(1), fixture(2)
        second["public_input"] = deepcopy(first["public_input"])
        _, report, rejected = self.prepare([first, second])
        self.assertEqual(2, report["input_attempts"])
        self.assertEqual(1, report["input_unique_public_roots"])
        self.assertEqual(1, report["usable_new_roots_before_holdout_exclusion"])
        self.assertEqual(1, report["duplicate_attempts"])
        self.assertEqual("exact_public_input_duplicate", rejected[0]["reason"])
        self.assertEqual(2, report["throughput"]["decision_roots"])

    def test_numeric_json_digest_equivalence(self):
        self.assertEqual(data.public_digest({"x": 1}), data.public_digest({"x": 1.0}))

    def test_partition_independent_of_record_order(self):
        records = [fixture(i) for i in range(20)]
        one, _, _ = self.prepare(records)
        two, _, _ = self.prepare(list(reversed(records)))
        mapping = lambda splits: {data.public_digest(row["public_input"]): split for split, rows in splits.items() for row in rows}
        self.assertEqual(mapping(one), mapping(two))

    def test_append_inherits_prior_split_even_if_group_hash_changes(self):
        source = self.representative_splits()["train"]
        _, report, _ = self.prepare([source])
        appended = fixture(301)
        appended["audit_only"]["source_run_group"] = source["audit_only"]["source_run_group"]
        splits, next_report, _ = self.prepare([appended], report["split_state"])
        self.assertEqual(1, len(splits["train"]))
        self.assertEqual(1, len(next_report["split_state"]["components"]))

    def test_frozen_test_never_extended(self):
        roots = self.representative_splits()
        _, first, _ = self.prepare(list(roots.values()))
        appended = fixture(302)
        appended["audit_only"]["source_run_group"] = roots["test"]["audit_only"]["source_run_group"]
        splits, second, rejected = self.prepare([appended], first["split_state"])
        self.assertEqual([], splits["test"])
        self.assertEqual(1, second["usable_new_roots_before_holdout_exclusion"])
        self.assertEqual(1, second["frozen_holdout_excluded_roots"])
        self.assertEqual(first["split_state"]["frozen_test_digests"], second["split_state"]["frozen_test_digests"])
        self.assertEqual("test", rejected[0]["inherited_split"])

    def test_crosssplit_bridge_quarantines_and_permanently_blocks(self):
        roots = self.representative_splits()
        _, first, _ = self.prepare(list(roots.values()))
        bridge = fixture(303)
        bridge["audit_only"]["source_run_group"] = roots["train"]["audit_only"]["source_run_group"]
        bridge["audit_only"]["branch_family"] = roots["test"]["audit_only"]["branch_family"]
        _, second, rejected = self.prepare([bridge], first["split_state"])
        self.assertEqual("historical_cross_split_merge", rejected[0]["reason"])
        self.assertFalse(second["split"]["audit_passed"])
        _, third, _ = self.prepare([fixture(304)], second["split_state"])
        self.assertFalse(third["split"]["audit_passed"])

    def test_previous_component_edges_connect_new_rows(self):
        old_a, old_b = fixture(1), fixture(2)
        old_a["audit_only"]["source_run_group"] = old_b["audit_only"]["source_run_group"]
        _, first, _ = self.prepare([old_a, old_b])
        a, b = fixture(301), fixture(302)
        a["audit_only"]["source_combat_id"] = old_a["audit_only"]["source_combat_id"]
        b["audit_only"]["source_combat_id"] = old_b["audit_only"]["source_combat_id"]
        _, second, _ = self.prepare([a, b], first["split_state"])
        self.assertEqual(1, len(second["split_state"]["components"]))

    def test_changed_config_or_mode_rejects_append(self):
        _, report, _ = self.prepare([fixture()])
        with self.assertRaisesRegex(data.ValidationError, "configuration_or_mode"):
            self.prepare([fixture(1)], report["split_state"], mode="engineering-smoke")
        self.config["split_seed"] += "changed"
        with self.assertRaisesRegex(data.ValidationError, "configuration_or_mode"):
            self.prepare([fixture(1)], report["split_state"])

    def test_changed_versions_quarantined(self):
        _, report, _ = self.prepare([fixture()])
        record = fixture(1)
        record["audit_only"]["versions"]["teacher"] = "T1-different"
        _, _, rejected = self.prepare([record], report["split_state"])
        self.assertEqual("append_record_versions_mismatch", rejected[0]["reason"])

    def test_per_root_policy_digest_is_audit_only(self):
        a, b = fixture(0), fixture(1)
        a["audit_only"]["continuation_version"] = "frozen:123"
        b["audit_only"]["continuation_version"] = "frozen:456"
        _, report, rejected = self.prepare([a, b])
        self.assertFalse(rejected)
        self.assertEqual(2, report["usable_new_roots_before_holdout_exclusion"])

    def test_throughput_unknown_timers_not_zero(self):
        record = fixture()
        record["audit_only"]["costs"]["clone_seconds"] = None
        _, report, _ = self.prepare([record])
        self.assertIsNone(report["throughput"]["clone_seconds_sum"])
        self.assertEqual(1, report["throughput"]["missing_cost_rows"]["clone_seconds"])
        self.assertFalse(report["quality_gate"]["throughput_measurements_complete"])

    def test_formal_never_enabled_by_flipping_config(self):
        self.config["formal_labels_enabled"], self.config["formal_blockers"] = True, []
        with self.assertRaisesRegex(data.ValidationError, "formal_data_blocked"):
            self.prepare([fixture()], mode="formal")

    def test_probability_mass_and_hp_mean(self):
        record = fixture()
        record["targets"]["actions"][0]["hp_distribution"][0]["probability"] = .9
        self.assertRejected(record, "hp_distribution_probability_mass_invalid")
        record = fixture()
        record["targets"]["actions"][0]["expected_final_hp"] = 20
        self.assertRejected(record, "hp_distribution_mean_inconsistent")

    def test_independent_world_count_not_action_world_count(self):
        _, report, _ = self.prepare([fixture()])
        self.assertEqual(8, report["throughput"]["independent_eval_world_draws"])
        self.assertEqual(16, report["throughput"]["allocated_action_worlds"])
        record = fixture()
        record["audit_only"]["n_independent_eval"] = 16
        self.assertRejected(record, "root_independent_world_count_inconsistent")

    def test_repeated_evaluation_seeds_not_independent(self):
        record = fixture()
        record["audit_only"].update(sampler_seeds=[1] * 8, exploration_seeds=[20, 21])
        self.assertRejected(record, "sampler_seed_count_or_uniqueness_invalid")

    def test_cumulative_effective_counts_exclude_duplicate_and_frozen_new_roots(self):
        roots = self.representative_splits()
        _, initial, _ = self.prepare(list(roots.values()))
        new_test = fixture(401)
        new_test["audit_only"]["source_run_group"] = roots["test"]["audit_only"]["source_run_group"]
        _, appended, _ = self.prepare([roots["train"], new_test], initial["split_state"])
        self.assertEqual(5, appended["cumulative"]["input_attempts"])
        self.assertEqual(4, appended["cumulative"]["unique_usable_roots_before_holdout_exclusion"])
        self.assertEqual(3, appended["cumulative"]["effective_dataset_roots"])
        self.assertEqual(1, appended["duplicate_attempts"])
        self.assertEqual(1, appended["frozen_holdout_excluded_roots"])

    def test_state_owns_versions_not_raw_record_reference(self):
        record = fixture()
        _, report, _ = self.prepare([record])
        record["audit_only"]["versions"]["teacher"] = "changed_after_preparation"
        self.assertEqual("T0-test", report["split_state"]["versions"]["teacher"])

    def persist(self, output, records, resume=False, tag="a", shard_size=1):
        return data.persist_batch(output, records, [{"path": tag, "line": i + 1} for i in range(len(records))],
                                  self.config, "pilot", [{"path": tag, "sha256": data.object_digest(records), "bytes": 0}],
                                  resume, shard_size)

    def test_shards_verify_and_identical_resume_is_idempotent(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            records = list(self.representative_splits().values())
            self.persist(output, records)
            before = (output / "manifest.json").read_bytes()
            result = self.persist(output, records, resume=True)
            self.assertEqual("ALREADY_COMMITTED", result["status"])
            self.assertEqual(before, (output / "manifest.json").read_bytes())
            self.assertEqual(1, len(data.iter_dataset_paths(output, "test")))
            data.verify_manifest(output)

    def test_append_keeps_original_test_shard_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            roots = self.representative_splits()
            self.persist(output, list(roots.values()))
            tests = data.iter_dataset_paths(output, "test")
            original = {path: path.read_bytes() for path in tests}
            new = fixture(306)
            new["audit_only"]["source_run_group"] = roots["test"]["audit_only"]["source_run_group"]
            self.persist(output, [new], resume=True, tag="b")
            self.assertEqual(tests, data.iter_dataset_paths(output, "test"))
            self.assertTrue(all(path.read_bytes() == value for path, value in original.items()))
            self.assertEqual(2, len(data.verify_manifest(output)["stages"]))

    def test_tampered_shard_stops_resume_without_mutating_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            roots = self.representative_splits()
            self.persist(output, list(roots.values()))
            before = (output / "manifest.json").read_bytes()
            path = data.iter_dataset_paths(output, "train")[0]
            path.write_text("tampered\n")
            with self.assertRaisesRegex(data.ValidationError, "checksum_mismatch"):
                self.persist(output, [fixture(309)], resume=True, tag="b")
            self.assertEqual(before, (output / "manifest.json").read_bytes())

    def test_mixed_versions_do_not_commit_stage(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            a, b = fixture(), fixture(1)
            b["audit_only"]["versions"]["objective"] = "new"
            with self.assertRaisesRegex(data.ValidationError, "versions_mismatch"):
                self.persist(output, [a, b])
            self.assertFalse((output / "manifest.json").exists())

    def test_recover_stage_published_before_root_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            original_replace = data.os.replace
            def fail_root(src, dst):
                if Path(dst).name == "manifest.json":
                    raise OSError("simulated interruption")
                return original_replace(src, dst)
            with patch.object(data.os, "replace", side_effect=fail_root):
                with self.assertRaisesRegex(OSError, "simulated interruption"):
                    self.persist(output, [fixture()])
            self.assertFalse((output / "manifest.json").exists())
            self.persist(output, [fixture()], resume=True)
            self.assertEqual(1, len(data.verify_manifest(output)["stages"]))

    def test_concurrent_writer_lock(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with data.output_lock(output):
                with self.assertRaisesRegex(data.ValidationError, "locked_by_another"):
                    self.persist(output, [fixture()])

    def test_manifest_path_traversal_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaisesRegex(data.ValidationError, "outside_output"):
                data.safe_path(Path(temp), "../escape")

    def test_cli_prepare_resume_verify(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            input_path = root / "synthetic.jsonl"
            input_path.write_text(json.dumps(fixture()) + "\n", encoding="utf-8")
            command = [sys.executable, "-B", str(ROOT / "tools/prepare_dataset.py"), str(input_path),
                       "--output-dir", str(root / "dataset"), "--mode", "engineering-smoke", "--shard-size", "1"]
            run = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stderr)
            repeated = subprocess.run(command + ["--resume"], capture_output=True, text=True)
            self.assertEqual("ALREADY_COMMITTED", json.loads(repeated.stdout)["status"])
            verify = subprocess.run(command[:3] + ["--output-dir", str(root / "dataset"), "--verify-only"], capture_output=True, text=True)
            self.assertEqual(0, verify.returncode, verify.stderr)


if __name__ == "__main__":
    unittest.main()
