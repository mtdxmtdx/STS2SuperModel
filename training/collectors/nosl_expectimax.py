"""Belief-state Expectimax for the NOSL offline teacher.

The solver is intentionally agnostic about game semantics.  Callers provide
legal public actions, a chance-distribution provider, terminal detection and
an objective evaluator.  No RNG state or realized hidden outcome is read by
this module.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable, Iterable, Protocol, Sequence


OBJECTIVES = ("Balanced", "HighestDamage", "MinimumLoss")


@dataclass(frozen=True)
class ChanceOutcome:
    label: str
    probability: float
    state: Any


class ChanceDistributionProvider(Protocol):
    def outcomes(self, state: Any, action: Any) -> Sequence[ChanceOutcome]: ...


class BeliefStateKeyProvider(Protocol):
    def key(self, state: Any) -> str: ...


@dataclass(frozen=True)
class NoslExpectimaxOptions:
    max_depth: int = 8
    top_k: int = 5
    max_nodes: int = 100_000_000
    tie_epsilon: float = 1e-6
    max_chance_branches: int = 100_000_000
    chance_sample_count: int = 32


@dataclass(frozen=True)
class ActionValue:
    action_id: str
    value: float
    rank: int


@dataclass(frozen=True)
class NoslTeacherResult:
    objectives: dict[str, dict[str, Any]]
    teacher_best_actions: list[str]
    teacher_top_k: list[ActionValue]
    action_values: dict[str, float]
    expanded_nodes: int
    search_complete: bool
    confidence: str
    label_quality: str


class NodeLimitExceeded(RuntimeError):
    pass


class NoslExpectimaxTeacher:
    def __init__(
        self,
        actions: Callable[[Any], Iterable[Any]],
        action_id: Callable[[Any], str],
        chance_provider: ChanceDistributionProvider,
        terminal: Callable[[Any], bool],
        evaluate: Callable[[Any, str], float],
        key_provider: BeliefStateKeyProvider | Callable[[Any], str],
        options: NoslExpectimaxOptions | None = None,
    ) -> None:
        self.actions = actions
        self.action_id = action_id
        self.chance_provider = chance_provider
        self.terminal = terminal
        self.evaluate = evaluate
        self.key_provider = key_provider
        self.options = options or NoslExpectimaxOptions()
        if (self.options.max_depth <= 0 or self.options.top_k <= 0 or self.options.max_nodes <= 0 or
                self.options.max_chance_branches <= 0 or self.options.chance_sample_count <= 0):
            raise ValueError("search limits must be positive")
        self.expanded_nodes = 0
        self._cache: dict[tuple[str, int, str], float] = {}

    def _key(self, state: Any) -> str:
        provider = self.key_provider
        return provider.key(state) if hasattr(provider, "key") else provider(state)

    def _visit(self) -> None:
        self.expanded_nodes += 1
        if self.expanded_nodes > self.options.max_nodes:
            raise NodeLimitExceeded

    def _value(self, state: Any, depth: int, objective: str) -> float:
        self._visit()
        if depth <= 0 or self.terminal(state):
            return float(self.evaluate(state, objective))
        cache_key = (self._key(state), depth, objective)
        if cache_key in self._cache:
            return self._cache[cache_key]
        best: float | None = None
        for action in self.actions(state):
            expected = self._action_value(state, action, depth, objective)
            if expected is None:
                continue
            best = expected if best is None else max(best, expected)
        result = float(self.evaluate(state, objective)) if best is None else best
        self._cache[cache_key] = result
        return result

    def _action_value(self, state: Any, action: Any, depth: int, objective: str) -> float | None:
        outcomes = list(self.chance_provider.outcomes(state, action))
        if not outcomes:
            return None
        if len(outcomes) > self.options.max_chance_branches:
            raise NodeLimitExceeded("chance branch limit exceeded")
        total = sum(float(outcome.probability) for outcome in outcomes)
        if any(outcome.probability < 0 for outcome in outcomes) or abs(total - 1.0) > self.options.tie_epsilon:
            raise ValueError("chance probabilities must be non-negative and sum to 1")
        return sum(float(outcome.probability) * self._value(outcome.state, depth - 1, objective)
                   for outcome in outcomes)

    def _rank(self, values: dict[str, float]) -> list[ActionValue]:
        ordered = sorted(values.items(), key=lambda item: (-item[1], item[0]))
        return [ActionValue(action_id, value, rank) for rank, (action_id, value) in enumerate(ordered[:self.options.top_k], 1)]

    def solve(self, initial_state: Any) -> NoslTeacherResult:
        self.expanded_nodes = 0
        self._cache.clear()
        complete = True
        objective_results: dict[str, dict[str, Any]] = {}
        for objective in OBJECTIVES:
            try:
                values = {}
                for action in self.actions(initial_state):
                    value = self._action_value(initial_state, action, self.options.max_depth, objective)
                    if value is not None:
                        values[self.action_id(action)] = value
            except NodeLimitExceeded:
                complete = False
                break
            ranking = self._rank(values)
            objective_results[objective] = {
                "best_actions": [ranking[0].action_id] if ranking else [],
                "value": ranking[0].value if ranking else 0.0,
                "action_values": values,
                "top_k": [item.__dict__ for item in ranking],
            }
        if not complete:
            return NoslTeacherResult({}, [], [], {}, self.expanded_nodes, False, "Uncalculable", "Uncalculable")
        balanced = objective_results["Balanced"]
        ranking = [ActionValue(**item) for item in balanced["top_k"]]
        return NoslTeacherResult(
            objective_results,
            balanced["best_actions"],
            ranking,
            balanced["action_values"],
            self.expanded_nodes,
            True,
            "Reliable",
            "ExactComplete",
        )
