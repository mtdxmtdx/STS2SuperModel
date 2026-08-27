import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from build_dataset_manifest import build


META = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
}


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.source = Path(__file__).parent / "_test_manifest_input.jsonl"
        self.output = Path(__file__).parent / "_test_manifest_output.json"

    def tearDown(self):
        for path in (self.source, self.output):
            try:
                path.unlink()
            except FileNotFoundError:
                pass

    def test_manifest_requires_version_metadata(self):
        self.source.write_text(json.dumps({"public_state_hash": "s"}) + "\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            build([self.source], self.output)

    def test_manifest_rejects_mixed_versions(self):
        rows = [META | {"public_state_hash": "s1"}, META | {"public_state_hash": "s2", "game_commit": "other"}]
        self.source.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        with self.assertRaises(ValueError):
            build([self.source], self.output)


if __name__ == "__main__":
    unittest.main()
