"""Audit-only native collection admission; no simulator or learning dependencies."""
import hashlib
import math
import re

from .data import is_sha256
from .schema import integer, number, object_keys, reject

DATASET_VERSION = "nosl.dataset.native-pilot.v1"
COLLECTION_VERSION = "nosl.native-pilot-collection.v1"
ADMISSION_VERSION = "fresh-certified-v1"
SOURCE_PREFIX = "nosl-native-pilot/"
PROFILES = {
    "native-public-entry-reviewed-memory-exchangeable-v2",
    "native-public-entry-reviewed-memory-conditional-choice-v1",
    "native-public-entry-generation-potions-exchangeable-v1",
}


def _digest(*parts):
    return hashlib.sha256("".join(str(len(p.encode("utf-8"))) + ":" + p for p in parts).encode("utf-8")).hexdigest()


def source_run_identity(seed):
    return _digest("nosl.native-source-run.v1", "Silent", "10", seed)


def source_battle_identity(seed, act, floor, encounter):
    return _digest("nosl.native-source-battle.v1", source_run_identity(seed), str(act), str(floor), encounter)


def native_source_tokens(audit):
    """Recover historic native provenance even from invalid/development rows.

    Caller naming prefixes and sampler versions never alter the original run.
    The raw source seed and these identities stay outside the public input.
    """
    if not isinstance(audit, dict) or not isinstance(audit.get("actual_seed"), str) or not audit["actual_seed"]:
        return set()
    if audit.get("source_kind") != "natural" and audit.get("native_run") is not True:
        return set()
    seed = audit["actual_seed"]
    tokens = {"source_run_group:native-source-v1:" + source_run_identity(seed)}
    if (type(audit.get("act")) is int and type(audit.get("floor")) is int
            and isinstance(audit.get("encounter"), str) and audit["encounter"]):
        tokens.add("source_combat_id:native-battle-v1:" + source_battle_identity(seed, audit["act"], audit["floor"], audit["encounter"]))
    return tokens


def is_native_pilot(record):
    return isinstance(record, dict) and record.get("schema_version") == DATASET_VERSION


def validate_native_pilot(record):
    audit, public, targets = record["audit_only"], record["public_input"], record["targets"]
    if audit.get("source_kind") != "natural": reject("native_pilot_source_kind_invalid")
    if not isinstance(audit.get("versions"), dict): reject("versions_missing")
    if record.get("record_kind") != "fresh_native_pilot_teacher_candidate": reject("native_pilot_record_kind_invalid")
    collection = object_keys(audit.get("native_collection"), ["schema_version", "collection_id", "purpose",
        "protection_registry_sha256", "seed_namespace", "export_origin", "distribution_accepted", "formal_training_authorized"], "native collection")
    identifier = collection["collection_id"]
    if (collection["schema_version"] != COLLECTION_VERSION or not isinstance(identifier, str)
            or re.fullmatch(r"[a-zA-Z0-9_-]{1,100}", identifier) is None
            or collection["seed_namespace"] != SOURCE_PREFIX + identifier
            or collection["purpose"] not in ("engineering-smoke", "pilot")
            or not is_sha256(collection["protection_registry_sha256"])
            or collection["export_origin"] != "live_native_boundary"
            or collection["distribution_accepted"] is not False or collection["formal_training_authorized"] is not False):
        reject("native_pilot_collection_invalid")
    seed = audit.get("actual_seed")
    if not isinstance(seed, str) or re.fullmatch(re.escape(collection["seed_namespace"]) + r":(?:0|[1-9][0-9]*)", seed) is None:
        reject("native_pilot_fresh_source_namespace_required")
    for field in ("act", "floor", "decision_index"): integer(audit.get(field), field)
    if not isinstance(audit.get("encounter"), str) or not audit["encounter"]: reject("native_pilot_encounter_missing")
    if (audit.get("native_source_run_identity") != source_run_identity(seed)
            or audit.get("native_source_battle_identity") != source_battle_identity(seed, audit["act"], audit["floor"], audit["encounter"])):
        reject("native_pilot_source_identity_mismatch")
    for field, expected in (("native_run", True), ("native_default_start", True), ("history_from_room_entry", True),
        ("constructed_hp_override", False), ("constructed_deck_override", False), ("posterior_supported", True),
        ("source_seed_conditioning", False), ("formal_labels", False), ("trainable", True)):
        if audit.get(field) is not expected: reject("native_pilot_certificate_flag_invalid:" + field)
    if (audit.get("engineering_smoke") is not (collection["purpose"] == "engineering-smoke")
            or audit.get("posterior_evaluation") != "supported" or audit.get("posterior_reason") is not None
            or audit.get("label_status") != "fresh_native_pilot_teacher"
            or audit.get("natural_reachability_evidence") != "live_native_collector:" + identifier
            or audit.get("dataset_version") != DATASET_VERSION
            or audit.get("versions", {}).get("native_collection") != COLLECTION_VERSION
            or audit.get("posterior_profile") not in PROFILES
            or audit.get("posterior_implementation") != audit.get("sampler_version")):
        reject("native_pilot_certificate_metadata_invalid")
    # Choice profile, not an inferred public-field spelling, determines import path.
    choice = audit["posterior_profile"] == "native-public-entry-reviewed-memory-conditional-choice-v1"
    if choice != (public["observation"]["choice"] is not None): reject("native_pilot_choice_profile_mismatch")
    if choice and public["observation"]["choice"]["source"] not in ("Survivor", "Prepared", "Acrobatics", "ThinkingAhead", "DaggerThrow"):
        reject("native_pilot_choice_source_unreviewed")
    expected_import = "certified_owned_stable_origin_choice_replay_v1" if choice else "certified_detached_boundary_clone_v2"
    if audit.get("native_import") != expected_import: reject("native_pilot_import_profile_mismatch")
    pinned = {"simulator": "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0", "rules": "0.111.0",
        "endpoint": "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",
        "controller": "nosl.controller.inactive.v1", "sampler": "nosl-belief-dispatch-v6",
        "posterior_implementation": "nosl-belief-dispatch-v6", "teacher": "nosl-full-combat-teacher-v1:T0",
        "objective": "nosl_silent_a10_terminal_v4_candidate"}
    if (any(audit.get("versions", {}).get(k) != value for k, value in pinned.items())
            or audit.get("outside_combat_script") != "nosl-natural-public-script-v2"
            or audit.get("source_prior") != "native_sequential_run_silent_a10_public_entry_v2"
            or audit.get("objective_calibrated") is not False
            or audit.get("continuation_version") not in ("nosl-public-rules-v1", "nosl-public-rules-v2")
            or audit.get("combat_policy") not in ("nosl-public-rules-v1", "nosl-public-rules-v2")):
        reject("native_pilot_unreviewed_runtime_version")
    if targets["pairwise"] or targets["equivalent_action_set"] or audit.get("ranking_evidence") != "MASKED_NO_CERTIFIED_UTILITY_SUPPORT":
        reject("native_pilot_uncertified_ranking")
    if (audit.get("native_public_history") != public["observation"]["history"]
            or not any(e["kind"] == "native_entry_assets" for e in public["observation"]["history"])
            or not isinstance(audit.get("source_trace"), list) or not audit["source_trace"]):
        reject("native_pilot_history_missing_or_mismatched")
    if audit.get("teacher_label_count") != len(targets["actions"]) or audit.get("n_exploration") != 0:
        reject("native_pilot_bounded_teacher_required")
    samples = audit.get("outcome_samples")
    if not isinstance(samples, list) or len(samples) != len(targets["actions"]): reject("native_pilot_outcome_samples_missing")
    completed, unresolved = 0, 0
    for index, (sample, target) in enumerate(zip(samples, targets["actions"])):
        if not isinstance(sample, dict) or sample.get("action_index") != index or not isinstance(sample.get("outcomes"), list):
            reject("native_pilot_outcome_alignment_invalid")
        outcomes = sample["outcomes"]
        if len(outcomes) != target["allocated_worlds"]: reject("native_pilot_outcome_count_mismatch")
        counts = {"completed_worlds": 0, "truncated_worlds": 0, "error_worlds": 0, "other_worlds": 0}
        for outcome in outcomes:
            if not isinstance(outcome, dict): reject("native_pilot_outcome_invalid")
            terminal = outcome.get("terminalKind")
            if terminal in ("Win", "Loss"):
                if any(outcome.get(k) is not True for k in ("settlementComplete", "hpEventDiagnosticsComplete",
                    "resourceProvenanceComplete", "inventorySnapshotsComplete", "permanentChangesComplete")):
                    reject("native_pilot_incomplete_terminal_diagnostics")
                hp = integer(outcome.get("hpAfterSettlement"), "native final HP")
                initial_hp = integer(outcome.get("hpAtCombatStart"), "native entry HP")
                maximum = integer(outcome.get("maxHpAfterSettlement"), "native final max HP")
                damage = number(outcome.get("cumulativeHpDamage"), "native damage", 0, float("inf"))
                healing = number(outcome.get("healingReceived"), "native healing", 0, float("inf"))
                other = number(outcome.get("otherHpAdjustment"), "native HP adjustment", -float("inf"), float("inf"))
                if (hp > maximum or not math.isclose(hp, initial_hp - damage + healing + other, abs_tol=1e-7)
                        or outcome.get("playerAlive") is not (hp > 0)
                        or outcome.get("settlementProfileId") != pinned["endpoint"]
                        or outcome.get("continuationPolicyId") != audit.get("continuation_version")):
                    reject("native_pilot_terminal_diagnostics_inconsistent")
                for key in ("inventoryStart", "inventoryEnd"):
                    if not isinstance(outcome.get(key), list): reject("native_pilot_inventory_missing")
                    for item in outcome[key]:
                        if not isinstance(item, dict) or not isinstance(item.get("resourceId"), str): reject("native_pilot_inventory_invalid")
                        integer(item.get("count"), "native inventory quantity")
                counts["completed_worlds"] += 1
            elif terminal in ("ComputeTruncated", "EngineError", "PolicyNonterminating"):
                counts[{"ComputeTruncated": "truncated_worlds", "EngineError": "error_worlds", "PolicyNonterminating": "other_worlds"}[terminal]] += 1
            else: reject("native_pilot_unknown_outcome")
        if any(target[k] != value for k, value in counts.items()): reject("native_pilot_outcome_count_mismatch")
        if outcomes and counts["completed_worlds"] == len(outcomes):
            empirical = {"win_probability": sum(o["terminalKind"] == "Win" for o in outcomes) / len(outcomes),
                "death_probability": sum(o["playerAlive"] is False for o in outcomes) / len(outcomes),
                "expected_final_hp": sum(o["hpAfterSettlement"] for o in outcomes) / len(outcomes),
                "potion_net_change": sum(sum(x["count"] for x in o["inventoryEnd"]) - sum(x["count"] for x in o["inventoryStart"]) for o in outcomes) / len(outcomes)}
            for head, value in empirical.items():
                if target["masks"][head] and not math.isclose(target[head], value, rel_tol=1e-7, abs_tol=1e-7):
                    reject("native_pilot_empirical_target_mismatch:" + head)
            expected_distribution = [{"hp": hp, "probability": sum(o["hpAfterSettlement"] == hp for o in outcomes) / len(outcomes)}
                                     for hp in sorted({o["hpAfterSettlement"] for o in outcomes})]
            if target["masks"]["hp_distribution"] and target["hp_distribution"] != expected_distribution:
                reject("native_pilot_empirical_target_mismatch:hp_distribution")
        completed += counts["completed_worlds"]
        unresolved += len(outcomes) - counts["completed_worlds"]
    diagnostics = object_keys(audit.get("native_diagnostics"), ["certified_root", "outcome_diagnostics_required",
        "settled_worlds", "unresolved_worlds", "complete_hp_diagnostics", "complete_resource_diagnostics"], "native diagnostics")
    if (any(diagnostics[k] is not True for k in ("certified_root", "outcome_diagnostics_required", "complete_hp_diagnostics", "complete_resource_diagnostics"))
            or diagnostics["settled_worlds"] != completed or diagnostics["unresolved_worlds"] != unresolved):
        reject("native_pilot_diagnostics_mismatch")
    return record
