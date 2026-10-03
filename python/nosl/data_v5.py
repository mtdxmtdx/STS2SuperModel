"""Full-context v5 supervision and opt-in bounded auxiliary admission.

Existing engineering/raw envelopes remain quarantined. A separate new candidate
envelope requires an exact-bound reviewed cohort, complete outcomes and historic
protection. No old labels are upgraded; admission grants no fitting permission.
"""
from copy import deepcopy

from .data import validate_targets as validate_action_targets
from .evidence_v4 import numeric_guard
from .public_identity_v5 import public_input_digest
from .schema import boolean, integer, number, object_keys, reject
from .schema_regen import CONTEXT_SCHEMA as REGEN_CONTEXT_SCHEMA
from .schema_v2 import PLAN_HEADS
from .schema_v5 import validate_public

RECORD_SCHEMA = "nosl.public-map-complete-graph.engineering.v1"
RECORD_KIND = "public_complete_map_engineering"
QUARANTINE_REASON = "public_complete_map_v5_production_admission_not_implemented"
NATIVE_SOURCE_SCHEMA = "nosl.natural-source.v5"
NATIVE_SOURCE_KIND = "natural_raw_source_candidate"


@numeric_guard
def validate_targets(targets, public, config):
    """Validate masks against the full v5 input, with no context projection."""
    validate_public(public, config)
    if not isinstance(targets, dict): reject("v5 targets must be an object")
    keys = ["actions", "pairwise", "equivalent_action_set"]
    finite = public["controller_context"]["status"] != "inactive"
    if finite or "plan" in targets: keys.append("plan")
    object_keys(targets, keys, "v5 targets")
    validate_action_targets({key: targets[key] for key in keys if key != "plan"}, public,
                            config["base_config"]["base_config"]["base_config"]["base_config"])
    if "plan" not in targets: return targets
    plan = object_keys(targets["plan"], ["label_scope", *PLAN_HEADS, "masks", "allocated_worlds",
                                       "success_completed_worlds", "paired_completed_worlds"], "plan targets")
    object_keys(plan["masks"], PLAN_HEADS, "plan masks")
    allocated = integer(plan["allocated_worlds"], "allocated_worlds")
    successes = integer(plan["success_completed_worlds"], "success_completed_worlds", 0, allocated)
    paired = integer(plan["paired_completed_worlds"], "paired_completed_worlds", 0, successes)
    if plan["label_scope"] not in ("whole_plan_from_anchor", "unavailable"): reject("unknown plan label scope")
    current_anchor = deepcopy(public)
    current_anchor["controller_context"] = {"status": "inactive"}
    at_anchor = (finite and public["controller_context"]["status"] == "active"
                 and current_anchor == public["controller_context"]["anchor"])
    if plan["label_scope"] == "whole_plan_from_anchor" and not at_anchor:
        reject("whole-plan labels require the exact original active v5 anchor including all public evidence")
    for head, completed in zip(PLAN_HEADS, (successes, paired)):
        if not boolean(plan["masks"][head], "plan mask." + head):
            if plan[head] is not None: reject("missing plan targets must be null, not zero")
        else:
            if not at_anchor or plan["label_scope"] != "whole_plan_from_anchor" or allocated == 0 or completed != allocated:
                reject("unresolved or off-anchor mass cannot produce normalized plan labels")
            if head == "specified_success_probability": number(plan[head], head, 0, 1)
            else:
                hp = public["controller_context"]["anchor"]["observation"]["hp"]
                number(plan[head], head, -hp, hp)
    if public["controller_context"].get("schemaVersion") == REGEN_CONTEXT_SCHEMA:
        if (any(plan["masks"].values()) or targets["pairwise"] or targets["equivalent_action_set"]
                or any(any(row["masks"].values()) for row in targets["actions"])):
            reject("finite Regen evidence has no applicable learned target heads")
    return targets


def validate_record(record, config):
    object_keys(record, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "v5 engineering record")
    if record["schema_version"] != RECORD_SCHEMA or record["record_kind"] != RECORD_KIND:
        reject("explicit quarantined v5 engineering envelope required", "UNSUPPORTED")
    audit = record["audit_only"]
    if not isinstance(audit, dict) or audit.get("trainable") is not False:
        reject("v5 engineering rows must explicitly remain trainable:false")
    validate_targets(record["targets"], record["public_input"], config)
    if audit.get("conditioned_public_input_digest") != public_input_digest(record["public_input"]):
        reject("engineering labels must bind the full v5 conditioning input; legacy/context-stripped labels are invalid")
    return record


def validate_production_record(record, config, *, admission=None, cohort_records=None,
                               attempts=None, protection=None, require_usable=True):
    from .data import canonical_object_digest
    from .native_v5 import (CANDIDATE_SCHEMA, candidate_digest, has_usable_targets,
                            require, validate_admission, validate_candidate)
    from .protection_v5 import group_records, validate_protection
    if isinstance(record, dict) and record.get("schema_version") == CANDIDATE_SCHEMA:
        require(all(v is not None for v in (admission, cohort_records, attempts, protection)), "explicit_cohort_admission_required")
        validate_protection(protection)
        validate_admission(admission, cohort_records, attempts, canonical_object_digest(protection), config)
        validate_candidate(record, config)
        digests = [candidate_digest(r) for r in cohort_records]
        require(candidate_digest(record) in digests, "candidate_not_in_admitted_cohort")
        provenance = []
        for attempt in attempts:
            value = deepcopy(attempt["provenance"])
            value.setdefault("audit_only", {})["source_draw_seed"] = attempt["source_draw_seed"]
            provenance.append(value)
        groups, tokens, historic = group_records([*cohort_records, *provenance], protection["components"])
        require(all(len(owners) == 1 for owners in historic.values()), "historical_cross_split_bridge")
        protected = {t for c in protection["components"].values() for t in c["tokens"]}
        require(all(not tokens[g] & protected for g in groups[:len(cohort_records)]), "previously_observed_source_or_alias")
        require(not require_usable or has_usable_targets(record), "no_usable_auxiliary_targets")
        return record
    if isinstance(record, dict) and record.get("schema_version") == NATIVE_SOURCE_SCHEMA:
        validate_native_source_record(record, config)
    else:
        validate_record(record, config)
    reject(QUARANTINE_REASON, "UNSUPPORTED")


def validate_native_source_record(record, config):
    """Inspect a label-free native capture without inventing mask/utility rows.

    This only checks its explicit envelope and public schema. It does not verify
    publicity, trust audit/source claims, or promote it into an admitted corpus.
    """
    object_keys(record, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "v5 native source record")
    if record["schema_version"] != NATIVE_SOURCE_SCHEMA or record["record_kind"] != NATIVE_SOURCE_KIND:
        reject("explicit label-free v5 native-source envelope required", "UNSUPPORTED")
    object_keys(record["targets"], ("actions", "pairwise", "equivalent_action_set"), "raw source targets")
    if any(value != [] for value in record["targets"].values()): reject("raw native-source captures must remain label-free")
    audit = record["audit_only"]
    if not isinstance(audit, dict) or audit.get("trainable") is not False:
        reject("raw native-source captures require trainable:false and zero teacher labels")
    integer(audit.get("teacher_label_count"), "teacher_label_count", 0, 0)
    validate_public(record["public_input"], config)
    return record


# Version-specific immutable reader; this does not import a trainer or simulator.
from .prepare_v5 import PreparedDatasetV5
