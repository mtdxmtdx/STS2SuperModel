import copy
import json
from pathlib import Path

import torch
import numpy as np

from training.combat_model.decision_parity import canonical_ranking, compare_decisions, inject_near_tie_perturbation
from training.combat_model.encoder import CombatFeatureEncoder, TokenVocabulary
from training.combat_model.model import CombatPolicyValueModel
from training.combat_model.train import character_distribution


ROOT = Path(__file__).resolve().parents[1]


def test_feature_contract_has_no_hidden_input_and_sets_are_order_invariant():
    manifest = json.loads((ROOT / "data/combat_model/combat-feature-manifest.json").read_text(encoding="utf-8"))
    forbidden = set(manifest["forbidden_input_fields"])
    assert not any(part in forbidden for field in manifest["public_input_fields"] for part in field.replace("[]", "").split("."))
    assert manifest["decision_parity"]["tie_tolerance"] == 1.0e-4
    assert manifest["decision_parity"]["tie_break"] == "ActionId Ordinal ascending within each tie group"
    fixture = json.loads((ROOT / "data/combat_model/model-v1/feature-parity-fixture.json").read_text(encoding="utf-8"))
    row = fixture["row"]
    encoder = CombatFeatureEncoder(TokenVocabulary.from_dict(fixture["vocabulary"]))
    expected = encoder.encode(row)
    reordered = copy.deepcopy(row)
    reordered["public_state"]["hand"].reverse()
    reordered["public_state"]["enemies"].reverse()
    actual = encoder.encode(reordered)
    for key in ("state_numeric", "state_token_ids", "state_token_weights", "enemy_token_ids", "enemy_numeric"):
        assert actual[key] == expected[key]


def test_model_requires_and_applies_legal_mask():
    model = CombatPolicyValueModel(vocab_size=8, embedding_dim=4, hidden_dim=8)
    args = (
        torch.zeros(1, 16), torch.ones(1, 2, dtype=torch.long), torch.ones(1, 2),
        torch.ones(1, 1, dtype=torch.long), torch.zeros(1, 1, 8), torch.ones(1, 1, dtype=torch.bool),
        torch.ones(1, 2, 3, dtype=torch.long), torch.zeros(1, 2, 8), torch.tensor([[True, False]]),
    )
    logits, values, risk = model(*args)
    assert logits.shape == (1, 2)
    assert values.shape == (1, 2, 3)
    assert risk.shape == (1, 2)
    assert logits[0, 1].item() == -1.0e9


def test_decision_parity_uses_ordinal_tie_break_and_detects_perturbation():
    action_ids = [["action:B", "action:A", "action:C"]]
    legal = np.array([[True, True, True]])
    reference = np.array([[1.0, 1.00005, 0.0]], dtype=np.float32)
    equivalent = np.array([[1.00001, 1.00004, 0.0]], dtype=np.float32)
    assert [action_ids[0][index] for index in canonical_ranking(reference[0], action_ids[0], legal[0])] == [
        "action:A", "action:B", "action:C",
    ]
    assert compare_decisions(reference, equivalent, action_ids, legal).passed
    perturbed, sample, action = inject_near_tie_perturbation(
        equivalent, reference, action_ids, legal, 1.0e-2,
    )
    assert (sample, action) == (0, 0)
    assert not compare_decisions(reference, perturbed, action_ids, legal).passed


def test_character_distribution_uses_real_row_denominator():
    assert character_distribution([
        {"character": "The Ironclad"},
        {"character": "The Silent"},
        {"character": "The Silent"},
    ]) == {
        "The Ironclad": {"rows": 1, "ratio": 1 / 3},
        "The Silent": {"rows": 2, "ratio": 2 / 3},
    }
