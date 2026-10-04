"""Declared Lantern initial-shuffle version and exact retained report checks."""
from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/data")]
from nosl import constructed_native_event_v5 as event_source
from nosl.constructed_native_policy_v5 import (EVALUATOR_SOURCE_FILES, EXECUTION_FIELDS,
    PRIOR_SCHEMA, RUBY_DRAW_SAMPLER, _profile, adapt_report, validate_record)
from nosl.schema_v5 import load_config, loads
from test_constructed_native_policy_v5 import reencode_report

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURE = os.environ.get("NOSL_CONSTRUCTED_EVENT_SHUFFLE_FIXTURE")


class EventShuffleContractTests(unittest.TestCase):
    def report(self, sampler, enabled, schema=event_source.PRIOR_SCHEMA):
        return {"options": {"enableConditioning": enabled,
            "prior": {"schemaVersion": schema, "setup": {"encounter": "MysteriousKnightEventEncounter"}}},
            "collection_contract_json": json.dumps({"sampler_version": sampler})}

    def test_enabled_v2_and_historical_rejection_ids_remain_explicit(self):
        self.assertEqual((event_source.SHUFFLE_SAMPLER, event_source.SHUFFLE_POSTERIOR, event_source.SOURCE_KIND),
            _profile(self.report(event_source.SHUFFLE_SAMPLER, True)))
        for enabled in (False, True):
            self.assertEqual((event_source.SAMPLER, event_source.POSTERIOR, event_source.SOURCE_KIND),
                _profile(self.report(event_source.SAMPLER, enabled)))
        for report, reason in ((self.report(event_source.SHUFFLE_SAMPLER, False), "shuffle_version_requires_enabled_declaration"),
                (self.report(event_source.SHUFFLE_SAMPLER, True, PRIOR_SCHEMA), "ordinary_sampler_profile"),
                (self.report(RUBY_DRAW_SAMPLER, True), "event_sampler_profile")):
            with self.subTest(reason=reason), self.assertRaisesRegex(ValueError, reason): _profile(report)

    def test_initial_shuffle_word_accounting_and_error_prefixes(self):
        entry = {"deck": [None] * 16}
        complete = {"conditionedHpCount": 0, "conditionedShuffles": 1, "conditionedTapeCells": 15,
            "distinctTapeCells": 20, "status": "accepted"}
        check = lambda p: event_source.validate_proposal_conditioning(p, event_source.SHUFFLE_SAMPLER, entry)
        check(complete)
        for status in ("proposal_engine_error", "proposal_cleanup_error", "computation_cancelled", "public_constraint_mismatch"):
            partial = dict(complete, status=status, conditionedTapeCells=3); check(partial)
        for changed, reason in (({"conditionedHpCount": 1}, "hp_conditioning_forbidden"),
                ({"conditionedShuffles": 2}, "only_one_initial_shuffle"),
                ({"conditionedTapeCells": 16}, "initial_shuffle_word_bound"),
                ({"conditionedTapeCells": 14}, "complete_initial_shuffle_words"),
                ({"conditionedShuffles": 0}, "conditioned_words_without_initial_shuffle")):
            with self.subTest(changed=changed), self.assertRaisesRegex(ValueError, reason): check(dict(complete, **changed))
        event_source.validate_proposal_conditioning(dict(complete, conditionedTapeCells=0), event_source.SHUFFLE_SAMPLER, {"deck": [None]})

    def test_uncertified_fallback_has_no_conditioned_words_or_hp(self):
        audit = {"conditioning_eligible": False, "conditioning_reason": "uncertified_entry_hook",
            "posterior_proposals": [{"conditionedHpCount": 0, "conditionedShuffles": 0, "conditionedTapeCells": 0}]}
        event_source.validate_conditioning_audit({}, audit, event_source.SHUFFLE_SAMPLER)
        for mutate in (lambda a: a.update(conditioning_reason=event_source.SHUFFLE_REASON),
                lambda a: a["posterior_proposals"][0].update(conditionedShuffles=1),
                lambda a: a["posterior_proposals"][0].update(conditionedTapeCells=1)):
            changed = deepcopy(audit); mutate(changed)
            with self.assertRaisesRegex(ValueError, "uncertified_startup_requires_ordinary_rejection"):
                event_source.validate_conditioning_audit({}, changed, event_source.SHUFFLE_SAMPLER)

    def test_native_density_and_word_consumption_dependencies_are_bound(self):
        self.assertTrue({"src/Nosl.Worker/ConditionalShuffleProposal.cs", "src/Nosl.Worker/NativeLabelTape.cs",
            "src/Nosl.Worker/NativeConstructedEventOwnerSetup.cs", "src/Nosl.Worker/NativePublicCombatPrefixCondition.cs",
            "src/Nosl.Worker/NativeInitialShuffleCondition.cs"}.issubset(EVALUATOR_SOURCE_FILES))


class RetainedEventShuffleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not FIXTURE or not Path(FIXTURE).is_file():
            raise unittest.SkipTest("Retained Lantern shuffle-v2 report missing; no native run started")
        cls.payload = Path(FIXTURE).read_bytes(); cls.raw = loads(cls.payload.decode())

    def test_exact_new_raw_report_retains_every_action_and_bound_attempt(self):
        rows = adapt_report(self.payload, CONFIG)
        self.assertEqual(event_source.SHUFFLE_SAMPLER, loads(self.raw["collection_contract_json"])["sampler_version"])
        self.assertEqual(len(self.raw["records"]), len(rows)); self.assertTrue(rows)
        for raw, row in zip(self.raw["records"], rows):
            validate_record(row, CONFIG)
            expected = deepcopy(raw["targets"])
            for action in expected["actions"]:
                for field in EXECUTION_FIELDS: action.pop(field)
            self.assertEqual(expected, row["targets"])
            self.assertEqual(raw["public_input"], row["public_input"])
            self.assertEqual(self.payload, row["audit_only"]["constructed_tape_evidence"]["report_utf8"].encode())
            self.assertFalse(row["audit_only"]["native_run"]); self.assertFalse(row["audit_only"]["trainable"])

    def test_hp_extra_shuffle_and_incomplete_word_claims_fail_specifically(self):
        self.assertTrue(adapt_report(reencode_report(self.raw), CONFIG))
        for changes, reason in (({"conditionedHpCount": 1}, "hp_conditioning_forbidden"),
                ({"conditionedShuffles": 2}, "completed_certified_startup_requires_shuffle"),
                ({"conditionedTapeCells": 1}, "complete_initial_shuffle_words")):
            report = loads(self.payload.decode())
            report["attempts"][0]["posterior_proposals"][0].update(changes)
            report["records"][0]["audit_only"]["posterior_proposals"][0].update(changes)
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, reason): adapt_report(reencode_report(report), CONFIG)

    def test_profile_reason_and_public_startup_substitutions_fail(self):
        public = self.raw["records"][0]["public_input"]
        audit = self.raw["records"][0]["audit_only"]
        event_source.validate_conditioning_audit(public, audit, event_source.SHUFFLE_SAMPLER)
        changed = deepcopy(audit); changed["conditioning_reason"] = event_source.CONDITIONING_REASON
        with self.assertRaisesRegex(ValueError, "shuffle_conditioning_reason"):
            event_source.validate_conditioning_audit(public, changed, event_source.SHUFFLE_SAMPLER)
        for field, value in (("hp", 109), ("block", 0)):
            changed = deepcopy(public)
            first = next(e["payload"] for e in changed["public_evidence"]["events"] if e["payload"]["kind"] == "combat_decision")
            first["observation"]["enemies"][0][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "exact_lantern_startup_snapshot"):
                event_source.validate_conditioning_audit(changed, audit, event_source.SHUFFLE_SAMPLER)
        changed = deepcopy(public)
        fact = next(e["payload"] for e in changed["public_evidence"]["events"] if e["payload"].get("factKind") == "power_changed")
        fact["model"] = "PlatingPower"
        with self.assertRaisesRegex(ValueError, "exact_lantern_startup_power_facts"):
            event_source.validate_conditioning_audit(changed, audit, event_source.SHUFFLE_SAMPLER)
        report = loads(self.payload.decode()); report["records"][0]["audit_only"]["posterior_profile"] = event_source.POSTERIOR
        with self.assertRaisesRegex(ValueError, "raw_identity:posterior_profile"): adapt_report(reencode_report(report), CONFIG)

    def test_certified_startup_rejects_extra_global_gap_and_missing_complete_shuffle(self):
        public = self.raw["records"][0]["public_input"]
        audit = self.raw["records"][0]["audit_only"]
        changed = deepcopy(public)
        changed["public_evidence"]["events"].insert(1, {"eventOrdinal": 1, "ownerOrdinal": None,
            "payload": {"kind": "gap", "reason": "observation_missing"}})
        with self.assertRaisesRegex(ValueError, "only_initial_global_gap"):
            event_source.validate_conditioning_audit(changed, audit, event_source.SHUFFLE_SAMPLER)
        for status in ("accepted", "public_packet_mismatch", "exact_proposal_density_correction_rejected"):
            changed = deepcopy(audit)
            changed["posterior_proposals"][0].update(status=status, conditionedShuffles=0, conditionedTapeCells=0)
            with self.subTest(status=status), self.assertRaisesRegex(ValueError, "completed_certified_startup_requires_shuffle"):
                event_source.validate_conditioning_audit(public, changed, event_source.SHUFFLE_SAMPLER)


if __name__ == "__main__": unittest.main()
