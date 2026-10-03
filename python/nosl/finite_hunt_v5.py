"""Validate fresh constructed full-v5 Hunt evidence without admitting training.

The two whole-plan measurements are recomputed from paired settled outcomes.
Unknown worlds retain their allocated mass. Hashes bind bytes and public input;
they neither authenticate the producer nor establish natural-run provenance.
No legacy label reader, model, optimizer or training entry point is imported.
"""
from __future__ import annotations

from copy import deepcopy
from collections import Counter
import hashlib
import json
import math

from .data_v5 import RECORD_KIND, RECORD_SCHEMA, validate_record, validate_targets
from .evidence_v4 import numeric_guard, validate_evidence
from .public_identity_v5 import public_input_digest
from .schema import HEADS, boolean, integer, number, object_keys, reject, sequence, validate_action
from .schema_v5 import EVIDENCE_SCHEMA, loads, validate_current_map, validate_public

RAW_SCHEMA = "nosl.dataset.finite-hunt.full-v5.raw.v1"
RAW_KIND = "constructed_finite_hunt_raw"
PRODUCER_VERSION = "nosl.finite-hunt.full-v5.producer.v1"
ADAPTER_VERSION = "nosl.finite-hunt.full-v5.adapter.v1"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
OBJECTIVE = "nosl_silent_a10_terminal_v4_candidate"
CARD_REWARD = "earned_extra_reward_opportunity:CardReward"
TERMINALS = ("Win", "Loss", "ComputeTruncated", "EngineError", "PolicyNonterminating")
AUDIT_FIELDS = "producer_version trainable formal_labels source_kind native_run source_run_group source_combat_id branch_family scenario evaluation_options public_input_json public_input_json_sha256 paired_evidence public_conditioning posterior_profile legacy_labels_read sampled_public_roots terminal_public_evidence".split()
OUTCOME_FIELDS = "terminalKind playerAlive hpAtCombatStart hpAfterSettlement maxHpStart maxHpAfterSettlement cumulativeHpDamage healingReceived otherHpAdjustment hpEventDiagnosticsComplete inventorySnapshotsComplete inventoryStart inventoryEnd resourceEvents resourceProvenanceComplete permanentChanges persistentAssetsAtStartJson persistentAssetsAfterSettlementJson permanentChangesComplete specifiedFinishSuccess earnedBonus deadlineMet playerTurnsElapsed atomicActionsExecuted settlementComplete settlementProfileId continuationPolicyId detail isTrueTerminal".split()
ANCHOR_FIELDS = "publicSummary startPlayerTurn deadlinePlayerTurn hpSafetyFloor templateId baselinePolicyId goal".split()
EVALUATION_FIELDS = "anchor worlds baselineSummary planSummary meanExtraNetHpLoss extraExpectedLossInterval unconditionalSuccessInterval excessDeathProbabilityInterval safetyAcceptable eligibility utilityComparisonMask meanSpecifiedSuccess masks limitations audit".split()


def _require(condition, reason):
    if not condition: reject("finite_hunt_v5:" + reason)


def _text(value, name):
    _require(isinstance(value, str) and bool(value.strip()), name + "_text_required")
    return value


def _equal(actual, expected, name):
    """Numeric means tolerate only float roundoff; bools never equal counts."""
    if type(expected) in (int, float):
        number(actual, name, -float("inf"), float("inf"))
        _require(math.isclose(actual, expected, rel_tol=1e-12, abs_tol=1e-12), name + "_mismatch")
    elif isinstance(expected, (list, dict)):
        _same_json(actual, expected, name)
    else:
        _require(type(actual) is type(expected) and actual == expected, name + "_mismatch")


def _same_json(actual, expected, name):
    # JSON preserves bool versus number distinctions that ordinary dict equality loses.
    _require(json.dumps(actual, sort_keys=True, allow_nan=False) ==
             json.dumps(expected, sort_keys=True, allow_nan=False), name + "_mismatch")


def _json(text, name):
    _require(isinstance(text, str), name + "_json_text_required")
    return loads(text)


def _sha(payload):
    return hashlib.sha256(payload).hexdigest()


def _validate_evidence(evidence, config):
    object_keys(evidence, ("schemaVersion", "completeFromRunStart", "events"), "terminal public evidence")
    _equal(evidence["schemaVersion"], EVIDENCE_SCHEMA, "terminal_evidence_version")
    projected = deepcopy(evidence)
    projected["schemaVersion"] = "nosl.public-run-evidence.v1"
    for event in sequence(projected["events"], "terminal events", config["base_config"]["evidence_max_events"]):
        object_keys(event, ("eventOrdinal", "ownerOrdinal", "payload"), "terminal evidence event")
        payload = event["payload"]
        _require(isinstance(payload, dict), "terminal_payload_object_required")
        if payload.get("kind") == "map":
            _require("currentMap" in payload, "terminal_map_capture_missing")
            validate_current_map(payload["currentMap"], payload, config)
            payload.pop("currentMap")
    validate_evidence(projected, config["base_config"])


def _validate_anchor(public, anchor, options):
    object_keys(anchor, ANCHOR_FIELDS, "paired anchor")
    context = public["controller_context"]
    _require(context.get("schemaVersion") == "nosl.controller.finite-hunt.v1"
             and context.get("status") == "active", "original_active_hunt_anchor_required")
    inactive = deepcopy(public); inactive["controller_context"] = {"status": "inactive"}
    _same_json(context["anchor"], inactive, "exact_full_v5_anchor")
    packet = _json(anchor["publicSummary"], "anchor_public_summary")
    object_keys(packet, ("status", "observation", "actions", "publicEvidence"), "anchor DecisionPacket")
    _equal(packet["status"], "player_decision", "anchor_status")
    for key, name in (("observation", "observation"), ("actions", "candidate_actions"), ("publicEvidence", "public_evidence")):
        _same_json(packet[key], public[name], "anchor_packet_" + key)
    for key in ("startPlayerTurn", "deadlinePlayerTurn", "hpSafetyFloor", "templateId", "baselinePolicyId"):
        _equal(anchor[key], context[key], "anchor_" + key)
    _equal(anchor["deadlinePlayerTurn"], anchor["startPlayerTurn"] + 1, "fixed_deadline")
    _equal(anchor["hpSafetyFloor"], options["hpSafetyFloor"], "hp_safety_floor")
    _equal(anchor["goal"], "TheHunt fatal with an actually offered extra CardReward", "specified_goal")
    return packet


def _validate_fresh_setup(scenario, public):
    """Bind the declared setup to the producer's first, constructed decision."""
    observation, evidence = public["observation"], public["public_evidence"]
    _equal(observation["turn"], 1, "fresh_initial_player_turn")
    _equal(observation["hp"], observation["startHp"], "fresh_initial_hp")
    _equal(observation["gold"], observation["startGold"], "fresh_initial_gold")
    _same_json(observation["runContext"], {"schemaVersion": "nosl.public-run-context.v1", "actIndex": 0,
               "floor": 0, "combatEntryIndex": None, "completeFromRunStart": False}, "constructed_run_context")
    _equal(evidence["completeFromRunStart"], False, "constructed_run_completeness")
    events = evidence["events"]
    _same_json(events[0], {"eventOrdinal": 0, "ownerOrdinal": None,
               "payload": {"kind": "gap", "reason": "run_start_not_observed"}}, "constructed_initial_gap")
    starts = [event for event in events if event["payload"]["kind"] == "owner_started"]
    _require(len(starts) == 1, "one_constructed_combat_owner_required")
    _same_json(starts[0], {"eventOrdinal": 1, "ownerOrdinal": 0, "payload": {"kind": "owner_started",
               "ownerKind": "combat", "actIndex": 0, "floor": 0, "parentOwnerOrdinal": None,
               "completeFromOwnerStart": True}}, "constructed_combat_owner")
    _require(all(event["ownerOrdinal"] == 0 and event["payload"]["kind"] in
                 ("owner_started", "combat_fact", "combat_decision") for event in events[1:]),
             "fresh_source_has_no_native_history_or_prior_actions")
    decisions = [event for event in events if event["payload"]["kind"] == "combat_decision"]
    _require(len(decisions) == 1 and decisions[0] == events[-1]
             and decisions[0]["payload"]["historyCompleteFromCombatStart"] is True,
             "fresh_first_complete_combat_decision_required")
    declared = Counter()
    for text in scenario["deck"]:
        card = text.rstrip("+"); upgrades = len(text) - len(card)
        _require(upgrades <= (0 if card in ("AscendersBane", "Slimed") else 1), "unsupported_declared_card_upgrade")
        declared[card, upgrades] += 1
    visible = Counter((card["id"], card["upgrade"]) for pile in ("hand", "discard", "exhaust") for card in observation[pile])
    visible.update((row["card"]["id"], row["card"]["upgrade"]) for row in observation["knownDraw"])
    for row in observation["unknownDraw"]: visible[row["card"]["id"], row["card"]["upgrade"]] += row["count"]
    _require(visible == declared, "declared_deck_does_not_match_public_piles")
    enemies = scenario["enemies"] if scenario["enemies"] is not None else [scenario["enemy"]]
    _equal(observation["enemies"][0]["id"], enemies[0], "declared_enemy")
    for key, actual in (("hp", observation["startHp"]), ("maxHp", observation["maxHp"]),
                        ("gold", observation["startGold"]), ("enemyHp", observation["enemies"][0]["hp"])):
        if scenario[key] is not None: _equal(scenario[key], actual, "declared_" + key)
    if scenario["enemyHp"] is not None:
        _equal(scenario["enemyHp"], observation["enemies"][0]["maxHp"], "declared_enemy_max_hp")


def _objective(outcome):
    """The existing uncalibrated Candidate objective, with all prices unknown."""
    if outcome["terminalKind"] not in ("Win", "Loss"):
        return {"status": 1, "cost": None, "netHpLoss": None, "inventoryAdjustment": None,
                "permanentFutureValue": None, "reasons": [outcome["terminalKind"]]}
    reasons = []
    if not outcome["inventorySnapshotsComplete"]: reasons.append("inventory_snapshots_incomplete")
    quantities = []
    for key in ("inventoryStart", "inventoryEnd"):
        values = {}
        for row in outcome[key]: values[row["resourceId"]] = values.get(row["resourceId"], 0) + row["count"]
        quantities.append(values)
    before, after = quantities
    for key in sorted(set(before) | set(after)):
        if before.get(key, 0) != (after.get(key, 0) if outcome["terminalKind"] == "Win" else 0):
            reasons.append("inventory_value_unresolved:" + key)
    if not outcome["permanentChangesComplete"]: reasons.append("permanent_change_ledger_incomplete")
    changes = {}
    for row in outcome["permanentChanges"]: changes[row["kind"]] = changes.get(row["kind"], 0) + row["amount"]
    if changes.get("max_hp", 0) != outcome["maxHpAfterSettlement"] - outcome["maxHpStart"]:
        reasons.append("permanent_change_not_recorded:max_hp")
    if outcome["terminalKind"] == "Win":
        reasons.extend("permanent_future_value_unresolved:" + key for key in changes if changes[key] != 0)
    loss = outcome["hpAtCombatStart"] - outcome["hpAfterSettlement"]
    cost = (1000 if outcome["terminalKind"] == "Loss" else 0) + loss + .2 * max(loss, 0) ** 2 / max(1, outcome["hpAtCombatStart"])
    return {"status": 2 if reasons else 0, "cost": None if reasons else cost, "netHpLoss": loss,
            "inventoryAdjustment": None if reasons else 0, "permanentFutureValue": None if reasons else 0,
            "reasons": reasons}


def _validate_outcome(outcome, public, policy):
    object_keys(outcome, OUTCOME_FIELDS, "paired outcome")
    kind = outcome["terminalKind"]
    _require(kind in TERMINALS, "unknown_terminal_kind")
    terminal = kind in ("Win", "Loss")
    _equal(outcome["isTrueTerminal"], terminal, "is_true_terminal")
    _equal(outcome["continuationPolicyId"], policy, "continuation_policy")
    observation = public["observation"]
    integer(outcome["hpAtCombatStart"], "outcome start HP", 1)
    integer(outcome["maxHpStart"], "outcome start max HP", outcome["hpAtCombatStart"])
    _equal(outcome["hpAtCombatStart"], observation["startHp"], "fixed_combat_start_hp")
    _equal(outcome["maxHpStart"], observation["maxHp"], "fixed_combat_start_max_hp")
    for key in ("hpEventDiagnosticsComplete", "inventorySnapshotsComplete", "resourceProvenanceComplete", "permanentChangesComplete", "settlementComplete"):
        boolean(outcome[key], key)
    for key in ("playerAlive", "specifiedFinishSuccess", "earnedBonus", "deadlineMet"):
        if outcome[key] is not None: boolean(outcome[key], key)
    integer(outcome["playerTurnsElapsed"], "player turns", 0, 2**31 - 1)
    integer(outcome["atomicActionsExecuted"], "atomic actions", 0, 2**63 - 1)
    for key in ("cumulativeHpDamage", "healingReceived", "otherHpAdjustment"):
        if outcome[key] is not None: number(outcome[key], key, 0 if key != "otherHpAdjustment" else -1e6)
    for key in ("inventoryStart", "inventoryEnd"):
        for row in sequence(outcome[key], key):
            object_keys(row, ("resourceId", "count"), key + " quantity")
            _text(row["resourceId"], "resource_id"); integer(row["count"], "inventory_count")
    for row in sequence(outcome["resourceEvents"], "resource events"):
        object_keys(row, ("kind", "resourceId", "quantity", "publicSource"), "resource event")
        _text(row["kind"], "resource_event_kind"); _text(row["resourceId"], "resource_id")
        integer(row["quantity"], "resource_quantity"); _text(row["publicSource"], "resource_source")
    for row in sequence(outcome["permanentChanges"], "permanent changes"):
        object_keys(row, ("kind", "amount", "publicSource"), "permanent change")
        _text(row["kind"], "permanent_change_kind"); number(row["amount"], "permanent_change_amount")
        _text(row["publicSource"], "permanent_change_source")
    for key in ("persistentAssetsAtStartJson", "persistentAssetsAfterSettlementJson"):
        if outcome[key] is not None: _json(outcome[key], key)
    if outcome["detail"] is not None: _text(outcome["detail"], "outcome_detail")
    if not terminal:
        _equal(outcome["settlementComplete"], False, "incomplete_settlement")
        for key in ("playerAlive", "hpAfterSettlement", "maxHpAfterSettlement", "specifiedFinishSuccess", "earnedBonus", "deadlineMet"):
            _equal(outcome[key], None, "incomplete_" + key)
        return False
    _equal(outcome["settlementComplete"], True, "actual_settlement_required")
    _equal(outcome["settlementProfileId"], ENDPOINT, "settlement_endpoint")
    final = integer(outcome["hpAfterSettlement"], "settled HP", 0, observation["hp"])
    _equal(outcome["maxHpAfterSettlement"], observation["maxHp"], "no_max_hp_change")
    _equal(outcome["playerAlive"], final > 0, "settled_alive")
    _require(kind != "Win" or final > 0, "win_requires_alive")
    _require(outcome["healingReceived"] in (None, 0) and outcome["otherHpAdjustment"] in (None, 0), "no_healing_support")
    if outcome["hpEventDiagnosticsComplete"]:
        _require(all(outcome[key] is not None for key in ("cumulativeHpDamage", "healingReceived", "otherHpAdjustment")), "hp_diagnostics_missing")
        _equal(outcome["hpAtCombatStart"] - outcome["cumulativeHpDamage"] + outcome["healingReceived"] + outcome["otherHpAdjustment"], final, "hp_diagnostics")
    return True


def _terminal_facts(evidence, root, trace, outcome, anchor, config):
    _validate_evidence(evidence, config)
    prefix = root["publicEvidence"]["events"]
    events = evidence["events"]
    _same_json(events[:len(prefix)], prefix, "terminal_anchor_prefix")
    owner = prefix[-1]["ownerOrdinal"]
    suffix = [event["payload"] for event in events[len(prefix):] if event["ownerOrdinal"] == owner]
    ended = [event for event in suffix if event["kind"] == "owner_ended"]
    _require(len(ended) == 1, "actual_terminal_owner_end_required")
    final = ended[0]
    _equal(final["outcome"], "victory" if outcome["terminalKind"] == "Win" else "defeat", "terminal_owner_result")
    _require(isinstance(final["assets"], dict), "terminal_assets_required")
    _equal(final["assets"]["hp"], outcome["hpAfterSettlement"], "terminal_assets_hp")
    _equal(final["assets"]["maxHp"], outcome["maxHpAfterSettlement"], "terminal_assets_max_hp")
    actions = [event["action"] for event in suffix if event["kind"] == "combat_action"]
    _same_json(actions, [step["action"] for step in trace], "terminal_trace_actions")
    decision_turns = [events[event["decisionEventOrdinal"]]["payload"]["observation"]["turn"]
                      for event in suffix if event["kind"] == "combat_action"]
    _same_json(decision_turns, [step["playerTurn"] for step in trace], "terminal_trace_player_turns")
    turn, fatal = anchor["startPlayerTurn"], None
    for event in suffix:
        if event["kind"] == "combat_fact" and event["factKind"] == "player_turn_started": turn = event["turn"]
        if event["kind"] == "combat_fact" and event["factKind"] == "power_changed" and event["model"] == "TheHuntPower" and event["amount"] > 0:
            if fatal is None: fatal = turn
    return fatal


def _trajectory(value, role, public, anchor, options, root, terminal_evidence, config):
    object_keys(value, ("outcome", "objective", "publicTrace", "controller", "fatalPlayerTurn", "actualExtraCardRewardsOffered"), "paired trajectory")
    outcome = value["outcome"]
    settled = _validate_outcome(outcome, public, anchor["baselinePolicyId" if role == "baseline" else "templateId"])
    controller = object_keys(value["controller"], ("anchor", "status", "exitReason", "lastObservedPlayerTurn"), "trajectory controller")
    _same_json(controller["anchor"], anchor, "controller_anchor")
    integer(controller["lastObservedPlayerTurn"], "last observed player turn", anchor["startPlayerTurn"], 2**31 - 1)
    trace = sequence(value["publicTrace"], "public trace", options["maxDecisionsPerPolicy"])
    prior_turn = anchor["startPlayerTurn"]
    for step in trace:
        object_keys(step, ("playerTurn", "fixedDeadlinePlayerTurn", "status", "exitReason", "action"), "Hunt decision trace")
        prior_turn = integer(step["playerTurn"], "trace player turn", prior_turn, 2**31 - 1)
        _equal(step["fixedDeadlinePlayerTurn"], anchor["deadlinePlayerTurn"], "trace_fixed_deadline")
        _require(step["status"] in (("baseline",) if role == "baseline" else ("active", "aborted")), "trace_controller_status")
        _require((step["exitReason"] is None) == (step["status"] in ("active", "baseline")), "trace_exit_reason")
        if step["exitReason"] is not None: _text(step["exitReason"], "trace_exit_reason")
        validate_action(step["action"])
    _require(not trace or root is not None, "executed_branch_requires_sampled_root")
    _equal(outcome["atomicActionsExecuted"], len(trace), "execution_action_tally")
    _equal(outcome["playerTurnsElapsed"], prior_turn, "outcome_last_player_turn")
    _require(controller["lastObservedPlayerTurn"] == prior_turn, "controller_last_turn")
    if role == "baseline":
        _equal(controller["status"], "baseline", "baseline_controller"); _equal(controller["exitReason"], None, "baseline_exit_reason")
    else:
        _require(controller["status"] in ("finished", "aborted", "unresolved"), "final_plan_controller_status")
        _require((controller["exitReason"] is None) == (controller["status"] == "finished"), "final_plan_exit_reason")
        if controller["exitReason"] is not None: _text(controller["exitReason"], "final_plan_exit_reason")
    offered = integer(value["actualExtraCardRewardsOffered"], "actual extra rewards")
    if value["fatalPlayerTurn"] is not None: integer(value["fatalPlayerTurn"], "fatal player turn", anchor["startPlayerTurn"], prior_turn)
    if settled:
        _require(root is not None and terminal_evidence is not None, "settled_branch_requires_actual_public_evidence")
        fatal = _terminal_facts(terminal_evidence, root, trace, outcome, anchor, config)
        _equal(value["fatalPlayerTurn"], fatal, "actual_fatal_turn")
        recorded_rewards = sum(row["amount"] for row in outcome["permanentChanges"] if row["kind"] == CARD_REWARD)
        _equal(recorded_rewards, offered, "actual_offered_reward_ledger")
        won = outcome["terminalKind"] == "Win"
        _equal(outcome["specifiedFinishSuccess"], won and fatal is not None, "specified_finish_facts")
        _equal(outcome["earnedBonus"], won and fatal is not None and offered > 0, "earned_bonus_facts")
        _equal(outcome["deadlineMet"], won and fatal is not None and fatal <= anchor["deadlinePlayerTurn"], "fixed_deadline_facts")
    else:
        _equal(offered, 0, "incomplete_offered_rewards"); _equal(value["fatalPlayerTurn"], None, "incomplete_fatal_turn")
    objective = _objective(outcome)
    object_keys(value["objective"], tuple(objective), "trajectory objective")
    for key, expected in objective.items(): _equal(value["objective"][key], expected, "objective_" + key)
    success = settled and outcome["terminalKind"] == "Win" and all(outcome[key] is True for key in ("playerAlive", "specifiedFinishSuccess", "earnedBonus", "deadlineMet"))
    if role == "plan" and controller["status"] == "finished": _require(success, "finished_requires_actual_success")
    return {"settled": settled, "success": success, "outcome": outcome, "objective": objective}


def _bound(values, lower, upper, alpha):
    radius = (upper - lower) * math.sqrt(math.log(2 / alpha) / (2 * len(values)))
    return {"lower": max(lower, sum(lower if v is None else v for v in values) / len(values) - radius),
            "upper": min(upper, sum(upper if v is None else v for v in values) / len(values) + radius)}


def _check_interval(actual, expected, name):
    object_keys(actual, ("lower", "upper"), name)
    for key in expected: _equal(actual[key], expected[key], name + "_" + key)


def _summary(actual, rows):
    object_keys(actual, "assignedWorlds wins losses computeTruncated engineErrors policyNonterminating valueUnresolved invalidOutcomes completionRate expectedCost expectedNetHpLoss winProbabilityBounds lossProbabilityBounds deathProbabilityBounds formalLabelsAllowed reasons".split(), "paired summary")
    n = len(rows); completed = sum(row["settled"] for row in rows)
    kinds = [row["outcome"]["terminalKind"] for row in rows]
    scored = all(row["objective"]["status"] == 0 for row in rows)
    counts = {"assignedWorlds": n, "wins": kinds.count("Win"), "losses": kinds.count("Loss"),
              "computeTruncated": kinds.count("ComputeTruncated"), "engineErrors": kinds.count("EngineError"),
              "policyNonterminating": kinds.count("PolicyNonterminating"), "valueUnresolved": sum(row["objective"]["status"] == 2 for row in rows), "invalidOutcomes": 0}
    for key, expected in counts.items(): integer(actual.get(key), key, 0, n); _equal(actual[key], expected, "summary_" + key)
    _equal(actual.get("completionRate"), completed / n, "summary_completion_rate")
    _equal(actual.get("expectedCost"), sum(row["objective"]["cost"] for row in rows) / n if scored else None, "summary_expected_cost")
    _equal(actual.get("expectedNetHpLoss"), sum(row["objective"]["netHpLoss"] for row in rows) / n if completed == n else None, "summary_expected_hp_loss")
    for key, known in (("winProbabilityBounds", counts["wins"]), ("lossProbabilityBounds", counts["losses"]), ("deathProbabilityBounds", sum(row["settled"] and row["outcome"]["playerAlive"] is False for row in rows))):
        _check_interval(actual.get(key), {"lower": known / n, "upper": (known + n - completed) / n}, "summary_" + key)
    _equal(actual.get("formalLabelsAllowed"), False, "summary_formal_labels")
    reasons = {reason for row in rows for reason in row["objective"]["reasons"]}
    reasons.add("objective_profile_not_calibrated_for_formal_labels")
    if completed != n: reasons.add("incomplete_probability_mass_preserved")
    _equal(actual.get("reasons"), sorted(reasons), "summary_reasons")
    return scored


@numeric_guard
def validate_raw_record(record, config):
    """Check full-v5 inputs and independently recompute every emitted target."""
    object_keys(record, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "finite Hunt raw record")
    _equal(record["schema_version"], RAW_SCHEMA, "fresh_raw_schema")
    _equal(record["record_kind"], RAW_KIND, "constructed_record_kind")
    public, targets, audit = record["public_input"], record["targets"], record["audit_only"]
    # The published full boundary is always checked before evidence interpretation.
    validate_public(public, config); validate_targets(targets, public, config)
    object_keys(audit, AUDIT_FIELDS, "finite Hunt raw audit")
    constants = {"producer_version": PRODUCER_VERSION, "trainable": False, "formal_labels": False,
                 "source_kind": "constructed_declared_setup", "native_run": False,
                 "public_conditioning": "full_v5_exact_sample_and_branch_equality",
                 "posterior_profile": "whole-setup-rejection-v1", "legacy_labels_read": False}
    for key, expected in constants.items(): _equal(audit[key], expected, "audit_" + key)
    for key in ("source_run_group", "source_combat_id", "branch_family"): _text(audit[key], key)
    _equal(_sha(_text(audit["public_input_json"], "public_input_json").encode("utf-8")), audit["public_input_json_sha256"], "csharp_public_json_sha256")
    _same_json(_json(audit["public_input_json"], "public_input"), public, "csharp_public_json")
    options = object_keys(audit["evaluation_options"], ("evaluationSeeds", "maxDecisionsPerPolicy", "maxPosteriorAttempts", "hpSafetyFloor", "familywiseAlpha"), "Hunt options")
    seeds = sequence(options["evaluationSeeds"], "evaluation seeds", 16)
    _require(bool(seeds), "planned_seeds_required")
    for seed in seeds: integer(seed, "evaluation seed", 0, 2**64 - 1)
    _require(len(set(seeds)) == len(seeds), "independent_seeds_must_be_unique")
    integer(options["maxDecisionsPerPolicy"], "decision budget", 1, 200); integer(options["maxPosteriorAttempts"], "posterior budget", 1, 4096)
    integer(options["hpSafetyFloor"], "HP floor")
    number(options["familywiseAlpha"], "familywise alpha", 0, 1)
    _require(0 < options["familywiseAlpha"] < 1, "alpha_must_be_open_interval")
    scenario = object_keys(audit["scenario"], "seed enemy deck potions relics hp maxHp enemyHp encounter enemies gold forcedEvent".split(), "declared Scenario")
    _text(scenario["seed"], "scenario_seed")
    cards = {"TheHunt", "StrikeSilent", "DefendSilent", "Neutralize", "Survivor", "AscendersBane", "Acrobatics", "Backflip", "Prepared", "ThinkingAhead", "Slimed"}
    deck = sequence(scenario["deck"], "declared deck")
    _require(all(isinstance(card, str) and card.rstrip("+") in cards for card in deck) and sum(card.rstrip("+") == "TheHunt" for card in deck) == 1, "reviewed_hunt_deck_required")
    for key in ("potions", "relics", "enemies"):
        if scenario[key] is not None: sequence(scenario[key], "scenario_" + key)
    for key in ("hp", "maxHp", "enemyHp", "gold"):
        if scenario[key] is not None: integer(scenario[key], "scenario_" + key, 0 if key == "gold" else 1)
    _require(scenario["encounter"] is None and scenario["forcedEvent"] is None and (scenario["potions"] or []) == [] and all(relic == "RingOfTheSnake" for relic in scenario["relics"] or []), "unsupported_declared_setup")
    enemies = scenario["enemies"] if scenario["enemies"] is not None else [scenario["enemy"]]
    _require(isinstance(enemies, list) and len(enemies) == 1 and enemies[0] in ("TwigSlimeS", "LeafSlimeS", "Nibbit"), "reviewed_single_enemy_required")
    _validate_fresh_setup(scenario, public)
    evaluation = object_keys(audit["paired_evidence"], EVALUATION_FIELDS, "paired evaluation")
    anchor = evaluation["anchor"]; packet = _validate_anchor(public, anchor, options)
    inner = object_keys(evaluation["audit"], "independentEvaluationSeeds samplingMethod deltaHpSupportCertificate specifiedBenefit formalLabelsAllowed maxDecisionsPerPolicy maxPosteriorAttempts familywiseAlpha evaluatorVersion objectiveProfileId".split(), "evaluation audit")
    _same_json(inner["independentEvaluationSeeds"], seeds, "planned_evaluation_seeds")
    for key in ("maxDecisionsPerPolicy", "maxPosteriorAttempts", "familywiseAlpha"): _equal(inner[key], options[key], "evaluation_" + key)
    for key, expected in {"formalLabelsAllowed": False, "samplingMethod": "independent_declared_setup_belief_worlds_paired_policy_execution", "deltaHpSupportCertificate": "finite-hunt-no-healing-v1:delta-loss-in-minus-anchor-hp-to-anchor-hp", "specifiedBenefit": "TheHunt fatal plus actual offered extra CardReward by fixed next-player-turn deadline", "evaluatorVersion": "nosl-anchored-hunt-full-v5-evaluator-v1", "objectiveProfileId": OBJECTIVE}.items():
        _equal(inner[key], expected, "evaluation_" + key)
    n = len(seeds)
    roots = {}
    for row in sequence(audit["sampled_public_roots"], "sampled public roots", n):
        object_keys(row, ("worldIndex", "publicRoot"), "sampled root")
        index = integer(row["worldIndex"], "root world index", 0, n - 1)
        _require(index not in roots, "duplicate_sampled_root")
        _same_json(row["publicRoot"], packet, "sampled_exact_full_public_root")
        roots[index] = row["publicRoot"]
    terminal = {}
    for row in sequence(audit["terminal_public_evidence"], "terminal public evidence", 2 * n):
        object_keys(row, ("worldIndex", "policyRole", "publicEvidence"), "terminal evidence entry")
        index = integer(row["worldIndex"], "terminal world index", 0, n - 1)
        _require(row["policyRole"] in ("baseline", "plan"), "terminal_policy_role")
        key = index, row["policyRole"]
        _require(key not in terminal and index in roots, "duplicate_or_unsampled_terminal_evidence")
        _validate_evidence(row["publicEvidence"], config)
        _same_json(row["publicEvidence"]["events"][:len(packet["publicEvidence"]["events"])], packet["publicEvidence"]["events"], "terminal_full_root_prefix")
        terminal[key] = row["publicEvidence"]
    worlds = sequence(evaluation["worlds"], "paired worlds", n)
    _require(len(worlds) == n, "every_planned_draw_requires_paired_world")
    seen, rows = set(), {"baseline": [], "plan": []}
    for pair in worlds:
        object_keys(pair, ("worldIndex", "baseline", "plan"), "paired world")
        index = integer(pair["worldIndex"], "paired world index", 0, n - 1)
        _require(index not in seen, "duplicate_paired_world_index"); seen.add(index)
        for role in rows:
            rows[role].append(_trajectory(pair[role], role, public, anchor, options, roots.get(index), terminal.get((index, role)), config))
    complete = {role: sum(row["settled"] for row in values) for role, values in rows.items()}
    success = sum(row["success"] for row in rows["plan"]) / n if complete["plan"] == n else None
    paired_count = sum(a["settled"] and b["settled"] for a, b in zip(rows["baseline"], rows["plan"]))
    delta = [a["outcome"]["hpAfterSettlement"] - b["outcome"]["hpAfterSettlement"] if a["settled"] and b["settled"] else None for a, b in zip(rows["baseline"], rows["plan"])]
    extra = sum(delta) / n if paired_count == n else None
    plan = targets["plan"]
    for key, expected in {"label_scope": "whole_plan_from_anchor", "allocated_worlds": n, "success_completed_worlds": complete["plan"], "paired_completed_worlds": paired_count, "specified_success_probability": success, "extra_net_hp_loss": extra}.items(): _equal(plan[key], expected, "target_" + key)
    _equal(plan["masks"], {"specified_success_probability": success is not None, "extra_net_hp_loss": extra is not None}, "target_masks")
    for row in targets["actions"]:
        object_keys(row, ("action_index", "quality", "masks", *HEADS, "allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds"), "finite Hunt action target")
        _equal(row["quality"], "unresolved", "action_quality")
        _require(all(row["masks"][head] is False and row[head] is None for head in HEADS), "action_heads_must_remain_unavailable")
        for key in ("allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds"):
            integer(row.get(key), "action_" + key, 0, 0)
    _require(targets["pairwise"] == [] and targets["equivalent_action_set"] == [], "ranking_not_available")
    baseline_scored = _summary(evaluation["baselineSummary"], rows["baseline"])
    plan_scored = _summary(evaluation["planSummary"], rows["plan"])
    utility = baseline_scored and plan_scored
    _equal(evaluation["meanSpecifiedSuccess"], success, "evaluation_success_mean")
    _equal(evaluation["meanExtraNetHpLoss"], extra, "evaluation_extra_hp_mean")
    _equal(evaluation["utilityComparisonMask"], utility, "utility_comparison_mask")
    _equal(evaluation["masks"], {"baselineTerminalTargets": complete["baseline"] == n, "planTerminalTargets": complete["plan"] == n, "meanExtraNetHpLoss": paired_count == n, "meanSpecifiedSuccess": complete["plan"] == n, "utilityComparison": utility}, "evaluation_masks")
    alpha, hp = options["familywiseAlpha"] / 3, public["observation"]["hp"]
    delta_bound = _bound(delta, -hp, hp, alpha)
    death_bound = _bound([int(b["outcome"]["playerAlive"] is False) - int(a["outcome"]["playerAlive"] is False) if a["settled"] and b["settled"] else None for a, b in zip(rows["baseline"], rows["plan"])], -1, 1, alpha)
    success_bound = _bound([int(row["success"]) if row["settled"] else None for row in rows["plan"]], 0, 1, alpha)
    for key, expected in (("extraExpectedLossInterval", delta_bound), ("excessDeathProbabilityInterval", death_bound), ("unconditionalSuccessInterval", success_bound)): _check_interval(evaluation[key], expected, key)
    safe = True if death_bound["upper"] <= 0 else False if death_bound["lower"] > 0 else None
    _equal(evaluation["safetyAcceptable"], safe, "safety_bound")
    eligibility = 1 if safe is False or delta_bound["lower"] > 5 or success_bound["upper"] < .8 else 0 if safe is True and delta_bound["upper"] <= 5 and success_bound["lower"] >= .8 else 2
    _equal(evaluation["eligibility"], eligibility, "eligibility_is_not_a_target")
    for limitation in sequence(evaluation["limitations"], "limitations"): _text(limitation, "limitation")
    return record


def adapt_record(record, config, *, source_raw_bytes):
    """Losslessly wrap one validated raw record in the existing engineering envelope."""
    _require(isinstance(source_raw_bytes, bytes), "exact_raw_source_bytes_required")
    try: source_text = source_raw_bytes.decode("utf-8")
    except UnicodeDecodeError: reject("finite_hunt_v5:raw_source_must_be_utf8")
    _same_json(loads(source_text), record, "raw_source_bytes")
    validate_raw_record(record, config)
    audit = deepcopy(record["audit_only"])
    audit.update(adapter_version=ADAPTER_VERSION, conditioned_public_input_digest=public_input_digest(record["public_input"]),
                 source_raw_sha256=_sha(source_raw_bytes), source_raw_utf8=source_text,
                 source_raw_record=deepcopy(record))
    result = {"schema_version": RECORD_SCHEMA, "record_kind": RECORD_KIND,
              "public_input": deepcopy(record["public_input"]), "targets": deepcopy(record["targets"]), "audit_only": audit}
    validate_record(result, config)
    return result


def validate_adapted_record(record, config):
    """Recheck raw evidence, byte binding and lossless adaptation, never admission."""
    validate_record(record, config)
    audit = record["audit_only"]
    _require(isinstance(audit.get("source_raw_utf8"), str), "adapted_raw_source_missing")
    raw = audit.get("source_raw_record")
    expected = adapt_record(raw, config, source_raw_bytes=audit["source_raw_utf8"].encode("utf-8"))
    _same_json(record, expected, "adapted_record")
    return record


def adapt_jsonl(payload, config):
    """Adapt exact individual JSONL line bytes, retaining all nonblank records."""
    _require(isinstance(payload, bytes), "exact_jsonl_bytes_required")
    result = []
    for line in payload.splitlines(keepends=True):
        if line.strip(): result.append(adapt_record(loads(line.decode("utf-8")), config, source_raw_bytes=line))
    _require(bool(result), "empty_raw_artifact")
    return result
