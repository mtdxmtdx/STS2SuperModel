"""Strict public owner contract for the declared Lantern Key tape prior.

This is an engineering source-law check. It grants no natural provenance,
calibration, corpus admission or fitting authority.
"""
from .policy_v5 import digest, require
from .schema import integer, object_keys

PRIOR_SCHEMA = "nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1"
SOURCE_KIND = "constructed_native_event_tape_action_fixture_v1"
SAMPLER = "nosl-constructed-native-event-tape-rejection-v1-public-evidence-v2"
POSTERIOR = "owned-constructed-native-event-tape-conditional-v1-public-evidence-v2"
ROOT_LAW = "one-declared-native-event-owned-combat-fixed-public-stopping-rule-with-absence-v1"
SETUP_LAW = "fixed-acts-declared-event-location-base-inventory-native-event-owner-public-choices-v1"
CONDITIONING_REASON = "declared_event_owner_ordinary_tape_rejection_v1"


def validate_owner_prior(prior):
    owner = object_keys(prior.get("eventOwner"), ("event", "act", "fixtureFloor", "choiceRule"), "declared event owner")
    require(prior.get("schemaVersion") == PRIOR_SCHEMA
        and owner["event"] == "TheLanternKey" and owner["act"] == "Hive"
        and owner["choiceRule"] == "keep-the-key-then-fight-v1"
        and prior["setup"]["encounter"] == "MysteriousKnightEventEncounter",
        "constructed_event:typed_lantern_owner_required")
    # Pinned native Hive.BaseNumberOfRooms, not a natural floor-history claim.
    integer(owner["fixtureFloor"], "event_fixture_floor", 1, 14)
    require(prior["rootLaw"] == ROOT_LAW and prior["setupLaw"] == SETUP_LAW,
        "constructed_event:owner_law_required")
    return owner


def validate_public_owner(public, combat, prior):
    """Mirror the native fixed owner rule using only recorded public evidence."""
    declared = validate_owner_prior(prior)
    events = public["public_evidence"]["events"]
    owners = [event for event in events if event["payload"]["kind"] == "owner_started"
        and event["payload"]["ownerKind"] == "event"]
    require(len(owners) == 1, "constructed_event:one_event_owner_required")
    owner = owners[0]; start = owner["payload"]; child = combat["payload"]
    context = public["observation"]["runContext"]
    require(start["completeFromOwnerStart"] is True and start["parentOwnerOrdinal"] is None
        and start["actIndex"] == child["actIndex"] == context["actIndex"] == 1
        and start["floor"] == child["floor"] == context["floor"] == declared["fixtureFloor"]
        and child["completeFromOwnerStart"] is True and child["parentOwnerOrdinal"] == owner["ownerOrdinal"],
        "constructed_event:exact_event_to_combat_owner_required")
    history = [event for event in events if event["ownerOrdinal"] == owner["ownerOrdinal"]]
    require(len(history) == 5 and history[0] == owner,
        "constructed_event:complete_precombat_owner_history_required")
    first, keep, second, fight = (event["payload"] for event in history[1:])
    require(first == {"kind": "options", "options": [
            {"key": "RETURN_THE_KEY", "isLocked": False, "price": None},
            {"key": "KEEP_THE_KEY", "isLocked": False, "price": None}]}
        and second == {"kind": "options", "options": [{"key": "FIGHT", "isLocked": False, "price": None}]},
        "constructed_event:exact_public_offers_required")
    require(digest(keep) == digest({"kind": "option_chosen", "offerEventOrdinal": history[1]["eventOrdinal"], "key": "KEEP_THE_KEY"})
        and digest(fight) == digest({"kind": "option_chosen", "offerEventOrdinal": history[3]["eventOrdinal"], "key": "FIGHT"})
        and history[4]["eventOrdinal"] < combat["eventOrdinal"],
        "constructed_event:keep_then_fight_public_choices_required")
    return declared
