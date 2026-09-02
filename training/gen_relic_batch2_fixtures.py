#!/usr/bin/env python3
"""Generate batch-2 relic CLI fixtures (v0.111.0), reusing the batch-1
fixture machinery. Run from the model repo root or anywhere; paths resolve
relative to this file.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gen_relic_batch1_fixtures import DEFEND, STRIKE, build_fixture

OUTRAGE = "OUTRAGE"

# Mixed 0-cost attack / defend deck: enough plays to empty the hand for the
# Screaming Flagon trigger within a 3-energy turn.
flagon_deck = [OUTRAGE] * 5 + [DEFEND] * 5
attack_deck = [STRIKE] * 9 + [DEFEND]
skill_deck = [DEFEND] * 9 + [STRIKE]


def main() -> None:
    # CLOAK_CLASP (block = 1 x hand) + RIPPLE_BASIN (4 block when no attacks).
    build_fixture(
        "p1-relic-turn-end-block", "p1-turn-end-block-seed",
        ["CLOAK_CLASP", "RIPPLE_BASIN"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # PARRYING_SHIELD: 10+ block at end of turn -> 6 damage to ALL.
    build_fixture(
        "p1-relic-parrying-shield", "p1-parrying-shield-seed",
        ["PARRYING_SHIELD"], skill_deck, "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("play", "Skill", 0), ("end_turn",)],
    )

    # SCREAMING_FLAGON: empty hand at end of turn -> 20 damage to ALL.
    build_fixture(
        "p1-relic-screaming-flagon", "p1-screaming-flagon-seed",
        ["SCREAMING_FLAGON"], flagon_deck, "TERROR_EEL_ELITE",
        plan=[("play", "Any", "any_enemy"), ("play", "Any", 0),
              ("play", "Any", "any_enemy"), ("play", "Any", 0),
              ("play", "Any", "any_enemy"), ("end_turn",)],
    )

    # KUSARIGAMA: every 3rd Attack per turn -> 6 damage to ALL.
    build_fixture(
        "p1-relic-kusarigama", "p1-kusarigama-seed",
        ["KUSARIGAMA"], attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"),
              ("play", "Attack", "any_enemy"), ("end_turn",),
              ("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy")],
    )

    # PAELS_TEARS: unspent energy at end of turn -> +2 Energy next turn.
    build_fixture(
        "p1-relic-paels-tears", "p1-paels-tears-seed",
        ["PAELS_TEARS"], skill_deck, "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("end_turn",), ("end_turn",)],
    )

    # STRIKE_DUMMY (+3) + FAKE_STRIKE_DUMMY (+1) on Strike attacks.
    build_fixture(
        "p1-relic-strike-dummy", "p1-strike-dummy-seed",
        ["STRIKE_DUMMY", "FAKE_STRIKE_DUMMY"], attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"),
              ("play", "Attack", "any_enemy"), ("end_turn",)],
    )

    # SNECKO_EYE: +2 cards drawn at every turn start (turn-1 hand already
    # carries the first bonus: 7 cards).
    build_fixture(
        "p1-relic-snecko-eye", "p1-snecko-eye-seed",
        ["SNECKO_EYE"], attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("end_turn",), ("end_turn",)],
    )

    # WHISPERING_EARRING: +1 Energy at every turn start.
    build_fixture(
        "p1-relic-whispering-earring", "p1-whispering-earring-seed",
        ["WHISPERING_EARRING"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # Combat-start effects carried inside the snapshot: heal, block, max
    # energy, enemy Strength (PHILOSOPHERS_STONE).
    build_fixture(
        "p1-relic-combat-start-carried", "p1-combat-start-carried-seed",
        ["FAKE_BLOOD_VIAL", "FAKE_ANCHOR", "VERY_HOT_COCOA", "PHILOSOPHERS_STONE"],
        attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("end_turn",)],
    )

    # Combat-start orb relics carried in the snapshot: extra orb slots,
    # channeled Lightning/Dark with their turn-boundary passives.
    build_fixture(
        "p1-relic-combat-start-orbs", "p1-combat-start-orbs-seed",
        ["RUNIC_CAPACITOR", "INFUSED_CORE", "SYMBIOTIC_VIRUS"],
        attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )

    # TEA_OF_DISCOURTESY: Dazed shuffled into the draw pile at combat start.
    build_fixture(
        "p1-relic-tea-of-discourtesy", "p1-tea-of-discourtesy-seed",
        ["TEA_OF_DISCOURTESY"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)],
    )


if __name__ == "__main__":
    main()
