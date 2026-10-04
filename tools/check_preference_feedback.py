#!/usr/bin/env python3
"""Check versioned analytical preferences with exact weights; never fit or simulate.

The CLI reads the fixed candidate profile and emits only an ignored evidence report.
Unspecified HP and inventory values stay unresolved. No formal labels are emitted.
"""
from __future__ import annotations

import argparse
from decimal import Decimal, localcontext
from fractions import Fraction
import hashlib
import json
import math
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
FEEDBACK_PATH = ROOT / "configs/preference_feedback_2026_10_02.json"
CANDIDATE_PATH = ROOT / "configs/objective_profile.candidate.json"
REPORT_DIR = ROOT / "artifacts/reports/m3-m6"
sys.path.insert(0, str(ROOT / "docs/spec/v4"))
from contracts.preference_contract import (  # noqa: E402
    ObjectiveProfile, ResultKind, TerminalOutcome, expected_terminal_cost,
)


class UnresolvedCase(ValueError):
    """Required scenario facts or resource values are absent, not zero."""


def rational(value) -> Fraction:
    if isinstance(value, bool) or not isinstance(value, (int, str, Decimal, Fraction)):
        raise ValueError("use finite exact integers, decimal strings or fractions")
    try:
        return Fraction(value)
    except (ValueError, ZeroDivisionError, OverflowError) as exc:
        raise ValueError("invalid exact number") from exc


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"), parse_float=Decimal)


def number(value: Fraction) -> dict:
    """Exact value is authoritative; the decimal is display-only."""
    value = rational(value)
    with localcontext() as context:
        context.prec = 28
        display = format(Decimal(value.numerator) / Decimal(value.denominator), ".12f")
    return {"exact": str(value), "decimal": display}


def hp_cost(initial_hp, terminal_hp, eta) -> Fraction:
    if initial_hp is None:
        raise UnresolvedCase("initial HP is not stated; common H0 must remain a parameter")
    start, end, eta = map(rational, (initial_hp, terminal_hp, eta))
    if start <= 0 or end < 0 or eta < 0:
        raise ValueError("invalid initial HP, terminal HP or downside coefficient")
    loss = start - end
    return loss + eta * max(loss, 0) ** 2 / max(1, start)


def expected_cost(initial_hp, outcomes, defeat_cost, eta, inventory_values=None) -> Fraction:
    """Exact hypothetical expectation; inventory coefficients already charge once.

    This is an arithmetic fixture, not a native terminal-vector admission API.
    Loss coefficients must already account for no retained inventory after death.
    """
    defeat, eta = map(rational, (defeat_cost, eta))
    if defeat < 0 or eta < 0:
        raise ValueError("negative objective coefficients")
    if initial_hp is None:
        raise UnresolvedCase("initial HP is not stated; common H0 must remain a parameter")
    if not outcomes:
        raise ValueError("empty distribution")
    inventory_values = {} if inventory_values is None else inventory_values
    total, mass = Fraction(0), Fraction(0)
    for row in outcomes:
        p, hp = rational(row["probability"]), rational(row["terminal_hp"])
        kind = row["terminal_kind"]
        if not 0 < p <= 1 or kind not in ("win", "loss"):
            raise ValueError("only positive exact terminal weights are admitted")
        if (kind == "win" and hp <= 0) or (kind == "loss" and hp != 0):
            raise ValueError("inconsistent hypothetical win/death HP")
        cost = hp_cost(initial_hp, hp, eta) + (defeat if kind == "loss" else 0)
        for item, coefficient in row["inventory_value_coefficients"].items():
            coefficient = rational(coefficient)
            if coefficient and item not in inventory_values:
                raise UnresolvedCase("unpriced inventory change: " + item)
            if coefficient:
                cost += coefficient * rational(inventory_values[item])
        mass += p
        total += p * cost
    if mass != 1:
        raise ValueError("exact probability mass must be one; never renormalize")
    return total


def fire_value_upper_bound(initial_hp, eta) -> Fraction:
    """Strict A preference needs v < bound; no value is assigned or fitted."""
    return hp_cost(initial_hp, 38, eta) - hp_cost(initial_hp, 50, eta)


def preference_result(cost_a: Fraction, cost_b: Fraction, preferred: str) -> str:
    if cost_a == cost_b:
        return "TIE_NOT_STRICT_PREFERENCE"
    return "AGREES" if (cost_a < cost_b) == (preferred == "A") else "REVERSED"


def validate_inputs(feedback: dict, profile: dict) -> dict:
    if feedback.get("schema") != "nosl.confirmed-preference-feedback.v1":
        raise ValueError("unsupported feedback schema")
    if feedback.get("feedback_id") != "nosl-preference-feedback-2026-10-02-v1":
        raise ValueError("unsupported feedback version")
    if profile.get("id") != feedback.get("candidate_objective_id"):
        raise ValueError("candidate objective identity mismatch")
    if profile.get("calibrated") is not False or feedback.get("calibrated") is not False:
        raise ValueError("this evidence cannot mark a candidate calibrated")
    if feedback.get("formal_label_profiles_admitted") != [] or feedback.get("formal_labels_allowed") is not False:
        raise ValueError("feedback cannot admit formal label profiles")
    if profile.get("inventoryValues") != {} or profile.get("permanentFutureValues") != {}:
        raise ValueError("this feedback version requires the unpriced candidate tables")
    if rational(profile["defeatCost"]) <= 0 or rational(profile["downsideCoefficient"]) < 0:
        raise ValueError("invalid candidate coefficients")
    if (feedback.get("evidence_kind") != "exact_hypothetical_preference_cases"
            or feedback.get("probabilities_are_assumptions") is not True
            or type(feedback.get("simulator_worlds")) is not int
            or feedback["simulator_worlds"] != 0):
        raise ValueError("analytical fixtures cannot be claimed as simulator probability evidence")
    cases = {case["id"]: case for case in feedback["cases"]}
    if len(feedback["cases"]) != 3 or set(cases) != {
        "death_risk_70hp", "fire_potion_50_vs_38", "rare_potion_50_vs_38"
    }:
        raise ValueError("unexpected or duplicated feedback cases")
    for case_id, preferred in (("death_risk_70hp", "B"), ("fire_potion_50_vs_38", "A"),
                               ("rare_potion_50_vs_38", "A")):
        case = cases[case_id]
        tentative = case_id.startswith("rare_")
        expected = {"strength": "tentative" if tentative else "confirmed_strict",
                    "preferred": preferred, "other": "A" if preferred == "B" else "B",
                    "hard_constraint": not tentative}
        if case.get("preference") != expected:
            raise ValueError("confirmed and tentative preference scopes must be preserved")
        if not feedback["transcript"].get(case.get("question")):
            raise ValueError("missing source question")
    death, fire, rare = (cases[k] for k in ("death_risk_70hp", "fire_potion_50_vs_38", "rare_potion_50_vs_38"))
    if death.get("initial_hp") != 70 or fire.get("initial_hp", "missing") is not None:
        raise ValueError("Q1 states 70 HP; Q2 must retain missing initial HP")
    if set(death["alternatives"]) != {"A", "B"}:
        raise ValueError("Q1 must compare exactly the two authored alternatives")
    for arm, expected in (("A", [(Fraction(1), "win", Fraction(50))]),
                          ("B", [(Fraction(99, 100), "win", Fraction(70)),
                                 (Fraction(1, 100), "loss", Fraction(0))])):
        rows = death["alternatives"][arm]
        actual = [(rational(row["probability"]), row["terminal_kind"], rational(row["terminal_hp"]))
                  for row in rows]
        if actual != expected or any(row["inventory_value_coefficients"] != {} for row in rows):
            raise ValueError("Q1 weighted terminal facts or unchanged resources differ from the question")
    if fire.get("conditions") != {"both_win": True, "better_later_use_in_this_combat": False,
                                  "automatic_replenishment": False}:
        raise ValueError("potion context changed")
    if fire.get("resource_id") != "FirePotion" or fire["constraint"].get("assigned_price", "missing") is not None:
        raise ValueError("FirePotion is specified but its value is not assigned")
    if fire.get("illustration") != {"initial_hp": 70, "initial_hp_is_confirmed": False,
                                   "purpose": "explicit assumption for illustrating the bound only"}:
        raise ValueError("70 HP is only an illustration for question 2")
    if (rare.get("hard_label", "missing") is not None or rare.get("assigned_price", "missing") is not None
            or rare.get("resource_id", "missing") is not None
            or rare.get("inherits_scenario") != fire["id"]):
        raise ValueError("tentative unspecified rare potion cannot acquire a hard label or price")
    return cases


def report(feedback: dict | None = None, profile: dict | None = None) -> dict:
    feedback = read_json(FEEDBACK_PATH) if feedback is None else feedback
    profile = read_json(CANDIDATE_PATH) if profile is None else profile
    cases = validate_inputs(feedback, profile)
    death, fire = cases["death_risk_70hp"], cases["fire_potion_50_vs_38"]
    k, eta = rational(profile["defeatCost"]), rational(profile["downsideCoefficient"])
    arms = death["alternatives"]
    # Derive the affine inequality independently from exact weighted outcomes.
    def delta(defeat, downside):
        return expected_cost(70, arms["A"], defeat, downside) - expected_cost(70, arms["B"], defeat, downside)
    intercept = delta(0, 0)
    k_slope, eta_slope = delta(1, 0) - intercept, delta(0, 1) - intercept
    if k_slope != Fraction(-1, 100):
        raise ValueError("Q1 death probability or alternatives changed")
    constant, multiplier = -intercept / k_slope, -eta_slope / k_slope
    constraint = death["constraint"]
    if (constraint.get("variable") != "defeatCost" or constraint.get("relation") != "<"
            or constant != rational(constraint["constant"])
            or multiplier != rational(constraint["downsideCoefficient_multiplier"])):
        raise ValueError("Q1 stored constraint disagrees with exact weighted algebra")
    costs = {arm: expected_cost(70, rows, k, eta) for arm, rows in arms.items()}
    risk_status = preference_result(costs["A"], costs["B"], "B")
    if (risk_status == "AGREES") != (k < constant + multiplier * eta):
        raise ValueError("Q1 inequality and exact expectation disagree")
    # Cross-check the frozen objective implementation, with no resources in Q1.
    contract_profile = ObjectiveProfile(float(k), float(eta), calibrated=False)
    for arm, rows in arms.items():
        outcomes = [TerminalOutcome(float(rational(row["probability"])), ResultKind(row["terminal_kind"]),
                                    float(rational(row["terminal_hp"]))) for row in rows]
        actual = expected_terminal_cost(70, outcomes, contract_profile)
        if not math.isclose(actual, float(costs[arm]), rel_tol=1e-12, abs_tol=1e-12):
            raise ValueError("frozen objective and exact Q1 expectation disagree")
    expected_fire = {
        "A": [{"probability": "1", "terminal_kind": "win", "terminal_hp": 50,
               "inventory_value_coefficients": {"FirePotion": 1}}],
        "B": [{"probability": "1", "terminal_kind": "win", "terminal_hp": 38,
               "inventory_value_coefficients": {}}],
    }
    if fire["alternatives"] != expected_fire:
        raise ValueError("Q2 terminal facts or single inventory charge changed")
    if (fire["constraint"].get("relation") != "<" or fire["constraint"].get("upper_bound") !=
            "12 + eta/H0 * (max(H0-38,0)^2 - max(H0-50,0)^2)"):
        raise ValueError("Q2 parameterized strict bound changed")
    illustration_bound = fire_value_upper_bound(fire["illustration"]["initial_hp"], eta)
    return {
        "schema": "nosl.preference-feedback-check.v1",
        "feedback_id": feedback["feedback_id"],
        "method": "exact_weighted_synthetic_distributions_and_algebra",
        "checks_passed": risk_status == "AGREES",
        "candidate": {"id": profile["id"], "defeat_cost": number(k),
                      "downside_coefficient": number(eta), "calibrated": False},
        "hard_preference_case_count": 2,
        "numerically_evaluated_hard_case_count": 1,
        "death_risk": {
            "status": risk_status, "preferred": "B", "cost_A": number(costs["A"]),
            "cost_B": number(costs["B"]), "strict_K_upper_bound": number(constant + multiplier * eta),
            "constraint": "K < 1930 + (3510/7) * eta", "equality_is_tie": True,
            "scope": "this stated 70-HP comparison only; no unique coefficient or global 1% risk budget",
        },
        "fire_potion": {
            "status": "CONFIRMED_PREFERENCE_PARAMETERIZED_VALUE_BOUND",
            "candidate_numerical_ranking": "UNRESOLVED_INITIAL_HP_AND_INVENTORY_VALUE",
            "preferred": "A", "initial_hp": None,
            "constraint": "v_FirePotion < " + fire["constraint"]["upper_bound"],
            "sufficient_not_necessary": "v_FirePotion < 12 for every H0 > 0 and eta >= 0",
            "illustration": {"assumed_initial_hp": 70, "initial_hp_confirmed": False,
                             "strict_value_upper_bound": number(illustration_bound)},
            "assigned_price": None, "inventory_charge_count": 1,
            "nine_hp_gate": "separate consideration gate; neither price nor forced use",
        },
        "rare_potion": {"status": "TENTATIVE_ONLY", "leaning": "A", "hard_constraint": False,
                        "hard_label": None, "assigned_price": None},
        "simulator_worlds": 0, "probability_evidence": False, "unique_objective_identified": False,
        "formal_label_profiles_admitted": [], "formal_labels_allowed": False,
        "training_authorized_by_report": False,
        "calibration_status": "PARTIAL_PREFERENCE_CONSTRAINTS_ONLY",
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=REPORT_DIR / "preference-feedback-2026-10-02.json")
    args = parser.parse_args(argv)
    destination = args.output.resolve()
    if not destination.is_relative_to(REPORT_DIR.resolve()):
        parser.error("report output must stay under ignored artifacts/reports/m3-m6")
    result = report()
    result["sources"] = {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (FEEDBACK_PATH, CANDIDATE_PATH,
                                      Path(__file__).resolve(),
                                      ROOT / "docs/spec/v4/contracts/preference_contract.py")}
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(json.dumps({"checks_passed": result["checks_passed"], "report": str(destination.relative_to(ROOT)),
                      "formal_labels_allowed": False}, separators=(",", ":")))
    return 0 if result["checks_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
