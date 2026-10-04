"""Public-only finite Regen context. No engine, labels, or hidden-world access."""
from __future__ import annotations

import copy
import json

from .schema import enum, integer, object_keys, reject, sequence, validate_event, validate_public as validate_legacy

CONTEXT_SCHEMA = "nosl.controller.finite-regen.v1"
TEMPLATE = "nosl-finite-regen-v1"
BASELINE = "nosl-public-rules-v1"
EXIT_REASONS = ("full_hp", "regen_exhausted", "fixed_deadline_expired", "reviewed_safety_scope_failed",
                "no_safe_defense", "public_history_incomplete", "evaluation_incomplete",
                "terminal_loss", "terminal_before_benefit_exit")


def regen(obs):
    return next((p["amount"] for p in obs["powers"] if p["id"] == "RegenPower"), 0)


def plain_card(card):
    attack = card["id"] == "StrikeSilent"
    cost = 1 if attack else 0
    details = {"costsXEnergy": False, "costsXStar": False, "localEnergyCost": cost,
               "localStarCost": -1, "retainThisTurn": False, "slyThisTurn": False,
               "baseReplayCount": 0, "exhaustOnNextPlay": False, "freeThisTurn": False,
               "freeUntilPlayed": False, "freeThisCombat": False, "starCostThisTurn": None, "energyModifiers": []}
    return (card["id"] in ("Finesse", "StrikeSilent") and card["upgrade"] == 0
            and card["cost"] == cost and card["starCost"] == -1 and card["type"] == ("Attack" if attack else "Skill")
            and card["keywords"] == [] and card["enchantments"] == [] and card["affliction"] is None
            and card["publicState"] == {"targetType": "AnyEnemy" if attack else "Self", "tags": "Strike" if attack else ""}
            and card["details"] == details)


def reviewed_scope(obs):
    enemies = obs["enemies"]
    if (obs["schema"] != "nosl.public.v2" or obs["ascension"] != 10 or obs["turn"] < 1 or not 0 < obs["hp"] <= obs["maxHp"]
            or obs["choice"] is not None or any(p is not None for p in obs["potions"]) or obs["stars"] != 0
            or obs["orbCapacity"] != 0 or obs["orbs"] != [] or obs["pets"] != [] or obs["exhaust"] != []
            or obs["unidentifiedDrawCount"] != 0 or obs["drawCount"] < 0
            or any(x["count"] <= 0 for x in obs["unknownDraw"])
            or sum(x["count"] for x in obs["unknownDraw"]) + len(obs["knownDraw"]) != obs["drawCount"]
            or any(not 0 <= x["position"] < obs["drawCount"] for x in obs["knownDraw"])
            or len({x["position"] for x in obs["knownDraw"]}) != len(obs["knownDraw"])
            or len(enemies) != 1): return False
    enemy = enemies[0]
    if (enemy["id"] != "TwigSlimeS" or not 0 < enemy["hp"] <= 6 or enemy["block"] != 0
            or enemy["powers"] != [] or enemy["intents"] != [{"kind": "Attack", "damage": 5, "repeats": 1}]): return False
    if len(obs["powers"]) > 1 or any(p["id"] != "RegenPower" or not 1 <= p["amount"] <= 5 or p["amount"] % 1
            or p["skipNextDurationTick"] or p["selectedCard"] is not None or p["selectedUpgrade"] is not None for p in obs["powers"]): return False
    if (obs["relics"] != ["RingOfTheSnake"] or obs["relicStates"] != [{"id": "RingOfTheSnake",
            "details": {"isWax": 0, "isMelted": 0, "isUsedUp": 0, "stackCount": 1}, "cards": [], "selectedModel": None}]): return False
    cards = [*obs["hand"], *obs["discard"], *(x["card"] for x in obs["knownDraw"])]
    # Count public multiplicities without materializing an unbounded list.
    counts = {"Finesse": 0, "StrikeSilent": 0}
    for card, count in [(c, 1) for c in cards] + [(x["card"], x["count"]) for x in obs["unknownDraw"]]:
        if not plain_card(card): return False
        counts[card["id"]] += count
    return counts == {"Finesse": 2, "StrikeSilent": 1}


def guard_exit(public):
    c, obs = public["controller_context"], public["observation"]
    origin = c["anchor"]["observation"]
    if (not reviewed_scope(obs) or obs["startHp"] != origin["startHp"] or obs["maxHp"] != origin["maxHp"]
            or regen(obs) > c["initialRegen"] or obs["hp"] < origin["hp"]):
        return "aborted", "reviewed_safety_scope_failed"
    if not observed_progress(origin, obs, c["observedEvents"]):
        return "unresolved", "public_history_incomplete"
    if obs["turn"] > c["deadlinePlayerTurn"]: return "aborted", "fixed_deadline_expired"
    if obs["hp"] == obs["maxHp"]:
        return ("finished" if obs["hp"] > origin["hp"] else "aborted"), "full_hp"
    if regen(obs) == 0:
        return ("finished" if obs["hp"] > origin["hp"] else "aborted"), "regen_exhausted"
    if obs["turn"] >= c["deadlinePlayerTurn"]: return "aborted", "fixed_deadline_expired"
    if (obs["block"] < obs["enemies"][0]["intents"][0]["damage"]
            and not any(legal and a["kind"] == "play" and obs["hand"][a["slot"]]["id"] == "Finesse"
                        for a, legal in zip(public["candidate_actions"], public["legal_mask"]))):
        return "aborted", "no_safe_defense"
    if not any(legal and a["kind"] == "end_turn" for a, legal in zip(public["candidate_actions"], public["legal_mask"])):
        return "aborted", "no_safe_defense"
    return None


def observed_progress(origin, obs, events):
    """Reconcile committed public facts; never calculate Regen or card effects."""
    delta, last_hp, consumed_tick, ended = 0, None, False, False
    for event in events:
        if event["kind"] == "player_turn_ended": ended = True
        if event["kind"] not in ("power_changed", "damage"): continue
        detail = json.loads(event["detail"])
        if detail.get("target") != "player": continue
        if event["kind"] == "damage": last_hp = detail["hpAfter"]
        elif detail["id"] == "RegenPower":
            delta += detail["amount"]
            consumed_tick |= detail["amount"] < 0
    if last_hp is None and delta == 0:
        return (obs["hp"], regen(obs)) == (origin["hp"], regen(origin))
    return (obs["turn"] > origin["turn"] and ended and consumed_tick
            and last_hp == obs["hp"] and regen(origin) + delta == regen(obs))


def validate_context(public, config):
    from .schema_v2 import validate_player_turns
    c = object_keys(public["controller_context"], ("schemaVersion", "kind", "status", "exitReason", "anchor", "target",
        "startPlayerTurn", "deadlinePlayerTurn", "initialRegen", "lastObservedPlayerTurn", "templateId", "baselinePolicyId", "observedEvents"), "Regen controller")
    if c["schemaVersion"] != CONTEXT_SCHEMA or c["kind"] != "safe_finite_regen": reject("unknown finite Regen kind", "UNSUPPORTED")
    enum(c["status"], ("active", "aborted", "finished", "unresolved"), "Regen status")
    if c["status"] == "active":
        if c["exitReason"] is not None: reject("active Regen controller cannot have exit reason")
    else:
        enum(c["exitReason"], EXIT_REASONS, "Regen exit reason")
        if c["status"] == "unresolved" and c["exitReason"] not in ("public_history_incomplete", "evaluation_incomplete"):
            reject("unresolved Regen controller requires uncertainty reason")
        if c["status"] == "aborted" and c["exitReason"] in ("public_history_incomplete", "evaluation_incomplete"):
            reject("aborted Regen controller requires public exit reason")
    validate_legacy(c["anchor"], config["base_config"])
    origin, obs = c["anchor"]["observation"], public["observation"]
    validate_player_turns(origin["history"], origin["turn"])
    if not reviewed_scope(origin): reject("outside reviewed finite Regen anchor scope", "UNSUPPORTED")
    start = integer(c["startPlayerTurn"], "startPlayerTurn", 1)
    amount = integer(c["initialRegen"], "initialRegen", 0, 5)
    deadline = integer(c["deadlinePlayerTurn"], "deadlinePlayerTurn", 2)
    current = integer(c["lastObservedPlayerTurn"], "lastObservedPlayerTurn", start)
    if start != origin["turn"] or amount != regen(origin) or deadline != start + max(1, amount) or current != obs["turn"]:
        reject("Regen anchor/start/fixed deadline/current turn mismatch")
    if c["templateId"] != TEMPLATE or c["baselinePolicyId"] != BASELINE: reject("unknown finite Regen template or baseline", "UNSUPPORTED")
    if object_keys(c["target"], ("powerId",), "Regen target") != {"powerId": "RegenPower"}: reject("unknown Regen target")
    original, history = origin["history"], obs["history"]
    sequence(c["observedEvents"], "observedEvents", config["base_config"]["max_history"])
    if history[:len(original)] != original or c["observedEvents"] != history[len(original):]:
        reject("observed events must exactly extend frozen Regen anchor history")
    if obs["startHp"] != origin["startHp"]: reject("combat HP reference changed")
    if not observed_progress(origin, obs, c["observedEvents"]):
        reject("public Regen HP/power progress contradicts observed history")
    # A genuine public departure is representable so the controller can fail closed.
    # Structural/private-field validation has already run for both packets.
    if c["status"] == "finished" and guard_exit(public) != ("finished", c["exitReason"]):
        reject("finished Regen benefit requires actual public healing and exhausted benefit")
    return public


class PublicRegenController:
    """Immutable single plan; no restart, simulator, search, or learned execution."""
    def __init__(self, public, config):
        from .schema_v2 import validate_public
        validate_public(public, config)
        if public["controller_context"].get("schemaVersion") != CONTEXT_SCHEMA: reject("finite Regen context required")
        self.config, self._public, self._settled = copy.deepcopy(config), copy.deepcopy(public), False
        self._apply_guard()

    @property
    def public(self): return copy.deepcopy(self._public)

    def _apply_guard(self):
        c = self._public["controller_context"]
        if c["status"] == "active" and (exit_state := guard_exit(self._public)):
            c.update(status=exit_state[0], exitReason=exit_state[1])

    def advance(self, public):
        from .schema_v2 import validate_public
        if self._settled: reject("settled finite plan cannot receive another decision")
        validate_public(public, self.config)
        old, new = self._public["controller_context"], public["controller_context"]
        immutable = ("schemaVersion", "kind", "anchor", "target", "startPlayerTurn", "deadlinePlayerTurn", "initialRegen", "templateId", "baselinePolicyId")
        if any(old[k] != new.get(k) for k in immutable): reject("cannot replace or renew an existing finite plan anchor")
        history = self._public["observation"]["history"]
        if new["lastObservedPlayerTurn"] < old["lastObservedPlayerTurn"] or public["observation"]["history"][:len(history)] != history:
            reject("public history/turn cannot move backwards or change")
        if old["status"] != "active" and (new["status"], new["exitReason"]) != (old["status"], old["exitReason"]):
            reject("exited plan cannot reopen or change its exit")
        self._public = copy.deepcopy(public)
        self._apply_guard()
        return self.public

    def settle(self, terminal):
        from .schema_v2 import validate_player_turns
        if self._settled: reject("finite plan already settled")
        object_keys(terminal, ("result", "events", "final_hp", "final_max_hp"), "Regen terminal public facts")
        enum(terminal["result"], ("win", "loss"), "terminal result")
        hp = integer(terminal["final_hp"], "final_hp", 0, self.config["base_config"]["hp_max"])
        maximum = integer(terminal["final_max_hp"], "final_max_hp", max(1, hp), self.config["base_config"]["hp_max"])
        if (hp == 0) != (terminal["result"] == "loss"): reject("terminal result/HP mismatch")
        events = sequence(terminal["events"], "terminal events", self.config["base_config"]["max_history"])
        previous = self._public["observation"]["history"]
        if events[:len(previous)] != previous: reject("terminal history must extend observed public history")
        config = {**self.config["base_config"], "_require_v2": True}
        for event in events: validate_event(event, config)
        validate_player_turns(events)
        c = self._public["controller_context"]
        if maximum != c["anchor"]["observation"]["maxHp"]: reject("terminal max HP left reviewed scope")
        if c["status"] == "active":
            c.update(status="aborted", exitReason="terminal_loss" if terminal["result"] == "loss" else "terminal_before_benefit_exit")
        self._settled = True
        return copy.deepcopy(c)
