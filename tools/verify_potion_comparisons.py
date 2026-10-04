#!/usr/bin/env python3
"""Fixed-N native potion/B0 comparisons, with no resource prices or training labels.

The four small constructed setups were selected diagnostically. Confirmation
conditions on their fixed public roots; it is not unbiased production coverage.
Uses the existing frozen JSONL worker without building or changing it.
"""
from __future__ import annotations

import argparse
from collections import Counter
import datetime as dt
import json
import math
import os
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from calibrate_objective import outcome_facts  # noqa: E402
from evaluate_pilot_policy import (  # noqa: E402
    Guard, Worker, canonical, digest, file_hash, recover_action, runtime_identity, save,
)
from contracts.preference_contract import Interval, potion_reference_eligibility  # noqa: E402

BASELINE = "nosl-public-rules-v1"
SCHEMA = "nosl.native-potion-paired-evidence.v1"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
DIAGNOSTIC_SEEDS = [8800000 + i * 10 + j for i in range(4) for j in range(2)]
METRICS = ("hp_saved", "use_death", "hold_death", "paired_rescue", "paired_harm")
SOURCE_FILES = [
    "src/Nosl.Contracts/ContinuationPolicy.cs", "src/Nosl.Worker/CombatTeacher.cs",
    "src/Nosl.Worker/TeacherDataset.cs",
    "src/Nosl.Worker/CombatSession.cs", "src/Nosl.Worker/BeliefSampler.cs",
    "src/Nosl.Worker/RolloutRecorder.cs", "src/Nosl.Worker/EncounterCoverage.cs",
    "src/Nosl.Objectives/PreferenceGates.cs",
    "docs/spec/v4/contracts/preference_contract.py", "tools/calibrate_objective.py",
    "tools/evaluate_pilot_policy.py",
] + ["vendor/sts2-sim/src/Sts2Sim.Core/Models/" + p for p in (
    "Cards/StrikeSilent.cs", "Cards/Neutralize.cs", "Potions/FirePotion.cs",
    "Monsters/TwigSlimeS.cs", "Monsters/Nibbit.cs", "Relics/RingOfTheSnake.cs")]
SOURCE_FILES += ["vendor/sts2-sim/src/Sts2Sim.Core/" + p for p in (
    "Commands/PotionCmd.cs", "Commands/CreatureCmd.cs", "Combat/CombatEngine.cs",
    "Combat/CombatState.Clone.cs", "Combat/CombatState.cs", "Rooms/CombatRoom.cs",
    "Hooks/Hook.cs", "Models/AbstractModel.cs", "Models/MonsterModel.cs",
    "Models/RelicModel.cs", "Models/Characters/Silent.cs", "Entities/Creatures/Creature.cs",
    "Runs/RunState.cs", "Models/Cards/GeneratedCardModel.cs", "Models/Powers/WeakPower.cs",
    "Models/Powers/StrengthPower.cs")]


def declared_cases():
    recipes = [
        ("below", "below-nine", "TwigSlimeS", 8, 10, ["StrikeSilent"]),
        ("nine", "empirical-nine-boundary", "Nibbit", 20, 15, ["StrikeSilent", "Neutralize"]),
        ("large", "large-saving", "TwigSlimeS", 16, 20, ["StrikeSilent"]),
        ("rescue", "empirical-rescue-below-nine", "TwigSlimeS", 1, 10, ["StrikeSilent"]),
    ]
    return [{"case_id": name, "purpose": purpose,
             "scenario": {"seed": "nosl-potion-diagnostic-v1:" + name, "enemy": enemy,
                          "hp": hp, "enemyHp": enemy_hp, "deck": deck, "potions": ["FirePotion"]},
             "evaluation_seeds": list(range(9100000 + i * 100, 9100000 + i * 100 + 32))}
            for i, (name, purpose, enemy, hp, enemy_hp, deck) in enumerate(recipes)]


def interval_with_missing(values, planned_count, lower, upper, alpha):
    """Fixed-N Hoeffding plus worst-case missing mass; never drop/renormalize.

    Each None is an allocated but unknown outcome. Its true value can lie
    anywhere in the independently established support, even with biased failure.
    """
    if (type(planned_count) is not int or planned_count <= 0 or len(values) != planned_count
            or not all(math.isfinite(x) for x in (lower, upper, alpha))
            or lower > upper or not 0 < alpha < 1):
        raise ValueError("fixed count and finite support/alpha required")
    known = [x for x in values if x is not None]
    if any(type(x) not in (int, float) or not math.isfinite(x) or not lower <= x <= upper for x in known):
        raise ValueError("sample contradicts reviewed support")
    missing = planned_count - len(known)
    low_mean = (sum(known) + missing * lower) / planned_count
    high_mean = (sum(known) + missing * upper) / planned_count
    radius = (upper - lower) * math.sqrt(math.log(2 / alpha) / (2 * planned_count))
    return {"lower": max(lower, low_mean - radius), "upper": min(upper, high_mean + radius),
            "sample_mean": None if missing else sum(known) / planned_count,
            "sample_mean_identification_interval": [low_mean, high_mean],
            "allocated_worlds": planned_count, "known_worlds": len(known), "missing_worlds": missing,
            "support": [lower, upper], "alpha": alpha, "radius": radius}


def validate_root(case, packet):
    """The support certificate is intentionally confined to these reviewed fixtures."""
    allowed = {r["case_id"]: r for r in declared_cases()}
    if case["case_id"] not in allowed or case["scenario"] != allowed[case["case_id"]]["scenario"]:
        raise ValueError("unreviewed scenario: no HP support certificate")
    if packet.get("status") != "player_decision":
        raise ValueError("expected stable player root")
    o, s = packet["observation"], case["scenario"]
    if not (o["startHp"] == o["hp"] == s["hp"] and o["turn"] == 1 and o["ascension"] == 10
            and o["block"] == 0 and not o["powers"] and o["choice"] is None
            and o["relics"] == ["RingOfTheSnake"]
            and [p for p in o["potions"] if p is not None] == ["FirePotion"]
            and not o["discard"] and not o["exhaust"] and not o["unknownDraw"] and not o["knownDraw"]
            and o["drawCount"] == 0 and o.get("unidentifiedDrawCount", 0) == 0
            and not o.get("orbs") and not o.get("pets") and len(o["enemies"]) == 1
            and Counter(c["id"] for c in o["hand"]) == Counter(s["deck"])
            and all(c["upgrade"] == 0 and not c.get("enchantments") and c.get("affliction") is None for c in o["hand"])):
        raise ValueError("public root outside reviewed no-healing fixture scope")
    enemy = o["enemies"][0]
    if not (enemy["id"] == s["enemy"] and enemy["hp"] == s["enemyHp"] <= 20
            and enemy["block"] == 0 and not enemy["powers"]):
        raise ValueError("root does not support immediate FirePotion finish")


def freeze_case(worker, case):
    root = worker.request({"op": "reset", "scenario": case["scenario"]})
    validate_root(case, root)
    after = worker.request({"op": "continue"})
    terminal = worker.request({"op": "settle"}) if after.get("status") == "terminal_settled" else None
    action = recover_action(root, after, terminal)
    if action["kind"] in ("potion", "discard_potion"):
        raise ValueError("native B0 root action does not preserve potion")
    potion_indices = [i for i, a in enumerate(root["actions"]) if a["kind"] == "potion"]
    if len(potion_indices) != 1:
        raise ValueError("fixture must declare one legal potion action")
    return {**case, "public_root": root, "public_root_sha256": digest(root),
            "baseline_action": action, "baseline_action_index": root["actions"].index(action),
            "potion_action_index": potion_indices[0],
            "baseline_action_evidence": {"method": "native_continue_public_action_event_before_confirmation",
                                         "after": after, "terminal": terminal},
            "hp_saved_support": [0, case["scenario"]["hp"]]}


def summarize_case(case, record, alpha):
    audit, public = record["audit_only"], record["public_input"]
    root = case["public_root"]
    if (public["observation"] != root["observation"] or public["candidate_actions"] != root["actions"]
            or audit["sampler_seeds"] != case["evaluation_seeds"] or audit["exploration_seeds"]
            or audit["n_independent_eval"] != len(case["evaluation_seeds"])
            or audit["independent_final_evaluation"] is not True
            or audit["continuation_version"] != BASELINE or audit["teacher_version"] != "nosl-full-combat-teacher-v1:T0"
            or audit["objective_calibrated"] is not False or audit["label_endpoint"] != ENDPOINT):
        raise ValueError("unanchored/mismatched teacher record")
    n = len(case["evaluation_seeds"])
    raw = {r["action_index"]: r["outcomes"] for r in audit["outcome_samples"]}
    if (len(raw) != len(audit["outcome_samples"]) or set(raw) != set(range(len(root["actions"])))
            or any(len(rows) != n for rows in raw.values())):
        raise ValueError("candidate/world ledger mismatch; never shorten fixed N")
    use_i, hold_i = case["potion_action_index"], case["baseline_action_index"]
    target = record["targets"]["actions"][use_i]
    if target["action_index"] != use_i or target["masks"]["value"] is not False or target["value"] is not None:
        raise ValueError("unpriced potion utility must remain masked")
    facts, paired = {}, []
    for name, index in (("use", use_i), ("hold", hold_i)):
        facts[name] = []
        for outcome in raw[index]:
            fact = outcome_facts(outcome)
            if outcome["hpAtCombatStart"] != root["observation"]["startHp"] or outcome["continuationPolicyId"] != BASELINE:
                raise ValueError("changed combat anchor or continuation")
            if fact["complete"]:
                if (outcome["settlementProfileId"] != ENDPOINT or not outcome["inventorySnapshotsComplete"]
                        or not outcome["permanentChangesComplete"] or outcome["permanentChanges"]
                        or outcome["maxHpStart"] != outcome["maxHpAfterSettlement"]
                        or not 0 <= outcome["hpAfterSettlement"] <= case["scenario"]["hp"]):
                    raise ValueError("settled outcome contradicts support/asset certificate")
                expected_end = Counter() if name == "use" else Counter({"FirePotion": 1})
                if fact["initial"] != Counter({"FirePotion": 1}) or fact["final"] != expected_end:
                    raise ValueError("use/hold arm did not obey frozen inventory policy")
                if name == "use" and (outcome["terminalKind"] != "Win" or outcome["hpAfterSettlement"] != case["scenario"]["hp"]):
                    raise ValueError("immediate-win support certificate contradicted")
                fact["hp"] = outcome["hpAfterSettlement"]
            facts[name].append(fact)
    values = {metric: [] for metric in METRICS}
    for seed, use, hold in zip(case["evaluation_seeds"], facts["use"], facts["hold"]):
        both = use["complete"] and hold["complete"]
        row = {"seed": seed, "use_complete": use["complete"], "hold_complete": hold["complete"],
               "use_hp": use.get("hp"), "hold_hp": hold.get("hp"),
               "hp_saved": use["hp"] - hold["hp"] if both else None,
               "use_death": use.get("failure"), "hold_death": hold.get("failure"),
               "paired_rescue": int(hold["failure"] == 1 and use["failure"] == 0) if both else None,
               "paired_harm": int(hold["failure"] == 0 and use["failure"] == 1) if both else None}
        paired.append(row)
        for key in METRICS:
            values[key].append(row[key])
    estimates = {key: interval_with_missing(v, n, *(case["hp_saved_support"] if key == "hp_saved" else [0, 1]), alpha)
                 for key, v in values.items()}
    hp = estimates["hp_saved"]
    gate = potion_reference_eligibility(Interval(hp["lower"], hp["upper"]))
    return {"case_id": case["case_id"], "purpose": case["purpose"], "n": n,
            "baseline_action_index": hold_i, "potion_action_index": use_i,
            "estimates": estimates, "paired_samples": paired,
            "terminal_kinds": {name: dict(Counter(o["terminalKind"] for o in raw[index]))
                               for name, index in (("use", use_i), ("hold", hold_i))},
            "general_reference_eligibility": gate.value,
            "verified_rescue_need": None,
            "rescue_exception_eligibility": "unresolved",
            "rescue_certification": "UNAVAILABLE_FROZEN_API_HAS_NO_RESCUE_CERTIFICATE",
            "rescue_gate_if_independently_verified": potion_reference_eligibility(
                Interval(hp["lower"], hp["upper"]), verified_rescue_need=True).value,
            "full_expected_utility_comparison": None, "utility_comparison_mask": False,
            "calibrated": False, "formal_labels_allowed": False, "must_use_now": False}


def run(output, runtime_config, dotnet="dotnet"):
    # Pin this driver and its one child together to one CPU. No active worker is touched.
    if not hasattr(os, "sched_setaffinity"):
        raise RuntimeError("one-CPU affinity required for this bounded native run")
    cpu = min(os.sched_getaffinity(0))
    os.sched_setaffinity(0, {cpu})
    expected = json.loads(runtime_config.read_text())["runtime_files"]
    runtime = runtime_identity(ROOT)
    if runtime != expected:
        raise ValueError("native runtime differs from frozen generation manifest; no rebuild performed")
    cases = declared_cases()
    seeds = [s for c in cases for s in c["evaluation_seeds"]]
    if len(cases) > 4 or len(seeds) != len(set(seeds)) or set(seeds).intersection(DIAGNOSTIC_SEEDS):
        raise ValueError("invalid fixed/disjoint confirmation allocation")
    output.mkdir(parents=True, exist_ok=False)
    limits = {"max_decisions": 100, "max_battle_seconds": 60, "max_job_seconds": 600,
              "max_rss_mib": 768, "max_trace_mib": 128}
    guard = Guard(limits)
    worker = Worker(ROOT, output / "worker-stderr.log", dotnet, guard)
    try:
        frozen = []
        for case in cases:
            guard.battle_started = time.monotonic()
            frozen.append(freeze_case(worker, case))
        plan = {"schema": SCHEMA, "frozen_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
                "cases": frozen, "baseline_policy_id": BASELINE, "teacher_mode": "T0",
                "source_kind": "constructed", "diagnostic_selected_fixtures": True,
                "diagnostic_setups": 4, "diagnostic_worlds_per_setup": 2, "diagnostic_seeds": DIAGNOSTIC_SEEDS,
                "confirmation_worlds_per_case": 32, "family_alpha": .05,
                "simultaneous_intervals": len(frozen) * len(METRICS),
                "interval_method": "FIXED_N_HOEFFDING_BONFERRONI_WITH_WORST_CASE_MISSING_MASS",
                "hp_support_certificate": {
                    "scope": "only the four exact declared recipes and validated public roots",
                    "argument": [
                        "Native FirePotion deals 20 unpowered damage to the sole enemy with at most 20 HP, no block and no powers",
                        "TwigSlimeS and Nibbit have no retaliation/death hook; RingOfTheSnake only changes first-turn draw",
                        "The use arm immediately settles a win at startHp, before an enemy turn",
                        "StrikeSilent and Neutralize, these enemies and the starter relic cannot heal the player or change max HP",
                        "The hold arm therefore settles with HP in [0,startHp], including true defeat at zero",
                        "Subtracting hold HP from the fixed use HP gives [0,startHp], independently of observed extrema"],
                    "not_a_general_potion_monotonicity_claim": True},
                "runtime_files": runtime, "runtime_manifest_sha256": file_hash(runtime_config),
                "source_sha256": {p: file_hash(ROOT / p) for p in SOURCE_FILES},
                "evaluator_sha256": file_hash(Path(__file__)), "cpu_affinity": [cpu], "limits": limits,
                "formal_labels_allowed": False, "training_started": False}
        save(output / "plan.json", plan)
        (output / "plan.sha256").write_text(digest(plan) + "\n")
        (output / "evaluator-source.py").write_bytes(Path(__file__).read_bytes())
        summaries = []
        for case in frozen:
            guard.battle_started = time.monotonic()
            root = worker.request({"op": "reset", "scenario": case["scenario"]})
            if digest(root) != case["public_root_sha256"]:
                raise ValueError("public root changed after freeze")
            request = {"op": "teacher_record", "sourceRun": "potion-paired-confirmation-v1",
                       "sourceCombat": case["case_id"], "branchFamily": case["case_id"],
                       "options": {"mode": "T0", "evaluationSeeds": case["evaluation_seeds"],
                                   "explorationSeeds": [], "maxDecisions": limits["max_decisions"], "formalLabels": False}}
            save(output / (case["case_id"] + ".request.json"), request)
            record = worker.request(request)
            save(output / (case["case_id"] + ".record.json"), record)
            result = summarize_case(case, record, .05 / plan["simultaneous_intervals"])
            summaries.append(result)
            print(canonical({"case": case["case_id"], "hp_saved": result["estimates"]["hp_saved"],
                             "general_reference_eligibility": result["general_reference_eligibility"]}), flush=True)
        if runtime_identity(ROOT) != runtime or any(file_hash(ROOT / p) != h for p, h in plan["source_sha256"].items()):
            raise ValueError("runtime or reviewed source changed during confirmation")
        result = {"schema": SCHEMA, "plan_sha256": digest(plan), "cases": summaries,
                  "evidence_generator_sha256": plan["evaluator_sha256"],
                  "report_reducer_sha256": file_hash(Path(__file__)),
                  "artifact_sha256": {p.name: file_hash(p) for p in sorted(output.glob("*.record.json"))},
                  "source_kind": "constructed", "unique_production_roots_added": 0,
                  "root_world_draws": len(seeds), "selected_paired_outcomes": 2 * len(seeds),
                  "calibrated": False, "formal_labels_allowed": False, "training_started": False,
                  "exact_contract_boundary_sanity_only": {
                      "hp_saved": 9, "eligibility": potion_reference_eligibility(Interval(9, 9)).value,
                      "evidence_kind": "mathematical_contract_check_not_native_probability_certificate"},
                  "limitations": [
                      "Selected tiny constructed fixtures; no natural-distribution, original-client or full-content fidelity claim",
                      "Independent draws share a fixed public root and are paired across actions; they are not new independent source battles",
                      "Intervals require the reviewed restricted HP support and valid independent sampler; source hashes identify that review",
                      "General-reference ineligibility does not rule out a separately verified rescue exception",
                      "Observed rescue frequency and an empirical mean of exactly nine are not exact probability certificates",
                      "No frozen native primitive certifies rescue need, so verified_rescue_need remains unknown (null)",
                      "No universal potion price, mandatory action, full expected-utility ranking, calibration or formal label is inferred"],
                  "runtime_unchanged": True, "reviewed_sources_unchanged": True,
                  "peak_driver_worker_rss_mib": guard.peak_rss_mib}
        save(output / "report.json", result)
        return result
    except Exception as exc:
        save(output / "failure.json", {"status": "incomplete_confirmation", "error": str(exc),
                                       "formal_labels_allowed": False, "partial_samples_are_not_a_final_fixed_N_claim": True})
        raise
    finally:
        worker.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New evidence directory; never a production corpus")
    parser.add_argument("--runtime-config", type=Path, default=ROOT / "artifacts/data/pilot-5000-v4/shard-1/generation_config.json")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    result = run(args.output, args.runtime_config, args.dotnet)
    print(canonical({"report": str(args.output / "report.json"), "cases": len(result["cases"]),
                     "calibrated": False, "formal_labels_allowed": False}))


if __name__ == "__main__":
    main()
