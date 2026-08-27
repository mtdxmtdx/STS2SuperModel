import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from split_dataset import split, split_for


META = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "simulator_version": "test-simulator",
    "semantic_database_version": "test-semantics",
    "scorer_version": "test-scorer",
    "feature_schema_version": "1",
    "model_version": "none",
    "generator_config_hash": "A" * 64,
}


class SplitDatasetTests(unittest.TestCase):
    def test_split_is_deterministic_and_grouped(self):
        root = Path(__file__).parent / "_split_test"
        root.mkdir(exist_ok=True)
        source = root / "source.jsonl"
        output = root / "out"
        rows = [
            META | {"episode_id": "episode-a", "record": 1},
            META | {"episode_id": "episode-a", "record": 2},
            META | {"episode_id": "episode-b", "record": 3},
        ]
        source.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        first = split(source, output)
        second = split(source, output)
        self.assertEqual(first, second)
        self.assertEqual(first["total_rows"], 3)
        self.assertEqual(first["total_groups"], 2)
        selected = [name for name in ("train", "validation", "test", "challenge") if (output / f"{name}.jsonl").read_text(encoding="utf-8").strip()]
        self.assertLessEqual(len(selected), 2)
        self.assertIn(split_for("episode-a"), ("train", "validation", "test", "challenge"))
        for path in output.glob("*"):
            path.unlink()
        output.rmdir()
        source.unlink()
        root.rmdir()


if __name__ == "__main__":
    unittest.main()
