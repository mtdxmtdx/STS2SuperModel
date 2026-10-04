"""Finite conservative public screen for the unpriced experimental v5 profile.

Passing this list is not a proof of applicability for all content. No prices,
candidate filtering, fallback action, predicted outcomes or private state enter
this screen. A blocker rejects the entire decision.
"""
from __future__ import annotations

from .schema_v5 import loads, validate_public

APPLICABILITY_PROFILE = "nosl.full-policy.empty-price-public-screen.v5.1"
RESOURCE_CARDS = frozenset(("Feed", "TheHunt", "HandOfGreed", "Alchemize"))
RESOURCE_RELICS = frozenset(("ChosenCheese",))


def resource_screen(public, config):
    validate_public(public, config)
    observation, blockers = public["observation"], []
    indices = [i for i, (action, legal) in enumerate(zip(public["candidate_actions"], public["legal_mask"]))
               if legal and action["kind"] in ("potion", "discard_potion")]
    if indices: blockers.append({"code": "UNPRICED_LEGAL_POTION_ACTION", "action_indices": indices})
    entries, history, cards, relics = [], [], set(), set()

    def inspect(value):
        if isinstance(value, dict):
            identity = value.get("id")
            if isinstance(identity, str) and identity in RESOURCE_CARDS: cards.add(identity)
            if isinstance(identity, str) and identity in RESOURCE_RELICS: relics.add(identity)
            for item in value.values(): inspect(item)
        elif isinstance(value, list):
            for item in value: inspect(item)
        elif isinstance(value, str):
            if value in RESOURCE_CARDS: cards.add(value)
            if value in RESOURCE_RELICS: relics.add(value)

    inspect(observation)
    for index, event in enumerate(observation["history"]):
        try: detail = loads(event["detail"])
        except ValueError: detail = event["detail"]
        inspect(detail)
        if event["kind"] == "native_entry_assets": entries.append(loads(event["detail"]))
        if event["kind"] == "potion_used" or (event["kind"] == "action"
                and loads(event["detail"])["kind"] in ("potion", "discard_potion")):
            history.append(index)
    if history: blockers.append({"code": "UNPRICED_PUBLIC_POTION_HISTORY", "history_event_indices": history})
    if len(entries) != 1:
        blockers.append({"code": "PUBLIC_RESOURCE_ENTRY_ANCHOR_UNAVAILABLE"})
    else:
        entry = entries[0]
        if observation["gold"] != observation["startGold"] or observation["gold"] != entry["gold"]:
            blockers.append({"code": "UNPRICED_OBSERVED_GOLD_CHANGE"})
        if observation["maxHp"] != entry["maxHp"]:
            blockers.append({"code": "UNPRICED_OBSERVED_MAX_HP_CHANGE"})
    if cards: blockers.append({"code": "UNPRICED_KNOWN_RESOURCE_CARD", "card_ids": sorted(cards)})
    if relics: blockers.append({"code": "UNPRICED_KNOWN_RESOURCE_RELIC", "relic_ids": sorted(relics)})
    return {"profile": APPLICABILITY_PROFILE, "blockers": blockers, "certifies_full_applicability": False}
