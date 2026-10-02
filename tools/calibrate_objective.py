#!/usr/bin/env python3
"""Reproducible M3 synthetic sensitivity and read-only archived-outcome audits; never fits a profile.

Imports the frozen V4 contract rather than reimplementing game rules. The report cannot
mark calibration complete and is not a generator of simulator training labels.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import math
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "docs/spec/v4"))
from contracts.preference_contract import (  # noqa: E402
    Eligibility, Interval, ObjectiveProfile, ResultKind, TerminalOutcome,
    expected_terminal_cost, extra_benefit_eligibility, potion_reference_eligibility,
)


def exact_cost(losses: list[tuple[float, float]], eta: float = .2, defeat: float = 1000) -> float:
    return expected_terminal_cost(60, [TerminalOutcome(p, ResultKind.WIN, 60 - loss)
                                      for p, loss in losses], ObjectiveProfile(defeat, eta))


def report() -> dict:
    sensitivity = []
    for eta in [0, .01, .1, .2, .5, 1, 2, 5, 10, 12]:
        mixture = exact_cost([(.9, 0), (.1, 30)], eta)
        fixed3 = exact_cost([(1, 3)], eta)
        fixed8 = exact_cost([(1, 8)], eta)
        sensitivity.append(dict(eta=eta, mixture_cost=mixture, fixed3_cost=fixed3, fixed8_cost=fixed8,
                                mixture_preferred_to_fixed8=mixture < fixed8,
                                fixed3_preferred_to_mixture=fixed3 < mixture))
    risk = []
    for defeat in [100, 1000, 10000]:
        for death_probability in [.0001, .001, .01]:
            cost = expected_terminal_cost(60, [
                TerminalOutcome(1 - death_probability, ResultKind.WIN, 60),
                TerminalOutcome(death_probability, ResultKind.LOSS, 0),
            ], ObjectiveProfile(defeat, .2))
            safe = exact_cost([(1, 8)], defeat=defeat)
            risk.append(dict(defeat_cost=defeat, death_probability=death_probability,
                             risky_cost=cost, safe_fixed8_cost=safe, provisional_prefers_risky=cost < safe,
                             user_preference_known=False))
    boundaries = []
    for saved, expected in [((9, 9), Eligibility.ELIGIBLE), ((8.99, 8.99), Eligibility.INELIGIBLE),
                            ((8.9, 9.1), Eligibility.UNRESOLVED)]:
        got = potion_reference_eligibility(Interval(*saved))
        assert got == expected
        boundaries.append(dict(case="potion", hp_saved_interval=saved, status=got.value))
    for extra, success, safety, expected in [
        ((5, 5), (.8, .8), True, Eligibility.ELIGIBLE),
        ((5.01, 5.01), (1, 1), True, Eligibility.INELIGIBLE),
        ((4, 4), (.799, .799), True, Eligibility.INELIGIBLE),
        ((4.9, 5.1), (.8, .9), True, Eligibility.UNRESOLVED),
        ((4, 4), (.79, .81), True, Eligibility.UNRESOLVED),
        ((4, 4), (.9, .9), None, Eligibility.UNRESOLVED),
        ((4, 4), (.9, .9), False, Eligibility.INELIGIBLE),
    ]:
        got = extra_benefit_eligibility(Interval(*extra), Interval(*success), safety)
        assert got == expected
        boundaries.append(dict(case="anchored_specified_finish", extra_loss_interval=extra,
                               success_interval=success, safety=safety, status=got.value))
    candidate = next(x for x in sensitivity if x["eta"] == .2)
    assert candidate["mixture_preferred_to_fixed8"] and candidate["fixed3_preferred_to_mixture"]
    try:
        ObjectiveProfile().assert_production_ready()
    except RuntimeError:
        gate_blocks = True
    else:
        raise AssertionError("Uncalibrated production gate unexpectedly opened")
    source = ROOT / "docs/spec/v4/contracts/preference_contract.py"
    return {
        "schema": "nosl.objective-calibration-evidence.v1",
        "method": "exact_synthetic_distributions_no_parameter_fitting",
        "contract_source": str(source.relative_to(ROOT)),
        "contract_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "candidate": {"defeat_cost": 1000, "downside_coefficient": .2, "calibrated": False},
        "confirmed_examples_preserved": True,
        "formal_label_gate_blocks_candidate": gate_blocks,
        "eta_sensitivity": sensitivity,
        "identified_family_constraints": {
            "eta_strict_lower": 0, "eta_strict_upper": 150 / 13,
            "K_identified_by_two_all_win_examples": False,
            "interpretation": "Within this proposed quadratic family only: 0 < eta < 150/13 preserves both strict examples; it does not choose a coefficient or validate the family",
        },
        "fixed_sample_success_illustrations": [
            {"successes": successes, "n": n, "alpha": .05,
             "hoeffding_lower": max(0, successes / n - math.sqrt(math.log(40) / (2 * n))),
             "hoeffding_upper": min(1, successes / n + math.sqrt(math.log(40) / (2 * n))),
             "exact_probability_claimed": False}
            for successes, n in ((4, 4), (80, 100))
        ],
        "micro_death_risk_sensitivity_not_user_calibration": risk,
        "boundary_checks": boundaries,
        "boundary_check_count": len(boundaries),
        "calibration_status": "BLOCKED_PENDING_PREFERENCE_AND_RESOURCE_EVIDENCE",
        "finite_sample_rule": "fixed-predeclared-N Hoeffding intervals require valid support; exact .8 is eligible but sampled 80/100 remains unresolved",
        "simulator_evidence": {
            "test_source": "tests/Nosl.Tests/ObjectiveTests.cs",
            "tests_declared": [
                "RealSimulator_FiveHpWholePlanDifferenceAndSamePotionTimingCancel",
                "RealSimulator_AutomaticHealingAndMaxHpRemainSeparateFromFutureValue",
                "RealSimulator_RescueBelowNineIsConsiderationAndActualLossIsDistinct",
            ],
            "execution_status": "NOT_ASSERTED_BY_THIS_SYNTHETIC_TOOL_SEE_BUILD_TEST_REPORT",
        },
        "unresolved": [
            "K and eta are engineering candidates; examples do not identify unique risk preferences",
            "all potion inventory and permanent-future values require versioned evidence; 9 is not a universal liquidation price",
            "micro-death-risk tradeoffs have no confirmed target",
            "safe multi-turn healing templates require their own value/safety evidence",
            "arbitrary conditional bonus-budget allocation and general loop macros are unsupported",
            "original-client differential fidelity and full Silent A10 coverage remain separate gates",
        ],
        "formal_labels_allowed": False,
        "formal_training_started": False,
    }



def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def inventory_counts(rows):
    if not isinstance(rows, list):
        raise ValueError("missing inventory snapshot")
    result = Counter()
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get("resourceId"), str) or not row["resourceId"]:
            raise ValueError("invalid inventory identity")
        count = row.get("count")
        if not isinstance(count, int) or isinstance(count, bool) or count < 0:
            raise ValueError("invalid inventory count")
        result[row["resourceId"]] += count
    return result


def outcome_facts(outcome):
    """Audit saved facts with empty candidate value tables; this does not execute game rules."""
    kind = outcome.get("terminalKind")
    if kind not in ("Win", "Loss"):
        if kind not in ("ComputeTruncated", "EngineError", "PolicyNonterminating"):
            raise ValueError("invalid terminal kind")
        return {"complete": False, "unresolved": [kind], "cost": None}
    start, end = outcome.get("hpAtCombatStart"), outcome.get("hpAfterSettlement")
    max_start, max_end = outcome.get("maxHpStart"), outcome.get("maxHpAfterSettlement")
    if not all(isinstance(x, int) and not isinstance(x, bool) for x in (start, end, max_start, max_end)):
        raise ValueError("missing or noninteger terminal HP")
    alive = outcome.get("playerAlive")
    if not (0 < start <= max_start and 0 <= end <= max_end and max_end > 0
            and type(alive) is bool and alive == (end > 0) and (kind != "Win" or alive)):
        raise ValueError("inconsistent terminal HP/alive facts")
    if outcome.get("settlementComplete") is not True or not outcome.get("settlementProfileId") or not outcome.get("continuationPolicyId"):
        raise ValueError("missing verified settlement/continuation")
    if outcome.get("hpEventDiagnosticsComplete") is True:
        damage, healing, adjustment = (outcome.get(k) for k in
            ("cumulativeHpDamage", "healingReceived", "otherHpAdjustment"))
        if not all(finite_number(x) for x in (damage, healing, adjustment)) or damage < 0 or healing < 0:
            raise ValueError("missing or invalid complete HP diagnostics")
        if start - damage + healing + adjustment != end:
            raise ValueError("complete HP diagnostics do not reconcile")
    initial, final = inventory_counts(outcome.get("inventoryStart")), inventory_counts(outcome.get("inventoryEnd"))
    unresolved = []
    if outcome.get("inventorySnapshotsComplete") is not True:
        unresolved.append("inventory_snapshots_incomplete")
    for item in sorted(initial.keys() | final.keys()):
        difference = initial[item] - (final[item] if kind == "Win" else 0)
        if difference:
            unresolved.append("inventory_value_unresolved:" + item)
    changes = outcome.get("permanentChanges")
    if not isinstance(changes, list):
        raise ValueError("missing permanent change ledger")
    totals = Counter()
    for change in changes:
        if not isinstance(change.get("kind"), str) or not finite_number(change.get("amount")):
            raise ValueError("invalid permanent change")
        totals[change["kind"]] += change["amount"]
    if outcome.get("permanentChangesComplete") is not True:
        unresolved.append("permanent_change_ledger_incomplete")
    if totals["max_hp"] != max_end - max_start:
        unresolved.append("permanent_change_not_recorded:max_hp")
    if kind == "Win":
        unresolved.extend("permanent_future_value_unresolved:" + key for key, amount in sorted(totals.items()) if amount)
    cost = None if unresolved else expected_terminal_cost(start, [TerminalOutcome(1, ResultKind(kind.lower()), end)])
    return {"complete": True, "unresolved": unresolved, "cost": cost,
            "loss": start - end, "downside_unit": max(start - end, 0) ** 2 / max(1, start),
            "failure": int(kind == "Loss"), "initial": initial, "final": final}


def audit_records(records, attempts=()):
    """Reconcile every saved action/world and mask. Returns descriptive, never calibration, evidence."""
    records, attempts = list(records), list(attempts)
    counts, terminal, quality, unresolved = Counter(), Counter(), Counter(), Counter()
    inventories, resource_events, permanent = Counter(), Counter(), Counter()
    versions, groups, indices, source_battles = defaultdict(set), set(), set(), set()
    failures = []
    roots_for_sensitivity, potion_contrasts = [], []
    costs_errors = []
    def check(condition, code, root, action=None):
        counts["checks"] += 1
        if not condition:
            counts["failed_checks"] += 1
            if len(failures) < 50:
                failures.append({"check": code, "root": root, "action_index": action})
    for root_index, record in enumerate(records):
        audit, public, targets = record["audit_only"], record["public_input"], record["targets"]["actions"]
        counts["roots"] += 1
        groups.add(audit["source_run_group"])
        source_battles.add(audit["source_combat_id"])
        index = audit.get("generation_source_index", root_index)
        check(index not in indices, "duplicate_source_index", root_index); indices.add(index)
        for key, value in audit.get("versions", {}).items():
            versions[key].add(str(value))
        check(audit.get("objective_calibrated") is False, "candidate_marked_calibrated", root_index)
        check(audit.get("objective_version") == "nosl_silent_a10_terminal_v4_candidate", "unsupported_objective_version", root_index)
        candidates = public["candidate_actions"]
        samples = audit.get("outcome_samples", [])
        raw = {x["action_index"]: x["outcomes"] for x in samples}
        check(len(raw) == len(samples) == len(targets) == len(candidates), "candidate_ledger_mismatch", root_index)
        check(set(raw) == set(range(len(candidates))), "candidate_indices_mismatch", root_index)
        evaluation_seeds = audit.get("sampler_seeds", [])
        check(len(evaluation_seeds) == len(set(evaluation_seeds)) == audit["n_independent_eval"], "fixed_eval_sample_count", root_index)
        check(not set(evaluation_seeds).intersection(audit.get("exploration_seeds", [])), "exploration_eval_overlap", root_index)
        counts["root_world_draws"] += len(evaluation_seeds)
        observation = public["observation"]
        counts["roots_with_changed_current_hp"] += observation.get("hp") != observation.get("startHp")
        root_action_moments, audited = [], {}
        all_resolved = True
        for target in targets:
            action_index = target["action_index"]
            outcomes = raw.get(action_index, [])
            counts["actions"] += 1
            quality[target["quality"]] += 1
            check(len(outcomes) == target["allocated_worlds"] == audit["n_independent_eval"] > 0,
                  "allocated_world_conservation", root_index, action_index)
            check(target["allocated_worlds"] == sum(target.get(k, 0) for k in
                  ("completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")),
                  "outcome_kind_conservation", root_index, action_index)
            facts = []
            for outcome in outcomes:
                counts["outcomes"] += 1
                terminal[outcome.get("terminalKind", "MISSING")] += 1
                check(outcome.get("hpAtCombatStart") == observation["startHp"], "combat_start_anchor_changed", root_index, action_index)
                for flag in ("inventorySnapshotsComplete", "permanentChangesComplete", "resourceProvenanceComplete", "hpEventDiagnosticsComplete"):
                    counts[flag + "_true"] += outcome.get(flag) is True
                counts["healing_received_missing"] += outcome.get("healingReceived") is None
                counts["bonus_fields_all_present"] += all(outcome.get(k) is not None for k in ("specifiedFinishSuccess", "earnedBonus", "deadlineMet"))
                try:
                    fact = outcome_facts(outcome)
                except (ValueError, KeyError, TypeError) as exc:
                    check(False, "invalid_outcome:" + str(exc), root_index, action_index)
                    fact = {"complete": False, "cost": None, "unresolved": ["invalid_outcome"]}
                facts.append(fact)
                unresolved.update(fact["unresolved"])
                if not fact["complete"]:
                    continue
                counts["net_healing_outcomes"] += fact["loss"] < 0
                counts["max_hp_changed_outcomes"] += outcome["maxHpStart"] != outcome["maxHpAfterSettlement"]
                for item in fact["initial"]:
                    inventories[item] += 1
                for event in outcome.get("resourceEvents", []):
                    resource_events[event["kind"] + ":" + event["resourceId"]] += event["quantity"]
                permanent.update(change["kind"] for change in outcome.get("permanentChanges", []) if change["amount"])
                start_json, end_json = outcome.get("persistentAssetsAtStartJson"), outcome.get("persistentAssetsAfterSettlementJson")
                if start_json is None or end_json is None:
                    counts["persistent_asset_snapshots_missing"] += 1
                else:
                    try:
                        start_assets, end_assets = json.loads(start_json), json.loads(end_json)
                        counts["persistent_asset_snapshots_changed"] += start_assets != end_assets
                    except (ValueError, TypeError):
                        check(False, "invalid_persistent_asset_snapshot", root_index, action_index)
            complete = bool(facts) and all(x["complete"] for x in facts)
            resolved = complete and all(x["cost"] is not None for x in facts)
            all_resolved &= resolved
            expected_quality = "complete" if resolved else "objective_value_unresolved" if complete else "unresolved"
            check(target.get("quality") == expected_quality, "result_quality_mismatch", root_index, action_index)
            check(target["masks"].get("value") is resolved, "utility_mask_mismatch", root_index, action_index)
            if resolved:
                expected_value = -sum(x["cost"] for x in facts) / len(facts)
                correct = finite_number(target.get("value")) and math.isclose(target["value"], expected_value, rel_tol=1e-10, abs_tol=1e-9)
                check(correct, "terminal_utility_mismatch", root_index, action_index)
                if finite_number(target.get("value")):
                    costs_errors.append(abs(target["value"] - expected_value))
                counts["reconciled_utility_actions"] += 1
                root_action_moments.append((action_index, sum(x["failure"] for x in facts) / len(facts),
                                           sum(x["loss"] for x in facts) / len(facts),
                                           sum(x["downside_unit"] for x in facts) / len(facts)))
            else:
                check(target.get("value") is None, "unresolved_value_must_be_null", root_index, action_index)
                counts["masked_utility_actions"] += 1
            for key in ("win_probability", "death_probability", "expected_final_hp", "hp_distribution", "potion_net_change"):
                check(target["masks"].get(key) is complete, "auxiliary_mask_mismatch:" + key, root_index, action_index)
                if not complete:
                    check(target.get(key) is None, "incomplete_auxiliary_must_be_null:" + key, root_index, action_index)
            if complete:
                for key, expected in {
                    "win_probability": sum(o["terminalKind"] == "Win" for o in outcomes) / len(outcomes),
                    "death_probability": sum(not o["playerAlive"] for o in outcomes) / len(outcomes),
                    "expected_final_hp": sum(o["hpAfterSettlement"] for o in outcomes) / len(outcomes),
                    "potion_net_change": sum(sum(x["final"].values()) - sum(x["initial"].values()) for x in facts) / len(facts),
                }.items():
                    check(finite_number(target.get(key)) and math.isclose(target[key], expected, abs_tol=1e-9),
                          "terminal_target_mismatch:" + key, root_index, action_index)
                histogram = Counter(o["hpAfterSettlement"] for o in outcomes)
                distribution = target.get("hp_distribution")
                distribution_valid = isinstance(distribution, list) and len(distribution) == len(histogram)
                if distribution_valid:
                    found = {}
                    for atom in distribution:
                        if not isinstance(atom, dict) or not finite_number(atom.get("hp")) or not finite_number(atom.get("probability")):
                            distribution_valid = False; break
                        if atom["hp"] in found:
                            distribution_valid = False; break
                        found[atom["hp"]] = atom["probability"]
                    distribution_valid &= set(found) == set(histogram) and all(
                        math.isclose(found[hp], count / len(outcomes), abs_tol=1e-9) for hp, count in histogram.items())
                check(distribution_valid, "terminal_hp_distribution_mismatch", root_index, action_index)
                audited[action_index] = (outcomes, facts)
        counts["all_candidate_utility_roots"] += all_resolved
        if all_resolved:
            roots_for_sensitivity.append(root_action_moments)
        # All preserving alternatives are reported. No sample-selected best baseline and no eligibility claim.
        for potion_index, action in enumerate(candidates):
            if action.get("kind") != "potion" or potion_index not in audited:
                continue
            used, used_facts = audited[potion_index]
            for hold_index, hold_action in enumerate(candidates):
                if hold_action.get("kind") in ("potion", "discard_potion") or hold_index not in audited:
                    continue
                held, held_facts = audited[hold_index]
                if any(x["initial"] != x["final"] for x in held_facts):
                    continue
                deltas = [a["hpAfterSettlement"] - b["hpAfterSettlement"] for a, b in zip(used, held)]
                potion_contrasts.append({"source_index": index, "potion_action": potion_index, "hold_action": hold_index,
                    "potion_id": observation["potions"][action["slot"]], "n": len(deltas),
                    "sample_mean_hp_improvement": sum(deltas) / len(deltas), "sample_min": min(deltas), "sample_max": max(deltas),
                    "use_losses": sum(a["terminalKind"] == "Loss" for a in used), "hold_losses": sum(a["terminalKind"] == "Loss" for a in held),
                    "eligibility": "UNRESOLVED_NO_CERTIFIED_SUPPORT_OR_FIXED_BASELINE_PLAN"})
    attempts_by_status = Counter(x.get("status", "MISSING") for x in attempts)
    failed_attempts = Counter(x.get("reason", "MISSING").split(":", 1)[0] for x in attempts if x.get("status") != "accepted")
    if attempts:
        check(attempts_by_status["accepted"] == len(records), "accepted_attempt_record_conservation", "all")
    def argmins(moments, k, eta):
        costs = [(index, k * failure + loss + eta * downside) for index, failure, loss, downside in moments]
        minimum = min(value for _, value in costs)
        return tuple(index for index, value in costs if math.isclose(value, minimum, rel_tol=0, abs_tol=1e-9))
    sensitivity = []
    for k in (100, 1000, 10000):
        for eta in (0, .01, .1, .2, .5, 1, 2, 5, 10, 12):
            changed = sum(argmins(m, k, eta) != argmins(m, 1000, .2) for m in roots_for_sensitivity)
            sensitivity.append({"K": k, "eta": eta, "all_candidate_resolved_roots": len(roots_for_sensitivity),
                                "empirical_argmin_sets_changed_vs_candidate": changed})
    return {"schema": "nosl.archived-corpus-objective-audit.v1", "counts": dict(sorted(counts.items())),
        "source_run_groups": len(groups), "source_combat_groups": len(source_battles),
        "terminal_kinds": dict(terminal), "action_quality": dict(quality),
        "unresolved_value_reasons_by_outcome": dict(sorted(unresolved.items())),
        "inventory_ids_by_outcome": dict(sorted(inventories.items())),
        "recorded_resource_events": dict(sorted(resource_events.items())),
        "permanent_change_kinds_by_outcome": dict(sorted(permanent.items())),
        "attempt_statuses": dict(attempts_by_status), "failed_attempt_reasons": dict(failed_attempts),
        "versions": {k: sorted(v) for k, v in sorted(versions.items())},
        "maximum_absolute_utility_reconstruction_error": max(costs_errors, default=0),
        "audit_passed": not failures, "failures_first_50": failures,
        "risk_sensitivity": sensitivity, "potion_contrasts": potion_contrasts,
        "potion_contrast_point_estimates": dict(Counter("below_9" if c["sample_mean_hp_improvement"] < 9 else "equal_9" if c["sample_mean_hp_improvement"] == 9 else "above_9" for c in potion_contrasts)),
        "interpretation": [
            "Descriptive audit of accepted archived constructed roots only; failed attempts are a separate selection mechanism, not game losses",
            "Action-world copies share sampled worlds; roots share source runs. Outcome counts are not independent battles",
            "Empirical argmin sets and four-world potion differences are not reliable preference labels, exact equivalence, or calibrated probabilities",
            "No public-boundary conformance, item price, counterfactual bonus plan, client fidelity or full-content claim follows from this arithmetic audit",
        ], "formal_labels_allowed": False, "training_authorized_by_report": False}


def corpus_report(corpus):
    corpus = corpus.resolve()
    files = ("decisions.jsonl", "attempts.jsonl", "generation_config.json", "generation_recipe.py", "progress.json")
    hashes = {name: hashlib.sha256((corpus / name).read_bytes()).hexdigest() for name in files}
    with (corpus / "decisions.jsonl").open(encoding="utf-8") as stream:
        records = [json.loads(line) for line in stream if line.strip()]
    with (corpus / "attempts.jsonl").open(encoding="utf-8") as stream:
        attempts = [json.loads(line) for line in stream if line.strip()]
    result = audit_records(records, attempts)
    config = json.loads((corpus / "generation_config.json").read_text())
    result["corpus"] = str(corpus.relative_to(ROOT)) if corpus.is_relative_to(ROOT) else str(corpus)
    result["source_sha256"] = hashes
    result["archived_generator_hash_matches"] = hashes["generation_recipe.py"] == config.get("generator_sha256")
    if not result["archived_generator_hash_matches"]:
        result["audit_passed"] = False
        result["failures_first_50"].append({"check": "archived_generator_hash_mismatch", "root": "all", "action_index": None})
    canonical_config_hash = hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()).hexdigest()
    result["all_records_generation_config_hash_matches"] = all(
        row["audit_only"].get("generation_config_sha256") == canonical_config_hash for row in records)
    result["source_files_unchanged_during_audit"] = all(
        hashlib.sha256((corpus / name).read_bytes()).hexdigest() == digest for name, digest in hashes.items())
    for condition, code in ((result["all_records_generation_config_hash_matches"], "record_generation_config_hash_mismatch"),
                            (result["source_files_unchanged_during_audit"], "source_changed_during_audit")):
        if not condition:
            result["audit_passed"] = False
            result["failures_first_50"].append({"check": code, "root": "all", "action_index": None})
    result["runtime_fingerprints"] = config.get("runtime_files", {})
    result["use_for_current_training_or_boundary_validation"] = False
    result["boundary_status"] = "ARCHIVED_PUBLIC_V2_EVIDENCE_ONLY_PENDING_PUBLIC_BOUNDARY_REVERSIONING"
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "configs/objective_calibration_report.json")
    parser.add_argument("--corpus", type=Path, help="Read-only archived engineering corpus directory; does not relabel or approve training")
    args = parser.parse_args()
    result = report()
    if args.corpus:
        result["archived_corpus_evidence"] = corpus_report(args.corpus)
        result["schema"] = "nosl.objective-calibration-evidence.v2"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(args.output), "boundary_checks": result["boundary_check_count"],
                      "confirmed_examples_preserved": True, "calibrated": False, "formal_labels_allowed": False,
                      "corpus_audit_passed": result.get("archived_corpus_evidence", {}).get("audit_passed")}))
    if args.corpus and not result["archived_corpus_evidence"]["audit_passed"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
