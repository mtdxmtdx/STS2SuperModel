"""Opt-in v4 student boundary; legacy v1/v2/v3 envelopes remain unchanged."""
from __future__ import annotations

from copy import deepcopy
import json
import math
from pathlib import Path

from . import schema_v3
from .evidence_v4 import EVIDENCE_SCHEMA, numeric_guard, validate_current_history, validate_evidence
from .schema import SchemaError, integer, object_keys, reject

PUBLIC_SCHEMA = "nosl.student.public.v4"
CONFIG_VERSION = "nosl.student.config.v4"
MODEL_VERSION = "nosl.student.model.v4"
FEATURE_VERSION = "nosl.public-run-evidence.features.v1"
MAX_JSON_BYTES = 64 * 1024 * 1024


def loads(text):
    """Transport entry point: dictionaries cannot retain duplicate JSON keys."""
    if not isinstance(text, str) or len(text.encode("utf-8")) > MAX_JSON_BYTES:
        reject("v4 JSON exceeds bounded transport limit")
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: reject("duplicate JSON property: " + key)
            result[key] = value
        return result
    def constant(value): reject("non-finite JSON constant: " + value)
    try: result = json.loads(text, object_pairs_hook=pairs, parse_constant=constant)
    except (ValueError, RecursionError) as error:
        if isinstance(error, SchemaError): raise
        reject("invalid bounded v4 JSON: " + str(error))
    # Existing public combat history contains encoded JSON. Apply the same
    # duplicate/non-finite checks before a frozen validator sees those strings.
    def walk(value):
        if isinstance(value, float) and not math.isfinite(value): reject("non-finite JSON number")
        if isinstance(value, list):
            for item in value: walk(item)
        elif isinstance(value, dict):
            if isinstance(value.get("detail"), str) and value["detail"].lstrip().startswith(("{", "[")):
                try: walk(json.loads(value["detail"], object_pairs_hook=pairs, parse_constant=constant))
                except (ValueError, RecursionError) as error:
                    if isinstance(error, SchemaError): raise
                    reject("invalid encoded public event JSON")
            for item in value.values(): walk(item)
    try: walk(result)
    except RecursionError: reject("v4 JSON nesting limit exceeded")
    return result


def load_config(path: str | Path):
    return validate_config(loads(Path(path).read_text(encoding="utf-8")))


@numeric_guard
def validate_config(config):
    object_keys(config, ("config_version", "schema_version", "model_version", "base_config",
                         "evidence_feature_version", "evidence_hash_dim", "evidence_max_events",
                         "production_admission"), "v4 config")
    if (config["config_version"] != CONFIG_VERSION or config["schema_version"] != PUBLIC_SCHEMA
            or config["model_version"] != MODEL_VERSION or config["evidence_feature_version"] != FEATURE_VERSION
            or config["production_admission"] != "quarantined"):
        reject("unsupported public evidence engineering configuration", "UNSUPPORTED")
    integer(config["evidence_hash_dim"], "evidence_hash_dim", 16, 256)
    integer(config["evidence_max_events"], "evidence_max_events", 1, 65536)
    schema_v3.validate_config(config["base_config"])
    return config


def _v3_mechanics_projection(public):
    """Private guard/core adapter, never identity or supervision conditioning."""
    result = deepcopy(public)
    result["schema_version"] = schema_v3.PUBLIC_SCHEMA
    result.pop("public_evidence")
    context = result["controller_context"]
    if isinstance(context.get("anchor"), dict): context["anchor"] = _v3_mechanics_projection(context["anchor"])
    return result


def _bind_current(public):
    events = public["public_evidence"]["events"]
    final = events[-1]
    decision = final["payload"]
    if decision["kind"] != "combat_decision": reject("v4 root must end at recorded stable combat decision")
    expected = deepcopy(public["observation"])
    expected["history"] = []
    if decision["observation"] != expected: reject("root observation differs from recorded current decision")
    legal = [action for action, mask in zip(public["candidate_actions"], public["legal_mask"]) if mask]
    if decision["actions"] != legal: reject("root legal candidates differ from recorded offered actions")
    if decision["historyCompleteFromCombatStart"] != public["history_complete"]:
        reject("root history completeness differs from current evidence decision")
    owner = next(event["payload"] for event in events if event["ownerOrdinal"] == final["ownerOrdinal"] and event["payload"]["kind"] == "owner_started")
    context = public["observation"]["runContext"]
    if any(owner[key] != context[key] for key in ("actIndex", "floor")): reject("current owner location disagrees with runContext")
    if public["public_evidence"]["completeFromRunStart"] and context["completeFromRunStart"]:
        combat_count = sum(event["payload"]["kind"] == "owner_started" and event["payload"]["ownerKind"] == "combat" for event in events)
        if context["combatEntryIndex"] != combat_count - 1:
            reject("complete runContext combatEntryIndex contradicts recorded combat owners")


@numeric_guard
def validate_public(public, config):
    validate_config(config)
    object_keys(public, ("schema_version", "observation", "history_complete", "controller_context",
                         "candidate_actions", "legal_mask", "public_evidence"), "v4 public_input")
    if public["schema_version"] != PUBLIC_SCHEMA: reject("explicit v4 public evidence envelope required", "UNSUPPORTED")
    context = public["controller_context"]
    if not isinstance(context, dict): reject("controller_context must be object")
    if context.get("status") != "inactive":
        anchor = context.get("anchor")
        if not isinstance(anchor, dict) or anchor.get("controller_context") != {"status": "inactive"}:
            reject("finite anchor must be explicit inactive v4 public packet")
        validate_public(anchor, config)
        old = anchor["public_evidence"]["events"]
        current = public["public_evidence"].get("events") if isinstance(public["public_evidence"], dict) else None
        if not isinstance(current, list) or current[:len(old)] != old:
            reject("finite anchor evidence must be unchanged prefix of current public evidence")
    validate_evidence(public["public_evidence"], config)
    schema_v3.validate_public(_v3_mechanics_projection(public), config["base_config"])
    _bind_current(public)
    for event in public["observation"]["history"]:
        if event["detail"].lstrip().startswith(("{", "[")): loads(event["detail"])
    validate_current_history(public)
    return public


class _PublicControllerV4:
    controller_type = None

    def __init__(self, public, config):
        validate_public(public, config)
        self.config, self._public = deepcopy(config), deepcopy(public)
        self._controller = self.controller_type(_v3_mechanics_projection(public), config["base_config"])
        self._sync_exit()

    def _sync_exit(self):
        current = self._controller.public["controller_context"]
        self._public["controller_context"].update(status=current["status"], exitReason=current["exitReason"])

    @property
    def public(self): return deepcopy(self._public)

    def advance(self, public):
        validate_public(public, self.config)
        old = self._public["public_evidence"]["events"]
        if (public["controller_context"].get("anchor") != self._public["controller_context"]["anchor"]
                or public["public_evidence"]["events"][:len(old)] != old):
            reject("cannot replace finite anchor or rewrite recorded evidence")
        self._controller.advance(_v3_mechanics_projection(public))
        self._public = deepcopy(public); self._sync_exit()
        return self.public

    @numeric_guard
    def settle(self, terminal):
        self._controller.settle(terminal); self._sync_exit()
        return deepcopy(self._public["controller_context"])


class PublicHuntControllerV4(_PublicControllerV4):
    controller_type = schema_v3.PublicHuntControllerV3


class PublicRegenControllerV4(_PublicControllerV4):
    controller_type = schema_v3.PublicRegenControllerV3
