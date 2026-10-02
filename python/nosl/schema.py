"""Strict public boundary, independent of torch and the rules engine.

All unknown fields, including nested JSON encoded in public event detail, are rejected.
Audit metadata can be stored by the loader but is never passed to an encoder.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any

PUBLIC_SCHEMA = "nosl.student.public.v1"
HEADS = ("value", "win_probability", "death_probability", "expected_final_hp", "hp_distribution", "potion_net_change")
CARD_KEYS = "id upgrade cost starCost type keywords".split()
# Native CardType values for real cards; None is an engine sentinel, not a card.
CARD_TYPES = ("Attack", "Skill", "Power", "Status", "Curse", "Quest")
ACTION_KEYS = "revision kind slot target selection".split()
OBS_KEYS = "schema startHp ascension turn hp maxHp block energy stars hand discard exhaust unknownDraw knownDraw drawCount potions relics powers enemies history choice".split()
EVENT_KINDS = "combat_started player_turn intent_published draw card_played player_turn_ended potion_used damage power_changed shuffle action choice pre_settlement card_started card_generated hidden_card_generated automatic_selection".split()


class SchemaError(ValueError):
    def __init__(self, message: str, status: str = "INVALID_INPUT"):
        super().__init__(message)
        self.status = status


def reject(message: str, status: str = "INVALID_INPUT") -> None:
    raise SchemaError(message, status)


def object_keys(value: Any, keys: list[str] | tuple[str, ...], where: str) -> dict:
    if not isinstance(value, dict) or set(value) != set(keys):
        reject(f"{where}: fields must be exactly {sorted(keys)}")
    return value


def integer(value: Any, where: str, low: int = 0, high: int = 1_000_000) -> int:
    if type(value) is not int or not low <= value <= high:
        reject(f"{where}: integer outside [{low}, {high}]")
    return value


def number(value: Any, where: str, low: float = -1e6, high: float = 1e6) -> float:
    if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
        reject(f"{where}: invalid finite number")
    return float(value)


def boolean(value: Any, where: str) -> bool:
    if type(value) is not bool:
        reject(f"{where}: expected boolean")
    return value


def sequence(value: Any, where: str, limit: int = 1024) -> list:
    if not isinstance(value, list) or len(value) > limit:
        reject(f"{where}: expected list of at most {limit} items")
    return value


def enum(value: Any, allowed: list | tuple | set, where: str) -> str:
    if not isinstance(value, str) or value not in allowed:
        reject(f"{where}: unsupported value {value!r}", "UNSUPPORTED")
    return value


def load_config(path: str | Path) -> dict:
    config = json.loads(Path(path).read_text(encoding="utf-8"))
    if config.get("config_version") != "nosl.student.config.v1" or config.get("schema_version") != PUBLIC_SCHEMA:
        reject("unsupported model configuration")
    if config.get("bonus_heads_enabled") is not False:
        reject("bonus targets/controller contract not implemented", "UNSUPPORTED")
    integer(config.get("hidden_dim"), "hidden_dim", 16, 512)
    integer(config.get("hp_bins"), "hp_bins", 3, 1025)
    integer(config.get("hp_max"), "hp_max", 1, 10000)
    return config


def validate_card(card: Any, config: dict) -> None:
    extended = config.get("_require_v2", False) or "details" in card
    object_keys(card, CARD_KEYS + (["details", "enchantments", "affliction", "publicState"] if extended else []), "card")
    if extended:
        validate_card_v2(card, config)
    enum(card["id"], config["supported_cards"], "card.id")
    integer(card["upgrade"], "card.upgrade", 0, 10)
    integer(card["cost"], "card.cost", -2, 100)
    integer(card["starCost"], "card.starCost", -2, 100)
    enum(card["type"], CARD_TYPES, "card.type")
    for keyword in sequence(card["keywords"], "keywords", 32):
        enum(keyword, config["supported_keywords"], "keyword")


def validate_power(power: Any, config: dict) -> None:
    extended = config.get("_require_v2", False) or "amountOnTurnStart" in power
    object_keys(power, ["id", "amount"] + (["amountOnTurnStart", "skipNextDurationTick", "selectedCard", "selectedUpgrade", "applierSlot"] if extended else []), "power")
    if extended:
        integer(power["amountOnTurnStart"], "power.amountOnTurnStart", -1000000)
        boolean(power["skipNextDurationTick"], "power.skipNextDurationTick")
        if power["selectedCard"] is not None: enum(power["selectedCard"], config["supported_cards"], "power.selectedCard")
        if power["selectedUpgrade"] is not None: integer(power["selectedUpgrade"], "selectedUpgrade", 0, 10)
        if power["applierSlot"] is not None: integer(power["applierSlot"], "applierSlot", -2, 255)
    enum(power["id"], config["supported_powers"], "power.id")
    number(power["amount"], "power.amount")


def validate_intent(intent: Any, config: dict) -> None:
    object_keys(intent, ["kind", "damage", "repeats"], "intent")
    enum(intent["kind"], config["supported_intents"], "intent.kind")
    for key in ("damage", "repeats"):
        if intent[key] is not None:
            integer(intent[key], "intent." + key)


def validate_action(action: Any) -> None:
    object_keys(action, ACTION_KEYS, "action")
    integer(action["revision"], "action.revision")  # token validation only, not a feature
    enum(action["kind"], ("play", "potion", "discard_potion", "choose", "end_turn"), "action.kind")
    integer(action["slot"], "action.slot", -1, 255)
    integer(action["target"], "action.target", -2, 255)
    if action["selection"] is not None:
        for index in sequence(action["selection"], "selection", 256):
            integer(index, "selection.index", 0, 255)
        if len(set(action["selection"])) != len(action["selection"]):
            reject("duplicate choice selection")


def validate_choice(choice: Any, config: dict) -> None:
    extended = config.get("_require_v2", False) or "candidateOrder" in choice
    object_keys(choice, "source min max cancelable candidates".split() + (["candidateOrder", "bundles"] if extended else []), "choice")
    enum(choice["source"], config.get("supported_sources", config["supported_cards"]), "choice.source")
    if extended:
        enum(choice["candidateOrder"], ["public", "canonical_unordered_reveal"], "candidateOrder")
        if choice["bundles"] is not None:
            bundles = sequence(choice["bundles"], "bundles", config["max_entities"])
            if len(bundles) != len(choice["candidates"]): reject("bundle/candidate alignment mismatch")
            for bundle in bundles:
                for card in sequence(bundle, "bundle", config["max_entities"]): validate_card(card, config)
    candidates = sequence(choice["candidates"], "choice.candidates", config["max_entities"])
    integer(choice["min"], "choice.min", 0, len(candidates))
    integer(choice["max"], "choice.max", choice["min"], len(candidates))
    boolean(choice["cancelable"], "choice.cancelable")
    for card in candidates:
        validate_card(card, config)


def _public_target(value: Any, config: dict) -> None:
    enum(value, ["player"] + config["supported_enemies"], "event.target")


def validate_event(event: Any, config: dict) -> None:
    object_keys(event, ["kind", "detail"], "event")
    kind = enum(event["kind"], EVENT_KINDS, "event.kind")
    detail = event["detail"]
    if not isinstance(detail, str) or len(detail) > 65536:
        reject("event.detail must be bounded text")
    literals = {"combat_started": ["Silent:A10"], "player_turn_ended": [""], "shuffle": ["known_positions_reset"], "hidden_card_generated": ["draw"]}
    if kind in literals:
        enum(detail, literals[kind], "event.detail")
        return
    if kind == "potion_used":
        enum(detail, config["supported_potions"], "event.potion")
        return
    if kind == "player_turn":
        if not detail.isdigit():
            reject("player_turn detail must be an integer")
        integer(int(detail), "event.turn", 1)
        return
    try:
        obj = json.loads(detail)
    except (ValueError, TypeError):
        reject("event detail is not JSON")
    if kind in ("draw", "card_started", "card_generated"):
        validate_card(obj, config)
    elif kind == "action":
        validate_action(obj)
    elif kind == "choice":
        validate_choice(obj, config)
    elif kind == "intent_published":
        object_keys(obj, ["slot", "id", "intents"], kind)
        integer(obj["slot"], "event.slot", 0, 255)
        enum(obj["id"], config["supported_enemies"], "event.enemy")
        for intent in sequence(obj["intents"], "event.intents", 32):
            validate_intent(intent, config)
    elif kind == "card_played":
        object_keys(obj, ["card", "energySpent", "starsSpent", "resultPile"], kind)
        validate_card(obj["card"], config)
        for key in ("energySpent", "starsSpent"):
            if obj[key] is not None:
                number(obj[key], "event." + key, 0)
        if obj["resultPile"] is not None:
            enum(obj["resultPile"], ("None", "Hand", "Draw", "Discard", "Exhaust", "Play"), "resultPile")
    elif kind == "damage":
        object_keys(obj, "target blocked unblocked overkill hpAfter killed".split() + (["targetSlot", "sourceSlot"] if config.get("_require_v2") else []), kind)
        if config.get("_require_v2"): validate_event_slots(obj)
        _public_target(obj["target"], config)
        for key in ("blocked", "unblocked", "overkill", "hpAfter"):
            number(obj[key], "event." + key, 0)
        boolean(obj["killed"], "event.killed")
    elif kind == "power_changed":
        object_keys(obj, ["target", "id", "amount"] + (["targetSlot", "sourceSlot"] if config.get("_require_v2") else []), kind)
        if config.get("_require_v2"): validate_event_slots(obj)
        _public_target(obj["target"], config)
        enum(obj["id"], config["supported_powers"], "event.power.id")
        number(obj["amount"], "event.power.amount")
    elif kind == "automatic_selection":
        object_keys(obj, ["source", "cards", "unidentifiedCount"], kind)
        if obj["source"] is not None: enum(obj["source"], config.get("supported_sources", config["supported_cards"]), "automatic_selection.source")
        integer(obj["unidentifiedCount"], "unidentifiedCount", 0, config["max_entities"])
        for card in sequence(obj["cards"], "selected cards", config["max_entities"]): validate_card(card, config)
    elif kind == "pre_settlement":
        object_keys(obj, ["hp", "maxHp", "hand", "exhaust"], kind)
        number(obj["hp"], "event.hp", 0)
        number(obj["maxHp"], "event.maxHp", 1)
        for pile in ("hand", "exhaust"):
            for card in sequence(obj[pile], "event." + pile, config["max_entities"]):
                validate_card(card, config)


def validate_public(public: Any, config: dict) -> dict:
    object_keys(public, "schema_version observation history_complete controller_context candidate_actions legal_mask".split(), "public_input")
    if public["schema_version"] != PUBLIC_SCHEMA:
        reject("public schema version unsupported", "UNSUPPORTED")
    boolean(public["history_complete"], "history_complete")
    if not public["history_complete"]:
        reject("required public history is incomplete", "INCOMPLETE_HISTORY")
    context = object_keys(public["controller_context"], ["status"], "controller_context")
    enum(context["status"], ("inactive",), "controller_context.status")
    if not isinstance(public["observation"], dict): reject("observation must be object")
    schema = public["observation"].get("schema")
    if schema not in config.get("observation_schemas", ["nosl.public.v1", "nosl.public.v2"]): reject("unsupported observation version", "UNSUPPORTED")
    config = {**config, "_require_v2": schema == "nosl.public.v2"}
    if schema == "nosl.public.v1":
        # Legacy DTO lacks v2 counters/temporary states. Backward compatibility
        # is limited to its original audited technical scope, never all v2 IDs.
        for category, values in LEGACY_V1.items():
            config["supported_" + category] = [x for x in values if x in config["supported_" + category]]
    obs = object_keys(public["observation"], OBS_KEYS + (OBS_V2_KEYS if config["_require_v2"] else []), "observation")
    if obs["ascension"] != 10: reject("only Silent A10 supported", "UNSUPPORTED")
    if config["_require_v2"]: validate_observation_v2(obs, config)
    for key in ("startHp", "hp", "maxHp"):
        integer(obs[key], key, 1, config["hp_max"])
    if obs["hp"] > obs["maxHp"]:
        reject("hp exceeds maxHp")
    integer(obs["turn"], "turn", 1)
    integer(obs["drawCount"], "drawCount", 0, config["max_entities"])
    for key in ("energy", "stars"):
        integer(obs[key], key)
    number(obs["block"], "block", 0)
    for pile in ("hand", "discard", "exhaust"):
        for card in sequence(obs[pile], pile, config["max_entities"]):
            validate_card(card, config)
    for entry in sequence(obs["unknownDraw"], "unknownDraw", config["max_entities"]):
        object_keys(entry, ["card", "count"], "unknownDraw entry")
        validate_card(entry["card"], config)
        integer(entry["count"], "unknownDraw.count", 1, config["max_entities"])
    known = sequence(obs["knownDraw"], "knownDraw", config["max_entities"])
    for entry in known:
        object_keys(entry, ["position", "card"], "knownDraw entry")
        validate_card(entry["card"], config)
        integer(entry["position"], "knownDraw.position", 0, obs["drawCount"] - 1)
    if len({x["position"] for x in known}) != len(known):
        reject("duplicate known draw positions")
    if len(known) + sum(x["count"] for x in obs["unknownDraw"]) + obs.get("unidentifiedDrawCount", 0) != obs["drawCount"]:
        reject("drawCount contradicts public draw multiset")
    for potion in sequence(obs["potions"], "potions", 16):
        if potion is not None:
            enum(potion, config["supported_potions"], "potion")
    for relic in sequence(obs["relics"], "relics", config["max_entities"]):
        enum(relic, config["supported_relics"], "relic")
    for power in sequence(obs["powers"], "powers", config["max_entities"]):
        validate_power(power, config)
    enemies = sequence(obs["enemies"], "enemies", 32)
    if not enemies:
        reject("no live decision enemies", "NO_DECISION")
    for enemy in enemies:
        object_keys(enemy, "slot id hp maxHp block powers intents".split(), "enemy")
        integer(enemy["slot"], "enemy.slot", 0, 255)
        enum(enemy["id"], config["supported_enemies"], "enemy.id")
        for key in ("hp", "maxHp", "block"):
            number(enemy[key], "enemy." + key, 0)
        for power in sequence(enemy["powers"], "enemy.powers", config["max_entities"]):
            validate_power(power, config)
        for intent in sequence(enemy["intents"], "enemy.intents", 32):
            validate_intent(intent, config)
    if len({e["slot"] for e in enemies}) != len(enemies):
        reject("duplicate enemy slots")
    history = sequence(obs["history"], "history", config["max_history"])
    if not history or history[0] != {"kind": "combat_started", "detail": "Silent:A10"}:
        reject("complete history must start with public combat start", "INCOMPLETE_HISTORY")
    for event in history:
        validate_event(event, config)
    choice = obs["choice"]
    if choice is not None:
        validate_choice(choice, config)
    actions = sequence(public["candidate_actions"], "candidate_actions", config.get("max_candidates", 4096))
    masks = sequence(public["legal_mask"], "legal_mask", config.get("max_candidates", 4096))
    if len(actions) != len(masks):
        reject("legal mask length mismatch")
    for mask in masks:
        boolean(mask, "legal_mask")
    if not actions or not any(masks):
        reject("no legal candidate; terminal/wait state requires caller handling", "NO_DECISION")
    if len({json.dumps(a, sort_keys=True) for a in actions}) != len(actions):
        reject("duplicate candidates")
    revisions = set()
    for action in actions:
        validate_action(action)
        revisions.add(action["revision"])
        kind = action["kind"]
        if action["target"] >= 0 and action["target"] not in {e["slot"] for e in enemies + (obs.get("pets") or [])}:
            reject("action target not present")
        if (kind == "choose") != (choice is not None):
            reject("choice action does not match decision boundary")
        if kind in ("play", "potion", "discard_potion"):
            pile = obs["hand"] if kind == "play" else obs["potions"]
            if kind == "discard_potion" and action["target"] != -1: reject("discard potion cannot have target")
            if not 0 <= action["slot"] < len(pile) or pile[action["slot"]] is None or action["selection"] is not None:
                reject("action references unavailable entity")
        elif kind == "end_turn":
            if (action["slot"], action["target"], action["selection"]) != (-1, -1, None):
                reject("malformed end_turn")
        else:
            selected = action["selection"]
            if selected is None or action["slot"] != -1 or action["target"] != -1:
                reject("malformed choice")
            if any(i >= len(choice["candidates"]) for i in selected):
                reject("choice index out of range")
            if not (choice["min"] <= len(selected) <= choice["max"] or len(selected) == 0 and choice["cancelable"]):
                reject("choice count outside bounds")
    if len(revisions) != 1:
        reject("mixed action revisions")
    return public

# Reviewed public v2 fields from PublicCardDetailsBuilder/PublicRelicDetails.
# These are static schema, never reflected private object fields at inference.
OBS_V2_KEYS = "counters relicStates gold startGold orbCapacity orbs pets unidentifiedDrawCount".split()
COUNTER_KEYS = "attacksPlayed skillsPlayed shivsPlayed cardsDiscarded cardsDrawnCombat cardsPlayedCombat cardsGeneratedCombat cardsPlayed manualCardsPlayed playsStarted attacksStarted zeroCostAttacksStarted attackOrSkillStarts firstInSeriesStarts starsGained".split()
DETAIL_BOOLS = "costsXEnergy costsXStar retainThisTurn slyThisTurn exhaustOnNextPlay freeThisTurn freeUntilPlayed freeThisCombat".split()
DETAIL_INTS = "localEnergyCost localStarCost baseReplayCount".split()
STATE_NUMERIC = {
    "Rampage": ["damage"], "Thrash": ["damage"], "Claw": ["damage"], "Whistle": ["damage"],
    "Maul": ["damage", "damageFromMaulPlays"], "GeneticAlgorithm": ["block", "blockIncrease"],
    "TheScythe": ["damageIncrease"], "Wither": ["witherLevel", "turnEndDamage"],
    "Disintegration": ["powerAmount"], "TheHunt": ["pendingCardRewards"], "Guilty": ["combatsCompleted"], "Dowsing": ["unknownRoomsEntered"]}
RELIC_DETAIL_KEYS = "isWax isMelted isUsedUp stackCount attackPlayedThisTurn previousTurnHadNoAttack hpLostThisTurn dexterityApplied cardsAdded cardsPlayed wasUsedThisCombat triggeredThisCombat triggeredThisTurn shouldTrigger isArmed starsSpent turnCounter lastAttackTurn cardsExhausted etherealCount attacksThisTurn tookDamageThisCombat skillsThisTurn wasUsed orbsChanneled triggeredThisTurn wasUsedThisTurn attacksPlayed shouldTrigger usedThisCombat wasOwnerPartOfLastPlayerTurn eligibleForExtraTurn cooldown gainNextTurn handsSeen playedAttack playedSkill playedPower strengthApplied usedThisTurn attackPlayedThisTurn isUsed shouldTriggerThisTurn eliteVictories skillsPlayed finished cardsPlayedThisTurn gaveRelics combatsLeft turnsSeen combatsSeen timesLifted goldenPathAct combatRewardsSeen hasTriggered hasItemBeenBought rewardsSacrificed cardsPlayedPreviousTurn shouldDrawExtra canStillTriggerThisTurn kindleCount timesUsed treasureRoomsEntered combatsFinished".split()


def validate_event_slots(obj: dict) -> None:
    integer(obj["targetSlot"], "event.targetSlot", -2, 255)
    if obj["sourceSlot"] is not None:
        integer(obj["sourceSlot"], "event.sourceSlot", -2, 255)


def validate_card_v2(card: dict, config: dict) -> None:
    details = object_keys(card["details"], DETAIL_BOOLS + DETAIL_INTS + ["starCostThisTurn", "energyModifiers"], "card.details")
    for key in DETAIL_BOOLS:
        boolean(details[key], "card.details." + key)
    for key in DETAIL_INTS:
        integer(details[key], "card.details." + key, -1000000)
    if details["starCostThisTurn"] is not None:
        integer(details["starCostThisTurn"], "card.starCostThisTurn", -2)
    for modifier in sequence(details["energyModifiers"], "energyModifiers", config["max_entities"]):
        object_keys(modifier, ["kind", "amount"], "energy modifier")
        enum(modifier["kind"], "FreeThisTurn FreeUntilPlayed FreeThisCombat SetThisTurn SetThisTurnOrUntilPlayed SetThisCombat AddThisCombat AddThisTurn AddUntilPlayed".split(), "modifier.kind")
        integer(modifier["amount"], "modifier.amount", -1000000)
    enchantments = sequence(card["enchantments"], "card.enchantments", 256)
    for effect in enchantments:
        object_keys(effect, ["id", "amount"], "enchantment")
        enum(effect["id"], config.get("supported_enchantments", []), "enchantment.id")
        number(effect["amount"], "enchantment.amount")
    if card["affliction"] is not None:
        effect = object_keys(card["affliction"], ["id", "amount"], "affliction")
        enum(effect["id"], config.get("supported_afflictions", []), "affliction.id")
        number(effect["amount"], "affliction.amount")
    state = card["publicState"]
    keys = ["targetType", "tags"] + STATE_NUMERIC.get(card["id"], [])
    special = {"MadScience": "rider", "Bombardment": "hasReplayed", "Fetch": "playedThisTurn", "ThrummingHatchet": "playedOnTurn", "Bolas": "playedOnTurn"}
    if card["id"] in special:
        keys.append(special[card["id"]])
    for index, effect in enumerate(enchantments):
        keys.append(f"enchantment.{index}.status")
        if effect["id"] == "Momentum": keys.append(f"enchantment.{index}.extraDamage")
    object_keys(state, keys, "card.publicState")
    enum(state["targetType"], "None Self AnyEnemy AllEnemies RandomEnemy AnyPlayer AnyAlly AllAllies TargetedNoCreature Osty".split(), "targetType")
    if not isinstance(state["tags"], str): reject("tags must be string")
    for tag in filter(None, state["tags"].split(",")):
        enum(tag, "None Strike Defend Minion OstyAttack Shiv".split(), "tag")
    for key in keys:
        if key in ("targetType", "tags"): continue
        value = state[key]
        if not isinstance(value, str): reject("public card state values must be strings")
        if key == "rider": enum(value, "None Sapping Violence Choking Energized Wisdom Chaos Expertise Curious Improvement".split(), "rider")
        elif key in ("hasReplayed", "playedThisTurn"): enum(value, ["true", "false"], key)
        elif key.endswith(".status"): enum(value, ["Normal", "Disabled"], key)
        elif key == "playedOnTurn" and value == "none": pass
        else:
            try: numeric = float(value)
            except ValueError: reject("non-numeric public card value")
            number(numeric, "publicState." + key)


def validate_observation_v2(obs: dict, config: dict) -> None:
    for key in ("gold", "startGold", "orbCapacity", "unidentifiedDrawCount"):
        integer(obs[key], key, 0, 1000000 if key in ("gold", "startGold") else config["max_entities"])
    counters = object_keys(obs["counters"], COUNTER_KEYS, "counters")
    for key in COUNTER_KEYS:
        number(counters[key], "counter." + key, 0)
    relics = sequence(obs["relicStates"], "relicStates", config["max_entities"])
    if sorted(x["id"] for x in relics if isinstance(x, dict) and "id" in x) != sorted(obs["relics"]):
        reject("relicStates do not match relic ids")
    all_ids = config.get("supported_sources", config["supported_cards"])
    for relic in relics:
        object_keys(relic, ["id", "details", "cards", "selectedModel"], "relic state")
        enum(relic["id"], config["supported_relics"], "relic.id")
        details = relic["details"]
        if not isinstance(details, dict) or not {"isWax", "isMelted", "isUsedUp", "stackCount"} <= set(details) or not set(details) <= set(RELIC_DETAIL_KEYS):
            reject("unrecognized or incomplete public relic counters")
        for key, value in details.items(): integer(value, "relic." + key, -1000000)
        for card in sequence(relic["cards"], "relic.cards", config["max_entities"]): validate_card(card, config)
        if relic["selectedModel"] is not None:
            enum(relic["selectedModel"], config.get("supported_selected_models", all_ids), "relic.selectedModel")
    for orb in sequence(obs["orbs"], "orbs", config["max_entities"]):
        object_keys(orb, ["id", "passive", "evoke"], "orb")
        enum(orb["id"], config.get("supported_orbs", []), "orb.id")
        number(orb["passive"], "orb.passive")
        number(orb["evoke"], "orb.evoke")
    if len(obs["orbs"]) > obs["orbCapacity"]: reject("orbs exceed capacity")
    pets = sequence(obs["pets"], "pets", 32)
    for pet in pets:
        object_keys(pet, "slot id hp maxHp block powers".split(), "pet")
        enum(pet["id"], config["supported_enemies"], "pet.id")
        integer(pet["slot"], "pet.slot", 0, config["max_entities"] - 1)
        for key in ("hp", "maxHp", "block"): number(pet[key], "pet." + key, 0)
        for power in sequence(pet["powers"], "pet.powers", config["max_entities"]): validate_power(power, config)
    slots = [p["slot"] for p in pets] + [e["slot"] for e in obs["enemies"]]
    if len(set(slots)) != len(slots): reject("duplicate public creature slots")


LEGACY_V1 = {
    "cards": "StrikeSilent DefendSilent Neutralize Survivor AscendersBane Acrobatics Backflip Prepared ThinkingAhead DeadlyPoison Slimed".split(),
    "enemies": "TwigSlimeS LeafSlimeS Nibbit".split(),
    "potions": "FirePotion BlockPotion EnergyPotion SwiftPotion FruitJuice".split(),
    "relics": "RingOfTheSnake MeatOnTheBone ChosenCheese".split(),
    "powers": "StrengthPower WeakPower PoisonPower".split(),
}
