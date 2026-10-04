"""Bounded synthetic fixtures exercise diagnostics, never gameplay/data quality."""
from copy import deepcopy
import gzip
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).parent))
import report_data_stage as reporting
import prepare_dataset as preparing
from test_prepare_dataset import fixture, unresolved


def write_rows(path, rows):
    with path.open("w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row) + "\n")


def shard(path, shard_id=0, shard_count=1, **config_overrides):
    path.mkdir(parents=True)
    recipe = b"# Synthetic immutable generation recipe, not executed.\n"
    (path / "generation_recipe.py").write_bytes(recipe)
    config = {"roots_per_battle": 3, "data_mode": "pilot", "seed_prefix": "unit-corpus",
              "source_kind": "constructed", "version": "unit-v1", "formal_labels": False,
              "runtime_files": {}, "generator_sha256": hashlib.sha256(recipe).hexdigest(),
              "teacher_options": {"mode": "T0", "worlds": 8},
              "execution_partition": {"shard_id": shard_id, "shard_count": shard_count}, **config_overrides}
    (path / "generation_config.json").write_text(json.dumps(config), encoding="utf-8")
    write_rows(path / "decisions.jsonl", [])
    write_rows(path / "attempts.jsonl", [])
    return config


def record(index, config):
    row = fixture(index)
    audit = row["audit_only"]
    config_hash = reporting.digest_bytes(reporting.generator_canonical(reporting.stable_config(config)).encode())
    audit.update(generation_source_index=index, generation_config_sha256=config_hash,
                 source_category="synthetic_constructed", source_run_group=f"run-{index // 3}",
                 source_combat_id=f"battle-{index // 3}", branch_family=f"family-{index // 3}")
    audit["versions"].update(generation_config=config_hash, observation_schema=row["public_input"]["observation"]["schema"])
    return row


def attempt(index, status="accepted", elapsed=3., **extra):
    return {"source_index": index, "source_battle_index": index // 3, "status": status,
            "elapsed_seconds": elapsed, "source_category": "synthetic_constructed", **extra}


def outcomes(row):
    groups = []
    for action in row["targets"]["actions"]:
        values = []
        for key, terminal in (("completed_worlds", "Win"), ("truncated_worlds", "ComputeTruncated"),
                              ("error_worlds", "EngineError"), ("other_worlds", "PolicyNonterminating")):
            values.extend({"terminalKind": terminal, "isTrueTerminal": terminal == "Win",
                           "settlementComplete": terminal == "Win"} for _ in range(action[key]))
        groups.append({"action_index": action["action_index"], "outcomes": values})
    return groups


def diagnostic_record(index, config):
    row = record(index, config)
    for number, action in enumerate(row["targets"]["actions"]):
        action.update(quality="unresolved", completed_worlds=7)
        action["truncated_worlds" if number == 0 else "error_worlds"] = 1
        for head in reporting.HEADS:
            action[head], action["masks"][head] = None, False
    row["audit_only"].update(n_error=1, n_unresolved=1)
    row["audit_only"]["costs"]["worlds_completed"] = 14
    return row


def archive(path, row):
    raw = reporting.generator_canonical(outcomes(row)).encode()
    compressed = gzip.compress(raw, mtime=0)
    member = path / "outcomes.jsonl.gz"
    with member.open("ab") as stream:
        offset = stream.tell()
        stream.write(compressed)
    row["audit_only"]["outcome_samples_ref"] = {"path": member.name, "offset": offset,
        "compressed_bytes": len(compressed), "uncompressed_bytes": len(raw),
        "sha256": reporting.digest_bytes(raw), "format": "independent-gzip-member-json",
        "action_count": len(row["targets"]["actions"])}


class ReportingTests(unittest.TestCase):
    def test_attempt_deadline_is_reported_as_timeout_not_game_loss(self):
        self.assertEqual(("timeout", "source_attempt_deadline"), reporting.classify_failure("source_attempt_deadline"))

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / "shard-0"
        self.config = shard(self.path)

    def install(self, rows, attempts=None, path=None):
        path = path or self.path
        write_rows(path / "decisions.jsonl", rows)
        write_rows(path / "attempts.jsonl", attempts if attempts is not None else [attempt(row["audit_only"]["generation_source_index"]) for row in rows])

    def report(self, **kwargs):
        return reporting.report_stage([self.path], **kwargs)

    def test_complete_root_inline_outcomes_and_resource_units(self):
        row = record(0, self.config)
        row["audit_only"]["outcome_samples"] = outcomes(row)
        self.install([row])
        result = self.report(verify_outcomes=True)
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual(1, result["counts"]["full_utility_roots"])
        self.assertEqual(8, result["worlds"]["unique_root_independent_eval_worlds"])
        self.assertEqual(16, result["worlds"]["completed_worlds"])
        self.assertEqual(.1, result["timing"]["clone_fraction_of_teacher_elapsed"])
        self.assertIsNone(result["timing"]["wall_clock_seconds"])
        self.assertEqual(1, result["raw_outcomes"]["verified_records_by_storage"]["inline"])

    def test_numeric_canonicalization_deduplicates_across_shards(self):
        # Use two partition-compatible shards and different source indices.
        self.config["execution_partition"]["shard_count"] = 2
        (self.path / "generation_config.json").write_text(json.dumps(self.config))
        other = self.root / "shard-1"
        config = shard(other, 1, 2)
        a, b = record(0, self.config), record(1, config)
        b["public_input"] = deepcopy(a["public_input"])
        b["public_input"]["observation"]["block"] = 0.0
        self.install([a])
        self.install([b], path=other)
        result = reporting.report_stage([self.root])
        self.assertEqual(2, result["counts"]["valid_decision_rows"])
        self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
        self.assertEqual(1, result["counts"]["duplicate_public_roots"])
        self.assertEqual(1, result["source_identities"]["attempts"]["battles"])
        self.assertEqual(4., result["timing"]["valid_record_elapsed_seconds"])
        self.assertEqual(8, result["worlds"]["unique_root_independent_eval_worlds"])

    def test_incompatible_shards_cannot_be_combined(self):
        other = self.root / "engineering"
        shard(other, data_mode="engineering-smoke")
        with self.assertRaisesRegex(reporting.ReportError, "incompatible_generation_configs"):
            reporting.report_stage([self.path, other])

    def test_only_execution_partition_is_excluded_from_config_equality(self):
        other = self.root / "shard-1"
        shard(other, 1, 2, teacher_options={"mode": "T1", "worlds": 8})
        with self.assertRaisesRegex(reporting.ReportError, "incompatible_generation_configs"):
            reporting.report_stage([self.path, other])

    def test_invalid_record_does_not_inflate_effective_counts(self):
        good, bad = record(0, self.config), record(1, self.config)
        bad["targets"]["actions"][0]["completed_worlds"] = 7
        self.install([good, bad])
        result = self.report()
        self.assertEqual(1, result["counts"]["invalid_decision_rows"])
        self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
        self.assertEqual(16, result["worlds"]["allocated_worlds"])
        self.assertEqual(1, result["counts"]["accepted_journal_without_valid_record"])
        self.assertFalse(result["integrity"]["passed"])

    def test_all_masked_conserved_diagnostics_reconcile_without_effective_points(self):
        good, diagnostic = record(0, self.config), diagnostic_record(1, self.config)
        diagnostic["public_input"]["observation"].update(turn=3, hp=10)
        good["audit_only"]["posterior_profile"] = "stable-fast"
        diagnostic["audit_only"]["posterior_profile"] = "whole-setup-rejection"
        for row in (good, diagnostic):
            archive(self.path, row)
        self.install([good, diagnostic])
        before = deepcopy(diagnostic)
        result = self.report(verify_outcomes=True)
        self.assertTrue(result["integrity"]["passed"], result["integrity"])
        self.assertEqual(2, result["counts"]["valid_decision_rows"])
        self.assertEqual(2, result["counts"]["unique_valid_public_roots"])
        self.assertEqual(1, result["counts"]["diagnostic_only_decision_rows"])
        self.assertEqual(1, result["counts"]["diagnostic_only_public_roots"])
        self.assertEqual(1, result["counts"]["unique_effective_public_roots"])
        self.assertEqual(1, result["counts"]["completed_whole_candidate_roots"])
        self.assertEqual(0, result["counts"]["auxiliary_only_no_value_roots"])
        self.assertEqual(0, result["counts"]["accepted_journal_without_valid_record"])
        self.assertEqual(0, result["counts"]["valid_records_without_accepted_journal"])
        self.assertEqual(2, result["raw_outcomes"]["verified_records_by_storage"]["gzip_reference"])
        self.assertEqual(1, result["root_state_distribution"]["roots"])
        self.assertEqual({"1": 1}, result["root_state_distribution"]["turn_histogram"])
        self.assertEqual(0, result["counts"]["after_hp_loss_roots"])
        self.assertEqual(16, result["worlds"]["completed_worlds"])
        self.assertEqual(0, result["worlds"]["error_worlds"])
        saved = result["saved_outcome_accounting"]
        self.assertEqual(30, saved["valid_records"]["completed_worlds"])
        self.assertEqual(1, saved["diagnostic_only_records"]["error_worlds"])
        self.assertEqual(1, saved["diagnostic_only_records"]["truncated_worlds"])
        self.assertEqual(14, saved["diagnostic_only_records"]["completed_worlds"])
        self.assertEqual(4., result["timing"]["valid_record_elapsed_seconds"])
        self.assertEqual(6., result["timing"]["journal_total_service_seconds"])
        self.assertEqual(1 / 6, result["timing"]["unique_effective_roots_per_service_second"])
        distributions = result["source_distributions"]
        self.assertEqual({"stable-fast": 1, "whole-setup-rejection": 1}, distributions["unique_valid_roots"]["posterior_profile"])
        self.assertEqual({"stable-fast": 1}, distributions["unique_effective_roots"]["posterior_profile"])
        self.assertEqual(before, diagnostic)

    def test_default_preparation_still_rejects_structurally_valid_diagnostics(self):
        row = diagnostic_record(0, self.config)
        config = preparing.read_json(ROOT / "configs/data_pipeline.v1.json")
        preparing.validate_record(row, config, "pilot", require_usable=False)
        with self.assertRaisesRegex(preparing.ValidationError, "root_has_no_usable_targets"):
            preparing.validate_record(row, config, "pilot")
        splits, _, rejected = preparing.prepare([row], config, "pilot")
        self.assertEqual(0, sum(map(len, splits.values())))
        self.assertEqual("root_has_no_usable_targets", rejected[0]["reason"])

    def test_masked_diagnostics_still_check_late_audit_cost_schema_and_raw_evidence(self):
        corruptions = (
            (lambda r: r["audit_only"].update(n_error=0), "audit_error_count_inconsistent"),
            (lambda r: r["audit_only"].update(n_unresolved=0), "audit_unresolved_count_inconsistent"),
            (lambda r: r["audit_only"].update(n_independent_eval=9), "root_independent_world_count_inconsistent"),
            (lambda r: r["audit_only"]["costs"].update(worlds_completed=16), "cost_world_count_inconsistent"),
            (lambda r: r["audit_only"].update(source_combat_id=""), "provenance_missing"),
            (lambda r: r["targets"]["actions"][0].update(value=0), "masked_target_must_be_null"),
            (lambda r: r["audit_only"]["outcome_samples"][0]["outcomes"].pop(), "raw_outcome_accounting_mismatch"),
        )
        for mutate, error in corruptions:
            with self.subTest(error=error):
                row = diagnostic_record(0, self.config)
                row["audit_only"]["outcome_samples"] = outcomes(row)
                mutate(row)
                self.install([row])
                result = self.report(verify_outcomes=True)
                self.assertFalse(result["integrity"]["passed"])
                self.assertIn(error, str(result["integrity"]["error_counts"]))
                self.assertEqual(1, result["counts"]["invalid_decision_rows"])
                self.assertEqual(0, result["counts"]["diagnostic_only_decision_rows"])
                self.assertEqual(0, result["counts"]["unique_effective_public_roots"])
                self.assertEqual(0, result["saved_outcome_accounting"]["valid_records"]["allocated_worlds"])
                self.assertEqual(1, result["counts"]["accepted_journal_without_valid_record"])

    def test_diagnostic_first_duplicate_does_not_suppress_later_usable_whole_root(self):
        diagnostic, good = diagnostic_record(0, self.config), record(3, self.config)
        good["public_input"] = deepcopy(diagnostic["public_input"])
        for rows in ([diagnostic, good], [good, diagnostic]):
            with self.subTest(diagnostic_first=rows[0] is diagnostic):
                self.install(rows)
                result = self.report()
                self.assertTrue(result["integrity"]["passed"])
                self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
                self.assertEqual(1, result["counts"]["unique_effective_public_roots"])
                self.assertEqual(1, result["counts"]["diagnostic_only_decision_rows"])
                self.assertEqual(0, result["counts"]["diagnostic_only_public_roots"])
                self.assertEqual(1, result["counts"]["duplicate_public_roots"])
                self.assertEqual(16, result["worlds"]["completed_worlds"])
                self.assertEqual(2, result["source_identities"]["valid_records"]["battles"])

    def test_diagnostic_only_and_zero_weight_rows_have_no_effective_distribution(self):
        for row in (diagnostic_record(0, self.config), record(0, self.config)):
            with self.subTest(quality=row["targets"]["actions"][0]["quality"]):
                for action in row["targets"]["actions"]:
                    action["sample_weight"] = 0
                self.install([row])
                result = self.report()
                self.assertTrue(result["integrity"]["passed"])
                self.assertEqual(1, result["counts"]["diagnostic_only_public_roots"])
                self.assertEqual(0, result["counts"]["unique_effective_public_roots"])
                self.assertEqual(0, result["worlds"]["allocated_worlds"])
                self.assertEqual(0, result["root_state_distribution"]["roots"])
                self.assertEqual(0, result["observed_public_content"]["cards"]["distinct_ids"])
                self.assertEqual({"undeclared": 1}, result["source_distributions"]["unique_valid_roots"]["posterior_profile"])

    def test_failure_and_timeout_costs_are_not_losses_or_worlds(self):
        self.install([record(0, self.config)], [attempt(0), attempt(1, "failed_attempt", 12., reason="worker_response_deadline",
                     uncommitted_requested_action_worlds=50), attempt(2, "failed_attempt", 5., reason="reset:unsupported")])
        result = self.report()
        self.assertEqual(1, result["counts"]["failed_timeout"])
        self.assertEqual(1, result["counts"]["failed_reset_unclassified"])
        self.assertEqual(17., result["timing"]["journal_failed_attempt_seconds"])
        self.assertEqual(20., result["timing"]["journal_total_service_seconds"])
        self.assertEqual(50, result["worlds"]["failed_uncommitted_requested_action_worlds"])
        self.assertEqual(16, result["worlds"]["completed_worlds"])
        self.assertEqual(0, result["worlds"]["error_worlds"])
        self.assertEqual(1, result["resources"]["failed_attempts_missing_requested_world_counts"])

    def test_requested_public_phase_absence_and_budget_are_distinct(self):
        self.assertEqual("source_phase_absent", reporting.classify_failure(
            'source_snapshot_unavailable:{"reason":"terminal_before_requested_phase","status":"terminal_settled"}')[0])
        self.assertEqual("source_budget_exhausted", reporting.classify_failure(
            'source_snapshot_unavailable:{"reason":"source_decision_budget_exhausted"}')[0])
        self.assertEqual("source_snapshot_unclassified", reporting.classify_failure('source_snapshot_unavailable:bad')[0])

    def test_reset_terminal_boundary_is_not_engine_error_or_game_defeat(self):
        reason = 'reset:{"status":"terminal_settled","actions":[],"observation":null}'
        self.install([record(0, self.config)], [attempt(0), attempt(1, "failed_attempt", reason=reason)])
        result = self.report()
        self.assertEqual(1, result["counts"]["failed_reset_terminal_boundary"])
        self.assertEqual(0, result["counts"]["failed_reset_engine_error"])
        self.assertEqual(0, result["worlds"]["error_worlds"])
        self.assertEqual(16, result["worlds"]["completed_worlds"])

    def test_reset_explicit_unsupported_and_engine_error_are_separate(self):
        self.assertEqual("reset_unsupported", reporting.classify_failure('reset:{"status":"unsupported_capability"}')[0])
        self.assertEqual("reset_engine_error", reporting.classify_failure('reset:{"status":"engine_error"}')[0])

    def test_malformed_or_unknown_reset_is_unclassified(self):
        for reason in ('reset:broken', 'reset:{}', 'reset:{"status":"mystery"}'):
            self.assertEqual("reset_unclassified", reporting.classify_failure(reason)[0])

    def test_auxiliary_partial_and_full_utility_roots_are_distinct(self):
        full, partial, aux = [record(i, self.config) for i in range(3)]
        partial["targets"]["actions"][1].update(quality="objective_value_unresolved", value=None)
        partial["targets"]["actions"][1]["masks"]["value"] = False
        for action in aux["targets"]["actions"]:
            action.update(quality="objective_value_unresolved", value=None)
            action["masks"]["value"] = False
        self.install([full, partial, aux])
        result = self.report()
        self.assertEqual(3, result["counts"]["completed_whole_candidate_roots"])
        self.assertEqual(1, result["counts"]["full_utility_roots"])
        self.assertEqual(1, result["counts"]["partial_value_roots"])
        self.assertEqual(1, result["counts"]["auxiliary_only_no_value_roots"])
        self.assertEqual(2, result["counts"]["empirical_value_only_roots"])
        self.assertEqual(0, result["counts"]["strong_pair_labels"])

    def test_incomplete_whole_candidate_root_not_counted_complete(self):
        row = unresolved(record(0, self.config))
        row["audit_only"]["outcome_samples"] = outcomes(row)
        self.install([row])
        result = self.report(verify_outcomes=True)
        self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
        self.assertEqual(0, result["counts"]["completed_whole_candidate_roots"])
        self.assertEqual(1, result["worlds"]["truncated_worlds"])

    def test_gzip_members_validate_independently_by_offset(self):
        rows = [record(i, self.config) for i in range(2)]
        for row in rows:
            archive(self.path, row)
        self.install(rows)
        result = self.report(verify_outcomes=True)
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual(2, result["raw_outcomes"]["verified_records_by_storage"]["gzip_reference"])

    def test_bad_raw_sha_excludes_row_when_requested(self):
        row = record(0, self.config)
        archive(self.path, row)
        row["audit_only"]["outcome_samples_ref"]["sha256"] = "0" * 64
        self.install([row])
        unchecked = self.report()
        self.assertEqual("NOT_CHECKED", unchecked["raw_outcomes"]["status"])
        checked = self.report(verify_outcomes=True)
        self.assertEqual(0, checked["counts"]["unique_valid_public_roots"])
        self.assertFalse(checked["integrity"]["passed"])

    def test_missing_raw_is_explicit_and_not_verified(self):
        self.install([record(0, self.config)])
        result = self.report(verify_outcomes=True)
        self.assertEqual(1, result["counts"]["invalid_decision_rows"])
        self.assertEqual(0, result["counts"]["unique_valid_public_roots"])

    def test_raw_accounting_mismatch_rejected(self):
        row = record(0, self.config)
        row["audit_only"]["outcome_samples"] = outcomes(row)
        row["audit_only"]["outcome_samples"][0]["outcomes"].pop()
        self.install([row])
        result = self.report(verify_outcomes=True)
        self.assertEqual(0, result["counts"]["unique_valid_public_roots"])
        self.assertIn("raw_outcome_accounting_mismatch", str(result["integrity"]["error_counts"]))

    def test_gzip_path_escape_and_size_budget(self):
        row = record(0, self.config)
        archive(self.path, row)
        self.install([row])
        result = self.report(verify_outcomes=True, max_outcome_bytes=128)
        self.assertEqual(0, result["counts"]["unique_valid_public_roots"])
        row["audit_only"]["outcome_samples_ref"]["path"] = "../secret.gz"
        self.install([row])
        result = self.report(verify_outcomes=True)
        self.assertIn("outcome_path_outside_corpus", str(result["integrity"]["error_counts"]))

    def test_recipe_hash_and_generation_versions_checked(self):
        self.install([record(0, self.config)])
        (self.path / "generation_recipe.py").write_text("tampered")
        with self.assertRaisesRegex(reporting.ReportError, "recipe_checksum"):
            self.report()

    def test_row_version_drift_does_not_add_effective_root(self):
        a, b = record(0, self.config), record(1, self.config)
        b["audit_only"]["versions"]["teacher"] = "different"
        self.install([a, b])
        result = self.report()
        self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
        self.assertIn("record_versions_mismatch", str(result["integrity"]["error_counts"]))

    def test_missing_timers_remain_missing_not_zero_fraction(self):
        row = record(0, self.config)
        row["audit_only"]["costs"]["clone_seconds"] = None
        self.install([row])
        result = self.report()
        self.assertIsNone(result["timing"]["clone_fraction_of_teacher_elapsed"])
        self.assertEqual(1, result["resources"]["missing_valid_record_costs"]["clone_seconds"])

    def test_recovered_unknown_timing_is_explicit_missing_not_zero_total(self):
        self.install([record(0, self.config)], [attempt(0, elapsed=None, recovered=True, timing_status="recovered_unknown", elapsed_seconds_observed=None)])
        result = self.report()
        self.assertTrue(result["integrity"]["passed"], result["integrity"])
        self.assertEqual(1, result["counts"]["journal_missing_elapsed_rows"])
        self.assertEqual(1, result["counts"]["journal_recovered_unknown_rows"])
        self.assertFalse(result["timing"]["journal_timing_complete"])
        self.assertIsNone(result["timing"]["journal_accepted_seconds"])
        self.assertIsNone(result["timing"]["journal_total_service_seconds"])
        self.assertIsNone(result["timing"]["unique_effective_roots_per_service_second"])
        self.assertEqual(0, result["timing"]["journal_measured_service_seconds"])

    def test_interrupted_attempt_retains_partial_observed_cost_and_budget(self):
        self.install([record(0, self.config)], [attempt(0), attempt(1, "interrupted_attempt", elapsed=None,
            recovered=True, timing_status="recovered_partial", elapsed_seconds_observed=2.5,
            reason="process_interrupted_without_durable_decision", uncommitted_requested_action_worlds=16)])
        result = self.report()
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual(1, result["counts"]["journal_interrupted_attempt"])
        self.assertEqual(1, result["counts"]["failed_process_interruption"])
        self.assertIsNone(result["timing"]["journal_total_service_seconds"])
        self.assertEqual(5.5, result["timing"]["journal_total_service_lower_bound_seconds"])
        self.assertEqual(16, result["worlds"]["interrupted_uncommitted_requested_action_worlds"])
        self.assertEqual(16, result["worlds"]["completed_worlds"])
        self.assertEqual(0, result["worlds"]["error_worlds"])

    def test_missing_time_without_recovery_evidence_is_invalid(self):
        self.install([record(0, self.config)], [attempt(0, elapsed=None)])
        result = self.report()
        self.assertFalse(result["integrity"]["passed"])
        self.assertEqual(1, result["counts"]["invalid_attempt_rows"])
        self.assertIsNone(result["timing"]["journal_total_service_seconds"])

    def test_accepted_duplicate_journal_keeps_cost_and_provenance_but_not_effective_count(self):
        a, b = record(0, self.config), record(3, self.config)
        b["public_input"] = deepcopy(a["public_input"])
        b["audit_only"]["duplicate_public_input"] = True
        self.install([a, b], [attempt(0), attempt(3, duplicate_public_input=True)])
        result = self.report()
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual(2, result["counts"]["journal_accepted"])
        self.assertEqual(1, result["counts"]["accepted_duplicate_record_journals"])
        self.assertEqual(1, result["counts"]["unique_valid_public_roots"])
        self.assertEqual(2, result["source_identities"]["valid_records"]["battles"])
        self.assertEqual(6., result["timing"]["journal_accepted_seconds"])

    def test_repeated_journal_row_not_double_counted(self):
        self.install([record(0, self.config)], [attempt(0), attempt(0)])
        result = self.report()
        self.assertEqual(2, result["counts"]["attempt_journal_rows"])
        self.assertEqual(1, result["counts"]["journal_accepted"])
        self.assertEqual(3., result["timing"]["journal_accepted_seconds"])
        self.assertFalse(result["integrity"]["passed"])

    def test_phase_histograms_hp_changes_and_size_summaries(self):
        a, b = record(0, self.config), record(1, self.config)
        obs = a["public_input"]["observation"]
        obs.update(turn=3, hp=30)
        card = deepcopy(obs["hand"][0])
        obs["hand"].append(deepcopy(card))
        obs["discard"] = [deepcopy(card)]
        obs["exhaust"] = [deepcopy(card), deepcopy(card)]
        obs["unknownDraw"][0]["count"], obs["drawCount"] = 4, 4
        a["audit_only"].update(generation_source_step=2, generation_decision_index=7)
        b["audit_only"].update(generation_source_step=4, generation_decision_index=9)
        for action in a["public_input"]["candidate_actions"]:
            action["revision"] = 7
        for action in b["public_input"]["candidate_actions"]:
            action["revision"] = 9
        self.install([a, b])
        distribution = self.report()["root_state_distribution"]
        self.assertEqual({"1": 1, "3": 1}, distribution["turn_histogram"])
        self.assertEqual({"2": 1, "4": 1}, distribution["source_step_histogram"])
        self.assertEqual({"7": 1, "9": 1}, distribution["decision_index_histogram"])
        self.assertEqual({"7": 1, "9": 1}, distribution["action_revision_histogram"])
        self.assertEqual(1, distribution["hp_comparison_to_combat_start"]["after_hp_loss_roots"]["roots"])
        self.assertEqual(.5, distribution["adequacy_indicators"]["turn_one_fraction"])
        self.assertEqual(1, distribution["adequacy_indicators"]["later_turn_after_net_hp_loss_roots"])
        for pile, expected_mean in (("hand", 1.5), ("discard", .5), ("exhaust", 1.), ("draw", 2.5)):
            self.assertEqual(expected_mean, distribution["pile_sizes"][pile]["mean"])
        self.assertEqual(1, distribution["pile_sizes"]["draw"]["min"])
        self.assertEqual(4, distribution["pile_sizes"]["draw"]["max"])

    def test_low_starting_hp_is_not_misreported_as_combat_hp_loss(self):
        row = record(0, self.config)
        row["public_input"]["observation"].update(hp=5, startHp=5, maxHp=20)
        self.install([row])
        distribution = self.report()["root_state_distribution"]
        bucket = distribution["current_hp_over_current_max_hp_buckets"]["(0.10,0.25]"]
        self.assertEqual(1, bucket["roots"])
        self.assertEqual(0, bucket["after_net_hp_loss_roots"])
        self.assertEqual(0, distribution["hp_comparison_to_combat_start"]["after_hp_change_roots"]["roots"])
        self.assertIn("no_net_hp_loss_roots", distribution["adequacy_indicators"]["observed_absences"])
        self.assertFalse(distribution["adequacy_indicators"]["universal_pass_threshold_applied"])

    def test_hp_ratio_bucket_boundaries_and_gain_are_explicit(self):
        for hp, max_hp, bucket in ((1, 10, "[0,0.10]"), (1, 4, "(0.10,0.25]"),
                                   (1, 2, "(0.25,0.50]"), (3, 4, "(0.50,0.75]"), (1, 1, "(0.75,1.00]")):
            self.assertEqual(bucket, reporting.hp_fraction_bucket(hp, max_hp))
        row = record(0, self.config)
        row["public_input"]["observation"]["hp"] = 65
        self.install([row])
        comparison = self.report()["root_state_distribution"]["hp_comparison_to_combat_start"]
        self.assertEqual(1, comparison["after_hp_gain_roots"]["roots"])
        self.assertEqual(1, comparison["after_hp_change_roots"]["roots"])
        self.assertEqual(0, comparison["after_hp_loss_roots"]["roots"])

    def test_pending_choice_count_is_a_real_choice_boundary(self):
        row = record(0, self.config)
        public = row["public_input"]
        public["observation"]["choice"] = {"source": "Survivor", "min": 1, "max": 1, "cancelable": False,
                                             "candidates": [deepcopy(public["observation"]["hand"][0])]}
        public["candidate_actions"] = [{"revision": 0, "kind": "choose", "slot": -1, "target": -1, "selection": [0]}]
        public["legal_mask"] = [True]
        row["targets"]["actions"] = row["targets"]["actions"][:1]
        row["audit_only"]["costs"].update(root_candidates=1, worlds_allocated=8, worlds_completed=8)
        self.install([row])
        result = self.report()
        self.assertTrue(result["integrity"]["passed"], result["integrity"])
        distribution = result["root_state_distribution"]
        self.assertEqual({"roots": 1, "fraction": 1.}, distribution["pending_choice"])
        self.assertNotIn("no_pending_choice_roots", distribution["adequacy_indicators"]["observed_absences"])

    def test_distribution_counts_exclude_duplicate_and_invalid_roots(self):
        good, duplicate, invalid = record(0, self.config), record(1, self.config), record(2, self.config)
        good["audit_only"]["generation_source_step"] = 0
        duplicate["public_input"] = deepcopy(good["public_input"])
        duplicate["audit_only"]["generation_source_step"] = 2
        invalid["public_input"]["observation"].update(turn=3, hp=10)
        invalid["targets"]["actions"][0]["completed_worlds"] = 7
        self.install([good, duplicate, invalid])
        distribution = self.report()["root_state_distribution"]
        self.assertEqual(1, distribution["roots"])
        self.assertEqual({"1": 1}, distribution["turn_histogram"])
        self.assertEqual({"0": 1}, distribution["source_step_histogram"])
        self.assertEqual(0, distribution["hp_comparison_to_combat_start"]["after_hp_loss_roots"]["roots"])
        self.assertEqual(1, distribution["pile_sizes"]["hand"]["roots"])

    def test_missing_actual_decision_count_not_inferred_from_revision(self):
        self.install([record(0, self.config)])
        distribution = self.report()["root_state_distribution"]
        self.assertEqual({"missing": 1}, distribution["decision_index_histogram"])
        self.assertEqual({"0": 1}, distribution["action_revision_histogram"])
        self.assertEqual(1, distribution["adequacy_indicators"]["actual_decision_index_missing_roots"])
        self.assertEqual("1001_plus", reporting.index_bucket(1001))
        self.assertEqual("invalid", reporting.index_bucket("unknown"))

    def test_empty_distribution_does_not_claim_adequacy(self):
        distribution = self.report()["root_state_distribution"]
        self.assertIsNone(distribution["adequacy_indicators"]["turn_one_fraction"])
        self.assertIsNone(distribution["pile_sizes"]["draw"]["mean"])
        self.assertEqual(["no_usable_unique_roots"], distribution["adequacy_indicators"]["observed_absences"])
        self.assertEqual("TASK_SPECIFIC_DISTRIBUTION_REVIEW_REQUIRED", distribution["adequacy_indicators"]["verdict"])

    def test_public_content_does_not_count_requested_but_unobserved_recipe(self):
        row = record(0, self.config)
        row["audit_only"]["scenario_recipe"] = {"deck": ["NotActuallyObservedCard"]}
        self.install([row])
        result = self.report()
        self.assertNotIn("NotActuallyObservedCard", result["observed_public_content"]["cards"]["roots_by_id"])
        self.assertEqual(1, result["observed_public_content"]["cards"]["distinct_ids"])

    def test_trailing_partial_line_never_repaired(self):
        self.install([record(0, self.config)])
        path = self.path / "decisions.jsonl"
        original = path.read_bytes() + b'{"partial":'
        path.write_bytes(original)
        result = self.report()
        self.assertEqual(original, path.read_bytes())
        self.assertEqual(1, result["counts"]["invalid_decision_rows"])
        self.assertIn("unterminated_record_not_repaired", str(result["integrity"]["error_counts"]))

    def test_oversized_line_is_bounded_and_read_only(self):
        self.install([record(0, self.config)])
        result = self.report(max_line_bytes=128)
        self.assertEqual(0, result["counts"]["unique_valid_public_roots"])
        self.assertIn("line_size_budget_exceeded", str(result["integrity"]["error_counts"]))

    def test_report_does_not_mutate_any_corpus_file(self):
        row = record(0, self.config)
        archive(self.path, row)
        self.install([row])
        before = {str(path): path.read_bytes() for path in self.path.iterdir() if path.is_file()}
        self.report(verify_outcomes=True)
        self.assertEqual(before, {str(path): path.read_bytes() for path in self.path.iterdir() if path.is_file()})

    def test_change_after_own_read_is_caught_at_end_of_multishard_scan(self):
        path = self.root / "changing-after-read.jsonl"
        write_rows(path, [{"a": 1}])
        problems = reporting.Problems()
        reader = reporting.StreamingReader(problems, 1024)
        list(reader.rows(path))
        with path.open("ab") as stream:
            stream.write(b'{"a":2}\n')
        reader.finish()
        self.assertFalse(reader.files[0]["stable_during_read"])
        self.assertIn("source_changed_before_scan_completed", str(problems.counts))

    def test_observed_source_change_marks_unstable_snapshot(self):
        path = self.root / "changing.jsonl"
        write_rows(path, [{"a": 1}])
        problems = reporting.Problems()
        reader = reporting.StreamingReader(problems, 1024)
        iterator = reader.rows(path)
        next(iterator)
        with path.open("ab") as stream:
            stream.write(b'{"a":2}\n')
        list(iterator)
        self.assertFalse(reader.files[0]["stable_during_read"])
        self.assertTrue(problems.counts)


class AttemptedPlanCompletionTests(unittest.TestCase):
    """Immutable-plan diagnostics use saved synthetic journals, never a worker."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def balanced(self, name, *, start=5, count=1, shard_id=0, shard_count=1, indices=()):
        path = self.root / name
        config = shard(path, shard_id, shard_count, roots_per_battle=4, root_policy="public-phase-v1",
            recipe_version=reporting.BALANCED_RECIPE, attempted_battle_block={"start": start, "count": count})
        write_rows(path / "attempts.jsonl", [attempt(index, "failed_attempt", source_battle_index=index // 4,
            reason="worker_response_deadline", uncommitted_requested_action_worlds=8) for index in indices])
        return path, config

    def test_partial_valid_snapshot_is_incomplete_without_failing_integrity(self):
        path, _ = self.balanced("partial", indices=(20, 22))
        (path / "progress.json").write_text(json.dumps({"status": "attempted_block_complete", "attempts": 4}))
        before = {file.name: file.read_bytes() for file in path.iterdir()}
        result = reporting.report_stage([path])
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual(0, result["counts"]["invalid_attempt_rows"])
        self.assertEqual(0, result["counts"]["unique_effective_public_roots"])
        plan = result["attempted_plan_completion"]
        self.assertEqual("INCOMPLETE", plan["status"])
        block, = plan["blocks"]
        self.assertTrue(block["all_declared_partitions_supplied"])
        self.assertEqual((4, 2, 2), (block["expected_global_indices"], block["validated_unique_journal_indices"], block["missing_indices"]))
        part, = block["partitions"]
        self.assertEqual((4, 2, 2, "INCOMPLETE"), (part["expected_assigned_indices"], part["validated_unique_journal_indices"], part["missing_indices"], part["status"]))
        self.assertEqual(before, {file.name: file.read_bytes() for file in path.iterdir()})

    def test_failed_and_interrupted_journals_complete_requests_without_usable_roots(self):
        path, _ = self.balanced("complete", indices=(20, 21, 23))
        with (path / "attempts.jsonl").open("a") as stream:
            stream.write(json.dumps(attempt(22, "interrupted_attempt", elapsed=None, source_battle_index=5,
                recovered=True, timing_status="recovered_unknown", elapsed_seconds_observed=None,
                uncommitted_requested_action_worlds=8)) + "\n")
        result = reporting.report_stage([path])
        self.assertTrue(result["integrity"]["passed"])
        self.assertEqual((3, 1, 0), (result["counts"]["journal_failed_attempt"],
            result["counts"]["journal_interrupted_attempt"], result["counts"]["unique_effective_public_roots"]))
        plan = result["attempted_plan_completion"]
        self.assertEqual("COMPLETE", plan["status"])
        self.assertEqual((4, 0), (plan["blocks"][0]["validated_unique_journal_indices"], plan["blocks"][0]["missing_indices"]))

    def test_full_seven_way_block_includes_empty_partitions_and_detects_omissions(self):
        paths = [self.balanced(f"shard-{i}", start=0, shard_id=i, shard_count=7,
                              indices=[i] if i < 4 else [])[0] for i in range(7)]
        full = reporting.report_stage(paths)["attempted_plan_completion"]
        self.assertEqual("COMPLETE", full["status"])
        block, = full["blocks"]
        self.assertEqual([1, 1, 1, 1, 0, 0, 0], [part["expected_assigned_indices"] for part in block["partitions"]])
        self.assertTrue(all(part["status"] == "COMPLETE" for part in block["partitions"]))
        for omitted, missing in ((6, 0), (2, 1)):
            result = reporting.report_stage([path for i, path in enumerate(paths) if i != omitted])
            self.assertTrue(result["integrity"]["passed"])
            plan = result["attempted_plan_completion"]
            self.assertEqual("INCOMPLETE", plan["status"])
            block, = plan["blocks"]
            self.assertFalse(block["all_declared_partitions_supplied"])
            self.assertEqual((1, missing), (block["missing_partition_count"], block["missing_indices"]))

    def test_duplicate_and_outside_indices_cannot_fill_missing_plan_positions(self):
        path, _ = self.balanced("invalid", indices=(20, 21, 20, 24))
        result = reporting.report_stage([path])
        self.assertFalse(result["integrity"]["passed"])
        self.assertEqual(2, result["counts"]["invalid_attempt_rows"])
        plan = result["attempted_plan_completion"]
        self.assertEqual("INCOMPLETE", plan["status"])
        self.assertEqual((2, 2), (plan["blocks"][0]["validated_unique_journal_indices"], plan["blocks"][0]["missing_indices"]))

    def test_disjoint_blocks_with_different_partition_counts_complete_separately(self):
        first, _ = self.balanced("first", start=0, indices=range(4))
        second = [self.balanced(f"second-{i}", start=1, shard_id=i, shard_count=2,
                               indices=range(4 + i, 8, 2))[0] for i in range(2)]
        result = reporting.report_stage([first, *second])
        self.assertTrue(result["integrity"]["passed"])
        plan = result["attempted_plan_completion"]
        self.assertEqual("COMPLETE", plan["status"])
        self.assertEqual([4, 4], [block["validated_unique_journal_indices"] for block in plan["blocks"]])
        self.assertEqual([1, 2], [block["shard_count"] for block in plan["blocks"]])

    def test_large_empty_plan_uses_arithmetic_counts(self):
        start, count, shards = 10**9, 10**12, 7
        path, _ = self.balanced("huge", start=start, count=count, shard_id=3, shard_count=shards)
        result = reporting.report_stage([path])
        self.assertTrue(result["integrity"]["passed"])
        block, = result["attempted_plan_completion"]["blocks"]
        first_index = start * 4 + (3 - start * 4) % shards
        expected = ((start + count) * 4 - first_index + shards - 1) // shards
        self.assertEqual(expected, block["partitions"][0]["expected_assigned_indices"])
        self.assertEqual(count * 4, block["missing_indices"])
        self.assertEqual(6, block["missing_partition_count"])

    def test_legacy_success_quota_has_no_attempted_block_completion_claim(self):
        path = self.root / "legacy"
        shard(path)
        result = reporting.report_stage([path])
        self.assertTrue(result["integrity"]["passed"])
        plan = result["attempted_plan_completion"]
        self.assertFalse(plan["applicable"])
        self.assertEqual(("NOT_APPLICABLE", []), (plan["status"], plan["blocks"]))


if __name__ == "__main__":
    unittest.main()
