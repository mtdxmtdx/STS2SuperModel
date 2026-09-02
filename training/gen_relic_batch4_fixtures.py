#!/usr/bin/env python3
"""Generate batch-4 relic CLI fixtures (v0.111.0)."""
from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_relic_batch1_fixtures import DEFEND, STRIKE, build_fixture

attack_deck = [STRIKE] * 9 + [DEFEND]

def main() -> None:
    build_fixture("p1-relic-delicate-frond", "p1-delicate-frond-seed",
        ["DELICATE_FROND"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-belt-buckle", "p1-belt-buckle-seed",
        ["BELT_BUCKLE"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)])
    build_fixture("p1-relic-fake-snecko-eye", "p1-fake-snecko-eye-seed",
        ["FAKE_SNECKO_EYE"], attack_deck, "SEAPUNK_WEAK",
        plan=[("end_turn",), ("end_turn",)])

if __name__ == "__main__":
    main()
