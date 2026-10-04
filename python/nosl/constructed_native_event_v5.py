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
SHUFFLE_SAMPLER = "nosl-constructed-native-event-tape-shuffle-v2-public-evidence-v2"
SHUFFLE_POSTERIOR = "owned-constructed-native-event-tape-shuffle-v2-public-evidence-v2"
ROOT_LAW = "one-declared-native-event-owned-combat-fixed-public-stopping-rule-with-absence-v1"
SETUP_LAW = "fixed-acts-declared-event-location-base-inventory-native-event-owner-public-choices-v1"
CONDITIONING_REASON = "declared_event_owner_ordinary_tape_rejection_v1"
SHUFFLE_REASON = "certified_declared_lantern_initial_shuffle_v2"


def sampler_profile(sampler, enabled):
    if sampler == SHUFFLE_SAMPLER:
        require(enabled is True, "constructed_event:shuffle_version_requires_enabled_declaration")
        return SHUFFLE_POSTERIOR
    # Historical v1 producers ignored enableConditioning for event owners. Their
    # retained enabled=true report must keep its original rejection semantics.
    require(sampler == SAMPLER, "constructed_tape:event_sampler_profile")
    return POSTERIOR


def validate_proposal_conditioning(proposal, sampler, entry):
    counts = tuple(proposal[key] for key in ("conditionedShuffles", "conditionedHpCount", "conditionedTapeCells"))
    if sampler == SAMPLER:
        require(counts == (0, 0, 0), "constructed_tape:event_ordinary_rejection_only")
        return
    require(proposal["conditionedHpCount"] == 0, "constructed_event:hp_conditioning_forbidden")
    require(proposal["conditionedShuffles"] <= 1, "constructed_event:only_one_initial_shuffle")
    require(entry is not None, "constructed_event:conditioned_entry_required")
    words = max(len(entry["deck"]) - 1, 0)
    cells = proposal["conditionedTapeCells"]
    require(cells <= words and cells <= proposal["distinctTapeCells"],
        "constructed_event:initial_shuffle_word_bound")
    require(proposal["conditionedShuffles"] == 1 or cells == 0,
        "constructed_event:conditioned_words_without_initial_shuffle")
    if proposal["conditionedShuffles"] and proposal["status"] in (
            "accepted", "public_packet_mismatch", "exact_proposal_density_correction_rejected"):
        require(cells == words, "constructed_event:complete_initial_shuffle_words")


def validate_conditioning_audit(public, audit, sampler):
    eligible = audit["conditioning_eligible"]
    reason = audit["conditioning_reason"]
    proposals = audit["posterior_proposals"]
    if sampler == SAMPLER:
        require(eligible is False and reason == CONDITIONING_REASON,
            "constructed_tape:event_ordinary_rejection_only")
        return
    if not eligible:
        require(reason not in (SHUFFLE_REASON, CONDITIONING_REASON, "conditioning_disabled_for_reference")
            and all(proposal[key] == 0 for proposal in proposals
                for key in ("conditionedShuffles", "conditionedHpCount", "conditionedTapeCells")),
            "constructed_event:uncertified_startup_requires_ordinary_rejection")
        return
    require(reason == SHUFFLE_REASON, "constructed_event:shuffle_conditioning_reason")
    require(all(proposal["conditionedShuffles"] == 1 for proposal in proposals if proposal["status"] in
        ("accepted", "public_packet_mismatch", "exact_proposal_density_correction_rejected")),
        "constructed_event:completed_certified_startup_requires_shuffle")
    # Public-verifiable startup facts only. Native hook/listener closure and the
    # absence of later-draw plans remain bound by the reviewed source fingerprint.
    events = public["public_evidence"]["events"]
    gaps = [event for event in events if event["ownerOrdinal"] is None and event["payload"]["kind"] == "gap"]
    require(len(gaps) == 1 and gaps[0] == events[0]
        and gaps[0]["eventOrdinal"] == 0 and gaps[0]["payload"]["reason"] == "run_start_not_observed",
        "constructed_event:only_initial_global_gap")
    first = next(event for event in events if event["payload"]["kind"] == "combat_decision")
    decision = first["payload"]; observation = decision["observation"]
    require(decision["status"] == "player_decision" and observation["turn"] == 1
        and all(action["revision"] == 0 for action in decision["actions"])
        and not observation["powers"] and len(observation["enemies"]) == 1,
        "constructed_event:initial_lantern_decision_required")
    knight = observation["enemies"][0]
    intent = [{"kind": "Attack", "damage": 17, "repeats": 1}]
    require(all(knight[key] == value for key, value in
        (("slot", 0), ("id", "MysteriousKnight"), ("hp", 108), ("maxHp", 108), ("block", 6)))
        and knight["intents"] == intent
        and sorted((p["id"], p["amount"], p["applierSlot"]) for p in knight["powers"])
            == [("PlatingPower", 6, 0), ("StrengthPower", 6, 0)],
        "constructed_event:exact_lantern_startup_snapshot")
    facts = [event["payload"] for event in events if event["ownerOrdinal"] == first["ownerOrdinal"]
        and event["eventOrdinal"] < first["eventOrdinal"] and event["payload"]["kind"] != "owner_started"]
    require(len(facts) >= 7 and [fact.get("factKind") for fact in facts[:4]]
        == ["started", "entry_assets", "power_changed", "power_changed"],
        "constructed_event:ordered_lantern_startup_facts")
    for fact, model in zip(facts[2:4], ("StrengthPower", "PlatingPower")):
        require(all(fact[key] == value for key, value in (("targetModel", "MysteriousKnight"),
            ("targetSlot", 0), ("sourceSlot", 0), ("model", model), ("amount", 6))),
            "constructed_event:exact_lantern_startup_power_facts")
    cursor = 4
    while cursor < len(facts) and facts[cursor].get("factKind") == "card_drawn": cursor += 1
    require(cursor > 4 and len(facts) == cursor + 2
        and facts[cursor].get("factKind") == "player_turn_started" and facts[cursor]["turn"] == 1
        and facts[cursor + 1].get("factKind") == "intent_published"
        and facts[cursor + 1]["targetSlot"] == 0 and facts[cursor + 1]["model"] == "MysteriousKnight"
        and facts[cursor + 1]["intents"] == intent,
        "constructed_event:initial_draw_and_intent_facts")


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
