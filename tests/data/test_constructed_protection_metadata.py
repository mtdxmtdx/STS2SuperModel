"""Metadata-only projector checks; no target fixtures or simulator execution."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools"), str(ROOT / "python")]

from extend_constructed_protection_v1 import array_items, object_fields, supplemental_rows
from nosl.protection_v5 import group_records, record_tokens, validate_metadata


class ConstructedProtectionMetadataTests(unittest.TestCase):
    def test_uninspected_values_are_only_lexically_sliced(self):
        # An intentionally non-JSON sentinel would fail if passed to a JSON
        # decoder. The projector may slice it, but only decodes selected fields.
        text = '{"audit_only":{"source_run_group":"family"},"targets":UNREAD_TARGET_SENTINEL,"outcomes":[UNREAD_OUTCOME_SENTINEL]}'
        fields = object_fields(text)
        self.assertEqual(fields["targets"], "UNREAD_TARGET_SENTINEL")
        self.assertEqual(list(array_items(fields["outcomes"])), ["UNREAD_OUTCOME_SENTINEL"])
        self.assertEqual(object_fields(fields["audit_only"]), {"source_run_group": '"family"'})

    def test_duplicate_or_trailing_container_data_is_rejected(self):
        for text in ('{"a":1,"a":2}', '{"a":1,}', '{"a":1}{}'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                object_fields(text)
        for text in ('[1,]', '[1][]'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                list(array_items(text))

    def test_supplemental_spellings_join_the_existing_family(self):
        audit = {"source_run_group": "family", "underlying_battle_alias": "configured/combat:0",
                 "public_root_alias": "public-root:abc"}
        standard = {"audit_only": {"source_run_group": "family", "public_state_digest": "abc"}}
        supplemental = supplemental_rows(audit)
        for row in supplemental:
            validate_metadata(row)
        groups, components, _ = group_records([standard, *supplemental], {})
        self.assertEqual(len(set(groups)), 1)
        self.assertEqual(components[groups[0]], {
            "source_run_group:family", "public_state_digest:abc", "source_combat_id:configured/combat:0",
            "source_run_group:configured", "public_state_digest:public-root:abc"})
        # Unmodified helpers do not consume arbitrary raw exporter fields.
        self.assertEqual(record_tokens({"audit_only": audit}), {"source_run_group:family"})


if __name__ == "__main__":
    unittest.main()
