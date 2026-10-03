"""Ruby v3 dispatch and retained draw-closure evidence, without native generation."""
import hashlib
import json
import os
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/data")]
from nosl import constructed_native_event_v5 as event_source
from nosl.constructed_native_policy_v5 import (EVALUATOR_SOURCE_FILES, PRIOR_SCHEMA, SAMPLER, POSTERIOR,
    RUBY_SAMPLER, RUBY_POSTERIOR, RUBY_DRAW_SAMPLER, RUBY_DRAW_POSTERIOR, SOURCE_KIND,
    _profile, adapt_report, validate_record)
from nosl.schema_v5 import load_config, loads
from test_constructed_native_policy_v5 import reencode_report

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURE = os.environ.get("NOSL_CONSTRUCTED_RUBY_DRAW_FIXTURE")


class RubyDrawSamplerDispatchTests(unittest.TestCase):
    def report(self, sampler, encounter="RubyRaiders", schema=PRIOR_SCHEMA, enabled=True):
        return {"options": {"enableConditioning": enabled,
                "prior": {"schemaVersion": schema, "setup": {"encounter": encounter}}},
                "collection_contract_json": json.dumps({"sampler_version": sampler})}

    def test_three_ruby_versions_keep_exact_distinct_profiles(self):
        for sampler, posterior in ((SAMPLER, POSTERIOR), (RUBY_SAMPLER, RUBY_POSTERIOR),
                (RUBY_DRAW_SAMPLER, RUBY_DRAW_POSTERIOR)):
            for enabled in (False, True):
                with self.subTest(sampler=sampler, enabled=enabled):
                    self.assertEqual((sampler, posterior, SOURCE_KIND), _profile(self.report(sampler, enabled=enabled)))

    def test_draw_closure_pair_cannot_cross_source_laws_or_encounters(self):
        for report, reason in ((self.report(RUBY_DRAW_SAMPLER, "SludgeSpinnerWeak"), "ruby_sampler_requires_declared_ruby"),
                (self.report(RUBY_DRAW_SAMPLER, schema=event_source.PRIOR_SCHEMA), "event_sampler_profile"),
                (self.report(RUBY_DRAW_POSTERIOR), "ordinary_sampler_profile"),
                (self.report(RUBY_DRAW_SAMPLER.replace("v3-", "v4-")), "ordinary_sampler_profile")):
            with self.subTest(reason=reason), self.assertRaisesRegex(ValueError, reason): _profile(report)

    def test_draw_closure_and_density_dependencies_are_bound(self):
        required = {"src/Nosl.Worker/NativeConstructedTapeSource.cs",
            "src/Nosl.Worker/NativePublicCombatPrefixCondition.cs", "src/Nosl.Worker/NativePublicCombatPrefixProposal.cs",
            "src/Nosl.Worker/NativePublicDrawPrefixCondition.cs", "src/Nosl.Worker/NativeInitialShuffleCondition.cs"}
        self.assertTrue(required.issubset(EVALUATOR_SOURCE_FILES))


class RetainedRubyDrawReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not FIXTURE or not Path(FIXTURE).is_file():
            raise unittest.SkipTest("Retained Ruby v3 report missing; no native run started")
        cls.payload = Path(FIXTURE).read_bytes()
        cls.raw = loads(cls.payload.decode())

    def test_exact_report_preserves_all_values_masks_attempts_and_public_input(self):
        rows = adapt_report(self.payload, CONFIG)
        self.assertEqual(RUBY_DRAW_SAMPLER, loads(self.raw["collection_contract_json"])["sampler_version"])
        self.assertEqual(len(self.raw["records"]), len(rows)); self.assertTrue(rows)
        for raw, row in zip(self.raw["records"], rows):
            validate_record(row, CONFIG)
            self.assertEqual(self.payload, row["audit_only"]["constructed_tape_evidence"]["report_utf8"].encode())
            self.assertEqual(hashlib.sha256(self.payload).hexdigest(), row["audit_only"]["source_artifact_sha256"])
            self.assertEqual(raw["public_input"], row["public_input"])
            self.assertEqual(RUBY_DRAW_SAMPLER, raw["audit_only"]["versions"]["sampler"])
            self.assertEqual(RUBY_DRAW_POSTERIOR, raw["audit_only"]["versions"]["posterior_profile"])
            self.assertFalse(row["audit_only"]["native_run"]); self.assertFalse(row["audit_only"]["trainable"])
            for source, target in zip(raw["targets"]["actions"], row["targets"]["actions"]):
                projected = {key: value for key, value in source.items()
                    if key not in ("requested_worlds", "candidate_worlds_returned", "executed_worlds")}
                self.assertEqual(projected, target)

    def test_contract_and_profile_substitutions_reject_with_specific_reason(self):
        self.assertTrue(adapt_report(reencode_report(self.raw), CONFIG))
        for field, replacement, reason in (("posterior_profile", RUBY_POSTERIOR, "raw_identity:posterior_profile"),
                ("sampler_version", RUBY_SAMPLER, "raw_identity:sampler_version")):
            report = loads(self.payload.decode())
            report["records"][0]["audit_only"][field] = replacement
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, reason):
                adapt_report(reencode_report(report), CONFIG)
        report = loads(self.payload.decode())
        report["records"][0]["audit_only"]["versions"]["sampler"] = RUBY_SAMPLER
        with self.assertRaisesRegex(ValueError, "row_versions"): adapt_report(reencode_report(report), CONFIG)


if __name__ == "__main__": unittest.main()
