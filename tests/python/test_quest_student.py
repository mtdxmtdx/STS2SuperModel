"""Native public Quest regression; no simulator dependency or optimizer steps."""
import copy
import json
from pathlib import Path
import sys
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.inference import Inference
from nosl.model import Student
from nosl.schema import HEADS, SchemaError, load_config, validate_public


class QuestStudentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.pilot.json")

    def setUp(self):
        self.public = json.loads((ROOT / "tests/python/fixtures/public-v2-quest.jsonl").read_text())
        self.quest_slot = next(i for i, card in enumerate(self.public["observation"]["hand"])
                               if card["id"] == "Dowsing")

    def test_native_public_packet_admitted_without_quest_play(self):
        self.assertIs(validate_public(self.public, self.config), self.public)
        obs = self.public["observation"]
        quest = obs["hand"][self.quest_slot]
        self.assertEqual(quest["type"], "Quest")
        self.assertEqual(quest["keywords"], ["Unplayable"])
        self.assertEqual(quest["publicState"]["unknownRoomsEntered"], "0")
        draws = [json.loads(event["detail"]) for event in obs["history"] if event["kind"] == "draw"]
        self.assertIn(quest, draws)
        actions = self.public["candidate_actions"]
        self.assertEqual(len(actions), 5)
        self.assertEqual({action["slot"] for action in actions if action["kind"] == "play"}, {0, 1, 3, 4})
        self.assertFalse(any(action["kind"] == "play" and action["slot"] == self.quest_slot for action in actions))
        result = Inference(self.config).predict(self.public)
        self.assertEqual(result["status"], "MODEL_UNTRAINED")
        self.assertIsNone(result["selected_action"])

    def test_full_forward_backward_reaches_quest_embedding_without_optimizer(self):
        torch.manual_seed(639)
        model = Student(self.config).eval()
        before = {name: tensor.clone() for name, tensor in model.state_dict().items()}
        output = model(self.public)
        count = len(self.public["candidate_actions"])
        for head in HEADS:
            self.assertEqual(output[head].shape[0], count)
            self.assertTrue(torch.isfinite(output[head]).all(), head)
        # A scalar engineering objective exercises every head, not invented labels.
        loss = sum(output[head].square().mean() for head in HEADS)
        loss.backward()
        gradients = [parameter.grad for parameter in model.parameters() if parameter.grad is not None]
        self.assertTrue(gradients)
        self.assertTrue(all(torch.isfinite(gradient).all() for gradient in gradients))
        quest_gradient = model.entity.weight.grad[model.vocab["type:Quest"]]
        self.assertGreater(quest_gradient.abs().sum().item(), 0)
        for name, tensor in model.state_dict().items():
            self.assertTrue(torch.equal(before[name], tensor), name)

    def test_sentinel_and_unknown_types_rejected_in_hand_and_history(self):
        for card_type in ("None", "Unknown", None):
            for location in ("hand", "history"):
                with self.subTest(card_type=card_type, location=location):
                    public = copy.deepcopy(self.public)
                    if location == "hand":
                        public["observation"]["hand"][self.quest_slot]["type"] = card_type
                    else:
                        event = next(event for event in public["observation"]["history"]
                                     if event["kind"] == "draw" and json.loads(event["detail"])["id"] == "Dowsing")
                        card = json.loads(event["detail"])
                        card["type"] = card_type
                        event["detail"] = json.dumps(card)
                    with self.assertRaisesRegex(SchemaError, "card.type") as error:
                        validate_public(public, self.config)
                    self.assertEqual(error.exception.status, "UNSUPPORTED")

    def test_quest_admission_keeps_public_state_whitelist(self):
        self.public["observation"]["hand"][self.quest_slot]["publicState"]["seed"] = "private"
        with self.assertRaises(SchemaError):
            validate_public(self.public, self.config)

    def test_previous_five_type_embedding_shape_rejected(self):
        model = Student(self.config)
        previous = model.state_dict()
        quest_index = model.vocab["type:Quest"]
        previous["entity.weight"] = torch.cat((previous["entity.weight"][:quest_index],
                                              previous["entity.weight"][quest_index + 1:]))
        with self.assertRaisesRegex(RuntimeError, "size mismatch for entity.weight"):
            model.load_state_dict(previous, strict=True)


if __name__ == "__main__":
    unittest.main()
