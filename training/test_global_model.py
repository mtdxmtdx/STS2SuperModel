from __future__ import annotations

import torch

from training.global_decision.model.candidate_scorer import GlobalRewardCandidateScorer
from training.global_decision.model.dataset import GlobalRewardDataset, collate_samples
from training.global_decision.model.encoder import CandidateEncoder, GlobalRewardEncoder
from training.global_decision.model.train_prototype import train_model
from training.global_decision.model.evaluate_prototype import evaluate_model
from training.global_decision.synthetic_global_states import generate_dataset


def test_encoders_are_fixed_size_and_index_free() -> None:
    row = generate_dataset(1, 20260831)[0]
    state_encoder, candidate_encoder = GlobalRewardEncoder(), CandidateEncoder()
    state = state_encoder.encode(row["state_public"])
    candidates = row["offer_snapshot"]["candidates"]
    assert state.shape == (state_encoder.state_dim,)
    assert candidate_encoder.encode(candidates[0]).shape == (candidate_encoder.candidate_dim,)
    assert torch.equal(candidate_encoder.encode(candidates[0]), candidate_encoder.encode(dict(candidates[0], candidate_index=99)))


def test_scorer_masks_illegal_candidates_and_supports_skip() -> None:
    model = GlobalRewardCandidateScorer(8, 6, hidden_dim=16)
    state = torch.zeros(1, 8)
    candidates = torch.zeros(1, 4, 6)
    mask = torch.tensor([[True, False, True, True]])
    out = model.rank(state, candidates, mask)
    assert out["selected_index"].item() != 1
    assert out["logits"][0, 1] < -1e20
    assert out["confidence"].min() >= 0 and out["confidence"].max() <= 1
    assert out["legal_mask"].equal(mask)


def test_64_sample_overfit_smoke_and_deterministic_repeat() -> None:
    rows = generate_dataset(64, 20260831)
    dataset = GlobalRewardDataset(rows)
    first_model, first_history = train_model(dataset, epochs=100, batch_size=64, hidden_dim=32, lr=1e-2, seed=7)
    second_model, second_history = train_model(dataset, epochs=100, batch_size=64, hidden_dim=32, lr=1e-2, seed=7)
    assert first_history[-1]["loss"] < first_history[0]["loss"]
    metrics = evaluate_model(rows, first_model, dataset.state_encoder, dataset.candidate_encoder)
    assert metrics["top1_accuracy"] >= 0.9
    for key, value in first_model.state_dict().items():
        assert torch.equal(value, second_model.state_dict()[key])
    batch = collate_samples([dataset[0]])
    first_model.eval()
    with torch.no_grad():
        a = first_model.rank(batch["state"], batch["candidates"], batch["legal_mask"])
        b = first_model.rank(batch["state"], batch["candidates"], batch["legal_mask"])
    assert torch.equal(a["selected_index"], b["selected_index"])
