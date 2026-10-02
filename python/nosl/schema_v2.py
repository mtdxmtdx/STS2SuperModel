"""Explicit finite-Hunt v2 boundary; legacy schema and checkpoints stay frozen."""
from __future__ import annotations

import copy
import json
from pathlib import Path

from .schema import (PUBLIC_SCHEMA as LEGACY_SCHEMA, HEADS, SchemaError, boolean, enum, integer,
                     number, object_keys, reject, sequence, validate_card, validate_observation_v2,
                     validate_event, validate_public as validate_legacy)

PUBLIC_SCHEMA = "nosl.student.public.v2"
CONFIG_VERSION = "nosl.student.config.v2"
MODEL_VERSION = "nosl.student.model.v2"
CONTEXT_SCHEMA = "nosl.controller.finite-hunt.v1"
TEMPLATE = "nosl-finite-hunt-next-turn-v1"
BASELINE = "nosl-public-rules-v1"
PLAN_HEADS = ("specified_success_probability", "extra_net_hp_loss")
STATUSES = ("active", "aborted", "finished", "unresolved")
EXIT_REASONS = ("fixed_deadline_expired", "public_hp_safety_guard", "designated_card_spent_without_finish",
                "deadline_turn_no_legal_hunt_or_draw", "terminal_loss", "terminal_without_specified_reward",
                "specified_reward_observed", "evaluation_incomplete", "public_history_incomplete")
CARDS = "TheHunt StrikeSilent DefendSilent Neutralize Survivor AscendersBane Acrobatics Backflip Prepared ThinkingAhead Slimed".split()
EXTENSION_EVENTS = ("native_entry_assets", "forced_event_context", "event_merchant_inventory_revealed")


def load_config(path: str | Path) -> dict:
    return validate_config(json.loads(Path(path).read_text(encoding="utf-8")))


def validate_config(config: dict) -> dict:
    object_keys(config, ("config_version", "schema_version", "model_version", "base_config",
                         "bonus_heads_enabled", "context_hash_dim", "plan_loss_weights"), "finite Hunt config")
    if (config["config_version"] != CONFIG_VERSION or config["schema_version"] != PUBLIC_SCHEMA
            or config["model_version"] != MODEL_VERSION or config["bonus_heads_enabled"] is not True):
        reject("unsupported finite Hunt configuration", "UNSUPPORTED")
    base = config["base_config"]
    if (not isinstance(base, dict) or base.get("config_version") != "nosl.student.config.v1"
            or base.get("schema_version") != LEGACY_SCHEMA or base.get("bonus_heads_enabled") is not False):
        reject("finite Hunt requires a separately frozen legacy core configuration")
    integer(base.get("hidden_dim"), "hidden_dim", 16, 512)
    integer(base.get("hp_bins"), "hp_bins", 3, 1025)
    integer(base.get("hp_max"), "hp_max", 1, 10000)
    # The bounded trainer must consume at least one root on each iteration.
    # Validate here so readiness, model construction and fit share one contract.
    integer(base.get("batch_size"), "batch_size", 1, 1024)
    integer(base.get("seed"), "seed", 0, 2**63 - 1)
    integer(base.get("torch_threads", 1), "torch_threads", 1, 256)
    number(base.get("learning_rate"), "learning_rate", 1e-12, 1)
    weights = object_keys(base.get("loss_weights"), ("pairwise", "equivalent", *HEADS), "base loss weights")
    for value in weights.values(): number(value, "base loss weight", 0, 100)
    integer(config["context_hash_dim"], "context_hash_dim", 16, 1024)
    object_keys(config["plan_loss_weights"], PLAN_HEADS, "plan loss weights")
    for value in config["plan_loss_weights"].values():
        number(value, "plan loss weight", 0, 100)
    return config


def legacy_input(public: dict) -> dict:
    # Only the frozen core sees this projection. The v2 encoder separately
    # consumes every native entry event, its detail and its sequence position.
    obs = public["observation"]
    return {**public, "schema_version": LEGACY_SCHEMA, "controller_context": {"status": "inactive"},
            "observation": {**obs, "history": [e for e in obs["history"] if not isinstance(e, dict) or e.get("kind") not in EXTENSION_EVENTS]}}


def validate_forced_events(public: dict, config: dict) -> None:
    history = public["observation"]["history"]
    contexts = [(i, e) for i, e in enumerate(history) if isinstance(e, dict) and e.get("kind") == "forced_event_context"]
    stocks = [(i, e) for i, e in enumerate(history) if isinstance(e, dict) and e.get("kind") == "event_merchant_inventory_revealed"]
    if not contexts and not stocks: return
    if len(contexts) != 1 or len(stocks) > 1 or contexts[0][0] != (2 if stocks else 1) or (stocks and stocks[0][0] != 1):
        reject("forced-event context and any opened inventory must occur once immediately after combat_started")
    def detail(event):
        object_keys(event, ("kind", "detail"), "forced public event")
        if not isinstance(event["detail"], str) or len(event["detail"]) > 65536: reject("forced public detail must be bounded JSON")
        try: return json.loads(event["detail"])
        except (ValueError, TypeError): reject("forced public detail must be JSON")
    context = object_keys(detail(contexts[0][1]), ("owner", "act", "floor", "options", "usedFoulPotionSlot", "purchasedRelicSlots"), "forced event context")
    paths = {"BattlewornDummy": ("Glory", (("SETTING_1",), ("SETTING_2",), ("SETTING_3",))),
             "DenseVegetation": ("Overgrowth", (("REST", "FIGHT"),)),
             "PunchOff": ("Underdocks", (("I_CAN_TAKE_THEM", "FIGHT"),)),
             "TheLanternKey": ("Hive", (("KEEP_THE_KEY", "FIGHT"),)),
             "FakeMerchant": (None, ((),))}
    owner = enum(context["owner"], list(paths), "forced event owner")
    integer(context["floor"], "forced event floor", 0, 1000)
    options = sequence(context["options"], "event options", 3)
    if any(not isinstance(x, str) for x in options) or tuple(options) not in paths[owner][1]: reject("unsupported forced event path", "UNSUPPORTED")
    enum(context["act"], ("Hive", "Glory") if owner == "FakeMerchant" else (paths[owner][0],), "forced event act")
    purchased = sequence(context["purchasedRelicSlots"], "purchased relic slots", 6)
    for slot in purchased: integer(slot, "purchased relic slot", 0, 5)
    if len(set(purchased)) != len(purchased): reject("duplicate relic purchase")
    if owner == "FakeMerchant":
        integer(context["usedFoulPotionSlot"], "usedFoulPotionSlot", 0, config["max_entities"] - 1)
        if purchased and not stocks: reject("purchased merchant relics require observed inventory")
    elif context["usedFoulPotionSlot"] is not None or purchased or stocks:
        reject("merchant state attached to a different forced-event owner")
    if stocks:
        inventory = sequence(detail(stocks[0][1]), "revealed merchant inventory", 6)
        if len(inventory) != 6: reject("native FakeMerchant reveals exactly six stock entries")
        for entry in inventory:
            object_keys(entry, ("relic", "price"), "revealed merchant item")
            enum(entry["relic"], config["supported_relics"], "revealed relic")
            integer(entry["price"], "revealed relic price")


def validate_native_entries(public: dict, config: dict) -> None:
    obs = public["observation"]
    history = sequence(obs["history"], "history", config["max_history"])
    entries = [(i, event) for i, event in enumerate(history) if isinstance(event, dict) and event.get("kind") == "native_entry_assets"]
    if not entries: return
    if len(entries) != 1 or entries[0][0] != 1 or obs["schema"] != "nosl.public.v2":
        reject("native entry assets must occur once immediately after combat_started")
    event = object_keys(entries[0][1], ("kind", "detail"), "native entry event")
    if not isinstance(event["detail"], str) or len(event["detail"]) > 262144:
        reject("native entry detail must be bounded JSON text")
    try: detail = json.loads(event["detail"])
    except (ValueError, TypeError): reject("native entry detail must be JSON")
    object_keys(detail, ("schemaVersion", "hp", "maxHp", "gold", "deck", "relics", "potions",
                         "maxEnergy", "potionSlots", "orbSlots", "cardRemovalsUsed"), "native entry assets")
    if detail["schemaVersion"] != "nosl.native-entry-assets.v1": reject("unknown native entry version", "UNSUPPORTED")
    integer(detail["hp"], "entry.hp", 1, config["hp_max"])
    integer(detail["maxHp"], "entry.maxHp", detail["hp"], config["hp_max"])
    for key in ("gold", "maxEnergy", "potionSlots", "orbSlots", "cardRemovalsUsed"):
        integer(detail[key], "entry." + key)
    card_config = {**config, "_require_v2": True}
    for card in sequence(detail["deck"], "entry.deck", config["max_entities"]): validate_card(card, card_config)
    for potion in sequence(detail["potions"], "entry.potions", config["max_entities"]):
        if potion is not None: enum(potion, config["supported_potions"], "entry.potion")
    if len(detail["potions"]) != detail["potionSlots"]: reject("entry potion capacity mismatch")
    relics = sequence(detail["relics"], "entry.relics", config["max_entities"])
    validate_observation_v2({**obs, "relics": [r.get("id") for r in relics if isinstance(r, dict)],
                             "relicStates": relics}, card_config)
    if detail["hp"] != obs["startHp"] or detail["gold"] != obs["startGold"]:
        reject("native entry combat-start reference mismatch")


def validate_anchor_scope(anchor: dict) -> None:
    obs = anchor["observation"]
    cards = [*obs["hand"], *obs["discard"], *obs["exhaust"],
             *(x["card"] for x in obs["knownDraw"]), *(x["card"] for x in obs["unknownDraw"])]
    hunt_count = sum(c["id"] == "TheHunt" for c in [*obs["hand"], *obs["discard"], *obs["exhaust"], *(x["card"] for x in obs["knownDraw"])])
    hunt_count += sum(x["count"] for x in obs["unknownDraw"] if x["card"]["id"] == "TheHunt")
    if (obs["schema"] != "nosl.public.v2" or obs["choice"] is not None
            or len(obs["enemies"]) != 1 or obs["enemies"][0]["id"] not in ("TwigSlimeS", "LeafSlimeS", "Nibbit")
            or any(x is not None for x in obs["potions"]) or any(x != "RingOfTheSnake" for x in obs["relics"])
            or obs.get("unidentifiedDrawCount", 0) or hunt_count != 1
            or any(c["id"] not in CARDS or c.get("enchantments") or c.get("affliction") for c in cards)
            or any(c["id"] == "TheHunt" for c in obs["exhaust"])
            or any(p["id"] not in ("WeakPower", "FrailPower") for p in obs["powers"])):
        reject("outside reviewed finite Hunt public anchor scope", "UNSUPPORTED")


def validate_player_turns(events, expected=None):
    turn = 0
    for event in events:
        if event["kind"] == "player_turn":
            value = int(event["detail"])
            if value < turn: reject("public player-turn history cannot move backwards")
            turn = value
    if expected is not None and turn != expected:
        reject("observation turn disagrees with complete public player-turn history")
    return turn


def validate_public(public: dict, config: dict) -> dict:
    validate_config(config)
    if not isinstance(public, dict) or public.get("schema_version") != PUBLIC_SCHEMA:
        reject("finite Hunt public schema required", "UNSUPPORTED")
    object_keys(public, "schema_version observation history_complete controller_context candidate_actions legal_mask".split(), "public_input")
    if not isinstance(public["observation"], dict): reject("observation must be object")
    validate_native_entries(public, config["base_config"])
    validate_forced_events(public, config["base_config"])
    # Exact outer, nested observation, action and JSON event whitelists remain enforced.
    validate_legacy(legacy_input(public), config["base_config"])
    validate_player_turns(public["observation"]["history"], public["observation"]["turn"])
    if public["controller_context"] == {"status": "inactive"}: return public
    context = object_keys(public["controller_context"], ("schemaVersion", "status", "exitReason", "anchor", "target",
        "startPlayerTurn", "deadlinePlayerTurn", "hpSafetyFloor", "lastObservedPlayerTurn", "templateId",
        "baselinePolicyId", "observedEvents"), "controller_context")
    if context["schemaVersion"] != CONTEXT_SCHEMA:
        reject("unknown controller context", "UNSUPPORTED")
    enum(context["status"], STATUSES, "controller status")
    if context["status"] == "finished":
        reject("finished requires observed terminal settlement, not an active decision packet")
    if context["status"] == "active":
        if context["exitReason"] is not None: reject("active controller cannot have an exit reason")
    else:
        enum(context["exitReason"], EXIT_REASONS, "exitReason")
        if context["status"] == "unresolved" and context["exitReason"] not in ("evaluation_incomplete", "public_history_incomplete"):
            reject("unresolved controller requires an uncertainty reason")
        if context["status"] == "aborted" and context["exitReason"] in ("specified_reward_observed", "evaluation_incomplete", "public_history_incomplete"):
            reject("aborted controller requires an actual public exit reason")
    anchor = context["anchor"]
    validate_legacy(anchor, config["base_config"])
    validate_anchor_scope(anchor)
    start = integer(context["startPlayerTurn"], "startPlayerTurn", 1)
    deadline = integer(context["deadlinePlayerTurn"], "deadlinePlayerTurn", 2)
    floor = integer(context["hpSafetyFloor"], "hpSafetyFloor", 0, config["base_config"]["hp_max"])
    current = integer(context["lastObservedPlayerTurn"], "lastObservedPlayerTurn", start)
    if start != anchor["observation"]["turn"] or deadline != start + 1 or current != public["observation"]["turn"]:
        reject("anchor/start/fixed deadline/current turn mismatch")
    if context["templateId"] != TEMPLATE + ":hp-floor=" + str(floor) or context["baselinePolicyId"] != BASELINE:
        reject("unknown finite template or baseline", "UNSUPPORTED")
    target = object_keys(context["target"], ("cardId", "enemySlot"), "plan target")
    integer(target["enemySlot"], "enemySlot", 0, 255)
    if target != {"cardId": "TheHunt", "enemySlot": anchor["observation"]["enemies"][0]["slot"]}:
        reject("plan target differs from anchor")
    original = anchor["observation"]["history"]
    history = public["observation"]["history"]
    sequence(context["observedEvents"], "observedEvents", config["base_config"]["max_history"])
    if history[:len(original)] != original or context["observedEvents"] != history[len(original):]:
        reject("observed events must exactly extend the frozen public anchor history")
    if public["observation"]["startHp"] != anchor["observation"]["startHp"]:
        reject("combat HP reference changed")
    obs = public["observation"]
    visible_cards = [*obs["hand"], *obs["discard"], *obs["exhaust"], *(x["card"] for x in obs["knownDraw"]), *(x["card"] for x in obs["unknownDraw"])]
    if (obs["schema"] != "nosl.public.v2" or obs["maxHp"] != anchor["observation"]["maxHp"]
            or obs["hp"] > anchor["observation"]["hp"] or obs.get("unidentifiedDrawCount", 0)
            or any(x is not None for x in obs["potions"]) or obs["relics"] != anchor["observation"]["relics"]
            or any(c["id"] not in CARDS or c.get("enchantments") or c.get("affliction") for c in visible_cards)):
        reject("current observation left the reviewed finite Hunt mechanics scope", "UNSUPPORTED")
    return public


def guard_exit(public: dict) -> str | None:
    """Public exits of the reviewed template; never infer counterfactual HP spend."""
    c, obs = public["controller_context"], public["observation"]
    if obs["turn"] > c["deadlinePlayerTurn"]: return "fixed_deadline_expired"
    if obs["choice"] is not None: return None
    if obs["hp"] <= c["hpSafetyFloor"]: return "public_hp_safety_guard"
    if any(card["id"] == "TheHunt" for card in obs["exhaust"]): return "designated_card_spent_without_finish"
    if obs["turn"] == c["deadlinePlayerTurn"]:
        ids = {obs["hand"][a["slot"]]["id"] for a, legal in zip(public["candidate_actions"], public["legal_mask"])
               if legal and a["kind"] == "play"}
        if not ids.intersection(("TheHunt", "Backflip", "Acrobatics", "Prepared", "ThinkingAhead")):
            return "deadline_turn_no_legal_hunt_or_draw"
    return None


class PublicHuntController:
    """One immutable plan per instance. No restart/reset, search, or hidden state."""
    def __init__(self, public: dict, config: dict):
        validate_public(public, config)
        if public["controller_context"].get("status") == "inactive": reject("finite Hunt context required")
        self.config = copy.deepcopy(config)
        self._public = copy.deepcopy(public)
        self._settled = False
        self._apply_guard()

    @property
    def public(self):
        return copy.deepcopy(self._public)

    def _apply_guard(self):
        c = self._public["controller_context"]
        if c["status"] == "active" and (reason := guard_exit(self._public)):
            c.update(status="aborted", exitReason=reason)

    def advance(self, public: dict) -> dict:
        if self._settled: reject("settled finite plan cannot receive another decision")
        validate_public(public, self.config)
        old, new = self._public["controller_context"], public["controller_context"]
        immutable = ("anchor", "target", "startPlayerTurn", "deadlinePlayerTurn", "hpSafetyFloor", "templateId", "baselinePolicyId")
        if any(old[key] != new[key] for key in immutable):
            reject("cannot replace or renew an existing finite plan anchor")
        history = self._public["observation"]["history"]
        if (new["lastObservedPlayerTurn"] < old["lastObservedPlayerTurn"]
                or public["observation"]["history"][:len(history)] != history):
            reject("public history/turn cannot move backwards or change")
        if old["status"] != "active" and (new["status"], new["exitReason"]) != (old["status"], old["exitReason"]):
            reject("exited plan cannot reopen or change its exit")
        self._public = copy.deepcopy(public)
        self._apply_guard()
        return self.public

    def settle(self, terminal: dict) -> dict:
        """Observe actual public settlement facts; rewards offered are not utility."""
        if self._settled: reject("finite plan already settled")
        object_keys(terminal, ("result", "events", "extra_card_rewards_offered"), "terminal public facts")
        enum(terminal["result"], ("win", "loss"), "terminal result")
        offered = integer(terminal["extra_card_rewards_offered"], "extra_card_rewards_offered", 0, 1000)
        events = sequence(terminal["events"], "terminal events", self.config["base_config"]["max_history"])
        previous = self._public["observation"]["history"]
        if events[:len(previous)] != previous: reject("terminal history must extend observed public history")
        config = {**self.config["base_config"], "_require_v2": True}
        for event in events: validate_event(event, config)
        validate_player_turns(events)
        current, fatal = 1, None
        for event in events:
            if event["kind"] == "player_turn": current = int(event["detail"])
            if event["kind"] == "power_changed":
                detail = json.loads(event["detail"])
                if detail["id"] == "TheHuntPower" and detail["amount"] > 0: fatal = current if fatal is None else fatal
        c = self._public["controller_context"]
        if c["status"] == "active":
            success = terminal["result"] == "win" and offered > 0 and fatal is not None and c["startPlayerTurn"] <= fatal <= c["deadlinePlayerTurn"]
            c.update(status="finished" if success else "aborted", exitReason="specified_reward_observed" if success else
                     "terminal_loss" if terminal["result"] == "loss" else "terminal_without_specified_reward")
        self._settled = True
        return copy.deepcopy(c)
