"""Split checking only; no model construction, fitting, backward or optimizer."""
from copy import deepcopy
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
sys.path.insert(0, str(ROOT / "tests/data"))
from nosl.schema import SchemaError
from nosl.train import check_split_isolation
from test_public_identity import equivalent_cross_battle_records


class PublicIdentityLeakageTests(unittest.TestCase):
    def test_representationally_equivalent_cross_battle_roots_cannot_cross_splits(self):
        a, b = equivalent_cross_battle_records()
        with self.assertRaisesRegex(SchemaError, "recomputed public_input digest"):
            check_split_isolation(SimpleNamespace(records=[a]), SimpleNamespace(records=[b]))

    def test_candidate_revision_only_difference_cannot_hide_leakage(self):
        a, b = equivalent_cross_battle_records()
        b["public_input"] = deepcopy(a["public_input"])
        for action in b["public_input"]["candidate_actions"]:
            action["revision"] = 500
        with self.assertRaisesRegex(SchemaError, "recomputed public_input digest"):
            check_split_isolation(SimpleNamespace(records=[a]), SimpleNamespace(records=[b]))

    def test_meaningful_public_difference_with_distinct_provenance_is_allowed(self):
        a, b = equivalent_cross_battle_records()
        b["public_input"]["observation"]["turn"] = 2
        check_split_isolation(SimpleNamespace(records=[a]), SimpleNamespace(records=[b]))


if __name__ == "__main__":
    unittest.main()
