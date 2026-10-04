"""Closed public observation channel, independent of torch and native engines.

Wire names and enums match PublicRunEvidenceJson, not arbitrary serialized game
objects. Structural checks cannot certify where a producer obtained a value.
"""
from __future__ import annotations

from copy import deepcopy
from functools import wraps
import json
import unicodedata

from . import schema, schema_v3
from .schema import boolean, enum, integer, number, object_keys, reject, sequence

EVIDENCE_SCHEMA = "nosl.public-run-evidence.v1"
INT32_MAX = 2**31 - 1
INT64_MAX = 2**63 - 1
OWNER_KINDS = "combat event reward rest shop map outside_choice".split()
GAP_REASONS = "run_start_not_observed owner_start_not_observed observation_missing ambiguous_visibility unsupported_observation interrupted".split()
OUTCOMES = "completed victory defeat escaped interrupted".split()
KINDS = "run_started gap owner_started owner_ended offers options option_chosen card_choice cards_chosen map map_chosen combat_fact combat_decision combat_action".split()
FACT_FIELDS = "turn targetSlot sourceSlot model cards intents choice assets damage settlement amount energySpent starsSpent resultPile unidentifiedCount targetModel".split()
FACT_ALLOWED = {
    "started": (), "player_turn_ended": (), "shuffled": (), "hidden_card_generated": (),
    "entry_assets": ("assets",), "pre_settlement": ("settlement",), "player_turn_started": ("turn",),
    "intent_published": ("targetSlot", "model", "intents"),
    "card_drawn": ("cards",), "card_started": ("cards",), "card_generated": ("cards",),
    "card_played": ("cards", "energySpent", "starsSpent", "resultPile"), "potion_used": ("model",),
    "damage": ("targetSlot", "sourceSlot", "damage", "targetModel"),
    "power_changed": ("targetSlot", "sourceSlot", "model", "amount", "targetModel"),
    "choice_offered": ("choice",), "automatic_selection": ("model", "cards", "unidentifiedCount"),
}
FACT_REQUIRED = {
    "entry_assets": ("assets",), "pre_settlement": ("settlement",), "player_turn_started": ("turn",),
    "intent_published": ("targetSlot", "model"), "potion_used": ("model",),
    "damage": ("targetSlot", "damage"), "power_changed": ("targetSlot", "model", "amount"),
    "choice_offered": ("choice",), "automatic_selection": ("unidentifiedCount",),
}


def numeric_guard(function):
    """Keep frozen numerical guards' overflow inside the v4 schema boundary."""
    @wraps(function)
    def guarded(*args, **kwargs):
        try: return function(*args, **kwargs)
        except OverflowError: reject("v4 numeric value exceeds supported range")
    return guarded


def _key(value, where="public key"):
    if (not isinstance(value, str) or not value.strip() or len(value) > 256
            or any(unicodedata.category(c) in ("Cc", "Cs") for c in value)):
        reject(where + ": invalid public key")
    return value


def _count(value, where):
    return integer(value, where, 0, INT32_MAX)


def _ordinal(value, where):
    return integer(value, where, 0, INT64_MAX)


def _optional(value, validator, where):
    if value is not None: validator(value, where)


def _unique(values, where):
    if len(values) != len(set(values)): reject(where + ": duplicate public entries")


def _cards(cards, base):
    for card in sequence(cards, "evidence cards", base["max_entities"]):
        if not isinstance(card, dict): reject("evidence card must be object")
        schema.validate_card(card, base)


def _relic(relic, base):
    object_keys(relic, ("id", "details", "cards", "selectedModel"), "evidence relic")
    enum(relic["id"], base["supported_relics"], "relic.id")
    details = relic["details"]
    if (not isinstance(details, dict) or not {"isWax", "isMelted", "isUsedUp", "stackCount"} <= set(details)
            or not set(details) <= set(schema.RELIC_DETAIL_KEYS)):
        reject("unrecognized or incomplete public relic counters")
    for key, value in details.items(): integer(value, "relic." + key, -1000000)
    _cards(relic["cards"], base)
    if relic["selectedModel"] is not None:
        enum(relic["selectedModel"], base.get("supported_selected_models", base["supported_sources"]), "relic.selectedModel")


def _assets(value, base):
    object_keys(value, "hp maxHp gold deck relics potions maxEnergy potionSlots orbSlots cardRemovalsUsed".split(), "assets")
    for key in ("hp", "maxHp", "gold", "maxEnergy", "potionSlots", "orbSlots", "cardRemovalsUsed"):
        _count(value[key], "assets." + key)
    if value["hp"] > value["maxHp"]: reject("assets HP exceeds maximum")
    _cards(value["deck"], base)
    for relic in sequence(value["relics"], "assets.relics", base["max_entities"]): _relic(relic, base)
    for potion in sequence(value["potions"], "assets.potions", 16):
        if potion is not None: enum(potion, base["supported_potions"], "assets.potion")
    if len(value["potions"]) != value["potionSlots"]: reject("assets potion capacity mismatch")


def _choice(value, base):
    if not isinstance(value, dict): reject("evidence choice must be object")
    schema.validate_choice(value, base)


def _coordinate(value):
    object_keys(value, ("col", "row"), "map coordinate")
    return tuple(integer(value[k], "coordinate." + k, -2**31, INT32_MAX) for k in ("col", "row"))


def _map(value):
    current = _coordinate(value["current"])
    nodes = sequence(value["nodes"], "map nodes", 4096)
    coordinates = []
    for node in nodes:
        object_keys(node, ("coordinate", "nodeType"), "map node")
        coordinates.append(_coordinate(node["coordinate"]))
        enum(node["nodeType"], "start unknown monster elite boss rest shop treasure event ancient".split(), "map node type")
    _unique(coordinates, "map nodes")
    if current not in coordinates: reject("current node missing from map slice")
    edges = []
    for edge in sequence(value["edges"], "map edges", 16384):
        object_keys(edge, ("from", "to"), "map edge")
        pair = _coordinate(edge["from"]), _coordinate(edge["to"])
        if pair[0] == pair[1] or any(p not in coordinates for p in pair): reject("invalid map edge")
        edges.append(pair)
    _unique(edges, "map edges")
    options = []
    for option in sequence(value["options"], "map options", 4096):
        object_keys(option, ("coordinate", "isOrdinaryConnection"), "map option")
        point = _coordinate(option["coordinate"])
        if point not in coordinates: reject("map option is outside visible slice")
        if boolean(option["isOrdinaryConnection"], "ordinary connection") != ((current, point) in edges):
            reject("map ordinary connection flag contradicts observed edges")
        options.append(point)
    _unique(options, "map options")


def _fact(payload, base):
    kind = enum(payload["factKind"], tuple(FACT_ALLOWED), "combat fact kind")
    for key in FACT_FIELDS:
        value = payload[key]
        present = bool(value) if key in ("cards", "intents") else value is not None
        if present and key not in FACT_ALLOWED[kind]: reject("combat payload does not match fact kind")
    if any(payload[key] is None for key in FACT_REQUIRED.get(kind, ())): reject("missing combat fact value")
    _cards(payload["cards"], base)
    if kind in ("card_drawn", "card_started", "card_generated", "card_played") and len(payload["cards"]) != 1:
        reject("card fact requires exactly one observed card")
    for intent in sequence(payload["intents"], "fact.intents", 32): schema.validate_intent(intent, base)
    for key in ("turn", "energySpent", "starsSpent", "unidentifiedCount"):
        _optional(payload[key], _count, key)
    for key in ("targetSlot", "sourceSlot"):
        if payload[key] is not None:
            integer(payload[key], key, -2, 255)
            if payload[key] == -1: reject("invalid combat entity slot")
    for key in ("model", "targetModel"): _optional(payload[key], _key, key)
    vocabulary = {"intent_published": "supported_enemies", "potion_used": "supported_potions",
                  "power_changed": "supported_powers", "automatic_selection": "supported_sources"}
    if payload["model"] is not None and kind in vocabulary:
        enum(payload["model"], base[vocabulary[kind]], "fact public model")
    if payload["targetModel"] is not None:
        enum(payload["targetModel"], ["player", *base["supported_enemies"]], "fact target model")
    if payload["amount"] is not None: number(payload["amount"], "amount")
    if payload["resultPile"] is not None: enum(payload["resultPile"], "hand draw discard exhaust play none".split(), "resultPile")
    if payload["choice"] is not None: _choice(payload["choice"], base)
    if payload["assets"] is not None: _assets(payload["assets"], base)
    if payload["damage"] is not None:
        damage = object_keys(payload["damage"], "blocked unblocked overkill hpAfter killed".split(), "damage")
        for key in ("blocked", "unblocked", "overkill"): number(damage[key], "damage." + key, 0)
        _count(damage["hpAfter"], "damage.hpAfter"); boolean(damage["killed"], "damage.killed")
    if payload["settlement"] is not None:
        settlement = object_keys(payload["settlement"], ("hp", "maxHp", "hand", "exhaust"), "pre-settlement")
        _count(settlement["hp"], "settlement.hp"); _count(settlement["maxHp"], "settlement.maxHp")
        if settlement["hp"] > settlement["maxHp"]: reject("settlement HP exceeds maximum")
        _cards(settlement["hand"], base); _cards(settlement["exhaust"], base)


def _decision(payload, config):
    enum(payload["status"], ("player_decision", "card_choice"), "decision status")
    _ordinal(payload["historyThroughEventOrdinal"], "historyThroughEventOrdinal")
    boolean(payload["historyCompleteFromCombatStart"], "decision history completeness")
    observation = payload["observation"]
    if not isinstance(observation, dict) or observation.get("history") != []:
        reject("evidence decision must have empty untyped history")
    if (payload["status"] == "card_choice") != (observation.get("choice") is not None): reject("decision choice status mismatch")
    # Syntactic mechanics validation only. This local sentinel is never returned,
    # hashed, encoded or used as evidence of completeness; lifecycle is below.
    projected = deepcopy(observation)
    projected["history"] = [{"kind": "combat_started", "detail": "Silent:A10"}]
    packet = {"schema_version": "nosl.student.public.v1", "observation": projected,
              "history_complete": True, "controller_context": {"status": "inactive"},
              "candidate_actions": payload["actions"], "legal_mask": [True] * len(sequence(payload["actions"], "decision actions", 4096))}
    if observation.get("schema") == "nosl.public.v3":
        schema_v3.validate_run_context(projected.pop("runContext", None))
        projected["schema"] = "nosl.public.v2"
    elif observation.get("schema") != "nosl.public.v2": reject("unknown evidence decision observation schema", "UNSUPPORTED")
    schema.validate_public(packet, config["base_config"]["base_config"]["base_config"])
    # EvidenceGuard's integer/lifecycle invariants are stricter than the frozen
    # public mechanics validator, which accepts general numbers in these fields.
    for kind in ("enemies", "pets"):
        for creature in observation[kind]:
            _count(creature["hp"], "evidence " + kind + ".hp")
            _count(creature["maxHp"], "evidence " + kind + ".maxHp")
            if creature["hp"] > creature["maxHp"]: reject("evidence " + kind + " HP exceeds maximum")
    power_groups = [observation["powers"], *(creature["powers"] for kind in ("enemies", "pets") for creature in observation[kind])]
    if any(power["applierSlot"] == -1 for powers in power_groups for power in powers):
        reject("evidence power applierSlot cannot be -1")


PAYLOAD_KEYS = {
    "run_started": ("character", "ascension", "assets"), "gap": ("reason",),
    "owner_started": ("ownerKind", "actIndex", "floor", "parentOwnerOrdinal", "completeFromOwnerStart"),
    "owner_ended": ("outcome", "assets"), "offers": ("groups", "replacesOfferEventOrdinal"),
    "options": ("options",), "option_chosen": ("offerEventOrdinal", "key"), "card_choice": ("choice",),
    "cards_chosen": ("offerEventOrdinal", "selection", "cancelled"), "map": ("current", "nodes", "edges", "options"),
    "map_chosen": ("offerEventOrdinal", "coordinate"), "combat_fact": ("factKind", *FACT_FIELDS),
    "combat_decision": ("status", "historyThroughEventOrdinal", "historyCompleteFromCombatStart", "observation", "actions"),
    "combat_action": ("decisionEventOrdinal", "action"),
}


def _payload(payload, config):
    if not isinstance(payload, dict): reject("evidence payload must be object")
    kind = enum(payload.get("kind"), KINDS, "evidence kind")
    object_keys(payload, ("kind", *PAYLOAD_KEYS[kind]), "evidence " + kind)
    base = {**config["base_config"]["base_config"]["base_config"], "_require_v2": True}
    if kind == "run_started":
        enum(payload["character"], ("Silent",), "supported run character")
        integer(payload["ascension"], "supported run ascension", 10, 10); _assets(payload["assets"], base)
    elif kind == "gap": enum(payload["reason"], GAP_REASONS, "gap reason")
    elif kind == "owner_started":
        enum(payload["ownerKind"], OWNER_KINDS, "owner kind")
        _count(payload["actIndex"], "actIndex"); _count(payload["floor"], "floor")
        _optional(payload["parentOwnerOrdinal"], _ordinal, "parentOwnerOrdinal")
        boolean(payload["completeFromOwnerStart"], "completeFromOwnerStart")
    elif kind == "owner_ended":
        enum(payload["outcome"], OUTCOMES, "owner outcome")
        if payload["assets"] is not None: _assets(payload["assets"], base)
    elif kind == "offers":
        groups = sequence(payload["groups"], "offer groups", 256)
        _optional(payload["replacesOfferEventOrdinal"], _ordinal, "replacesOfferEventOrdinal")
        keys = []
        for index, group in enumerate(groups):
            object_keys(group, ("groupKind", "selectionMode", "alternativeToGroupIndex", "offers"), "offer group")
            enum(group["groupKind"], ("primary", "extra", "alternative", "reroll"), "group kind")
            enum(group["selectionMode"], ("independent", "choose_one"), "selection mode")
            other = group["alternativeToGroupIndex"]
            if (group["groupKind"] == "alternative") != (other is not None): reject("alternative group reference missing or spurious")
            if other is not None:
                integer(other, "alternative group", 0, len(groups) - 1)
                if other == index: reject("alternative cannot reference itself")
            if group["groupKind"] == "reroll" and payload["replacesOfferEventOrdinal"] is None: reject("reroll requires previous offer")
            for offer in sequence(group["offers"], "offers", 1024):
                object_keys(offer, "key offerKind isLocked price card relic potion gold serviceKey".split(), "offer")
                keys.append(_key(offer["key"])); boolean(offer["isLocked"], "offer lock")
                _optional(offer["price"], _count, "price")
                offer_kind = enum(offer["offerKind"], "card relic potion gold service skip continue reroll".split(), "offer kind")
                fields = [key for key in ("card", "relic", "potion", "gold", "serviceKey") if offer[key] is not None]
                expected = ["serviceKey" if offer_kind == "service" else offer_kind] if offer_kind in ("card", "relic", "potion", "gold", "service") else []
                if fields != expected: reject("offer payload does not match kind")
                if offer_kind == "card": _cards([offer["card"]], base)
                elif offer_kind == "relic": _relic(offer["relic"], base)
                elif offer_kind == "potion": enum(offer["potion"], base["supported_potions"], "offered potion")
                elif offer_kind == "gold": _count(offer["gold"], "offered gold")
                elif offer_kind == "service": _key(offer["serviceKey"])
        _unique(keys, "offer keys")
    elif kind == "options":
        keys = []
        for option in sequence(payload["options"], "visible options", 1024):
            object_keys(option, ("key", "isLocked", "price"), "visible option")
            keys.append(_key(option["key"])); boolean(option["isLocked"], "option lock")
            _optional(option["price"], _count, "price")
        _unique(keys, "option keys")
    elif kind == "option_chosen": _ordinal(payload["offerEventOrdinal"], "offerEventOrdinal"); _key(payload["key"])
    elif kind == "card_choice": _choice(payload["choice"], base)
    elif kind == "cards_chosen":
        _ordinal(payload["offerEventOrdinal"], "offerEventOrdinal")
        selected = sequence(payload["selection"], "selection", base["max_entities"])
        for value in selected: _count(value, "selected index")
        _unique(selected, "selected indices")
        if boolean(payload["cancelled"], "cancelled") and selected: reject("cancelled card choice has selections")
    elif kind == "map": _map(payload)
    elif kind == "map_chosen": _ordinal(payload["offerEventOrdinal"], "offerEventOrdinal"); _coordinate(payload["coordinate"])
    elif kind == "combat_fact": _fact(payload, base)
    elif kind == "combat_decision": _decision(payload, config)
    elif kind == "combat_action":
        _ordinal(payload["decisionEventOrdinal"], "decisionEventOrdinal"); schema.validate_action(payload["action"])
    return kind


@numeric_guard
def validate_evidence(evidence, config):
    """Validate every event and reference; never truncate a long prefix silently."""
    object_keys(evidence, ("schemaVersion", "completeFromRunStart", "events"), "public_evidence")
    if evidence["schemaVersion"] != EVIDENCE_SCHEMA: reject("unknown public evidence version", "UNSUPPORTED")
    complete = boolean(evidence["completeFromRunStart"], "evidence completeness")
    events = sequence(evidence["events"], "evidence events", config["evidence_max_events"])
    if not events: reject("evidence requires observed run start or explicit run-start gap")
    owners, gap, began = {}, False, False
    for index, event in enumerate(events):
        object_keys(event, ("eventOrdinal", "ownerOrdinal", "payload"), "evidence event")
        if _ordinal(event["eventOrdinal"], "eventOrdinal") != index: reject("event ordinals must be contiguous")
        oid = event["ownerOrdinal"]
        _optional(oid, _ordinal, "ownerOrdinal")
        payload = event["payload"]
        kind = _payload(payload, config)
        if index == 0:
            began = kind == "run_started"
            if not began and not (kind == "gap" and payload["reason"] == "run_start_not_observed"):
                reject("missing explicit run-start gap")
        owner = None
        if kind == "owner_started":
            if oid != len(owners): reject("owner ordinals must be contiguous")
            parent = payload["parentOwnerOrdinal"]
            if parent is not None and (parent not in owners or owners[parent]["ended"]): reject("parent owner must exist and remain open")
            owner = {"start": payload, "ended": False, "gap": False, "combat_started": False, "last": index, "pending": None}
            owners[oid] = owner
            if not payload["completeFromOwnerStart"]: gap = True
        elif oid is not None:
            owner = owners.get(oid)
            if owner is None or owner["ended"]: reject("unknown or ended evidence owner")
        elif kind not in ("run_started", "gap"): reject("observation requires an owner")

        def referenced(ordinal, expected):
            if ordinal >= index or events[ordinal]["ownerOrdinal"] != oid: reject("reference must name earlier observation from same owner")
            value = events[ordinal]["payload"]
            if value["kind"] not in expected: reject("reference has incompatible observation kind")
            return value

        def choose(ordinal):
            if owner["pending"] != ordinal: reject("choice must consume latest unconsumed offer")
            owner["pending"] = None

        if kind == "run_started":
            if index != 0 or oid is not None: reject("run start only valid at ordinal zero without owner")
        elif kind == "gap":
            if payload["reason"] == "run_start_not_observed" and (index != 0 or oid is not None): reject("misplaced run-start gap")
            if payload["reason"] == "owner_start_not_observed" and (owner is None or owner["start"]["completeFromOwnerStart"]):
                reject("owner-start gap requires an incomplete owner")
            gap = True
            for active in ([owner] if owner is not None else [o for o in owners.values() if not o["ended"]]):
                active["gap"] = True
        elif kind == "owner_ended":
            if any(o["start"]["parentOwnerOrdinal"] == oid and not o["ended"] for o in owners.values()): reject("child owner must end before parent")
            if payload["outcome"] == "interrupted" and not owner["gap"]: reject("interrupted owner requires explicit gap")
            if owner["start"]["ownerKind"] == "combat" and not owner["combat_started"] and not owner["gap"]: reject("combat requires start or explicit gap")
            owner["ended"] = True
        elif kind in ("offers", "options", "card_choice", "map"):
            permitted = {"offers": ("reward", "shop", "event"), "options": ("event", "rest", "shop"), "map": ("map",)}
            if kind in permitted and owner["start"]["ownerKind"] not in permitted[kind]: reject(kind + " has incompatible owner")
            if kind == "offers" and payload["replacesOfferEventOrdinal"] is not None:
                referenced(payload["replacesOfferEventOrdinal"], ("offers",))
            owner["pending"] = index
        elif kind == "option_chosen":
            prior = referenced(payload["offerEventOrdinal"], ("offers", "options"))
            options = prior["options"] if prior["kind"] == "options" else [o for g in prior["groups"] for o in g["offers"]]
            if not any(o["key"] == payload["key"] and not o["isLocked"] for o in options): reject("chosen option was not visibly enabled")
            choose(payload["offerEventOrdinal"])
        elif kind == "cards_chosen":
            prior = referenced(payload["offerEventOrdinal"], ("card_choice",))["choice"]
            if payload["cancelled"]:
                if not prior["cancelable"]: reject("choice cannot be cancelled")
            elif not prior["min"] <= len(payload["selection"]) <= prior["max"] or any(i >= len(prior["candidates"]) for i in payload["selection"]):
                reject("selection outside visible card choice")
            choose(payload["offerEventOrdinal"])
        elif kind == "map_chosen":
            prior = referenced(payload["offerEventOrdinal"], ("map",))
            if not any(o["coordinate"] == payload["coordinate"] for o in prior["options"]): reject("chosen map node was not offered")
            choose(payload["offerEventOrdinal"])
        elif kind == "combat_fact":
            if owner["start"]["ownerKind"] != "combat": reject("combat fact requires combat owner")
            if payload["factKind"] == "started":
                if owner["combat_started"]: reject("duplicate combat start")
                owner["combat_started"] = True
            elif not owner["combat_started"] and not owner["gap"]: reject("missing combat start requires explicit gap")
        elif kind == "combat_decision":
            if owner["start"]["ownerKind"] != "combat" or payload["historyThroughEventOrdinal"] != owner["last"]:
                reject("decision must link complete preceding owner history")
            if not owner["combat_started"] and not owner["gap"]: reject("decision before combat start requires explicit gap")
            known = owner["start"]["completeFromOwnerStart"] and owner["combat_started"] and not owner["gap"]
            if payload["historyCompleteFromCombatStart"] != known: reject("decision completeness contradicts recorded prefix")
            owner["pending"] = index
        elif kind == "combat_action":
            prior = referenced(payload["decisionEventOrdinal"], ("combat_decision",))
            if payload["action"] not in prior["actions"]: reject("action was not offered by linked decision")
            if owner["last"] != payload["decisionEventOrdinal"]: reject("combat action must precede subsequent owner effects")
            choose(payload["decisionEventOrdinal"])
        if owner is not None: owner["last"] = index
    if complete != (began and not gap): reject("run completeness contradicts recorded boundaries or gaps")
    return evidence


def _project_legacy_event(event):
    """Exact supported-channel comparison for the current root's public history.

    This does not invent events, infer a past owner, or certify provenance. It
    verifies that the two present public representations have not diverged.
    """
    kind, detail = event["kind"], event["detail"]
    result = {"kind": "combat_fact", "factKind": None, **{key: None for key in FACT_FIELDS}}
    result.update(cards=[], intents=[])
    simple = {"combat_started": "started", "player_turn_ended": "player_turn_ended",
              "shuffle": "shuffled", "hidden_card_generated": "hidden_card_generated"}
    if kind in simple: result["factKind"] = simple[kind]
    elif kind == "player_turn": result.update(factKind="player_turn_started", turn=int(detail))
    elif kind == "potion_used": result.update(factKind="potion_used", model=detail)
    else:
        value = json.loads(detail)
        if kind == "action": return {"kind": "combat_action", "action": value}
        if kind == "native_entry_assets":
            result.update(factKind="entry_assets", assets={key: val for key, val in value.items() if key != "schemaVersion"})
        elif kind == "intent_published": result.update(factKind="intent_published", targetSlot=value["slot"], model=value["id"], intents=value["intents"])
        elif kind in ("draw", "card_started", "card_generated"):
            result.update(factKind={"draw": "card_drawn", "card_started": "card_started", "card_generated": "card_generated"}[kind], cards=[value])
        elif kind == "card_played":
            result.update(factKind="card_played", cards=[value["card"]], energySpent=value["energySpent"], starsSpent=value["starsSpent"],
                          resultPile=value["resultPile"].lower() if value["resultPile"] is not None else None)
        elif kind == "damage":
            result.update(factKind="damage", targetSlot=value["targetSlot"], sourceSlot=value["sourceSlot"], targetModel=value["target"],
                          damage={key: value[key] for key in ("blocked", "unblocked", "overkill", "hpAfter", "killed")})
        elif kind == "power_changed":
            result.update(factKind="power_changed", targetSlot=value["targetSlot"], sourceSlot=value["sourceSlot"], targetModel=value["target"], model=value["id"], amount=value["amount"])
        elif kind == "choice": result.update(factKind="choice_offered", choice=value)
        elif kind == "automatic_selection": result.update(factKind="automatic_selection", model=value["source"], cards=value["cards"], unidentifiedCount=value["unidentifiedCount"])
        elif kind == "pre_settlement": result.update(factKind="pre_settlement", settlement=value)
        else: reject("current history contains a fact outside the complete evidence channel", "INCOMPLETE_HISTORY")
    return result


def validate_current_history(public):
    events = public["public_evidence"]["events"]
    owner = events[-1]["ownerOrdinal"]
    typed = []
    for event in events:
        if event["ownerOrdinal"] != owner: continue
        payload = event["payload"]
        if payload["kind"] == "combat_fact": typed.append(payload)
        elif payload["kind"] == "combat_action": typed.append({"kind": "combat_action", "action": payload["action"]})
    projected = [_project_legacy_event(event) for event in public["observation"]["history"]]
    if typed != projected: reject("current typed evidence does not match the complete public combat history")
