from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent / "collectors"))
from nosl_expectimax import ChanceOutcome, NoslExpectimaxOptions, NoslExpectimaxTeacher


@dataclass(frozen=True)
class State:
    value: float
    terminal: bool = False


class Provider:
    def outcomes(self, state: State, action: str):
        if state.terminal:
            return []
        if action == "safe":
            return [ChanceOutcome("fixed", 1.0, State(4.0, True))]
        return [ChanceOutcome("high", 0.5, State(10.0, True)), ChanceOutcome("low", 0.5, State(-10.0, True))]


class Keys:
    def key(self, state: State) -> str:
        return f"value={state.value}|terminal={state.terminal}"


def test_nosl_expectimax_uses_expected_value_and_top_k():
    teacher = NoslExpectimaxTeacher(
        actions=lambda state: ["risky", "safe"] if not state.terminal else [],
        action_id=lambda action: action,
        chance_provider=Provider(),
        terminal=lambda state: state.terminal,
        evaluate=lambda state, objective: state.value,
        key_provider=Keys(),
        options=NoslExpectimaxOptions(max_depth=2, top_k=2),
    )
    result = teacher.solve(State(0.0))
    assert result.search_complete is True
    assert result.confidence == "Reliable"
    assert result.teacher_best_actions == ["safe"]
    assert result.action_values["safe"] == 4.0
    assert result.action_values["risky"] == 0.0
    assert result.teacher_top_k[0].action_id == "safe"


def test_chance_branch_identity_is_part_of_state_and_recomputed_after_chance():
    class BranchProvider:
        def outcomes(self, state: State, action: str):
            if state.terminal:
                return []
            return [ChanceOutcome("a", 0.5, State(2.0, True)), ChanceOutcome("b", 0.5, State(8.0, True))]

    teacher = NoslExpectimaxTeacher(
        actions=lambda state: ["draw"] if not state.terminal else [],
        action_id=lambda action: action,
        chance_provider=BranchProvider(),
        terminal=lambda state: state.terminal,
        evaluate=lambda state, objective: state.value,
        key_provider=Keys(),
        options=NoslExpectimaxOptions(max_depth=2, top_k=1),
    )
    result = teacher.solve(State(0.0))
    assert result.action_values["draw"] == 5.0
