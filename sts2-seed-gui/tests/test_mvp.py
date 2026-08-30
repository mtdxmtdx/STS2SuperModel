from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app import Application  # noqa: E402
from models import RunContext  # noqa: E402
from teacher_search import root_from_cli_response  # noqa: E402


class GuiMvpTests(unittest.TestCase):
    def test_context_hash_is_stable(self) -> None:
        first = RunContext(run_seed="demo").run_context_hash
        second = RunContext(run_seed="demo").run_context_hash
        self.assertEqual(first, second)

    def test_cli_response_action_ids_are_stable(self) -> None:
        root = root_from_cli_response({"decision": "map_select", "act": 1, "choices": [{"row": 3, "col": 2, "type": "Monster"}]}, {"map:1:3:2": [{"probability": 1.0, "value": 0.5}]})
        self.assertEqual(root["actions"][0]["action_id"], "map:1:3:2")
        self.assertEqual(root["actions"][0]["outcomes"][0]["value"], 0.5)
        shop = root_from_cli_response({"decision": "shop", "cards": [{"index": 2}], "relics": [{"index": 1}], "potions": []})
        self.assertEqual(shop["actions"][0]["cli_action"], "buy_card")
        self.assertEqual(shop["actions"][0]["args"]["card_index"], 2)

    def test_cli_map_decision_and_export(self) -> None:
        directory = ROOT / "test-output" / "mvp-run"
        directory.mkdir(parents=True, exist_ok=True)
        for child in directory.iterdir():
            if child.is_file():
                child.unlink()
        app = Application(directory)
        try:
            session = app.create_session({"context": {"run_seed": "demo", "character": "IRONCLAD"}, "cli_path": str(ROOT / "tests" / "fake_cli.py"), "start_cli": True, "source": {"type": "expert_video", "id": "v1"}})
            self.assertTrue(session["cli"]["started"])
            map_value = app.refresh_map(session["session_id"])
            self.assertEqual(map_value["type"], "map")
            checkpoint = app.create_checkpoint(session["session_id"], {"label": "before-route"})
            branch = app.create_branch(session["session_id"], {"name": "route-b", "parent_checkpoint_id": checkpoint["checkpoint_id"]})
            self.assertEqual(branch["parent_checkpoint_id"], checkpoint["checkpoint_id"])
            value = app.add_decision(session["session_id"], {"decision_type": "route", "act": 1, "floor": 1, "selected_action": "map:1:0", "legal_actions": [{"action_id": "map:1:0"}], "video_timestamp": "00:01:00", "sl_status": "verified_no_sl"})
            self.assertEqual(value["record"]["label_quality"], "ObservedPartial")
            self.assertEqual(value["record"]["branch_id"], "main")
            restored = app.restore_checkpoint(session["session_id"], checkpoint["checkpoint_id"])
            self.assertEqual(restored["session"]["restored_from_checkpoint"], checkpoint["checkpoint_id"])
            output = directory / "global_behavior.jsonl"
            result = app.export(session["session_id"], str(output))
            self.assertEqual(result["row_count"], 1)
            self.assertEqual(len(output.read_text(encoding="utf-8").splitlines()), 1)
            self.assertTrue(Path(result["manifest_path"]).is_file())
            validation = app.validate(session["session_id"])
            self.assertTrue(validation["ok"])
            self.assertEqual(validation["row_count"], 1)
            diff = app.diff(session["session_id"])
            self.assertEqual(diff["decision_type"], "route")
            reliable = app.export_reliable(session["session_id"], str(directory / "global_behavior.reliable.jsonl"))
            self.assertEqual(reliable["row_count"], 0)
            self.assertEqual(reliable["excluded_row_count"], 1)
            teacher = app.add_teacher_record(session["session_id"], {"parent_checkpoint_id": checkpoint["checkpoint_id"], "action_values": [{"action_id": "map:1:0", "value": 0.5}], "teacher_best_actions": ["map:1:0"], "teacher_value": 0.5})
            self.assertEqual(teacher["label_quality"], "CounterfactualTeacher")
            teacher_output = directory / "global_teacher.jsonl"
            teacher_result = app.export_teacher(session["session_id"], str(teacher_output))
            self.assertEqual(teacher_result["row_count"], 1)
            teacher_checkpoint = app.create_checkpoint(session["session_id"], {"label": "teacher", "save_cli": True})
            evaluated = app.evaluate_teacher(session["session_id"], {"parent_checkpoint_id": teacher_checkpoint["checkpoint_id"], "actions": [{"action_id": "map:1:0"}]})
            self.assertEqual(evaluated["record"]["label_quality"], "EstimatedByHeuristic")
            searched = app.search_teacher(session["session_id"], {"parent_checkpoint_id": teacher_checkpoint["checkpoint_id"], "depth": 2, "root": {"state": {}, "actions": [{"action_id": "a", "outcomes": [{"probability": 1.0, "next_node": {"terminal_value": 1}}]}, {"action_id": "b", "outcomes": [{"probability": 1.0, "next_node": {"terminal_value": 2}}]}]}})
            self.assertEqual(searched["search"]["best_actions"], ["b"])
            tree = app.build_cli_tree(session["session_id"], {"parent_checkpoint_id": teacher_checkpoint["checkpoint_id"], "actions": [{"action_id": "map:1:0", "cli_action": "select_map_node", "args": {"row": 1, "col": 0}}], "depth": 1})
            self.assertEqual(tree["root"]["actions"][0]["outcomes"][0]["probability"], 1.0)
            provider_tree = app.build_provider_tree(session["session_id"], {"parent_checkpoint_id": teacher_checkpoint["checkpoint_id"], "provider_path": str(ROOT / "tests" / "fake_provider.py"), "depth": 2, "root": {"state": {}, "actions": [{"action_id": "good"}]}})
            self.assertEqual(sum(item["probability"] for item in provider_tree["root"]["actions"][0]["outcomes"]), 1.0)
        finally:
            app.close()


if __name__ == "__main__":
    unittest.main()
