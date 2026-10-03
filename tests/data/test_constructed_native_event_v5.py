"""Strict event-owned constructed reports, with no native generation or fitting."""
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/data")]
from nosl import constructed_native_event_v5 as event_source
from nosl.constructed_native_policy_v5 import (EXECUTION_FIELDS, SAMPLER, adapt_report, validate_record,
    _profile, _source_aliases)
from nosl.data_policy_v5 import PolicyDatasetV5, supervision_coverage
from nosl.data_v5 import validate_production_record
from nosl.model_v5 import StudentV5
from nosl.native_v5 import validate_candidate
from nosl.policy_v5 import digest, state_digest
from nosl.schema_v5 import load_config, loads
from nosl.train_policy_v5 import batch_loss, execution_gate
from test_constructed_native_policy_v5 import encode, reencode_report

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURE = Path(os.environ.get("NOSL_CONSTRUCTED_EVENT_TAPE_FIXTURE",
    ROOT / "artifacts/reports/constructed-owner-ruby-v1/lantern-boundary/response.jsonl"))


class EventPriorContractTests(unittest.TestCase):
    def prior(self):
        return {"schemaVersion": event_source.PRIOR_SCHEMA,
            "setup": {"encounter": "MysteriousKnightEventEncounter"},
            "eventOwner": {"event": "TheLanternKey", "act": "Hive", "fixtureFloor": 1,
                           "choiceRule": "keep-the-key-then-fight-v1"},
            "rootLaw": event_source.ROOT_LAW, "setupLaw": event_source.SETUP_LAW}

    def test_exact_typed_owner_and_floor_bounds(self):
        event_source.validate_owner_prior(self.prior())
        mutations = [lambda p: p["eventOwner"].update(event="PunchOff"),
            lambda p: p["eventOwner"].update(act="Underdocks"),
            lambda p: p["eventOwner"].update(fixtureFloor=True),
            lambda p: p["eventOwner"].update(fixtureFloor=0),
            lambda p: p["eventOwner"].update(fixtureFloor=15),
            lambda p: p["eventOwner"].update(choiceRule="return-the-key"),
            lambda p: p["eventOwner"].update(actual_seed="private"),
            lambda p: p["setup"].update(encounter="SludgeSpinnerWeak")]
        for mutate in mutations:
            prior = self.prior(); mutate(prior)
            with self.subTest(mutate=mutate), self.assertRaises(ValueError): event_source.validate_owner_prior(prior)

    def test_event_sampler_cannot_be_relabelled_as_ordinary(self):
        report = {"options": {"prior": self.prior()}, "collection_contract_json": json.dumps({"sampler_version": event_source.SAMPLER})}
        self.assertEqual((event_source.SAMPLER, event_source.POSTERIOR, event_source.SOURCE_KIND), _profile(report))
        report["collection_contract_json"] = json.dumps({"sampler_version": SAMPLER})
        with self.assertRaisesRegex(ValueError, "event_sampler_profile"): _profile(report)


class ConstructedNativeEventTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not FIXTURE.is_file(): raise unittest.SkipTest("New retained event-owner report missing; no native run started")
        cls.payload = FIXTURE.read_bytes(); cls.raw = loads(cls.payload.decode("utf-8"))
        if cls.raw.get("options", {}).get("prior", {}).get("schemaVersion") != event_source.PRIOR_SCHEMA:
            raise AssertionError("Explicit new event-owner report required")
        torch.set_num_threads(1)

    def setUp(self):
        self.calls = []
        def forbidden(*unused, **also_unused):
            self.calls.append(True); raise AssertionError("Event-owner check attempted learning")
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
            "torch.optim.Optimizer.__init__", "torch.nn.utils.clip_grad_norm_"):
            guard = patch(target, side_effect=forbidden); guard.start(); self.addCleanup(guard.stop)
        context = torch.no_grad(); context.__enter__(); self.addCleanup(lambda: context.__exit__(None, None, None))
        self.addCleanup(lambda: self.assertEqual([], self.calls))

    def rows(self):
        return adapt_report(self.payload, CONFIG)

    def test_real_owner_evidence_and_raw_values_are_preserved(self):
        rows = self.rows(); self.assertEqual(len(self.raw["records"]), len(rows)); self.assertTrue(rows)
        for raw, row in zip(self.raw["records"], rows):
            expected = deepcopy(raw["targets"])
            for action in expected["actions"]:
                for field in EXECUTION_FIELDS: action.pop(field)
            self.assertEqual(expected, row["targets"]); self.assertEqual(raw["public_input"], row["public_input"])
            audit = row["audit_only"]
            self.assertEqual(event_source.SOURCE_KIND, audit["source_kind"])
            self.assertEqual(self.payload, audit["constructed_tape_evidence"]["report_utf8"].encode())
            self.assertIs(audit["native_run"], False); self.assertIs(audit["trainable"], False)
            self.assertIsNone(audit["producer_receipt_sha256"])
            public = row["public_input"]; evidence = public["public_evidence"]
            self.assertFalse(evidence["completeFromRunStart"])
            self.assertIsNone(public["observation"]["runContext"]["combatEntryIndex"])
            combat = next(e for e in evidence["events"] if e["payload"].get("ownerKind") == "combat")
            event_source.validate_public_owner(public, combat, self.raw["options"]["prior"])
            self.assertFalse(raw["audit_only"]["conditioning_eligible"])
            self.assertEqual(event_source.SAMPLER, raw["audit_only"]["sampler_version"])
            self.assertEqual(event_source.POSTERIOR, raw["audit_only"]["posterior_profile"])
            validate_record(row, CONFIG)
        self.assertEqual(self.raw, loads(self.payload.decode()))

    def test_bound_public_owner_parent_offers_and_choice_tampering_rejected(self):
        self.assertTrue(adapt_report(reencode_report(self.raw), CONFIG))
        def change_public(report, mutate):
            for raw in report["records"]: mutate(raw["public_input"])
            for attempt in report["attempts"]:
                if "public_input" in attempt["provenance"]: mutate(attempt["provenance"]["public_input"])
        def choice(public):
            next(e["payload"] for e in public["public_evidence"]["events"]
                if e["payload"].get("key") == "KEEP_THE_KEY")["key"] = "RETURN_THE_KEY"
        def parent(public):
            next(e["payload"] for e in public["public_evidence"]["events"]
                if e["payload"].get("ownerKind") == "combat")["parentOwnerOrdinal"] = None
        for mutate, reason in ((choice, "keep_then_fight_public_choices_required"), (parent, "exact_event_to_combat_owner_required")):
            report = deepcopy(self.raw); change_public(report, mutate)
            with self.subTest(reason=reason), self.assertRaisesRegex(ValueError, reason): adapt_report(reencode_report(report), CONFIG)
        public = self.raw["records"][0]["public_input"]
        combat = next(e for e in public["public_evidence"]["events"] if e["payload"].get("ownerKind") == "combat")
        prior = deepcopy(self.raw["options"]["prior"]); prior["eventOwner"]["fixtureFloor"] = 2
        with self.assertRaisesRegex(ValueError, "exact_event_to_combat_owner_required"):
            event_source.validate_public_owner(public, combat, prior)

    def test_event_conditioning_claims_and_profile_swaps_are_rejected(self):
        mutations = [
            (lambda r: r["records"][0]["audit_only"].update(conditioning_eligible=True), "event_ordinary_rejection_only"),
            (lambda r: r["records"][0]["audit_only"].update(sampler_version=SAMPLER), "raw_identity:sampler_version"),
            (lambda r: r["options"]["prior"].pop("eventOwner"), "constructed prior")]
        for mutate, reason in mutations:
            report = deepcopy(self.raw); mutate(report)
            with self.subTest(reason=reason), self.assertRaisesRegex(ValueError, reason): adapt_report(reencode_report(report), CONFIG)
        row = self.rows()[0]; row["audit_only"]["source_kind"] = "constructed_native_tape_action_fixture_v1"
        with self.assertRaisesRegex(ValueError, "record_regeneration_mismatch"): validate_record(row, CONFIG)

    def test_unknown_event_reward_value_is_not_zero_or_invented_plan_supervision(self):
        for raw, row in zip(self.raw["records"], self.rows()):
            self.assertNotIn("plan", row["targets"])
            for sample, target, computed in zip(raw["audit_only"]["outcome_samples"], row["targets"]["actions"], row["audit_only"]["objective_evaluations"]):
                for outcome, evaluation in zip(sample["outcomes"], computed["evaluations"]):
                    if outcome["terminalKind"] == "Win":
                        self.assertTrue(any(c["kind"] == "earned_extra_reward_opportunity:SpecialCardReward" and c["amount"] == 1 for c in outcome["permanentChanges"]))
                        self.assertEqual("ObjectiveValueUnresolved", evaluation["status"])
                        self.assertIn("permanent_future_value_unresolved:earned_extra_reward_opportunity:SpecialCardReward", evaluation["reasons"])
                        self.assertFalse(target["masks"]["value"]); self.assertIsNone(target["value"])
                    if outcome["terminalKind"] not in ("Win", "Loss"):
                        self.assertFalse(any(target["masks"].values()))

    def test_event_rows_load_and_actual_forward_keeps_gates_closed(self):
        rows = self.rows(); dataset = PolicyDatasetV5(rows, CONFIG, split="train", purpose="engineering-fixture")
        with torch.random.fork_rng(): torch.manual_seed(1729); model = StudentV5(CONFIG)
        model.eval(); before = state_digest(model.state_dict()); seen = []
        hook = model.register_forward_pre_hook(lambda unused, args: seen.append(deepcopy(args[0])))
        try: loss, terms = batch_loss(model, dataset.records, CONFIG)
        finally: hook.remove()
        self.assertEqual([r["public_input"] for r in rows], seen)
        self.assertTrue(torch.isfinite(loss)); self.assertFalse(loss.requires_grad); self.assertIsNone(loss.grad_fn)
        self.assertEqual(before, state_digest(model.state_dict())); self.assertTrue(all(p.grad is None for p in model.parameters()))
        for row in rows:
            with self.assertRaises(ValueError): validate_candidate(row, CONFIG)
            with self.assertRaises(ValueError): validate_production_record(row, CONFIG)
            gate = execution_gate({"purpose": "engineering-fixture", "training_supervision": supervision_coverage(row, CONFIG)})
            self.assertFalse(gate["accepted"])
            self.assertIn("unadmitted_or_engineering_records_cannot_fit", gate["reasons"])
        self.assertEqual(len(self.raw["attempts"]), len(dataset.engineering_source_metadata()))


if __name__ == "__main__": unittest.main()
