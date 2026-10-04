"""V3 engineering validation only; every row remains production-quarantined.

Public context changes the conditioning information. Legacy labels are never
upgraded/projected here. Any provided engineering supervision must be bound to
the complete v3 semantic input, including finite anchors. There is no dataset
loader, preparation path, trainer or production-admission implementation.
"""
from copy import deepcopy

from .data import validate_targets as validate_action_targets
from .public_identity_v3 import public_input_digest
from .schema import boolean, integer, number, object_keys, reject
from .schema_regen import CONTEXT_SCHEMA as REGEN_CONTEXT_SCHEMA
from .schema_v2 import PLAN_HEADS
from .schema_v3 import validate_public

RECORD_SCHEMA = "nosl.public-run-context.engineering.v1"
RECORD_KIND = "public_run_context_engineering"
QUARANTINE_REASON = "public_run_context_v3_production_admission_not_implemented"


def validate_targets(targets, public, config):
    """Validate masks against the full v3 input, with no context projection."""
    validate_public(public, config)
    keys = ["actions", "pairwise", "equivalent_action_set"]
    finite = public["controller_context"]["status"] != "inactive"
    if finite or "plan" in targets: keys.append("plan")
    object_keys(targets, keys, "v3 targets")
    validate_action_targets({key: targets[key] for key in keys if key != "plan"}, public,
                            config["base_config"]["base_config"])
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
        reject("whole-plan labels require the exact original active v3 anchor including runContext")
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
    object_keys(record, ("schema_version", "record_kind", "public_input", "targets", "audit_only"), "v3 engineering record")
    if record["schema_version"] != RECORD_SCHEMA or record["record_kind"] != RECORD_KIND:
        reject("explicit quarantined v3 engineering envelope required", "UNSUPPORTED")
    audit = record["audit_only"]
    if not isinstance(audit, dict) or audit.get("trainable") is not False:
        reject("v3 engineering rows must explicitly remain trainable:false")
    validate_targets(record["targets"], record["public_input"], config)
    if audit.get("conditioned_public_input_digest") != public_input_digest(record["public_input"]):
        reject("engineering labels must bind the full v3 conditioning input; legacy/context-stripped labels are invalid")
    return record


def validate_production_record(record, config, **_kwargs):
    validate_record(record, config)
    reject(QUARANTINE_REASON, "UNSUPPORTED")
