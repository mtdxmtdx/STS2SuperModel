"""Opt-in complete public-map boundary; frozen v4 bytes/features stay unchanged.

Only the typed currentMap extension is new. The map owner supplies actIndex.
The run-evidence wire version is explicitly v2; v4 rejects the new channel.
Neither graph validation nor a producer's Complete claim certifies live-client
visibility. Unsupported captures must explicitly say Missing, never partial.
"""
from __future__ import annotations

from copy import deepcopy
from pathlib import Path

from . import schema_v4
from .evidence_v4 import _coordinate, numeric_guard
from .schema import enum, integer, object_keys, reject, sequence

PUBLIC_SCHEMA = "nosl.student.public.v5"
CONFIG_VERSION = "nosl.student.config.v5"
MODEL_VERSION = "nosl.student.model.v5"
FEATURE_VERSION = "nosl.public-map-complete-graph.features.v1"
EVIDENCE_SCHEMA = "nosl.public-run-evidence.v2"
MAP_PROFILE = "nosl.public-map-complete-graph.v1"
NODE_TYPES = tuple("start unknown monster elite boss rest shop treasure event ancient".split())
loads = schema_v4.loads


def load_config(path: str | Path):
    return validate_config(loads(Path(path).read_text(encoding="utf-8")))


@numeric_guard
def validate_config(config):
    object_keys(config, ("config_version", "schema_version", "model_version", "base_config",
                         "map_feature_version", "map_max_nodes", "map_max_edges", "production_admission"), "v5 config")
    if (config["config_version"] != CONFIG_VERSION or config["schema_version"] != PUBLIC_SCHEMA
            or config["model_version"] != MODEL_VERSION or config["map_feature_version"] != FEATURE_VERSION
            or config["production_admission"] != "quarantined"):
        reject("unsupported complete-map engineering configuration", "UNSUPPORTED")
    integer(config["map_max_nodes"], "map_max_nodes", 2, 4096)
    integer(config["map_max_edges"], "map_max_edges", 1, 16384)
    schema_v4.validate_config(config["base_config"])
    return config


def coordinate_key(value):
    col, row = _coordinate(value)
    return row, col


def _ordered(values, where):
    if any(a >= b for a, b in zip(values, values[1:])):
        reject(where + " must be canonical and duplicate-free")


def validate_current_map(capture, payload, config):
    object_keys(capture, ("status", "nodes", "edges", "startingNode", "bossNodes"), "currentMap")
    status = enum(capture["status"], ("missing", "complete"), "currentMap.status")
    nodes = sequence(capture["nodes"], "currentMap.nodes", config["map_max_nodes"])
    edges = sequence(capture["edges"], "currentMap.edges", config["map_max_edges"])
    bosses = sequence(capture["bossNodes"], "currentMap.bossNodes", config["map_max_nodes"])
    if status == "missing":
        if nodes or edges or bosses or capture["startingNode"] is not None:
            reject("a missing currentMap cannot contain a partial graph")
        return capture
    by_coordinate = {}
    for node in nodes:
        object_keys(node, ("coordinate", "nodeType"), "currentMap node")
        key = coordinate_key(node["coordinate"])
        if key in by_coordinate: reject("duplicate currentMap node")
        by_coordinate[key] = enum(node["nodeType"], NODE_TYPES, "currentMap nodeType")
    _ordered(list(by_coordinate), "currentMap nodes")
    start = coordinate_key(capture["startingNode"])
    boss_keys = [coordinate_key(boss) for boss in bosses]
    _ordered(boss_keys, "currentMap bossNodes")
    if (by_coordinate.get(start) not in ("start", "ancient") or not boss_keys or start in boss_keys
            or set(boss_keys) != {key for key, kind in by_coordinate.items() if kind == "boss"}):
        reject("currentMap start/boss declarations contradict node types")
    pairs = []
    outgoing = {key: [] for key in by_coordinate}
    for edge in edges:
        object_keys(edge, ("from", "to"), "currentMap edge")
        a, b = coordinate_key(edge["from"]), coordinate_key(edge["to"])
        if a not in by_coordinate or b not in by_coordinate or b[0] <= a[0]:
            reject("currentMap edge must join known nodes in ascending row order")
        pairs.append((a, b)); outgoing[a].append(b)
    _ordered(pairs, "currentMap edges")
    reachable = {start}
    for key in by_coordinate:
        if key in reachable: reachable.update(outgoing[key])
    reaches_boss = set(boss_keys)
    for key in reversed(by_coordinate):
        if any(target in reaches_boss for target in outgoing[key]): reaches_boss.add(key)
    if reachable != set(by_coordinate) or reaches_boss != set(by_coordinate):
        reject("complete currentMap requires every node on a start-to-boss path")
    slice_coordinates = {coordinate_key(node["coordinate"]) for node in payload["nodes"]}
    if any(by_coordinate.get(coordinate_key(node["coordinate"])) != node["nodeType"] for node in payload["nodes"]):
        reject("map slice nodes differ from complete currentMap")
    slice_edges = {(coordinate_key(edge["from"]), coordinate_key(edge["to"])) for edge in payload["edges"]}
    if slice_edges != {(a, b) for a, b in pairs if a in slice_coordinates and b in slice_coordinates}:
        reject("map slice edges differ from complete currentMap")
    return capture


def _v4_mechanics_projection(public):
    """Private frozen encoder/guard adapter; never label identity or admission."""
    result = deepcopy(public)
    result["schema_version"] = schema_v4.PUBLIC_SCHEMA
    result["public_evidence"]["schemaVersion"] = "nosl.public-run-evidence.v1"
    for event in result["public_evidence"]["events"]:
        if event["payload"].get("kind") == "map": event["payload"].pop("currentMap", None)
    anchor = result["controller_context"].get("anchor")
    if isinstance(anchor, dict): result["controller_context"]["anchor"] = _v4_mechanics_projection(anchor)
    return result


@numeric_guard
def validate_public(public, config):
    validate_config(config)
    object_keys(public, ("schema_version", "observation", "history_complete", "controller_context",
                         "candidate_actions", "legal_mask", "public_evidence"), "v5 public_input")
    if public["schema_version"] != PUBLIC_SCHEMA: reject("explicit v5 complete-map envelope required", "UNSUPPORTED")
    evidence, context = public["public_evidence"], public["controller_context"]
    if not isinstance(evidence, dict) or not isinstance(context, dict): reject("v5 evidence/context must be objects")
    if evidence.get("schemaVersion") != EVIDENCE_SCHEMA: reject("v5 requires explicit public evidence v2", "UNSUPPORTED")
    events = sequence(evidence.get("events"), "evidence events", config["base_config"]["evidence_max_events"])
    # Validate the stripped copy before inspecting extension associations. This
    # retains all frozen owner, act, history, candidate and prefix constraints.
    try: projected = _v4_mechanics_projection(public)
    except (KeyError, TypeError, AttributeError): reject("malformed v5 evidence structure")
    schema_v4.validate_public(projected, config["base_config"])
    if context.get("status") != "inactive":
        anchor = context.get("anchor")
        validate_public(anchor, config)
        old = anchor["public_evidence"]["events"]
        if events[:len(old)] != old: reject("finite anchor full-map evidence must remain an unchanged prefix")
    maps = [event["payload"] for event in events if event["payload"]["kind"] == "map"]
    for payload in maps:
        if "currentMap" not in payload: reject("v5 map observations require Complete or Missing currentMap")
        _ordered([coordinate_key(node["coordinate"]) for node in payload["nodes"]], "v5 map slice nodes")
        _ordered([(coordinate_key(edge["from"]), coordinate_key(edge["to"])) for edge in payload["edges"]], "v5 map slice edges")
        _ordered([coordinate_key(option["coordinate"]) for option in payload["options"]], "v5 map options")
        validate_current_map(payload["currentMap"], payload, config)
    return public


class _PublicControllerV5:
    controller_type = None

    def __init__(self, public, config):
        validate_public(public, config)
        self.config, self._public = deepcopy(config), deepcopy(public)
        self._controller = self.controller_type(_v4_mechanics_projection(public), config["base_config"])
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
            reject("cannot replace finite anchor or rewrite full-map evidence")
        self._controller.advance(_v4_mechanics_projection(public))
        self._public = deepcopy(public); self._sync_exit()
        return self.public

    @numeric_guard
    def settle(self, terminal):
        self._controller.settle(terminal); self._sync_exit()
        return deepcopy(self._public["controller_context"])


class PublicHuntControllerV5(_PublicControllerV5):
    controller_type = schema_v4.PublicHuntControllerV4


class PublicRegenControllerV5(_PublicControllerV5):
    controller_type = schema_v4.PublicRegenControllerV4
