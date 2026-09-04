"""Decision-equivalence metrics for PyTorch/ONNX combat policy logits."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Sequence

import numpy as np


TIE_TOLERANCE = 1.0e-4
NEAR_TIE_THRESHOLD = 1.0e-3
NUMERIC_DIAGNOSTIC_THRESHOLD = 1.0e-3
LEGACY_ABSOLUTE_TOLERANCE = 1.0e-5


def canonical_ranking(
    scores: Sequence[float],
    action_ids: Sequence[str],
    legal: Sequence[bool],
    tie_tolerance: float = TIE_TOLERANCE,
) -> list[int]:
    """Rank legal actions, resolving an anchor-relative tie group by ActionId."""
    ordered = sorted(
        (index for index, is_legal in enumerate(legal) if is_legal),
        key=lambda index: (-float(scores[index]), action_ids[index]),
    )
    result: list[int] = []
    cursor = 0
    while cursor < len(ordered):
        anchor = float(scores[ordered[cursor]])
        end = cursor + 1
        while end < len(ordered) and anchor - float(scores[ordered[end]]) < tie_tolerance:
            end += 1
        result.extend(sorted(ordered[cursor:end], key=lambda index: action_ids[index]))
        cursor = end
    return result


@dataclass(frozen=True)
class DecisionParityResult:
    sample_count: int
    top1_agreement_rate: float
    top3_set_agreement_rate: float
    ranking_agreement: float
    tie_break_deterministic: bool
    near_tie_sample_count: int
    tie_sample_count: int
    discordant_pairs: int
    compared_pairs: int

    @property
    def passed(self) -> bool:
        return (
            self.top1_agreement_rate == 1.0
            and self.top3_set_agreement_rate == 1.0
            and self.ranking_agreement == 1.0
            and self.tie_break_deterministic
        )

    def to_dict(self) -> dict[str, object]:
        return {**asdict(self), "verdict": "pass" if self.passed else "fail"}


def compare_decisions(
    reference_logits: np.ndarray,
    candidate_logits: np.ndarray,
    action_ids: Sequence[Sequence[str]],
    legal_mask: np.ndarray,
    tie_tolerance: float = TIE_TOLERANCE,
) -> DecisionParityResult:
    if reference_logits.shape != candidate_logits.shape or reference_logits.shape != legal_mask.shape:
        raise ValueError("reference, candidate, and legal-mask shapes must match")
    if reference_logits.shape[0] != len(action_ids):
        raise ValueError("action_ids sample count must match logits")
    top1 = top3 = 0
    concordant = discordant = 0
    deterministic = True
    near_ties = ties = 0
    for sample_index in range(reference_logits.shape[0]):
        legal_count = int(np.count_nonzero(legal_mask[sample_index]))
        ids = list(action_ids[sample_index])
        if len(ids) != legal_count:
            raise ValueError(f"sample {sample_index} action_ids={len(ids)}, legal={legal_count}")
        padded_ids = ids + [f"<PAD:{index}>" for index in range(len(ids), reference_logits.shape[1])]
        legal = legal_mask[sample_index].tolist()
        reference = canonical_ranking(reference_logits[sample_index], padded_ids, legal, tie_tolerance)
        candidate = canonical_ranking(candidate_logits[sample_index], padded_ids, legal, tie_tolerance)
        deterministic &= reference == canonical_ranking(reference_logits[sample_index], padded_ids, legal, tie_tolerance)
        deterministic &= candidate == canonical_ranking(candidate_logits[sample_index], padded_ids, legal, tie_tolerance)
        if reference and candidate and reference[0] == candidate[0]:
            top1 += 1
        if set(reference[:3]) == set(candidate[:3]):
            top3 += 1
        reference_positions = {action: position for position, action in enumerate(reference)}
        candidate_positions = {action: position for position, action in enumerate(candidate)}
        for left in range(len(reference)):
            for right in range(left + 1, len(reference)):
                first, second = reference[left], reference[right]
                if (reference_positions[first] - reference_positions[second]) * (candidate_positions[first] - candidate_positions[second]) > 0:
                    concordant += 1
                else:
                    discordant += 1
        legal_scores = sorted((float(reference_logits[sample_index, index]) for index in reference), reverse=True)
        if len(legal_scores) >= 2:
            margin = legal_scores[0] - legal_scores[1]
            near_ties += int(margin < NEAR_TIE_THRESHOLD)
            ties += int(margin < tie_tolerance)
    samples = reference_logits.shape[0]
    pairs = concordant + discordant
    return DecisionParityResult(
        sample_count=samples,
        top1_agreement_rate=top1 / max(samples, 1),
        top3_set_agreement_rate=top3 / max(samples, 1),
        ranking_agreement=(concordant - discordant) / max(pairs, 1),
        tie_break_deterministic=deterministic,
        near_tie_sample_count=near_ties,
        tie_sample_count=ties,
        discordant_pairs=discordant,
        compared_pairs=pairs,
    )


def inject_near_tie_perturbation(
    candidate_logits: np.ndarray,
    reference_logits: np.ndarray,
    action_ids: Sequence[Sequence[str]],
    legal_mask: np.ndarray,
    amount: float,
) -> tuple[np.ndarray, int, int]:
    """Add ``amount`` to the runner-up of the first near-tie sample."""
    modified = candidate_logits.copy()
    for sample_index in range(reference_logits.shape[0]):
        count = int(np.count_nonzero(legal_mask[sample_index]))
        ids = list(action_ids[sample_index])
        legal = legal_mask[sample_index, :count].tolist()
        ranking = canonical_ranking(reference_logits[sample_index, :count], ids, legal)
        if len(ranking) < 2:
            continue
        margin = float(reference_logits[sample_index, ranking[0]] - reference_logits[sample_index, ranking[1]])
        if margin < NEAR_TIE_THRESHOLD:
            modified[sample_index, ranking[1]] += amount
            return modified, sample_index, ranking[1]
    raise ValueError("fixture contains no near-tie sample for perturbation test")
