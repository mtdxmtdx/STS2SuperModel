"""Opt-in public run context boundary; frozen v1/v2 contracts are unchanged.

Complete run history is a caller/recorder assertion, never inferred from a seed,
floor, history length, or simulator state. This engineering boundary provides no
production data admission or live-client provenance guarantee.
"""
from __future__ import annotations

import copy
import json
from pathlib import Path

from .schema import boolean, integer, object_keys, reject
from . import schema_v2
from .schema_regen import CONTEXT_SCHEMA as REGEN_CONTEXT_SCHEMA, PublicRegenController

PUBLIC_SCHEMA = "nosl.student.public.v3"
OBSERVATION_SCHEMA = "nosl.public.v3"
RUN_CONTEXT_SCHEMA = "nosl.public-run-context.v1"
CONFIG_VERSION = "nosl.student.config.v3"
MODEL_VERSION = "nosl.student.model.v3"
FEATURE_VERSION = "nosl.public-run-context.features.v1"
INT32_MAX = 2**31 - 1


def load_config(path: str | Path) -> dict:
    return validate_config(json.loads(Path(path).read_text(encoding="utf-8")))


def validate_config(config: dict) -> dict:
    object_keys(config, ("config_version", "schema_version", "model_version", "base_config",
                         "run_context_feature_version", "production_admission"), "public run context config")
    if (config["config_version"] != CONFIG_VERSION or config["schema_version"] != PUBLIC_SCHEMA
            or config["model_version"] != MODEL_VERSION
            or config["run_context_feature_version"] != FEATURE_VERSION
            or config["production_admission"] != "quarantined"):
        reject("unsupported public run context engineering configuration", "UNSUPPORTED")
    schema_v2.validate_config(config["base_config"])
    return config


def validate_run_context(context: dict) -> dict:
    object_keys(context, ("schemaVersion", "actIndex", "floor", "combatEntryIndex",
                          "completeFromRunStart"), "runContext")
    if context["schemaVersion"] != RUN_CONTEXT_SCHEMA:
        reject("unknown public run context version", "UNSUPPORTED")
    integer(context["actIndex"], "runContext.actIndex", 0, INT32_MAX)
    integer(context["floor"], "runContext.floor", 0, INT32_MAX)
    if boolean(context["completeFromRunStart"], "runContext.completeFromRunStart"):
        integer(context["combatEntryIndex"], "runContext.combatEntryIndex", 0, INT32_MAX)
    elif context["combatEntryIndex"] is not None:
        reject("incomplete run history requires null combatEntryIndex")
    return context


def _v2_mechanics_projection(public: dict, *, anchor=False) -> dict:
    """Private adapter for frozen mechanical guards/core, never a label identity.

    The caller first validates v3. The full context is separately consumed by
    StudentV3 and retained by every v3 controller and semantic identity.
    """
    result = copy.deepcopy(public)
    result["schema_version"] = "nosl.student.public.v1" if anchor else schema_v2.PUBLIC_SCHEMA
    observation = result["observation"]
    observation["schema"] = "nosl.public.v2"
    observation.pop("runContext")
    context = result["controller_context"]
    if context.get("status") != "inactive" and "anchor" in context:
        context["anchor"] = _v2_mechanics_projection(context["anchor"], anchor=True)
    return result


def validate_public(public: dict, config: dict) -> dict:
    validate_config(config)
    object_keys(public, ("schema_version", "observation", "history_complete", "controller_context",
                         "candidate_actions", "legal_mask"), "public_input")
    if public["schema_version"] != PUBLIC_SCHEMA:
        reject("explicit v3 public run context envelope required", "UNSUPPORTED")
    observation = public["observation"]
    if not isinstance(observation, dict) or observation.get("schema") != OBSERVATION_SCHEMA:
        reject("explicit v3 observation required", "UNSUPPORTED")
    validate_run_context(observation.get("runContext"))
    context = public["controller_context"]
    if not isinstance(context, dict): reject("controller_context must be object")
    if context.get("status") != "inactive":
        anchor = context.get("anchor")
        if not isinstance(anchor, dict) or anchor.get("controller_context") != {"status": "inactive"}:
            reject("finite anchor must be an explicit inactive v3 public packet")
        validate_public(anchor, config)
        if anchor["observation"]["runContext"] != observation["runContext"]:
            reject("finite anchor runContext must exactly match the current combat")
    # All old nested whitelists, history, finite scope and fixed deadlines remain
    # enforced; projection never filters an unknown key other than runContext.
    schema_v2.validate_public(_v2_mechanics_projection(public), config["base_config"])
    return public


class _PublicControllerV3:
    """Keep the complete v3 anchor while reusing all frozen mechanical guards."""
    controller_type = None

    def __init__(self, public: dict, config: dict):
        validate_public(public, config)
        self.config = copy.deepcopy(config)
        self._public = copy.deepcopy(public)
        self._controller = self.controller_type(_v2_mechanics_projection(public), config["base_config"])
        self._sync_exit()

    def _sync_exit(self):
        context = self._controller.public["controller_context"]
        self._public["controller_context"].update(status=context["status"], exitReason=context["exitReason"])

    @property
    def public(self):
        return copy.deepcopy(self._public)

    def advance(self, public: dict) -> dict:
        validate_public(public, self.config)
        previous, current = self._public["controller_context"], public["controller_context"]
        if (current.get("anchor") != previous["anchor"]
                or public["observation"]["runContext"] != self._public["observation"]["runContext"]):
            reject("cannot replace an existing finite plan anchor or runContext")
        self._controller.advance(_v2_mechanics_projection(public))
        self._public = copy.deepcopy(public)
        self._sync_exit()
        return self.public

    def settle(self, terminal: dict) -> dict:
        self._controller.settle(terminal)
        self._sync_exit()
        return copy.deepcopy(self._public["controller_context"])


class PublicHuntControllerV3(_PublicControllerV3):
    controller_type = schema_v2.PublicHuntController

    def __init__(self, public: dict, config: dict):
        if public.get("controller_context", {}).get("schemaVersion") != schema_v2.CONTEXT_SCHEMA:
            reject("finite Hunt context required")
        super().__init__(public, config)


class PublicRegenControllerV3(_PublicControllerV3):
    controller_type = PublicRegenController

    def __init__(self, public: dict, config: dict):
        if public.get("controller_context", {}).get("schemaVersion") != REGEN_CONTEXT_SCHEMA:
            reject("finite Regen context required")
        super().__init__(public, config)
