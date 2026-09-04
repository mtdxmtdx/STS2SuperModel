import copy
import hashlib
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


def test_v4_is_authoritative_and_hashes_match():
    model_dir = ROOT / "data/combat_model/model-v4"
    manifest = json.loads((model_dir / "combat-nosl-model-manifest.json").read_text(encoding="utf-8"))
    actual = hashlib.sha256((model_dir / "combat-nosl-v4.onnx").read_bytes()).hexdigest().upper()
    assert manifest["model_stage"] == "authoritative"
    assert manifest["promotion_status"] == "authoritative"
    assert manifest["onnx_sha256"] == actual
    assert manifest["promotion_cross_split_top1_comparison_used"] is False
    comparison = json.loads((ROOT / "data/combat_model/model-comparison.json").read_text(encoding="utf-8"))
    statuses = {run["model_id"]: run["promotion_status"] for run in comparison["runs"]}
    assert statuses["combat-nosl-policy-value-v3"] == "superseded"
    assert statuses["combat-nosl-policy-value-v4"] == "authoritative"


def test_core_holdout_is_frozen_balanced_and_has_v4_baseline():
    holdout_dir = ROOT / "data/combat_model/holdouts"
    holdout = json.loads((holdout_dir / "holdout-core-v1.json").read_text(encoding="utf-8"))
    test_ids = holdout["test_episode_ids"]
    challenge_ids = holdout["challenge_episode_ids"]
    assert test_ids == sorted(test_ids)
    assert challenge_ids == sorted(challenge_ids)
    assert not set(test_ids) & set(challenge_ids)
    for split in ("test", "challenge"):
        distribution = holdout["character_distribution"][split]
        assert 0.45 <= distribution["The Ironclad"]["ratio"] <= 0.55
        assert 0.45 <= distribution["The Silent"]["ratio"] <= 0.55
    baseline = json.loads((holdout_dir / "holdout-core-v1-baseline.json").read_text(encoding="utf-8"))
    assert baseline["model_id"] == "combat-nosl-policy-value-v4"
    assert baseline["test"]["rows"] == holdout["test_reliable_row_count"]
    assert baseline["challenge"]["rows"] == holdout["challenge_reliable_row_count"]
    assert baseline["top1"] == baseline["test"]["top1"]


def test_true_balanced_coverage_profile_is_version_locked():
    report = json.loads((ROOT / "data/combat_model/true-balanced-v1/coverage-profile.json").read_text(encoding="utf-8"))
    assert report["source_sha256"] == "93F9FCD4BF504FB737806E7A5074CA65FBDF802959A5917700121FD41DCF9AA3"
    assert report["row_count"] == 26971
    assert report["reliable_row_count"] == 20932
    assert report["proposed_acceptance_thresholds"]["status"] == "pending_human_confirmation"
