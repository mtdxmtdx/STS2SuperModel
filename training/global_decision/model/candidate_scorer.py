"""Candidate-conditioned scorer with explicit legal-action masking."""

from __future__ import annotations

import torch
from torch import nn


class GlobalRewardCandidateScorer(nn.Module):
    def __init__(self, state_dim: int, candidate_dim: int, hidden_dim: int = 128) -> None:
        super().__init__()
        self.state_dim = state_dim
        self.candidate_dim = candidate_dim
        self.hidden_dim = hidden_dim
        self.state_projection = nn.Sequential(nn.Linear(state_dim, hidden_dim), nn.LayerNorm(hidden_dim), nn.ReLU())
        self.candidate_projection = nn.Sequential(nn.Linear(candidate_dim, hidden_dim), nn.LayerNorm(hidden_dim), nn.ReLU())
        self.trunk = nn.Sequential(
            nn.Linear(hidden_dim * 3, hidden_dim),
            nn.ReLU(),
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.ReLU(),
        )
        self.score_head = nn.Linear(hidden_dim // 2, 1)
        self.confidence_head = nn.Linear(hidden_dim // 2, 1)

    def forward(
        self,
        state_features: torch.Tensor,
        candidate_features: torch.Tensor,
        legal_mask: torch.Tensor,
    ) -> dict[str, torch.Tensor]:
        if state_features.ndim == 1:
            state_features = state_features.unsqueeze(0)
        if candidate_features.ndim == 2:
            candidate_features = candidate_features.unsqueeze(0)
        state = self.state_projection(state_features).unsqueeze(1)
        candidates = self.candidate_projection(candidate_features)
        if legal_mask is None:
            raise ValueError("legal_mask must be supplied; legality cannot be inferred from features")
        valid = legal_mask.to(device=candidate_features.device, dtype=torch.bool)
        if valid.ndim == 1:
            valid = valid.unsqueeze(0)
        weights = valid.to(candidates.dtype).unsqueeze(-1)
        offer_context = (candidates * weights).sum(dim=1) / weights.sum(dim=1).clamp_min(1.0)
        offer_context = offer_context.unsqueeze(1).expand_as(candidates)
        hidden = self.trunk(torch.cat([state.expand_as(candidates), candidates, offer_context], dim=-1))
        raw_scores = self.score_head(hidden).squeeze(-1)
        confidence = torch.sigmoid(self.confidence_head(hidden).squeeze(-1))
        logits = raw_scores.masked_fill(~valid, torch.finfo(raw_scores.dtype).min)
        return {"scores": raw_scores, "logits": logits, "confidence": confidence, "legal_mask": valid}

    @torch.no_grad()
    def rank(self, state_features: torch.Tensor, candidate_features: torch.Tensor, legal_mask: torch.Tensor) -> dict[str, torch.Tensor]:
        output = self.forward(state_features, candidate_features, legal_mask)
        output["selected_index"] = output["logits"].argmax(dim=-1)
        output["ranked_indices"] = torch.argsort(output["logits"], dim=-1, descending=True)
        return output
