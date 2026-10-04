"""Source-only continuation tests; no report targets or simulation are loaded."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools"), str(ROOT / "python")]

from extend_constructed_final_protection import canonical, sha, standard_rows_for_ledger, verify_alias_extension
from nosl.protection_v5 import component_id


def component(tokens, split="train"):
    return {component_id(tokens): {"split": split, "tokens": sorted(tokens)}}


class ConstructedFinalProtectionMetadataTests(unittest.TestCase):
    def test_repeated_projection_keeps_report_attempt_denominator(self):
        row = {"audit_only": {"source_run_group": "observed"}}
        ledger = [{"standard_metadata_sha256": sha(canonical(row))} for _ in range(3)]
        rows = standard_rows_for_ledger([row], ledger)
        base = {"components": component(["source_run_group:historic"], "test")}
        successor = {"components": {**base["components"], **component(["source_run_group:observed"])}}
        stats = verify_alias_extension(base, successor, rows)
        self.assertEqual(stats["checked_report_attempts"], 3)
        self.assertEqual(stats["new_components"], 1)
        self.assertEqual(stats["new_tokens"], 1)

    def test_old_owner_change_fails_closed(self):
        tokens = ["source_run_group:historic"]
        base = {"components": component(tokens, "test")}
        altered = {"components": component(tokens, "train")}
        with self.assertRaisesRegex(ValueError, "old component tokens or owners changed"):
            verify_alias_extension(base, altered, [{"audit_only": {"source_run_group": "historic"}}])

    def test_family_overlap_cannot_hide_missing_public_alias(self):
        registry = {"components": component(["source_run_group:historic"])}
        row = {"audit_only": {"source_run_group": "historic", "public_state_digest": "missing-public-root"}}
        with self.assertRaisesRegex(ValueError, "normal source/public token missing"):
            verify_alias_extension(registry, registry, [row])


if __name__ == "__main__":
    unittest.main()
