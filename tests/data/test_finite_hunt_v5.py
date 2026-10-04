"""Mutations of newly executed C# full-v5 evidence; no fabricated label fixture.

Generate the two bounded source artifacts through FiniteHuntV5Tests with
NOSL_FRESH_HUNT_V5_EXPORT set to a directory. The same variable (or the optional
NOSL_FRESH_HUNT_V5_FIXTURES override) selects the artifacts here. The default is
artifacts/fresh-finite-v5. Missing artifacts explicitly skip this integration
suite; it never reads old finite-v2 labels or silently substitutes a fixture.
"""
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data_v5 import RECORD_KIND, RECORD_SCHEMA, validate_production_record
from nosl.finite_hunt_v5 import (CARD_REWARD, RAW_SCHEMA, adapt_jsonl, adapt_record,
                               validate_adapted_record, validate_raw_record)
from nosl.public_identity_v5 import public_input_digest
from nosl.schema import HEADS
from nosl.schema_v5 import load_config

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURES = Path(os.environ.get("NOSL_FRESH_HUNT_V5_FIXTURES",
                              os.environ.get("NOSL_FRESH_HUNT_V5_EXPORT",
                                             ROOT / "artifacts/fresh-finite-v5")))


def encode(value):
    return (json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


class FreshFiniteHuntV5Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = [FIXTURES / f"fresh-finite-hunt-v5-{kind}.raw.json" for kind in ("success", "truncated")]
        if not all(path.is_file() for path in paths):
            raise unittest.SkipTest("Fresh C# Hunt-v5 artifacts missing. From repository root run: "
                "NOSL_FRESH_HUNT_V5_EXPORT=artifacts/fresh-finite-v5 dotnet test tests/Nosl.Tests/Nosl.Tests.csproj "
                "--filter FullyQualifiedName~FiniteHuntV5Tests")
        cls.payloads = [path.read_bytes() for path in paths]
        cls.sources = [json.loads(payload) for payload in cls.payloads]
        for raw in cls.sources:
            if raw.get("schema_version") != RAW_SCHEMA:
                raise AssertionError("Only newly generated full-v5 raw artifacts are accepted")
            validate_raw_record(raw, CONFIG)

    def raw(self, *, truncated=False):
        return deepcopy(self.sources[int(truncated)])

    def assertRejected(self, mutate, *, truncated=False):
        raw = self.raw(truncated=truncated)
        mutate(raw)
        with self.assertRaises(ValueError): validate_raw_record(raw, CONFIG)

    def test_fresh_success_is_lossless_and_utility_is_still_unknown(self):
        raw = self.raw(); original = deepcopy(raw)
        row = adapt_record(raw, CONFIG, source_raw_bytes=self.payloads[0])
        self.assertEqual(raw, original)
        self.assertEqual(RECORD_SCHEMA, row["schema_version"])
        self.assertEqual(RECORD_KIND, row["record_kind"])
        self.assertEqual(raw["public_input"], row["public_input"])
        self.assertEqual(raw["targets"], row["targets"])
        self.assertEqual(1, row["targets"]["plan"]["specified_success_probability"])
        self.assertEqual(0, row["targets"]["plan"]["extra_net_hp_loss"])
        audit = row["audit_only"]
        self.assertFalse(audit["trainable"]); self.assertFalse(audit["formal_labels"])
        self.assertFalse(audit["native_run"]); self.assertFalse(audit["legacy_labels_read"])
        self.assertEqual(self.payloads[0], audit["source_raw_utf8"].encode("utf-8"))
        self.assertEqual(hashlib.sha256(self.payloads[0]).hexdigest(), audit["source_raw_sha256"])
        self.assertEqual(public_input_digest(raw["public_input"]), audit["conditioned_public_input_digest"])
        self.assertEqual(raw, audit["source_raw_record"])
        self.assertFalse(audit["paired_evidence"]["utilityComparisonMask"])
        for world in audit["paired_evidence"]["worlds"]:
            for role in ("baseline", "plan"):
                trajectory = world[role]
                self.assertEqual(1, trajectory["actualExtraCardRewardsOffered"])
                self.assertIsNone(trajectory["objective"]["cost"])
                self.assertIn("permanent_future_value_unresolved:" + CARD_REWARD, trajectory["objective"]["reasons"])
        for action in row["targets"]["actions"]:
            self.assertTrue(all(action["masks"][head] is False and action[head] is None for head in HEADS))
            self.assertEqual(0, action["allocated_worlds"])
        validate_adapted_record(row, CONFIG)
        with self.assertRaises(ValueError): validate_production_record(row, CONFIG)

    def test_real_truncation_retains_full_denominator_and_null_heads(self):
        raw = self.raw(truncated=True)
        validate_raw_record(raw, CONFIG)
        plan = raw["targets"]["plan"]
        self.assertEqual(len(raw["audit_only"]["evaluation_options"]["evaluationSeeds"]), plan["allocated_worlds"])
        self.assertEqual(0, plan["success_completed_worlds"]); self.assertEqual(0, plan["paired_completed_worlds"])
        self.assertTrue(all(value is False for value in plan["masks"].values()))
        self.assertIsNone(plan["specified_success_probability"]); self.assertIsNone(plan["extra_net_hp_loss"])
        for world in raw["audit_only"]["paired_evidence"]["worlds"]:
            for role in ("baseline", "plan"):
                self.assertEqual("ComputeTruncated", world[role]["outcome"]["terminalKind"])

    def test_error_and_nontermination_mutations_preserve_unknown_mass(self):
        for kind, field in (("EngineError", "engineErrors"), ("PolicyNonterminating", "policyNonterminating")):
            raw = self.raw(truncated=True); evaluation = raw["audit_only"]["paired_evidence"]
            n = len(evaluation["worlds"])
            for world in evaluation["worlds"]:
                for role in ("baseline", "plan"):
                    world[role]["outcome"]["terminalKind"] = kind
                    world[role]["objective"]["reasons"] = [kind]
            for role in ("baseline", "plan"):
                summary = evaluation[role + "Summary"]
                summary["computeTruncated"] = 0; summary[field] = n
                summary["reasons"] = sorted([kind, "incomplete_probability_mass_preserved", "objective_profile_not_calibrated_for_formal_labels"])
            with self.subTest(kind=kind):
                validate_raw_record(raw, CONFIG)
                row = adapt_record(raw, CONFIG, source_raw_bytes=encode(raw))
                self.assertEqual(n, row["targets"]["plan"]["allocated_worlds"])
                self.assertFalse(any(row["targets"]["plan"]["masks"].values()))

    def test_terminal_events_cannot_rescue_cleanup_error_outcomes(self):
        raw = self.raw(); unresolved = self.raw(truncated=True)
        evaluation = raw["audit_only"]["paired_evidence"]
        incomplete = unresolved["audit_only"]["paired_evidence"]
        for pair, source in zip(evaluation["worlds"], incomplete["worlds"]):
            for role in ("baseline", "plan"):
                trajectory = pair[role]
                trajectory["outcome"] = deepcopy(source[role]["outcome"])
                trajectory["outcome"]["terminalKind"] = "EngineError"
                trajectory["objective"] = deepcopy(source[role]["objective"])
                trajectory["objective"]["reasons"] = ["EngineError"]
                trajectory["fatalPlayerTurn"] = None; trajectory["actualExtraCardRewardsOffered"] = 0
                if role == "plan": trajectory["controller"].update(status="unresolved", exitReason="evaluation_incomplete")
        for key in ("meanExtraNetHpLoss", "extraExpectedLossInterval", "unconditionalSuccessInterval",
                    "excessDeathProbabilityInterval", "safetyAcceptable", "eligibility",
                    "utilityComparisonMask", "meanSpecifiedSuccess", "masks", "baselineSummary", "planSummary"):
            evaluation[key] = deepcopy(incomplete[key])
        for role in ("baseline", "plan"):
            summary = evaluation[role + "Summary"]
            summary.update(computeTruncated=0, engineErrors=len(evaluation["worlds"]))
            summary["reasons"] = sorted(["EngineError", "incomplete_probability_mass_preserved", "objective_profile_not_calibrated_for_formal_labels"])
        raw["targets"]["plan"] = deepcopy(unresolved["targets"]["plan"])
        self.assertTrue(raw["audit_only"]["terminal_public_evidence"])
        validate_raw_record(raw, CONFIG)
        evaluation["worlds"][0]["plan"]["controller"].update(status="finished", exitReason=None)
        with self.assertRaisesRegex(ValueError, "finished_requires_actual_success"): validate_raw_record(raw, CONFIG)

    def test_declared_setup_binds_first_combat_context_and_all_public_cards(self):
        for mutate in (
            lambda r: r["audit_only"]["scenario"].update(deck=["TheHunt", "DefendSilent"]),
            lambda r: r["audit_only"]["scenario"].update(deck=["TheHunt+"]),
            lambda r: r["audit_only"]["scenario"].update(deck=["TheHunt++"]),
            lambda r: r["audit_only"]["scenario"].update(deck=["TheHunt", "Slimed+"]),
            lambda r: r["audit_only"]["scenario"].update(enemy="Nibbit"),
            lambda r: r["audit_only"]["scenario"].update(hp=1),
            lambda r: r["audit_only"]["scenario"].update(maxHp=1),
            lambda r: r["audit_only"]["scenario"].update(enemyHp=1),
            lambda r: r["audit_only"]["scenario"].update(gold=1),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)

    def test_false_claims_legacy_envelopes_and_unknown_fields_are_rejected(self):
        for mutate in (
            lambda r: r.update(schema_version="nosl.dataset.finite-hunt.v2"),
            lambda r: r["audit_only"].update(trainable=True),
            lambda r: r["audit_only"].update(formal_labels=True),
            lambda r: r["audit_only"].update(native_run=True),
            lambda r: r["audit_only"].update(legacy_labels_read=True),
            lambda r: r["audit_only"].update(source_kind="natural"),
            lambda r: r["audit_only"].update(extra_claim="trusted"),
            lambda r: r["public_input"].update(private_seed="forbidden"),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)

    def test_target_arithmetic_masks_and_utility_are_recomputed(self):
        for mutate in (
            lambda r: r["targets"]["plan"].update(specified_success_probability=.5),
            lambda r: r["targets"]["plan"].update(extra_net_hp_loss=1),
            lambda r: r["targets"]["plan"].update(success_completed_worlds=1),
            lambda r: r["targets"]["actions"][0].update(allocated_worlds=1, other_worlds=1),
            lambda r: r["audit_only"]["paired_evidence"].update(meanSpecifiedSuccess=.5),
            lambda r: r["audit_only"]["paired_evidence"].update(utilityComparisonMask=True),
            lambda r: r["audit_only"]["paired_evidence"]["masks"].update(meanSpecifiedSuccess=1),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["objective"].update(cost=0),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(atomicActionsExecuted=2),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)
        self.assertRejected(lambda r: r["targets"]["plan"].update(specified_success_probability=0), truncated=True)

    def test_every_unique_planned_draw_and_policy_identity_is_required(self):
        for mutate in (
            lambda r: r["audit_only"]["paired_evidence"]["worlds"].pop(),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][1].update(worldIndex=0),
            lambda r: r["audit_only"]["evaluation_options"]["evaluationSeeds"].__setitem__(1, 7101),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(continuationPolicyId="replacement-policy"),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["publicTrace"][0].update(fixedDeadlinePlayerTurn=3),
            lambda r: r["audit_only"]["paired_evidence"]["anchor"].update(deadlinePlayerTurn=3),
            lambda r: r["audit_only"]["sampled_public_roots"].pop(),
            lambda r: r["audit_only"]["terminal_public_evidence"].pop(),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)

    def test_full_public_json_and_sample_anchor_equality_cannot_be_resealed_away(self):
        for mutate in (
            lambda r: r["audit_only"].update(public_input_json_sha256="0" * 64),
            lambda r: r["audit_only"]["sampled_public_roots"][0]["publicRoot"]["observation"].update(gold=123),
            lambda r: r["public_input"]["controller_context"]["anchor"]["observation"].update(gold=123),
            lambda r: r["audit_only"]["terminal_public_evidence"][0]["publicEvidence"]["events"].pop(0),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)
        raw = self.raw()
        raw["audit_only"]["public_input_json"] = json.dumps({})
        raw["audit_only"]["public_input_json_sha256"] = hashlib.sha256(raw["audit_only"]["public_input_json"].encode()).hexdigest()
        with self.assertRaises(ValueError): validate_raw_record(raw, CONFIG)

    def test_success_requires_actual_fatal_reward_and_terminal_hp_facts(self):
        for mutate in (
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"].update(fatalPlayerTurn=2),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"].update(actualExtraCardRewardsOffered=0),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(permanentChanges=[]),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(settlementComplete=False),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"][0]["plan"]["outcome"].update(healingReceived=1),
            lambda r: r["audit_only"]["terminal_public_evidence"][0]["publicEvidence"]["events"][-1]["payload"]["assets"].update(hp=1),
        ):
            with self.subTest(mutate=mutate): self.assertRejected(mutate)
        raw = self.raw()
        for entry in raw["audit_only"]["terminal_public_evidence"]:
            for event in entry["publicEvidence"]["events"]:
                payload = event["payload"]
                if payload.get("model") == "TheHuntPower": payload["amount"] = 0
        with self.assertRaisesRegex(ValueError, "actual_fatal_turn"): validate_raw_record(raw, CONFIG)

    def test_adapted_record_preserves_source_bytes_and_rejects_tampering(self):
        row = adapt_record(self.raw(), CONFIG, source_raw_bytes=self.payloads[0])
        for mutate in (
            lambda r: r["audit_only"].update(source_raw_sha256="a" * 64),
            lambda r: r["audit_only"].update(source_raw_utf8=r["audit_only"]["source_raw_utf8"] + " "),
            lambda r: r["audit_only"]["source_raw_record"]["targets"]["plan"].update(specified_success_probability=0),
            lambda r: r["audit_only"]["paired_evidence"]["worlds"].pop(),
            lambda r: r["targets"]["plan"].update(extra_net_hp_loss=1),
        ):
            changed = deepcopy(row); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(ValueError): validate_adapted_record(changed, CONFIG)
        with self.assertRaises(ValueError): adapt_record(self.raw(), CONFIG, source_raw_bytes=b"{}")

    def test_transport_rejects_duplicate_keys_nan_and_empty_sources(self):
        for payload in (b"", b'{}', b'{"x":0,"x":1}', b'{"x":NaN}'):
            with self.subTest(payload=payload), self.assertRaises(ValueError): adapt_jsonl(payload, CONFIG)
        rows = adapt_jsonl(b"\n" + self.payloads[0] + self.payloads[1], CONFIG)
        self.assertEqual(2, len(rows))

    def test_cli_writes_new_quarantined_artifact_and_has_no_learning_import(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "engineering.jsonl"
            command = [sys.executable, "-B", str(ROOT / "tools/validate_finite_hunt_v5.py"),
                       str(FIXTURES / "fresh-finite-hunt-v5-success.raw.json"), "--output", str(output)]
            result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("trainable:false", result.stdout)
            validate_adapted_record(json.loads(output.read_bytes()), CONFIG)
            before = output.read_bytes()
            again = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
            self.assertNotEqual(0, again.returncode)
            self.assertEqual(before, output.read_bytes())
        code = "import sys; sys.path.insert(0, 'python'); import nosl.finite_hunt_v5; assert 'torch' not in sys.modules; assert not any(k.startswith('nosl.train') for k in sys.modules)"
        result = subprocess.run([sys.executable, "-B", "-c", code], cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__": unittest.main()
