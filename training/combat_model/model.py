"""Candidate-conditioned DeepSets model for public NOSL combat state."""

from __future__ import annotations

import torch
from torch import nn


class CombatPolicyValueModel(nn.Module):
    def __init__(self, vocab_size: int, embedding_dim: int = 64, hidden_dim: int = 128) -> None:
        super().__init__()
        self.vocab_size = vocab_size
        self.embedding_dim = embedding_dim
        self.hidden_dim = hidden_dim
        self.embedding = nn.Embedding(vocab_size, embedding_dim, padding_idx=0)
        self.enemy_encoder = nn.Sequential(
            nn.Linear(embedding_dim + 8, hidden_dim), nn.ReLU(), nn.Linear(hidden_dim, hidden_dim), nn.ReLU(),
        )
        self.state_encoder = nn.Sequential(
            nn.Linear(16 + embedding_dim + hidden_dim, hidden_dim), nn.LayerNorm(hidden_dim), nn.ReLU(),
        )
        self.candidate_encoder = nn.Sequential(
            nn.Linear(embedding_dim + 8, hidden_dim), nn.LayerNorm(hidden_dim), nn.ReLU(),
        )
        self.trunk = nn.Sequential(
            nn.Linear(hidden_dim * 3, hidden_dim), nn.ReLU(),
            nn.Linear(hidden_dim, hidden_dim), nn.ReLU(),
        )
        self.policy_head = nn.Linear(hidden_dim, 1)
        self.value_head = nn.Linear(hidden_dim, 3)
        self.risk_head = nn.Linear(hidden_dim, 1)

    def forward(
        self,
        state_numeric: torch.Tensor,
        state_token_ids: torch.Tensor,
        state_token_weights: torch.Tensor,
        enemy_token_ids: torch.Tensor,
        enemy_numeric: torch.Tensor,
        enemy_mask: torch.Tensor,
        candidate_token_ids: torch.Tensor,
        candidate_numeric: torch.Tensor,
        legal_mask: torch.Tensor,
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        state_embeddings = self.embedding(state_token_ids)
        weights = state_token_weights.unsqueeze(-1)
        state_bag = (state_embeddings * weights).sum(dim=1) / weights.sum(dim=1).clamp_min(1.0)
        enemy_hidden = self.enemy_encoder(torch.cat([self.embedding(enemy_token_ids), enemy_numeric], dim=-1))
        enemy_weights = enemy_mask.to(enemy_hidden.dtype).unsqueeze(-1)
        enemy_bag = (enemy_hidden * enemy_weights).sum(dim=1) / enemy_weights.sum(dim=1).clamp_min(1.0)
        state = self.state_encoder(torch.cat([state_numeric, state_bag, enemy_bag], dim=-1))
        candidate_embeddings = self.embedding(candidate_token_ids)
        candidate_present = candidate_token_ids.ne(0).to(candidate_embeddings.dtype).unsqueeze(-1)
        candidate_bag = (candidate_embeddings * candidate_present).sum(dim=2) / candidate_present.sum(dim=2).clamp_min(1.0)
        candidates = self.candidate_encoder(torch.cat([candidate_bag, candidate_numeric], dim=-1))
        legal_weights = legal_mask.to(candidates.dtype).unsqueeze(-1)
        context = (candidates * legal_weights).sum(dim=1) / legal_weights.sum(dim=1).clamp_min(1.0)
        context = context.unsqueeze(1).expand_as(candidates)
        state_expanded = state.unsqueeze(1).expand_as(candidates)
        hidden = self.trunk(torch.cat([state_expanded, candidates, context], dim=-1))
        raw_logits = self.policy_head(hidden).squeeze(-1)
        logits = torch.where(legal_mask, raw_logits, torch.full_like(raw_logits, -1.0e9))
        values = self.value_head(hidden)
        risk = self.risk_head(hidden).squeeze(-1)
        return logits, values, risk
