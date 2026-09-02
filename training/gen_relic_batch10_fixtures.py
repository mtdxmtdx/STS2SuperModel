#!/usr/bin/env python3
"""Generate batch-10 relic CLI fixtures (v0.111.0)."""
from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_relic_batch1_fixtures import DEFEND, STRIKE, build_fixture

OUTRAGE = "OUTRAGE"
# Mixed decks sized so hand composition covers the trigger families.
mixed = [STRIKE]*4 + [DEFEND]*3 + [OUTRAGE]*3

def main() -> None:
    # Daughter of the Wind: +1 block per attack (relic-only fixture).
    build_fixture("p1-relic-wind-block", "p1-wind-block-seed",
        ["DAUGHTER_OF_THE_WIND"], mixed, "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"), ("end_turn",)])
    # Game Piece / Lost Wisp / Permafrost: power-play triggers (one fixture).
    build_fixture("p1-relic-power-triggers", "p1-power-triggers-seed",
        ["GAME_PIECE", "LOST_WISP", "PERMAFROST"],
        ["INFLAME", "INFLAME", STRIKE, STRIKE, DEFEND, STRIKE, DEFEND, STRIKE, DEFEND, STRIKE],
        "SEAPUNK_WEAK",
        plan=[("play", "Power", 0), ("play", "Power", 0), ("end_turn",)])
    # Intimidating Helmet / Ivory Tile / Iron Club: cost-gated + count relics.
    build_fixture("p1-relic-cost-gated", "p1-cost-gated-seed",
        ["INTIMIDATING_HELMET", "IVORY_TILE", "IRON_CLUB"],
        [STRIKE, STRIKE, STRIKE, DEFEND, DEFEND, DEFEND, DEFEND, STRIKE, STRIKE, STRIKE],
        "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"),
              ("play", "Attack", "any_enemy"), ("end_turn",)])
    # Charon's Ashes: exhaust -> damage ALL (Defend self-exhausts? no; use a
    # cheap attack-heavy sequence; Charon triggers only via exhaust cards —
    # replay BURNING_STICKS + CHARONS_ASHES with TREMBLE exhausts).
    build_fixture("p1-relic-exhaust-triggers", "p1-exhaust-triggers-seed",
        ["CHARONS_ASHES", "BURNING_STICKS"], ["TREMBLE"]*6 + [DEFEND]*4,
        "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("play", "Skill", 0), ("end_turn",)])

if __name__ == "__main__":
    main()
