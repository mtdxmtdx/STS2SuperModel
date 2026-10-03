"""Quarantined action labels from the explicit constructed native-tape law.

Exact report, declaration, runtime and all-attempt evidence is retained and
revalidated on every load. Hashes bind bytes, not producer authenticity,
natural-run provenance, quality admission or permission to learn. Only the
public packet enters the model. No plan/ranking labels or receipts are invented.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path

from . import constructed_native_event_v5 as event_source
from .data_policy_v5 import OBJECTIVE_FORMAT, RECORD_FORMAT, SOURCE_FIELDS
from .data_v5 import validate_targets
from .evidence_v4 import numeric_guard
from .native_policy_v5 import evaluate_candidate_outcome
from .policy_v5 import digest, require, sha, validate_objective_identity, OBJECTIVE_IDENTITY_FIELDS
from .prepare_v5 import public_metadata
from .public_identity_v5 import public_input_digest
from .schema import HEADS, boolean, integer, number, object_keys
from .schema_v5 import loads, validate_config, validate_public

SOURCE_KIND = "constructed_native_tape_action_fixture_v1"
RAW_SOURCE_KIND = "constructed_under_explicit_label_tape_prior"
RAW_SCHEMA = "nosl.native-constructed-tape.raw-candidate.v1"
RAW_KIND = "native_constructed_tape_raw_candidate"
REPORT_SCHEMA = "nosl.native-constructed-tape.raw-report.v1"
CONTRACT_SCHEMA = "nosl.native-constructed-tape.collection-contract.v1"
PRIOR_SCHEMA = "nosl.constructed-native-map-rewards-state-tape-prior.v1"
EVIDENCE_FORMAT = "nosl.full-policy.constructed-native-tape.evidence.v1"
PURPOSE = "bounded-constructed-raw"
SAMPLER = "nosl-constructed-native-tape-conditional-v1-public-evidence-v2"
POSTERIOR = "owned-constructed-native-tape-conditional-v1-public-evidence-v2"
RUBY_SAMPLER = "nosl-constructed-native-tape-ruby-formation-v2-public-evidence-v2"
RUBY_POSTERIOR = "owned-constructed-native-tape-ruby-formation-v2-public-evidence-v2"
RUBY_DRAW_SAMPLER = "nosl-constructed-native-tape-ruby-draw-closure-v3-public-evidence-v2"
RUBY_DRAW_POSTERIOR = "owned-constructed-native-tape-ruby-draw-closure-v3-public-evidence-v2"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
UPSTREAM = "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0"
COUNTS = ("allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")
EXECUTION_FIELDS = ("requested_worlds", "candidate_worlds_returned", "executed_worlds")
NO_RANKING = "MASKED_NO_CERTIFIED_UTILITY_SUPPORT"
COST_SEMANTICS = "worlds_allocated_and_target_allocated_worlds_count_requested_outcome_slots_not_observed_allocations"
ROOT = Path(__file__).resolve().parents[2]
OBJECTIVE_SPEC_FILES = ("docs/spec/v4/config/objective_profile.candidate.json", "configs/objective_profile.candidate.json")
EVALUATOR_SOURCE_FILES = (
    "src/Nosl.Objectives/RolloutOutcome.cs", "src/Nosl.Objectives/ObjectiveEvaluator.cs",
    "src/Nosl.Objectives/PreferenceGates.cs", "src/Nosl.Worker/TeacherDataset.cs",
    "src/Nosl.Worker/CombatTeacher.cs", "src/Nosl.Worker/NativeConstructedTapePrior.cs",
    "src/Nosl.Worker/NativeConstructedTapeSource.cs", "src/Nosl.Worker/NativeConstructedTapeDataset.cs",
    "src/Nosl.Worker/NativeConstructedEventOwnerSetup.cs",
    "src/Nosl.Worker/NativePublicRubyFormationCondition.cs", "src/Nosl.Worker/NativePublicRubyFormationProposal.cs",
    "src/Nosl.Worker/NativePublicCombatPrefixCondition.cs", "src/Nosl.Worker/NativePublicCombatPrefixProposal.cs",
    "src/Nosl.Worker/NativePublicDrawPrefixCondition.cs", "src/Nosl.Worker/NativeInitialShuffleCondition.cs",
    "python/nosl/native_policy_v5.py", "python/nosl/constructed_native_policy_v5.py",
    "python/nosl/constructed_native_event_v5.py")
AUDIT_FIELDS = ("purpose", "trainable", "source_kind", *SOURCE_FIELDS,
    "source_artifact_sha256", "source_record_sha256", "conditioned_public_input_digest", "targets_sha256",
    "native_run", "producer_receipt_sha256", "objective_evaluations", "collection_summary",
    "all_attempts_metadata_sha256", "constructed_tape_evidence")


def _sha(payload):
    return hashlib.sha256(payload).hexdigest()


def _equal(actual, expected, name):
    require(digest(actual) == digest(expected), "constructed_tape:" + name)


def _text(value, name):
    require(isinstance(value, str) and bool(value.strip()), "constructed_tape:" + name)
    return value


def _nullable_text(value, name):
    require(value is None or isinstance(value, str), "constructed_tape:" + name)


def _source_hashes(paths):
    return {path: _sha((ROOT / path).read_bytes()) for path in paths}


def _fragments(text, *, array=False):
    """Keep exact nested JSON lexemes; never guess the producer's serialization.

    The caller first passes the entire payload through strict schema_v5.loads,
    which rejects duplicate keys and nonfinite numbers recursively.
    """
    decoder, cursor = json.JSONDecoder(), 0
    def whitespace(index):
        while index < len(text) and text[index] in " \t\r\n": index += 1
        return index
    cursor = whitespace(cursor)
    require(text[cursor] == ("[" if array else "{"), "constructed_tape:json_container")
    cursor += 1; result = [] if array else {}
    while True:
        cursor = whitespace(cursor)
        if text[cursor] == ("]" if array else "}"): break
        if not array:
            key, cursor = decoder.raw_decode(text, cursor)
            cursor = whitespace(cursor)
            require(text[cursor] == ":", "constructed_tape:json_member")
            cursor = whitespace(cursor + 1)
        start = cursor
        _, cursor = decoder.raw_decode(text, cursor)
        if array: result.append(text[start:cursor])
        else: result[key] = text[start:cursor]
        cursor = whitespace(cursor)
        if text[cursor] == ",": cursor += 1
        else: break
    return result


def _seeds(value, name):
    require(isinstance(value, list) and 1 <= len(value) <= 16, "constructed_tape:" + name)
    for seed in value: integer(seed, name, 0, 2**64 - 1)
    require(len(set(value)) == len(value), "constructed_tape:duplicate_" + name)
    return value



def _profile(report):
    """An explicit source-law and producer-version pair; no identifier upgrade."""
    prior = report["options"]["prior"]
    schema = prior.get("schemaVersion")
    contract = loads(report["collection_contract_json"])
    require(isinstance(contract, dict), "constructed_tape:collection_contract_object")
    sampler = contract.get("sampler_version")
    if schema == event_source.PRIOR_SCHEMA:
        require(sampler == event_source.SAMPLER, "constructed_tape:event_sampler_profile")
        return sampler, event_source.POSTERIOR, event_source.SOURCE_KIND
    require(schema == PRIOR_SCHEMA, "constructed_tape:ordinary_sampler_profile")
    ruby_profiles = {RUBY_SAMPLER: RUBY_POSTERIOR, RUBY_DRAW_SAMPLER: RUBY_DRAW_POSTERIOR}
    if sampler in ruby_profiles:
        require(prior["setup"]["encounter"] == "RubyRaiders", "constructed_tape:ruby_sampler_requires_declared_ruby")
        return sampler, ruby_profiles[sampler], SOURCE_KIND
    # Old Ruby reports retain their original sampler IDs and interpretation.
    require(sampler == SAMPLER, "constructed_tape:ordinary_sampler_profile")
    return sampler, POSTERIOR, SOURCE_KIND

def _contract(report):
    options = object_keys(report["options"],
        ("prior", "sourceDrawSeeds", "collectionId", "enableConditioning", "wallBudgetSeconds"), "constructed options")
    _text(options["collectionId"], "collection_id")
    require(len(options["collectionId"]) <= 100, "constructed_tape:collection_id_length")
    _seeds(options["sourceDrawSeeds"], "source_seeds")
    boolean(options["enableConditioning"], "enable_conditioning")
    integer(options["wallBudgetSeconds"], "wall_budget", 1, 900)
    prior = options["prior"]
    require(isinstance(prior, dict) and prior.get("schemaVersion") in (PRIOR_SCHEMA, event_source.PRIOR_SCHEMA),
        "constructed_tape:prior_schema")
    event_owned = prior["schemaVersion"] == event_source.PRIOR_SCHEMA
    object_keys(prior, ("schemaVersion", "setup", "sourcePolicyId", "sourceDecisionHorizon",
        "rootSelection", "decisionIndex", "primitiveLaw", "primitiveImplementation", "rootLaw", "setupLaw",
        *(("eventOwner",) if event_owned else ())), "constructed prior")
    _equal(prior["primitiveLaw"], "independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1", "primitive_law")
    _equal(prior["primitiveImplementation"], "sha256-address-expansion-with-explicit-conditioned-overrides-v1", "primitive_implementation")
    _equal(prior["rootLaw"], event_source.ROOT_LAW if event_owned else "one-declared-native-combat-fixed-public-stopping-rule-with-absence-v1", "root_law")
    _equal(prior["setupLaw"], event_source.SETUP_LAW if event_owned else "fixed-acts-base-deck-hp-maxhp-gold-potions-then-native-relic-acquisition-v1", "setup_law")
    integer(prior["sourceDecisionHorizon"], "source_horizon", 1, 100000)
    integer(prior["decisionIndex"], "decision_index", 0, 100000)
    require(prior["rootSelection"] in ("opening", "decision_index", "first_player_turn_2", "first_player_turn_3", "first_pending_choice")
            and (prior["rootSelection"] == "decision_index" or prior["decisionIndex"] == 0), "constructed_tape:root_selection")
    policies = ("nosl-public-rules-v1", "nosl-public-rules-v2", "nosl-public-rules-v3")
    require(prior["sourcePolicyId"] in policies, "constructed_tape:source_policy")
    setup = object_keys(prior["setup"], ("encounter", "deck", "potions", "relics", "hp", "maxHp", "gold"), "constructed setup")
    _text(setup["encounter"], "encounter")
    if event_owned: event_source.validate_owner_prior(prior)
    for key, low, high in (("deck", 1, 512), ("potions", 0, 16), ("relics", 0, 64)):
        if setup[key] is not None:
            require(isinstance(setup[key], list) and low <= len(setup[key]) <= high, "constructed_tape:setup_" + key)
            for item in setup[key]: _text(item, "setup_" + key)
    for key in ("hp", "maxHp", "gold"):
        if setup[key] is not None: integer(setup[key], key, 0 if key == "gold" else 1, 2**31 - 1)
    if setup["hp"] is not None and setup["maxHp"] is not None:
        require(setup["hp"] <= setup["maxHp"], "constructed_tape:setup_hp")
    teacher = object_keys(report["teacher_options"], ("mode", "continuationPolicyId", "explorationSeeds", "evaluationSeeds",
        "maxDecisions", "treeDepth", "maxPosteriorAttempts", "uctExploration", "formalLabels"), "constructed teacher")
    require(teacher["mode"] == "T0" and teacher["explorationSeeds"] == [] and teacher["formalLabels"] is False
            and teacher["continuationPolicyId"] in policies, "constructed_tape:independent_T0_only")
    _seeds(teacher["evaluationSeeds"], "evaluation_seeds")
    require(not set(options["sourceDrawSeeds"]) & set(teacher["evaluationSeeds"]), "constructed_tape:seed_overlap")
    integer(teacher["maxDecisions"], "max_decisions", 1, 300)
    integer(teacher["maxPosteriorAttempts"], "posterior_attempts", 1, 4096)
    integer(teacher["treeDepth"], "tree_depth", 0, 2**31 - 1)
    number(teacher["uctExploration"], "uct_exploration", 0, float("inf"))
    build_text = _text(report["build_receipt_json"], "build_receipt_json")
    build = object_keys(loads(build_text), ("upstream_commit", "wrapper_source_sha256", "vendor_source_sha256",
        "worker_assembly_sha256", "core_assembly_sha256", "runtime_version"), "constructed build identity")
    require(build["upstream_commit"] == UPSTREAM, "constructed_tape:upstream")
    for key in ("wrapper_source_sha256", "vendor_source_sha256", "worker_assembly_sha256", "core_assembly_sha256"):
        sha(build[key], key)
    _text(build["runtime_version"], "runtime_version")
    _equal(report["build_receipt_sha256"], _sha(build_text.encode("utf-8")), "exact_build_receipt_hash")
    _equal(report["runtime_dependencies"], build, "runtime_dependencies")
    contract_text = _text(report["collection_contract_json"], "collection_contract_json")
    _equal(report["collection_contract_sha256"], _sha(contract_text.encode("utf-8")), "exact_collection_contract_hash")
    contract = loads(contract_text)
    sampler, _, _ = _profile(report)
    _equal(contract, {"schema_version": CONTRACT_SCHEMA, "purpose": PURPOSE, "raw_schema": RAW_SCHEMA,
        "source_kind": RAW_SOURCE_KIND, "source_draw_domain": "nosl-constructed-tape-source-draw-v1",
        "source_draw_rule": "one_independent_recipe_per_requested_draw_no_replacement", "options": options,
        "teacher_options": teacher, "sampler_version": sampler, "build_receipt_sha256": report["build_receipt_sha256"],
        "runtime_dependencies": build, "source_group_semantics": "conservative_primitive_rng_family",
        "source_generation_json": report["source_generation_json"],
        "source_generation_identity": report["source_generation_identity"]}, "frozen_collection_contract")
    prior_text = _fragments(_fragments(contract_text)["options"])["prior"]
    _equal(report["prior_identity"], _sha(prior_text.encode("utf-8")), "exact_prior_identity")
    _generation(report)
    return options, teacher



def _generation(report):
    prior = report["options"]["prior"]
    text = _text(report["source_generation_json"], "source_generation_json")
    _equal(report["source_generation_identity"], _sha(text.encode("utf-8")), "exact_source_generation_hash")
    domains = {
        "native_run_seed": "NOSL-NATIVE-TAPE-V1:{RunSeed:X16}",
        "native_state_words": "sha256-u64le:4E4F534C54415031,TapeSeed,State0,State1,State2,State3",
        "rewards_words": "sha256-u64le:4E4F534C52574431,TapeSeed,InitialSeed,RawCursor",
        "map_words": "sha256-u64le:4E4F534C4D415031,TapeSeed,ActIndex,InitialSeed,RawCursor"}
    expected = {"schema_version": "nosl.native-constructed-tape.source-generation.v1",
        "source_prior_schema": prior["schemaVersion"], "setup": prior["setup"], "setup_law": prior["setupLaw"],
        "source_policy": prior["sourcePolicyId"], "source_script": "nosl-natural-public-script-v3",
        "primitive_law": prior["primitiveLaw"], "primitive_implementation": prior["primitiveImplementation"],
        "character": "Silent", "ascension": 10, "simulator": UPSTREAM, "random_domains": domains}
    if prior["schemaVersion"] == event_source.PRIOR_SCHEMA:
        expected["event_owner"] = event_source.validate_owner_prior(prior)
    _equal(loads(text), expected, "source_generation_contract")
    # The alias namespace is fixed by the producer's field order and ASCII
    # constants. Never derive it from caller-chosen whitespace/key order: that
    # would let the same primitive family acquire a new isolation alias.
    return json.dumps({"primitive_law": prior["primitiveLaw"],
        "primitive_implementation": prior["primitiveImplementation"], "random_domains": domains},
        ensure_ascii=True, separators=(",", ":"))


def _source_aliases(report, recipe):
    seed = f"NOSL-NATIVE-TAPE-V1:{recipe['runSeed']:016X}"
    # System.Text.Json's default encoder writes quotes inside string values as
    # \u0022. The only nested string below is the pinned ASCII namespace above.
    namespace = json.dumps(_generation(report), ensure_ascii=True, separators=(",", ":")).replace('\\"', '\\u0022')
    tail = ',"native_run_seed":"' + seed + '","tape_seed":' + str(recipe["tapeSeed"]) + "}"
    family = "constructed-native-tape-random-family-v1:" + _sha(('{"primitive_namespace":' + namespace + tail).encode())
    battle = "constructed-native-tape-configured-source-v1:" + _sha(('{"source_generation_identity":"'
        + report["source_generation_identity"] + '"' + tail).encode()) + "/combat:0"
    return family, battle

def _public(public, config, prior):
    validate_public(public, config)
    evidence, context = public["public_evidence"], public["observation"]["runContext"]
    require(public["controller_context"] == {"status": "inactive"} and public["history_complete"] is True
        and bool(public["candidate_actions"]) and all(public["legal_mask"]), "constructed_tape:all_legal_public_root")
    require(evidence["completeFromRunStart"] is False and context["completeFromRunStart"] is False
        and context["combatEntryIndex"] is None, "constructed_tape:no_natural_start_claim")
    require(evidence["events"][0]["ownerOrdinal"] is None
        and evidence["events"][0]["payload"] == {"kind": "gap", "reason": "run_start_not_observed"}
        and all(e["payload"]["kind"] not in ("run_started", "map", "map_chosen") for e in evidence["events"]),
        "constructed_tape:recorded_initial_gap_required")
    owners = [event for event in evidence["events"] if event["payload"]["kind"] == "owner_started"
        and event["payload"]["ownerKind"] == "combat"]
    require(len(owners) == 1 and owners[0]["payload"]["completeFromOwnerStart"] is True,
        "constructed_tape:one_complete_combat_owner")
    if prior["schemaVersion"] == event_source.PRIOR_SCHEMA:
        event_source.validate_public_owner(public, owners[0], prior)
    else:
        require(owners[0]["payload"]["parentOwnerOrdinal"] is None, "constructed_tape:ordinary_combat_has_no_event_parent")
        require(context["floor"] == owners[0]["payload"]["floor"] == 0
            and context["actIndex"] == owners[0]["payload"]["actIndex"], "constructed_tape:constructed_combat_coordinates")
    revision = public["candidate_actions"][0]["revision"]
    integer(revision, "root_revision", 0, prior["sourceDecisionHorizon"] - 1)
    if prior["schemaVersion"] == event_source.PRIOR_SCHEMA:
        require(revision + 3 <= prior["sourceDecisionHorizon"], "constructed_event:owner_choices_exceed_source_horizon")
    require(all(action["revision"] == revision for action in public["candidate_actions"])
        and sum(event["kind"] == "action" for event in public["observation"]["history"]) == revision,
        "constructed_tape:complete_local_action_history")
    decisions = [event["payload"] for event in evidence["events"] if event["ownerOrdinal"] == owners[0]["ownerOrdinal"]
        and event["payload"]["kind"] == "combat_decision"]
    require(len(decisions) == revision + 1 and all(decision["historyCompleteFromCombatStart"] is True
        and bool(decision["actions"]) and all(action["revision"] == index for action in decision["actions"])
        for index, decision in enumerate(decisions)), "constructed_tape:every_local_decision_required")
    def selected(index, decision):
        rule = prior["rootSelection"]
        if rule in ("opening", "decision_index"): return index == (0 if rule == "opening" else prior["decisionIndex"])
        if rule == "first_pending_choice": return decision["status"] == "card_choice"
        return decision["observation"]["turn"] >= int(rule[-1])
    matches = [index for index, decision in enumerate(decisions) if selected(index, decision)]
    require(bool(matches) and matches[0] == revision, "constructed_tape:first_stopping_rule_match_required")
    entries = [row["detail"] for row in public["observation"]["history"] if row["kind"] == "native_entry_assets"]
    require(len(entries) == 1, "constructed_tape:one_entry_anchor")
    entry = loads(entries[0])
    require(entry.get("schemaVersion") == "nosl.native-entry-assets.v1" and isinstance(entry.get("potions"), list),
        "constructed_tape:native_entry_anchor")
    integer(entry.get("hp"), "entry_hp", 1); integer(entry.get("maxHp"), "entry_max_hp", entry["hp"])
    _equal(entry["hp"], public["observation"]["startHp"], "fixed_entry_hp")
    return entry, entries[0]


def _accounting(value, seeds, actions=None, *, max_decisions):
    fields = ("sample_calls", "sample_calls_started", "sampled_worlds_returned", "candidate_worlds_returned",
              "fork_calls", "candidate_branches_started", "step_calls", "steps_returned")
    object_keys(value, (*fields, "samples"), "constructed execution accounting")
    samples = value["samples"]
    require(isinstance(samples, list) and len(samples) <= len(seeds), "constructed_tape:sample_count")
    totals = dict.fromkeys(fields, 0); by_action = {}
    for index, sample in enumerate(samples):
        object_keys(sample, ("worldSeed", "started", "worldReturned", "status", "detail", "cleanupStatus", "cleanupDetail", "branches"), "sample execution")
        _equal(sample["worldSeed"], seeds[index], "ordered_sample_seed")
        for key in ("started", "worldReturned"): boolean(sample[key], key)
        require(not sample["worldReturned"] or sample["started"], "constructed_tape:unstarted_sample_return")
        require(sample["status"] in ("sampled", "posterior_exhausted", "not_executed_computation_cancelled", "computation_truncated", "engine_error"), "constructed_tape:sample_status")
        _equal(sample["status"] == "sampled", sample["worldReturned"], "sample_return_status")
        require(sample["cleanupStatus"] in (None, "completed", "failed"), "constructed_tape:sample_cleanup")
        require((sample["cleanupStatus"] is not None) is sample["worldReturned"], "constructed_tape:sample_cleanup_ownership")
        for key in ("detail", "cleanupDetail"): _nullable_text(sample[key], key)
        branches = sample["branches"]
        require(isinstance(branches, list) and (sample["worldReturned"] or not branches)
            and (actions is None or len(branches) == (actions if sample["worldReturned"] else 0)),
            "constructed_tape:all_owned_candidate_branches_required")
        totals["sample_calls"] += 1; totals["sample_calls_started"] += sample["started"]
        totals["sampled_worlds_returned"] += sample["worldReturned"]
        for index, branch in enumerate(branches):
            object_keys(branch, ("actionIndex", "worldReturned", "stepCalls", "stepsReturned", "settlementRecorded", "failureStage", "detail", "cleanupStatus", "cleanupDetail"), "candidate execution")
            _equal(branch["actionIndex"], index, "ordered_branch_index")
            for key in ("worldReturned", "settlementRecorded"): boolean(branch[key], key)
            calls = integer(branch["stepCalls"], "step_calls", 0, max_decisions)
            returns = integer(branch["stepsReturned"], "steps_returned", 0, calls)
            require(branch["worldReturned"] or calls == 0, "constructed_tape:unowned_steps")
            require(not branch["settlementRecorded"] or branch["worldReturned"] and returns > 0, "constructed_tape:unexecuted_settlement")
            require(branch["failureStage"] in (None, "fork", "step", "settlement_record")
                and branch["cleanupStatus"] in (None, "completed", "failed"), "constructed_tape:branch_status")
            require((branch["cleanupStatus"] is not None) is branch["worldReturned"], "constructed_tape:branch_cleanup_ownership")
            for key in ("detail", "cleanupDetail"): _nullable_text(branch[key], key)
            totals["fork_calls"] += 1; totals["candidate_worlds_returned"] += branch["worldReturned"]
            totals["candidate_branches_started"] += int(calls > 0)
            totals["step_calls"] += calls; totals["steps_returned"] += returns
            counts = by_action.setdefault(index, [0, 0])
            counts[0] += branch["worldReturned"]; counts[1] += int(calls > 0)
    for field in fields:
        integer(value[field], field, 0, 2**63 - 1); _equal(value[field], totals[field], "execution_counter:" + field)
    return by_action



def _proposals(proposals, accounting, teacher, prior):
    require(isinstance(proposals, list), "constructed_tape:proposal_ledger")
    by_call = {}
    for proposal in proposals:
        object_keys(proposal, ("sampleCall", "attempt", "recipe", "status", "elapsedSeconds", "detail",
            "conditionedShuffles", "conditionedHpCount", "distinctTapeCells", "conditionedTapeCells"), "posterior proposal")
        call = integer(proposal["sampleCall"], "proposal_sample_call", 1, len(teacher["evaluationSeeds"]))
        attempt = integer(proposal["attempt"], "proposal_attempt", 1, teacher["maxPosteriorAttempts"])
        rows = by_call.setdefault(call, [])
        _equal(attempt, len(rows) + 1, "ordered_proposal_attempt")
        require(not rows or rows[-1]["status"] not in ("accepted", "proposal_engine_error", "computation_cancelled", "proposal_cleanup_error"),
            "constructed_tape:proposal_after_terminal_status")
        require(proposal["status"] in ("accepted", "absent_under_declared_source_horizon", "public_packet_mismatch",
            "exact_proposal_density_correction_rejected", "public_constraint_mismatch", "computation_cancelled",
            "proposal_engine_error", "proposal_cleanup_error"), "constructed_tape:proposal_status")
        number(proposal["elapsedSeconds"], "proposal_seconds", 0, float("inf"))
        _nullable_text(proposal["detail"], "proposal_detail")
        for key in ("conditionedShuffles", "conditionedHpCount", "distinctTapeCells", "conditionedTapeCells"):
            integer(proposal[key], key)
        if prior["schemaVersion"] == event_source.PRIOR_SCHEMA:
            require(all(proposal[key] == 0 for key in ("conditionedShuffles", "conditionedHpCount", "conditionedTapeCells")),
                "constructed_tape:event_ordinary_rejection_only")
        recipe = object_keys(proposal["recipe"], ("runSeed", "tapeSeed", "proposalSeed", "combatIndex", "decisionIndex"), "proposal recipe")
        for key in ("runSeed", "tapeSeed", "proposalSeed"): integer(recipe[key], key, 0, 2**64 - 1)
        _equal(recipe["combatIndex"], 0, "proposal_combat_index")
        _equal(recipe["decisionIndex"], prior["decisionIndex"], "proposal_decision_index")
        rows.append(proposal)
    if accounting is None:
        require(not proposals, "constructed_tape:proposals_without_teacher")
        return
    call = 0
    for sample in accounting["samples"]:
        if not sample["started"]: continue
        call += 1; rows = by_call.get(call, [])
        # Every entered protocol sampler executes at least one declared proposal,
        # even when that first proposal records cancellation or an engine error.
        require(bool(rows), "constructed_tape:missing_entered_sampler_proposals")
        _equal(rows[-1]["status"] == "accepted", sample["worldReturned"], "proposal_sample_acceptance")
        if sample["status"] == "posterior_exhausted":
            _equal(len(rows), teacher["maxPosteriorAttempts"], "posterior_exhaustion_denominator")
    _equal(sorted(by_call), list(range(1, call + 1)), "all_proposal_calls_owned")

def _quantities(items):
    require(isinstance(items, list), "constructed_tape:inventory_list")
    result = {}
    for item in items:
        object_keys(item, ("resourceId", "count"), "inventory quantity")
        key = _text(item["resourceId"], "resource_id")
        result[key] = result.get(key, 0) + integer(item["count"], "inventory_count")
    return {key: value for key, value in result.items() if value}


def _outcome(outcome, entry, policy):
    require(isinstance(outcome, dict), "constructed_tape:outcome_object")
    _equal(outcome.get("hpAtCombatStart"), entry["hp"], "outcome_fixed_hp")
    _equal(outcome.get("maxHpStart"), entry["maxHp"], "outcome_fixed_maxhp")
    _equal(outcome.get("continuationPolicyId"), policy, "outcome_continuation")
    evaluated = evaluate_candidate_outcome(outcome)
    kind = outcome.get("terminalKind")
    _equal(outcome.get("isTrueTerminal"), kind in ("Win", "Loss"), "true_terminal_fact")
    expected_start = {p: entry["potions"].count(p) for p in entry["potions"] if p is not None}
    _equal(_quantities(outcome.get("inventoryStart")), expected_start, "inventory_start_anchor")
    if kind in ("Win", "Loss"):
        require(all(outcome.get(key) is True for key in ("settlementComplete", "hpEventDiagnosticsComplete",
            "resourceProvenanceComplete", "inventorySnapshotsComplete", "permanentChangesComplete")), "constructed_tape:terminal_diagnostics")
        _equal(outcome.get("settlementProfileId"), ENDPOINT, "settled_endpoint")
        end = dict(expected_start)
        require(isinstance(outcome.get("resourceEvents"), list), "constructed_tape:resource_events")
        for event in outcome["resourceEvents"]:
            object_keys(event, ("kind", "resourceId", "quantity", "publicSource"), "resource event")
            require(event["kind"] in ("consumed", "generated", "discarded"), "constructed_tape:resource_event_kind")
            key = _text(event["resourceId"], "resource_id"); _text(event["publicSource"], "resource_source")
            quantity = integer(event["quantity"], "resource_quantity")
            end[key] = end.get(key, 0) + (quantity if event["kind"] == "generated" else -quantity)
        require(all(value >= 0 for value in end.values()), "constructed_tape:negative_inventory")
        _equal(_quantities(outcome.get("inventoryEnd")), {key: value for key, value in end.items() if value}, "inventory_ledger_closure")
        require(isinstance(outcome.get("permanentChanges"), list), "constructed_tape:permanent_changes")
        for change in outcome["permanentChanges"]:
            object_keys(change, ("kind", "amount", "publicSource"), "permanent change")
            _text(change["kind"], "permanent_kind"); _text(change["publicSource"], "permanent_source")
            number(change["amount"], "permanent_amount")
        _equal(sum(c["amount"] for c in outcome["permanentChanges"] if c["kind"] == "max_hp"),
               outcome.get("maxHpAfterSettlement", 0) - entry["maxHp"], "permanent_max_hp_ledger")
    else:
        require(kind in ("ComputeTruncated", "EngineError", "PolicyNonterminating")
            and outcome.get("settlementComplete") is False and outcome.get("hpAfterSettlement") is None
            and outcome.get("playerAlive") is None, "constructed_tape:incomplete_is_not_terminal_loss")
    return evaluated


def _targets(raw, teacher, entry, config):
    public, targets, audit = raw["public_input"], raw["targets"], raw["audit_only"]
    object_keys(targets, ("actions", "pairwise", "equivalent_action_set"), "constructed action targets")
    require(isinstance(targets["actions"], list), "constructed_tape:action_rows")
    projected = deepcopy(targets)
    for row in projected["actions"]:
        for field in EXECUTION_FIELDS:
            require(field in row, "constructed_tape:execution_field:" + field)
            row.pop(field)
    validate_targets(projected, public, config)
    require(targets["pairwise"] == targets["equivalent_action_set"] == []
        and audit.get("ranking_evidence") == NO_RANKING and audit.get("ranking_intervals") == [], "constructed_tape:no_ranking_certificate")
    seeds = teacher["evaluationSeeds"]
    by_action = _accounting(audit["execution_accounting"], seeds, len(targets["actions"]), max_decisions=teacher["maxDecisions"])
    _equal(audit["execution_accounting"]["sample_calls"], len(seeds), "all_evaluation_slots_attempted")
    samples = audit.get("outcome_samples")
    require(isinstance(samples, list) and len(samples) == len(targets["actions"]), "constructed_tape:outcome_samples")
    totals = dict.fromkeys(COUNTS, 0); evaluations = []
    for index, (row, sample) in enumerate(zip(targets["actions"], samples)):
        _equal(row["action_index"], index, "action_index")
        object_keys(sample, ("action_index", "world_seeds", "outcomes"), "action outcome sample")
        _equal(sample["action_index"], index, "sample_action_index")
        _equal(sample["world_seeds"], seeds, "ordered_outcome_seeds")
        outcomes = sample["outcomes"]
        require(isinstance(outcomes, list) and len(outcomes) == len(seeds), "constructed_tape:all_outcome_mass_required")
        values = [_outcome(o, entry, teacher["continuationPolicyId"]) for o in outcomes]
        counts = dict.fromkeys(COUNTS, 0); counts["allocated_worlds"] = len(outcomes)
        for world_index, outcome in enumerate(outcomes):
            kind = outcome["terminalKind"]
            if kind in ("Win", "Loss"):
                counts["completed_worlds"] += 1
                execution = audit["execution_accounting"]["samples"][world_index]
                require(execution["worldReturned"] and execution["cleanupStatus"] == "completed"
                    and index < len(execution["branches"]), "constructed_tape:terminal_unowned_world")
                branch = execution["branches"][index]
                require(branch["worldReturned"] and branch["stepCalls"] > 0 and branch["settlementRecorded"]
                    and branch["cleanupStatus"] == "completed", "constructed_tape:terminal_unsettled_branch")
            else: counts[{"ComputeTruncated": "truncated_worlds", "EngineError": "error_worlds", "PolicyNonterminating": "other_worlds"}[kind]] += 1
        for field in COUNTS:
            _equal(row.get(field), counts[field], "outcome_count:" + field); totals[field] += counts[field]
        _equal(row["requested_worlds"], len(seeds), "requested_worlds")
        for field, count in zip(EXECUTION_FIELDS[1:], by_action.get(index, (0, 0))):
            _equal(row[field], count, "action_execution_count:" + field)
        require(0 <= row["executed_worlds"] <= row["candidate_worlds_returned"] <= row["requested_worlds"], "constructed_tape:action_execution_bounds")
        cost = -sum(v["cost"] for v in values) / len(values) if all(v["cost"] is not None for v in values) else None
        if counts["completed_worlds"] != len(outcomes):
            require(row["quality"] == "unresolved" and not any(row["masks"].values()), "constructed_tape:incomplete_mass_has_targets")
        else:
            expected = {"value": cost,
                "win_probability": sum(o["terminalKind"] == "Win" for o in outcomes) / len(outcomes),
                "death_probability": sum(o["playerAlive"] is False for o in outcomes) / len(outcomes),
                "expected_final_hp": sum(o["hpAfterSettlement"] for o in outcomes) / len(outcomes),
                "potion_net_change": sum(sum(i["count"] for i in o["inventoryEnd"]) - sum(i["count"] for i in o["inventoryStart"]) for o in outcomes) / len(outcomes)}
            for head, value in expected.items():
                if row["masks"][head]:
                    require(value is not None and math.isclose(row[head], value, rel_tol=1e-12, abs_tol=1e-9), "constructed_tape:empirical_target:" + head)
            distribution = [{"hp": hp, "probability": sum(o["hpAfterSettlement"] == hp for o in outcomes) / len(outcomes)}
                for hp in sorted({o["hpAfterSettlement"] for o in outcomes})]
            if row["masks"]["hp_distribution"]:
                require(row["hp_distribution"] == distribution, "constructed_tape:empirical_hp_distribution")
        evaluations.append({"action_index": index, "evaluations": values, "empirical_value": cost,
                            "source_value_mask": row["masks"]["value"]})
    _equal(audit["n_error"], totals["error_worlds"], "error_count")
    _equal(audit["n_unresolved"], totals["truncated_worlds"] + totals["other_worlds"], "unresolved_count")
    costs = audit["costs"]
    object_keys(costs, ("root_candidates", "worlds_allocated", "worlds_completed", "rollout_decisions", "elapsed_seconds", "clone_seconds",
        "settlement_seconds", "peak_worker_memory_bytes", "requested_outcome_slots", "sampled_worlds_returned", "candidate_worlds_returned", "candidate_branches_started"), "teacher costs")
    for field, value in costs.items():
        if field in ("elapsed_seconds", "clone_seconds", "settlement_seconds"):
            number(value, "cost." + field, 0, float("inf"))
        else: integer(value, "cost." + field, 0, 2**63 - 1)
    _equal(costs["rollout_decisions"], audit["execution_accounting"]["steps_returned"], "successful_rollout_decisions")
    _equal(costs["root_candidates"], len(samples), "cost_root_candidates")
    for key, expected in (("worlds_allocated", totals["allocated_worlds"]), ("requested_outcome_slots", totals["allocated_worlds"]), ("worlds_completed", totals["completed_worlds"])):
        _equal(costs[key], expected, "cost:" + key)
    for key in ("sampled_worlds_returned", "candidate_worlds_returned", "candidate_branches_started"):
        _equal(costs[key], audit["execution_accounting"][key], "observed_cost:" + key)
    return projected, evaluations, totals


def _record(raw, raw_text, report, teacher, config):
    object_keys(raw, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "constructed raw record")
    require(raw["schema_version"] == RAW_SCHEMA and raw["record_kind"] == RAW_KIND, "constructed_tape:new_raw_envelope_required")
    public, audit = raw["public_input"], raw["audit_only"]
    require(isinstance(audit, dict), "constructed_tape:audit_required")
    entry, entry_text = _public(public, config, report["options"]["prior"])
    sampler, posterior, _ = _profile(report)
    for field, value in (("source_kind", RAW_SOURCE_KIND), ("purpose", PURPOSE), ("dataset_version", RAW_SCHEMA),
                         ("posterior_implementation", sampler), ("sampler_version", sampler), ("posterior_profile", posterior),
                         ("teacher_cost_semantics", COST_SEMANTICS), ("label_endpoint", ENDPOINT),
                         ("objective_version", "nosl_silent_a10_terminal_v4_candidate")):
        _equal(audit.get(field), value, "raw_identity:" + field)
    for field, value in (("native_run", True), ("native_default_start", False), ("source_seed_conditioning", False),
                         ("trainable", False), ("formal_labels", False), ("objective_calibrated", False),
                         ("independent_final_evaluation", True)):
        require(audit.get(field) is value, "constructed_tape:raw_flag:" + field)
    for field in (*SOURCE_FIELDS, "actual_seed", "draw_id"):
        _text(audit.get(field), field)
    integer(audit.get("source_draw_seed"), "source_draw_seed", 0, 2**64 - 1)
    revision = integer(audit.get("selected_public_decision_index"), "public_decision_index")
    require(all(action["revision"] == revision for action in public["candidate_actions"])
        and sum(event["kind"] == "action" for event in public["observation"]["history"]) == revision,
        "constructed_tape:public_decision_coordinate")
    prior = report["options"]["prior"]
    _equal(audit.get("declared_prior"), prior, "row_declared_prior")
    _equal(audit.get("source_root_selection"), prior["rootSelection"], "row_root_rule")
    if prior["rootSelection"] in ("opening", "decision_index"):
        _equal(revision, 0 if prior["rootSelection"] == "opening" else prior["decisionIndex"], "selected_revision_rule")
    if prior["rootSelection"] in ("first_player_turn_2", "first_player_turn_3"):
        require(public["observation"]["turn"] >= int(prior["rootSelection"][-1]), "constructed_tape:selected_turn_rule")
    if prior["rootSelection"] == "first_pending_choice":
        require(public["observation"].get("choice") is not None, "constructed_tape:selected_choice_rule")
    for field in ("collection_contract_sha256", "build_receipt_sha256", "runtime_dependencies", "source_generation_json", "source_generation_identity"):
        _equal(audit.get(field), report[field], "row_report_binding:" + field)
    _equal(audit.get("source_prior_identity"), report["prior_identity"], "row_prior_identity")
    _equal(audit.get("teacher_options"), teacher, "row_teacher_options")
    _equal(audit.get("sampler_seeds"), teacher["evaluationSeeds"], "row_sampler_seeds")
    _equal(audit.get("exploration_seeds"), [], "row_exploration_seeds")
    _equal(audit.get("n_exploration"), 0, "row_no_exploration")
    _equal(audit.get("n_exploration_sampling_failures"), 0, "row_no_exploration_failures")
    _equal(audit.get("n_independent_eval"), len(teacher["evaluationSeeds"]), "row_evaluation_count")
    _equal(audit.get("versions"), {"posterior_implementation": sampler, "teacher": "nosl-full-combat-teacher-v1:T0",
        "continuation": teacher["continuationPolicyId"], "objective": "nosl_silent_a10_terminal_v4_candidate",
        "public_schema": "nosl.student.public.v5", "observation_schema": "nosl.public.v3", "simulator": UPSTREAM,
        "dataset": RAW_SCHEMA, "public_evidence": "nosl.public-run-evidence.v2", "rules": "0.111.0",
        "endpoint": ENDPOINT, "controller": "nosl.controller.inactive.v1", "sampler": sampler,
        "source_prior": report["prior_identity"], "posterior_profile": posterior}, "row_versions")
    boolean(audit.get("conditioning_eligible"), "conditioning_eligible")
    require(report["options"]["enableConditioning"] or audit["conditioning_eligible"] is False,
        "constructed_tape:undeclared_conditioning")
    _text(audit.get("conditioning_reason"), "conditioning_reason")
    if prior["schemaVersion"] == event_source.PRIOR_SCHEMA:
        require(audit["conditioning_eligible"] is False and audit["conditioning_reason"] == event_source.CONDITIONING_REASON,
            "constructed_tape:event_ordinary_rejection_only")
    public_text = _fragments(raw_text)["public_input"]
    _equal(audit["public_state_digest"], _sha(public_text.encode("utf-8")), "exact_raw_public_hash")
    _equal(audit["source_combat_id"], audit["source_run_group"] + "/public-entry:" + _sha(entry_text.encode("utf-8")), "source_combat_binding")
    _equal(audit["branch_family"], audit["source_combat_id"] + "/tape-root-family", "source_branch_binding")
    return _targets(raw, teacher, entry, config)


@numeric_guard
def _inspect(payload, config):
    require(isinstance(payload, bytes) and bool(payload), "constructed_tape:exact_report_bytes_required")
    validate_config(config)
    report_text = payload.decode("utf-8")
    report = loads(report_text)
    object_keys(report, ("schema_version", "status", "purpose", "source_kind", "selection",
        "collection_contract_json", "collection_contract_sha256", "build_receipt_json", "build_receipt_sha256",
        "runtime_dependencies", "options", "teacher_options", "prior_identity", "source_generation_json", "source_generation_identity", "source_draws_requested",
        "source_worlds_returned", "existing_roots", "source_worlds_disposed", "recorded_roots",
        "distinct_recorded_public_roots", "sampled_worlds_returned", "candidate_worlds_returned",
        "candidate_branches_started", "settled_candidate_outcomes", "elapsed_seconds", "peak_worker_memory_bytes",
        "budget_expired", "trainable", "formal_labels", "attempts", "records"), "constructed raw report")
    require(report["schema_version"] == REPORT_SCHEMA
        and report["status"] == "bounded_constructed_raw_requires_separate_quality_admission"
        and report["purpose"] == PURPOSE and report["source_kind"] == RAW_SOURCE_KIND
        and report["selection"] == "predeclared_all_attempts_no_replacement"
        and report["trainable"] is False and report["formal_labels"] is False, "constructed_tape:report_identity")
    boolean(report["budget_expired"], "budget_expired")
    number(report["elapsed_seconds"], "elapsed_seconds", 0, float("inf"))
    integer(report["peak_worker_memory_bytes"], "peak_worker_memory_bytes", 0, 2**63 - 1)
    options, teacher = _contract(report)
    require(isinstance(report["records"], list) and len(report["records"]) <= len(options["sourceDrawSeeds"]), "constructed_tape:records_bound")
    texts = _fragments(_fragments(report_text)["records"], array=True)
    inspected = [_record(raw, text, report, teacher, config) for raw, text in zip(report["records"], texts)]
    attempts = report["attempts"]
    attempt_texts = _fragments(_fragments(report_text)["attempts"], array=True)
    require(isinstance(attempts, list) and len(attempts) == len(options["sourceDrawSeeds"]), "constructed_tape:all_attempts_required")
    metadata, retained, exclusions, status_counts = [], [], [], {}
    totals = dict.fromkeys(("source_worlds_returned", "existing_roots", "source_worlds_disposed", "sampled_worlds_returned",
                            "candidate_worlds_returned", "candidate_branches_started"), 0)
    statuses = ("recorded_complete", "absent", "posterior_exhausted", "computation_truncated", "engine_error", "unsupported_capability", "not_executed")
    for index, attempt in enumerate(attempts):
        object_keys(attempt, ("draw_id", "source_draw_seed", "status", "source_status", "stage", "detail", "recipe",
            "raw_record_index", "source_opened", "root_observed", "source_disposed", "elapsed_seconds",
            "execution_accounting", "posterior_proposals", "provenance"), "constructed source attempt")
        _equal(attempt["draw_id"], options["collectionId"] + "/draw:" + str(index), "ordered_draw_id")
        _equal(attempt["source_draw_seed"], options["sourceDrawSeeds"][index], "ordered_source_seed")
        require(attempt["status"] in statuses, "constructed_tape:attempt_status")
        status_counts[attempt["status"]] = status_counts.get(attempt["status"], 0) + 1
        for key in ("source_status", "stage"): _text(attempt[key], key)
        _nullable_text(attempt["detail"], "attempt_detail")
        number(attempt["elapsed_seconds"], "attempt_seconds", 0, float("inf"))
        for key in ("source_opened", "root_observed", "source_disposed"): boolean(attempt[key], key)
        require(not (attempt["root_observed"] or attempt["source_disposed"]) or attempt["source_opened"], "constructed_tape:unopened_source_facts")
        for counter, flag in (("source_worlds_returned", "source_opened"), ("existing_roots", "root_observed"), ("source_worlds_disposed", "source_disposed")):
            totals[counter] += attempt[flag]
        recipe = object_keys(attempt["recipe"], ("runSeed", "tapeSeed", "proposalSeed", "combatIndex", "decisionIndex"), "source recipe")
        for key in ("runSeed", "tapeSeed", "proposalSeed"): integer(recipe[key], key, 0, 2**64 - 1)
        _equal(recipe["combatIndex"], 0, "constructed_combat_index")
        _equal(recipe["decisionIndex"], options["prior"]["decisionIndex"], "recipe_decision_index")
        provenance = attempt["provenance"]
        object_keys(provenance, ("audit_only", "public_input") if attempt["root_observed"] else ("audit_only",), "attempt provenance")
        audit = provenance["audit_only"]
        object_keys(audit, ("actual_seed", "source_run_group", "source_kind", "source_prior_identity", "native_run", "native_default_start",
            "source_group_semantics", "underlying_battle_alias", "source_random_family_alias", "source_generation_identity",
            *(("source_combat_id", "branch_family", "public_state_digest", "public_root_alias", "selected_public_decision_index") if attempt["root_observed"] else ())), "attempt audit")
        _equal(audit["actual_seed"], f"NOSL-NATIVE-TAPE-V1:{recipe['runSeed']:016X}:{recipe['tapeSeed']:016X}", "source_recipe_identity")
        _equal(audit["source_kind"], RAW_SOURCE_KIND, "attempt_source_kind")
        _equal(audit["source_prior_identity"], report["prior_identity"], "attempt_prior_identity")
        require(audit["native_run"] is True and audit["native_default_start"] is False, "constructed_tape:attempt_source_flags")
        family, battle = _source_aliases(report, recipe)
        _equal(audit["source_generation_identity"], report["source_generation_identity"], "attempt_generation_identity")
        _equal(audit["source_group_semantics"], "conservative_primitive_rng_family", "source_group_semantics")
        _equal(audit["source_run_group"], family, "source_run_group")
        _equal(audit["source_random_family_alias"], family, "source_random_family_alias")
        _equal(audit["underlying_battle_alias"], battle, "underlying_battle_alias")
        if attempt["root_observed"]:
            _, entry_text = _public(provenance["public_input"], config, options["prior"])
            public_text = _fragments(_fragments(attempt_texts[index])["provenance"])["public_input"]
            _equal(audit["public_state_digest"], _sha(public_text.encode("utf-8")), "attempt_exact_public_hash")
            _equal(audit["public_root_alias"], "constructed-native-tape-public-root-v1:" + audit["public_state_digest"], "attempt_public_root_alias")
            _equal(audit["source_combat_id"], family + "/public-entry:" + _sha(entry_text.encode()), "attempt_combat_id")
            _equal(audit["branch_family"], audit["source_combat_id"] + "/tape-root-family", "attempt_branch_family")
        accounting = attempt["execution_accounting"]
        if accounting is not None:
            require(attempt["source_disposed"] and attempt["root_observed"], "constructed_tape:teacher_before_source_disposal")
            _accounting(accounting, teacher["evaluationSeeds"], len(provenance["public_input"]["candidate_actions"]), max_decisions=teacher["maxDecisions"])
            for key in ("sampled_worlds_returned", "candidate_worlds_returned", "candidate_branches_started"): totals[key] += accounting[key]
        if attempt["posterior_proposals"] is None:
            require(accounting is None, "constructed_tape:missing_posterior_ledger")
        else:
            _proposals(attempt["posterior_proposals"], accounting, teacher, options["prior"])
        raw_index = attempt["raw_record_index"]
        if raw_index is None:
            require(attempt["status"] != "recorded_complete", "constructed_tape:missing_complete_record")
            exclusions.append({"draw_id": attempt["draw_id"], "status": attempt["status"], "stage": attempt["stage"]})
        else:
            integer(raw_index, "raw_record_index", 0, len(report["records"]) - 1)
            _equal(raw_index, len(retained), "ordered_record_index")
            raw = report["records"][raw_index]; retained.append(raw_index)
            require(attempt["source_disposed"] and attempt["root_observed"] and accounting is not None, "constructed_tape:record_source_release")
            for field, value in audit.items(): _equal(raw["audit_only"].get(field), value, "attempt_record_identity:" + field)
            _equal(raw["public_input"], provenance["public_input"], "attempt_record_public")
            for field in ("execution_accounting", "posterior_proposals", "draw_id", "source_draw_seed"):
                _equal(raw["audit_only"].get(field), attempt[field], "attempt_record_binding:" + field)
            kinds = [o["terminalKind"] for sample in raw["audit_only"]["outcome_samples"] for o in sample["outcomes"]]
            expected_status = ("engine_error" if "EngineError" in kinds else "posterior_exhausted"
                if any(s["status"] == "posterior_exhausted" for s in accounting["samples"]) else "computation_truncated"
                if any(kind not in ("Win", "Loss") for kind in kinds) else "recorded_complete")
            _equal(attempt["status"], expected_status, "attempt_outcome_status")
        meta = public_metadata(provenance)
        meta["audit_only"]["source_draw_seed"] = attempt["source_draw_seed"]
        metadata.append(meta)
    _equal(retained, list(range(len(report["records"]))), "all_raw_records_owned")
    for key, value in totals.items(): _equal(report[key], value, "report_count:" + key)
    for key, value in (("source_draws_requested", len(attempts)), ("recorded_roots", len(retained)),
        ("distinct_recorded_public_roots", len({r["audit_only"]["public_state_digest"] for r in report["records"]})),
        ("settled_candidate_outcomes", sum(item[2]["completed_worlds"] for item in inspected))):
        _equal(report[key], value, "report_count:" + key)
    summary = {"requested_sources": len(attempts), "retained_records": len(retained), "status_counts": status_counts,
        "exclusions": exclusions, "source_run_groups": sorted({m["audit_only"]["source_run_group"] for m in metadata}),
        "selection": report["selection"], "protection_status": "INCOMPLETE", "admitted": False}
    return report, texts, inspected, metadata, summary


def _normalize(payload, config):
    report, texts, inspected, metadata, summary = _inspect(payload, config)
    _, _, source_kind = _profile(report)
    objective_identity = {"format": OBJECTIVE_FORMAT,
        "objective_spec_sha256": digest(_source_hashes(OBJECTIVE_SPEC_FILES)),
        "evaluator_source_sha256": digest(_source_hashes(EVALUATOR_SOURCE_FILES)),
        "calibration_evidence_sha256": None, "endpoint": ENDPOINT,
        "continuation_policy_id": report["teacher_options"]["continuationPolicyId"],
        "independent_final_evaluation": True, "public_conditioning_scope": "full_v5",
        "objective_profile_status": "candidate", "objective_calibrated": False}
    validate_objective_identity(objective_identity)
    result = []
    for index, (raw, raw_text, (targets, evaluations, _)) in enumerate(zip(report["records"], texts, inspected)):
        public = raw["public_input"]
        audit = {field: deepcopy(raw["audit_only"][field]) for field in SOURCE_FIELDS}
        audit.update(purpose="engineering-fixture", trainable=False, source_kind=source_kind, native_run=False,
            producer_receipt_sha256=None, source_artifact_sha256=_sha(payload),
            source_record_sha256=_sha(raw_text.encode("utf-8")), conditioned_public_input_digest=public_input_digest(public),
            targets_sha256=digest(targets), objective_evaluations=evaluations, collection_summary=deepcopy(summary),
            all_attempts_metadata_sha256=digest(metadata), constructed_tape_evidence={"format": EVIDENCE_FORMAT,
                "report_utf8": payload.decode("utf-8"), "raw_record_index": index})
        objective = deepcopy(objective_identity)
        objective["evaluation_design_sha256"] = digest({"exact_report_sha256": _sha(payload),
            "raw_record_index": index, "collection_contract_sha256": report["collection_contract_sha256"],
            "build_receipt_sha256": report["build_receipt_sha256"], "all_attempts_metadata_sha256": digest(metadata)})
        result.append({"format": RECORD_FORMAT, "public_input": deepcopy(public), "targets": targets,
                       "audit_only": audit, "objective": objective})
    return result


@numeric_guard
def validate_raw_report(payload, config):
    """Validate a complete bounded report, including a zero-record denominator."""
    return _inspect(payload, config)[0]


@numeric_guard
def adapt_report(payload, config):
    """Normalize every retained raw row; never select away an incomplete row."""
    return _normalize(payload, config)


@numeric_guard
def validate_record(record, config):
    object_keys(record, ("format", "public_input", "targets", "audit_only", "objective"), "constructed full-policy record")
    audit = object_keys(record["audit_only"], AUDIT_FIELDS, "constructed full-policy audit")
    evidence = object_keys(audit["constructed_tape_evidence"], ("format", "report_utf8", "raw_record_index"), "constructed exact evidence")
    require(evidence["format"] == EVIDENCE_FORMAT and isinstance(evidence["report_utf8"], str), "constructed_tape:exact_evidence_required")
    expected = _normalize(evidence["report_utf8"].encode("utf-8"), config)
    index = integer(evidence["raw_record_index"], "raw_record_index", 0, len(expected) - 1)
    _equal(record, expected[index], "record_regeneration_mismatch")
    return record


def all_attempt_metadata(record, config):
    """Conservative source closure, with failed/excluded/unexecuted draws.

    Raw native execution flags remain diagnostic metadata. The normalized record
    is engineering-only native_run:false and this creates no producer receipt.
    """
    validate_record(record, config)
    payload = record["audit_only"]["constructed_tape_evidence"]["report_utf8"].encode("utf-8")
    return deepcopy(_inspect(payload, config)[3])
