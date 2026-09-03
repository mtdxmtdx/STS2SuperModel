import copy
import json
from pathlib import Path

import torch

from training.combat_model.encoder import CombatFeatureEncoder, TokenVocabulary
from training.combat_model.model import CombatPolicyValueModel


ROOT = Path(__file__).resolve().parents[1]


def test_feature_contract_has_no_hidden_input_and_sets_are_order_invariant():
    manifest = json.loads((ROOT / "data/combat_model/combat-feature-manifest.json").read_text(encoding="utf-8"))
    forbidden = set(manifest["forbidden_input_fields"])
    assert not any(part in forbidden for field in manifest["public_input_fields"] for part in field.replace("[]", "").split("."))
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
