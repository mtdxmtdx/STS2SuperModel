#!/usr/bin/env python3
"""Generate batch-5 relic CLI fixtures (v0.111.0)."""
from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_relic_batch1_fixtures import DEFEND, STRIKE, build_fixture

def main() -> None:
    build_fixture("p1-relic-the-boot", "p1-the-boot-seed",
        ["THE_BOOT"], ["BULLY", "BULLY", "BULLY", "BULLY", "STRIKE_IRONCLAD",
                        "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD",
                        "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
        "SEAPUNK_WEAK",
        plan=[("play", "Attack", "any_enemy"), ("play", "Attack", "any_enemy"), ("end_turn",)])
    build_fixture("p1-relic-demon-tongue", "p1-demon-tongue-seed",
        ["DEMON_TONGUE"], ["INFERNO", "INFERNO", "INFERNO", "INFERNO", "INFERNO",
                            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD",
                            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"],
        "SEAPUNK_WEAK",
        plan=[("play", "Power", 0), ("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-vambrace", "p1-vambrace-seed",
        ["VAMBRACE"], [DEFEND_IRONCLAD := "DEFEND_IRONCLAD"] * 5 + [STRIKE] * 5,
        "SEAPUNK_WEAK",
        plan=[("play", "Skill", 0), ("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-brilliant-scarf", "p1-brilliant-scarf-seed",
        ["BRILLIANT_SCARF"], ["OUTRAGE"] * 5 + [DEFEND_IRONCLAD] * 5,
        "SEAPUNK_WEAK",
        plan=[("play", "Any", "any_enemy"), ("play", "Any", 0), ("play", "Any", "any_enemy"),
              ("play", "Any", 0), ("play", "Any", "any_enemy"), ("end_turn",)])

if __name__ == "__main__":
    main()
