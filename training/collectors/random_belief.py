"""Exact NOSL probability helpers for finite random effects.

All functions operate on public-information multisets or explicitly supplied
weighted pools.  They never inspect a seed or RNG state.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from itertools import combinations
from math import comb
from typing import Any, Iterable, Mapping, Sequence


@dataclass(frozen=True)
class BeliefOutcome:
    label: str
    probability: float
    value: Any


def _check_distribution(outcomes: Sequence[BeliefOutcome]) -> tuple[BeliefOutcome, ...]:
    if not outcomes:
        return ()
    if any(item.probability < 0 for item in outcomes):
        raise ValueError("probabilities must be non-negative")
    total = sum(item.probability for item in outcomes)
    if total <= 0:
        raise ValueError("probability mass must be positive")
    return tuple(BeliefOutcome(item.label, item.probability / total, item.value) for item in outcomes)


def draw_one(multiset: Mapping[str, int]) -> tuple[BeliefOutcome, ...]:
    """Return exact without-replacement next-card outcomes."""
    counts = {str(key): int(value) for key, value in multiset.items() if int(value) > 0}
    total = sum(counts.values())
    if total <= 0:
        return ()
    outcomes = []
    for model_id in sorted(counts):
        next_counts = dict(counts)
        next_counts[model_id] -= 1
        if next_counts[model_id] == 0:
            del next_counts[model_id]
        outcomes.append(BeliefOutcome(model_id, counts[model_id] / total, tuple(sorted(next_counts.items()))))
    return _check_distribution(outcomes)


def draw_sequence(multiset: Mapping[str, int], count: int) -> tuple[BeliefOutcome, ...]:
    """Return exact ordered identity sequences and their probabilities."""
    if count < 0:
        raise ValueError("count must be non-negative")
    frontier: dict[tuple[tuple[str, int], ...], float] = {tuple(sorted((str(k), int(v)) for k, v in multiset.items() if int(v) > 0)): 1.0}
    labels: dict[tuple[tuple[str, int], ...], tuple[str, ...]] = {next(iter(frontier)): ()}
    for _ in range(count):
        next_frontier: dict[tuple[tuple[str, int], ...], float] = {}
        next_labels: dict[tuple[tuple[str, int], ...], set[tuple[str, ...]]] = {}
        for state, probability in frontier.items():
            for outcome in draw_one(dict(state)):
                target = outcome.value
                label = labels[state] + (outcome.label,)
                next_frontier[target] = next_frontier.get(target, 0.0) + probability * outcome.probability
                next_labels.setdefault(target, set()).add(label)
        frontier = next_frontier
        labels = {state: min(values) for state, values in next_labels.items()}
    return _check_distribution(tuple(BeliefOutcome(",".join(labels[state]), p, state) for state, p in frontier.items()))


def uniform_choice(items: Sequence[Any]) -> tuple[BeliefOutcome, ...]:
    """Return equally likely outcomes without consulting an RNG."""
    if not items:
        return ()
    probability = 1.0 / len(items)
    return tuple(BeliefOutcome(str(index), probability, value) for index, value in enumerate(items))


def random_combination(items: Sequence[Any], count: int) -> tuple[BeliefOutcome, ...]:
    """Return exact uniform unordered selection without replacement."""
    if count < 0 or count > len(items):
        return ()
    total = comb(len(items), count)
    if total == 0:
        return ()
    probability = 1.0 / total
    return tuple(
        BeliefOutcome(",".join(str(index) for index in indexes), probability,
                      tuple(items[index] for index in indexes))
        for indexes in combinations(range(len(items)), count)
    )


def weighted_pool(items: Iterable[tuple[Any, float]]) -> tuple[BeliefOutcome, ...]:
    """Return a normalized weighted pool for generated-card effects."""
    outcomes = tuple(BeliefOutcome(str(index), float(weight), value) for index, (value, weight) in enumerate(items))
    return _check_distribution(outcomes)
