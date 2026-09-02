#!/usr/bin/env python3
"""Generate batch-3 relic CLI fixtures (v0.111.0)."""
from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_relic_batch1_fixtures import DEFEND, STRIKE, build_fixture

TREMBLE = "TREMBLE"
attack_deck = [STRIKE] * 9 + [DEFEND]

def main() -> None:
    build_fixture("p1-relic-self-forming-clay", "p1-self-forming-clay-seed",
        ["SELF_FORMING_CLAY"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-pocketwatch", "p1-pocketwatch-seed",
        ["POCKETWATCH"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-stone-calendar", "p1-stone-calendar-seed",
        ["STONE_CALENDAR"], attack_deck, "SEAPUNK_WEAK", hp=99,
        plan=[("end_turn",)] * 7)
    build_fixture("p1-relic-joss-paper", "p1-joss-paper-seed",
        ["JOSS_PAPER"], [TREMBLE] * 10, "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("play", "Skill", 0), ("play", "Skill", 0),
              ("end_turn",), ("play", "Skill", 0), ("play", "Skill", 0)])
    build_fixture("p1-relic-ice-cream", "p1-ice-cream-seed",
        ["ICE_CREAM"], attack_deck, "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-ninja-scroll", "p1-ninja-scroll-seed",
        ["NINJA_SCROLL"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",)])

if __name__ == "__main__":
    main()
