"""NOSL v4 preference-contract demonstration, not a game simulator or teacher.

Uses exact synthetic outcome distributions to test the proposed terminal objective.
The coefficients are provisional engineering values, NOT fitted user preferences.
No actual game rules, hidden-state sampler, neural network, or training are included.
Python 3.10+; standard library only.
"""
from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from math import isclose, isfinite
from typing import Iterable


class ResultKind(str, Enum):
    WIN = "win"
    LOSS = "loss"
    COMPUTE_TRUNCATED = "compute_truncated"
    ENGINE_ERROR = "engine_error"


@dataclass(frozen=True)
class ObjectiveProfile:
    # Illustrative coefficients only: calibration is a production-readiness gate.
    defeat_cost: float = 1000.0
    downside_coefficient: float = 0.2
    calibrated: bool = False

    def __post_init__(self) -> None:
        if not isfinite(self.defeat_cost) or self.defeat_cost <= 0:
            raise ValueError("defeat_cost must be finite and positive")
        if not isfinite(self.downside_coefficient) or self.downside_coefficient < 0:
            raise ValueError("downside_coefficient must be finite and nonnegative")

    def assert_production_ready(self) -> None:
        if not self.calibrated:
            raise RuntimeError("Objective coefficients are not calibrated for production")


@dataclass(frozen=True)
class TerminalOutcome:
    probability: float
    kind: ResultKind
    terminal_hp: float
    # Difference in a frozen inventory value: start value minus terminal value.
    # This is NOT both a per-use penalty and another terminal inventory penalty.
    inventory_value_lost: float = 0.0
    # Value of permanent changes EXCLUDING their already-counted current-HP gain.
    permanent_future_value: float = 0.0

    def __post_init__(self) -> None:
        if not isinstance(self.kind, ResultKind):
            raise TypeError("kind must be a ResultKind")
        values = (self.probability, self.terminal_hp, self.inventory_value_lost,
                  self.permanent_future_value)
        if not all(isfinite(x) for x in values):
            raise ValueError("Outcome fields must be finite")
        if not 0 <= self.probability <= 1 or self.terminal_hp < 0:
            raise ValueError("Invalid probability or terminal HP")


def expected_terminal_cost(
    start_hp: float,
    outcomes: Iterable[TerminalOutcome],
    profile: ObjectiveProfile = ObjectiveProfile(),
) -> float:
    """Lower is better. No time discount; only settled complete outcomes accepted.

    Loss is start_hp - terminal_hp, so legitimate net healing has negative loss.
    start_hp and the associated scale must stay fixed throughout a combat.
    The caller must never discard incomplete rollouts before calling this function.
    This function cannot detect omitted records: the sampling ledger must do so.
    """
    if not isfinite(start_hp) or start_hp <= 0:
        raise ValueError("start_hp must be finite and positive")
    rows = tuple(outcomes)
    if not rows:
        raise ValueError("An outcome distribution is required")
    if any(x.kind not in (ResultKind.WIN, ResultKind.LOSS) for x in rows):
        raise ValueError("Incomplete or erroneous rollouts cannot be scored as terminal labels")
    if not isclose(sum(x.probability for x in rows), 1.0, abs_tol=1e-9, rel_tol=0):
        raise ValueError("Probabilities must sum to one; do not renormalize away failures")
    scale = max(1.0, start_hp)
    total = 0.0
    for row in rows:
        loss = start_hp - row.terminal_hp
        downside = profile.downside_coefficient * max(loss, 0.0) ** 2 / scale
        failure = profile.defeat_cost if row.kind == ResultKind.LOSS else 0.0
        # A defeated player does not retain a useful future permanent reward.
        future_bonus = row.permanent_future_value if row.kind == ResultKind.WIN else 0.0
        total += row.probability * (
            failure + loss + downside + row.inventory_value_lost - future_bonus
        )
    return total


@dataclass(frozen=True)
class Interval:
    lower: float
    upper: float

    def __post_init__(self) -> None:
        if not isfinite(self.lower) or not isfinite(self.upper) or self.lower > self.upper:
            raise ValueError("Invalid interval")


class Eligibility(str, Enum):
    ELIGIBLE = "eligible_not_mandatory"
    INELIGIBLE = "ineligible"
    UNRESOLVED = "unresolved"


def extra_benefit_eligibility(
    extra_expected_loss: Interval,
    specified_success_probability: Interval,
    safety_acceptable: bool | None,
    *,
    loss_budget: float = 5.0,
    success_floor: float = 0.8,
) -> Eligibility:
    """Gate a next-player-turn specified-finish plan, not arbitrary healing stall.

    Intervals must be valid for the estimation/stopping procedure used upstream.
    Exact toy probabilities may be supplied as degenerate intervals.
    Eligibility does not require the action to be chosen; compare other actions too.
    The loss budget belongs to the whole anchored plan, never a fresh per-turn budget.
    """
    if not isfinite(loss_budget) or loss_budget < 0:
        raise ValueError("loss_budget must be finite and nonnegative")
    if not 0 <= success_floor <= 1:
        raise ValueError("success_floor must be in [0,1]")
    if not 0 <= specified_success_probability.lower <= specified_success_probability.upper <= 1:
        raise ValueError("Probability interval must lie in [0,1]")
    if safety_acceptable not in (True, False, None):
        raise TypeError("safety_acceptable must be bool or None")
    if safety_acceptable is False:
        return Eligibility.INELIGIBLE
    if (extra_expected_loss.lower > loss_budget or
            specified_success_probability.upper < success_floor):
        return Eligibility.INELIGIBLE
    if (safety_acceptable is True and extra_expected_loss.upper <= loss_budget and
            specified_success_probability.lower >= success_floor):
        return Eligibility.ELIGIBLE
    return Eligibility.UNRESOLVED


def potion_reference_eligibility(
    whole_combat_hp_saved: Interval,
    *,
    verified_rescue_need: bool = False,
    reference_hp: float = 9.0,
) -> Eligibility:
    """General consideration threshold only, not a command to drink immediately.

    More valuable resources can carry a different declared reference value.
    Whole-combat counterfactuals must use comparable public-information policies.
    """
    if not isfinite(reference_hp) or reference_hp < 0:
        raise ValueError("reference_hp must be finite and nonnegative")
    if verified_rescue_need:
        return Eligibility.ELIGIBLE
    if whole_combat_hp_saved.upper < reference_hp:
        return Eligibility.INELIGIBLE
    if whole_combat_hp_saved.lower >= reference_hp:
        return Eligibility.ELIGIBLE
    return Eligibility.UNRESOLVED
