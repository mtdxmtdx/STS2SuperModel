"""Version-scoped Ruby proposal reports; never relabel historical raw evidence."""
from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tests/data")]
from nosl import constructed_native_event_v5 as event_source
from nosl.constructed_native_policy_v5 import (PRIOR_SCHEMA, SAMPLER, POSTERIOR, RUBY_SAMPLER, RUBY_POSTERIOR,
    SOURCE_KIND, _profile, adapt_report)
from nosl.schema_v5 import load_config, loads
from test_constructed_native_policy_v5 import reencode_report

CONFIG = load_config(ROOT / "configs/student.v5.engineering.json")
FIXTURES = Path(os.environ.get("NOSL_CONSTRUCTED_RUBY_FIXTURES", ROOT / "artifacts/ruby-proof"))


class RubySamplerDispatchTests(unittest.TestCase):
    def report(self, sampler, encounter="RubyRaiders", schema=PRIOR_SCHEMA):
        return {"options": {"prior": {"schemaVersion": schema, "setup": {"encounter": encounter}}},
                "collection_contract_json": json.dumps({"sampler_version": sampler})}

    def test_old_and_new_ruby_identifiers_remain_distinct(self):
        self.assertEqual((SAMPLER, POSTERIOR, SOURCE_KIND), _profile(self.report(SAMPLER)))
        self.assertEqual((RUBY_SAMPLER, RUBY_POSTERIOR, SOURCE_KIND), _profile(self.report(RUBY_SAMPLER)))
        self.assertEqual((SAMPLER, POSTERIOR, SOURCE_KIND), _profile(self.report(SAMPLER, "SludgeSpinnerWeak")))

    def test_new_ruby_pair_is_scoped_to_ordinary_ruby(self):
        for value, reason in ((self.report(RUBY_SAMPLER, "SludgeSpinnerWeak"), "ruby_sampler_requires_declared_ruby"),
            (self.report(RUBY_SAMPLER, schema=event_source.PRIOR_SCHEMA), "event_sampler_profile"),
            (self.report(event_source.SAMPLER), "ordinary_sampler_profile"),
            (self.report("invented-new-sampler"), "ordinary_sampler_profile")):
            with self.subTest(reason=reason), self.assertRaisesRegex(ValueError, reason): _profile(value)


class RetainedRubyReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        paths = [FIXTURES / f"case-{i:02d}" / "response.jsonl" for i in range(2)]
        if not all(p.is_file() for p in paths): raise unittest.SkipTest("Retained new Ruby reports missing; no native run started")
        cls.payloads = [p.read_bytes() for p in paths]

    def test_each_report_preserves_sampler_identity_all_rows_and_outcome_mass(self):
        for payload in self.payloads:
            report = loads(payload.decode()); rows = adapt_report(payload, CONFIG)
            self.assertEqual(RUBY_SAMPLER, loads(report["collection_contract_json"])["sampler_version"])
            self.assertEqual(len(report["records"]), len(rows)); self.assertTrue(rows)
            for raw, row in zip(report["records"], rows):
                self.assertEqual(payload, row["audit_only"]["constructed_tape_evidence"]["report_utf8"].encode())
                self.assertEqual(raw["public_input"], row["public_input"])
                self.assertEqual(RUBY_SAMPLER, raw["audit_only"]["versions"]["sampler"])
                self.assertEqual(RUBY_POSTERIOR, raw["audit_only"]["versions"]["posterior_profile"])
                for source, target in zip(raw["targets"]["actions"], row["targets"]["actions"]):
                    self.assertEqual(source["masks"], target["masks"]); self.assertEqual(source["value"], target["value"])
                    self.assertEqual(source["allocated_worlds"], target["allocated_worlds"])
            if all(a["status"] == "posterior_exhausted" for a in report["attempts"]):
                self.assertTrue(all(not any(a["masks"].values()) for row in rows for a in row["targets"]["actions"]))

    def test_raw_sampler_version_cannot_change_without_its_exact_contract(self):
        for payload in self.payloads:
            report = loads(payload.decode()); self.assertTrue(adapt_report(reencode_report(report), CONFIG))
            report["records"][0]["audit_only"]["versions"]["sampler"] = SAMPLER
            with self.assertRaisesRegex(ValueError, "row_versions"): adapt_report(reencode_report(report), CONFIG)


if __name__ == "__main__": unittest.main()
