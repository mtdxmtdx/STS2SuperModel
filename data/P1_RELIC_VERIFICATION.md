# P1 Relic Semantic Closeout (Batches 1-16)

Updated: 2026-08-30 (+08:00) — classifier/evidence audit after batches 1-16

## Version lock

- Game: `v0.111.0`, commit `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`
- Trace schema: `1`

## Verified matrix

The P1 relic runner registers 65 fixtures and 132 diff actions
(`training/run_p1_relic_probes.py`):

| Fixture | Reports | Relic semantics |
| --- | ---: | --- |
| TOUGH_BANDAGES | 1 | (batch 2) |
| TUNGSTEN_ROD | 2 | (batch 2) |
| UNCEASING_TOP | 3 + empty-hand-4 | whenever hand is empty during player turn, draw 1; empty-hand trace with a draw-pile card verifies the trigger |
| BRONZE_SCALES | 2 | (batch 2) |
| BLOOD_VIAL | 1 | (batch 2) |
| BRIMSTONE | 2 | (batch 2) |
| NUNCHAKU | 2 | (batch 2) |
| ANCHOR | 1 | (batch 2) |
| SHURIKEN / KUNAI / ORNAMENTAL_FAN | 4 | every 3rd Attack per turn: +1 STR / +1 DEX / +4 Block; counters reset per turn and export `count % 3` |
| LETTER_OPENER | 2 | every 3rd Skill per turn: 5 damage to ALL enemies |
| RAINBOW_RING | 2 | first Attack+Skill+Power each turn: +1 STR, +1 DEX (progress tracked as internal state; relic counter stays null as exported) |
| MERCURY_HOURGLASS | 2 | turn start: 3 damage to ALL enemies (STR/Weak-adjusted, non-powered) |
| MR_STRUGGLES | 2 | turn start: damage equal to the turn number to ALL enemies |
| SAI | 1 | turn start: +7 Block |
| CANDELABRA | 2 | start of turn 2 only: +2 Energy |
| CHANDELIER | 2 | start of turn 3 only: +3 Energy |
| FAKE_HAPPY_FLOWER | 2 | every 5th side turn start: +1 Energy (counter cycle exported) |
| FAKE_ORICHALCUM | 1 | end turn with 0 Block: +3 Block (absorbs the enemy turn) |
| CLOAK_CLASP / RIPPLE_BASIN | 2 | turn end: +1 Block per hand card / +4 Block when no attacks played |
| PARRYING_SHIELD | 1 + multi-2 | end turn with >=10 Block: 6 damage to one random alive/hittable enemy (1 CombatTargets RNG consumed); multi-enemy trace confirms one-target damage with masked target identity |
| SCREAMING_FLAGON | 1 | end turn with empty hand: 20 damage to ALL |
| KUSARIGAMA | 4 | every 3rd Attack per turn: 6 damage to ALL (1 CombatTargets RNG consumed; counter = attacks % 3) |
| PAELS_TEARS | 2 | unspent energy at end of turn: +2 Energy next turn |
| STRIKE_DUMMY / FAKE_STRIKE_DUMMY | 4 | Strike-tag additive damage — already baked into the CLI stats.damage preview; the shadow replays the snapshot value |
| SNECKO_EYE | 2 | +2 cards drawn per turn start (1 CombatEnergyCosts RNG per drawn card) |
| WHISPERING_EARRING | 2 | max-energy raise carried by the snapshot (Vakuu auto-play is turn-1 only) |
| FAKE_BLOOD_VIAL / FAKE_ANCHOR / VERY_HOT_COCOA / PHILOSOPHERS_STONE | 2 | combat-start heal / block / max-energy / enemy Strength carried by the snapshot (ANCHOR methodology) |
| SELF_FORMING_CLAY | 2 | unblocked damage schedules 3 Block for the next turn |
| POCKETWATCH | 2 | ending the turn with <=3 cards played draws 3 extra next turn |
| STONE_CALENDAR | 1 | end of turn 7: 52 damage to ALL (no RNG consumed) |
| JOSS_PAPER | 2 | every 5th exhaust draws 1 (counter = exhausts % 5) |
| ICE_CREAM | 2 | unspent energy carries over (new energy = leftover + max) |
| NINJA_SCROLL | 1 | combat-start Shivs carried by the snapshot |
| DELICATE_FROND | 2 | combat-start potion fill carried by the snapshot |
| BELT_BUCKLE | 2 | conditional Dexterity carried by the snapshot (no potions) |
| FAKE_SNECKO_EYE | 2 | Confused carried by the snapshot; per-draw CombatEnergyCosts mirrored |
| THE_BOOT | 3 | unblocked powered-attack HP loss below 5 is raised to 5 (verified with a 4-damage attack) |
| VAMBRACE | 2 | first card-block doubling already baked into the CLI stats.block preview — replay-only |
| BRILLIANT_SCARF | 2 | 5th card played from hand each turn is free; counter = plays this turn, null on the free 5th play |
| PANTOGRAPH | 2 | boss-combat-start 25 HP heal carried by the snapshot (VANTOM_BOSS encounter) |
| FESTIVE_POPPER | 2 | combat-start 9 damage to ALL carried by the snapshot (no re-application later) |
| ROYAL_POISON | 2 | combat-start 4 HP loss carried by the snapshot |
| RED_MASK | 2 | combat-start 1 Weak to ALL carried by the snapshot |
| TWISTED_FUNNEL | 2 | combat-start 4 Poison to ALL carried by the snapshot (poison tick handled by the base sim) |
| BREAD | 2 | turn-1 -2 energy carried by the snapshot; max energy base+1 from turn 2 (3 -> 4); counter stays null |
| PAELS_FLESH | 2 | max energy base+1 from turn 3 onward (one bump); counter = turn number below 3, null after |
| BLACK_BLOOD / MEAT_ON_THE_BONE | 1 | combat-end heals (+12 / +12 at <=50% HP) applied when a play kills the last enemy, verified on the CLI terminal trace row |
| PENDULUM | 2 | draw +1 at every 3rd turn start (counter = turn % 3); round 3->4 transition avoids the SEAPUNK Buff intent gap |
| SWORD_OF_STONE / FISHING_ROD / TOY_BOX / WONGOS_MYSTERY_TICKET | 2 | cross-combat counters (AfterCombatEnd / AfterCombatVictory hooks only) — no in-combat effect, carried and replay-verified |
| DAUGHTER_OF_THE_WIND | 3 | +1 Block per Attack played |
| INTIMIDATING_HELMET / IVORY_TILE / IRON_CLUB | 3 | cost>=2 -> +4 Block; cost>=3 -> +1 Energy; every 4th card draws 1 (counter modulo) |
| BEATING_REMNANT | 2 | HP loss per turn capped at 20 (per-turn loss tracking added to the shadow) |
| SEAL_OF_GOLD | 2 | spends 3 Gold each turn start for +1 Energy (gold tracking wired from the player summary) |
| VELVET_CHOKER | 2 | max energy +1 carried by the snapshot; 6-card play cap enforced in IsCardPlayableNow (search-side) |
| GAME_PIECE / PERMAFROST / LOST_WISP | 2 | unlocked by the teacher-side use-state export: the CLI teacher snapshot now carries each relic's *ThisCombat/*ThisTurn state and the ShadowDiff rebuilds the draw pile from the teacher pile for play ordinals; Lost Wisp verified FLAT (not Strength-scaled) |
| TUNING_FORK | 4 | every 10th Skill played grants 7 Block (counter = skills % 10), verified across a 4-turn 10-skill sequence |
| BOOMING_CONCH | 1 | elite-combat-start draw+energy carried by the snapshot (TERROR_EEL_ELITE) |
| HORN_CLEAT / CAPTAINS_WHEEL / SPARKLING_ROUGE | 2 | one-time block 14 / block 18 / STR+DEX at the 2nd / 3rd turn start |
| RING_OF_THE_DRAKE / PAELS_BLOOD | 2 | +2 cards during the first 3 turns / +1 card every turn start |
| ECTOPLASM / SOZU / PRISMATIC_GEM | 2 | max-energy raise carried by the snapshot (their out-of-combat rules are outside combat-diff scope) |
| HAND_DRILL | 1 | breaking an enemy's Block applies 2 Vulnerable (verified at ordinal 1) |
| FIDDLE | 2 | +2 cards at every turn start (verified: turn-1 hand is 7) |
| VENERABLE_TEA_SET / FAKE_VENERABLE_TEA_SET / SPIKED_GAUNTLETS / PUMPKIN_CANDLE / BIG_MUSHROOM / BLOOD_SOAKED_ROSE | 2 | max-energy / combat-start draw effects carried by the snapshot |
| **Total** | **132** | |

All 132 reports are generated by the current CLI and ShadowDiff binaries. Their
structural fields (version, schema, trace, action ordinal and relic identity)
are valid. The relic-only quality split is **47 Reliable, 48 Estimated and
37 Uncalculable**; all rows have `match=true` and `mismatch_count=0`, but a
mechanically matching Estimated/Uncalculable row is not Reliable evidence.
`UNCEASING_TOP` now has an empty-hand CLI trace and is eligible for the
Reliable whitelist. `PARRYING_SHIELD` has a multi-enemy trace; its target
identity is intentionally masked by NOSL CombatTargets RNG, so the report is
kept Uncalculable/evidence-ineligible while aggregate damage matching is
recorded. No hidden RNG outcome is promoted to a Reliable NOSL label. The
strict eligibility rule is: every
referenced report must be version/schema matched, mention the relic, have
`confidence=Reliable`, `match=true`, and `mismatch_count=0`; semantic holds
and reports with Estimated/Uncalculable confidence are excluded. A Reliable
row must also carry `outcome_quality=Exact`, `probability_known=true`, and a
`strict_public_state`/`terminal_summary` comparison scope; aggregate and
teacher-conditioned rows remain diagnostic only.
The combined P0 + P1 manifest must be regenerated after adding these two
fixtures; this document records the relic matrix independently (132 reports).

### Supporting simulator fixes required by batch 1 (verified by the same reports)

- `SkillsPlayedSinceSnapshot` now resets at the turn boundary with a matching
  `SkillsPlayedBeforeTurn` snapshot counter (was never reset — latent bug).
- ShadowDiff rebuilds per-turn attack/skill/power counters from the realized
  trace for play_card actions too (previously end_turn only).
- `ShuffleDiscardIntoDraw` mirrors the engine's Shuffle-stream counter
  consumption (`mergedCount - 1` Fisher-Yates calls) when the live RNG state is
  unavailable.
- `MutableCombatState` now carries `Round` (engine TurnNumber), needed by
  Candelabra / Chandelier / Mr. Struggles.

## Catalog status

`data/relics/v0.111/relic-coverage.json` (regenerated by
`RelicCatalogTests.GenerateAndExportFullRelicCatalog`) records 299 structured
relics with the following current status counts:

| Status/metric | Count |
| --- | ---: |
| runtime-probed (structurally valid references) | 100 |
| strict evidence-eligible probes | 24 |
| handler-supported (catalog status) | 99 |
| partially-supported semantic holds | 1 (`PARRYING_SHIELD`) |
| handler-supported but evidence pending | 75 |
| unsupported known combat hooks | 20 (including `GIRYA`) |
| UnverifiableByCli | 25 |
| Uncalculable | 56 |
| OutOfScope (verified non-combat) | 97 |
| NoCombatEffect | 1 |
| unknown | 0 |
| Reliable-eligible relics (strict probes + NoCombatEffect) | 25 |

All 200 combat-relevant relics (299 - 97 OutOfScope - 1 NoCombatEffect)
now have an explicit status. Ninety-nine have implemented handlers, and 24
relics currently satisfy the strict report-level evidence gate; 75 handler
entries remain evidence-pending, one implemented handler remains a semantic
hold, 20 known combat hooks (including GIRYA's combat-start Strength) still
require handlers or probes, and 81 engine-blocked relics remain terminal
(`UnverifiableByCli` + `Uncalculable`).

Batch 16 corrections: the batch-15 supported-set edits had silently failed
(missing asserts), leaving 8 verified relics (HAND_DRILL, FIDDLE,
VENERABLE_TEA_SET, FAKE_VENERABLE_TEA_SET, SPIKED_GAUNTLETS, PUMPKIN_CANDLE,
BIG_MUSHROOM, BLOOD_SOAKED_ROSE) unpromoted; they are now promoted with their
evidence references restored (the evidenceReferences dictionary had also lost
all batch 5-15 entries — rebuilt in full). RUINED_HELMET, TEA_OF_DISCOURTESY
(Uncalculable) and RADIANT_PEARL (UnverifiableByCli) received terminal
classifications with cited evidence.

Batch 14 also introduced RelicEffectSupportStatus.UnverifiableByCli and
.Uncalculable, each backed by a concrete observed limitation (teacher-snapshot
disambiguation evidence in P1_RELIC_VERIFICATION.md).

The two semantic holds, 20 unsupported combat hooks, 25
`UnverifiableByCli`, and 56 `Uncalculable` entries remain explicitly excluded
from Reliable labels. The 97 OutOfScope entries are classified by explicit
scope evidence rather than a name-only method scan. Multiplayer-only card
effects remain outside the single-player combat scope. Baseline/inventory artifacts:
`data/relic-card-gap-baseline.json`, `data/relic-card-gap-inventory.json`.

## Known simulator gaps (blocking long multi-turn fixtures)

- Enemy non-attack intents (`Buff`/`Defend`) carry no effect amounts in the
  public observation, so the shadow cannot replay enemy turns that use them
  (observed on SEAPUNK_WEAK round 3→4). Registered ordinals avoid it.
- Card order after a draw-pile reshuffle is not reproducible without live RNG
  state; the counter is mirrored (Fisher-Yates consumes discardCount - 1) and
  hand comparison falls back to count-only.
- Discard-pile contents are absent from the public observation, so draws that
  come from a freshly shuffled discard cannot resolve card identity (Joss
  Paper draw registered on counter ordinals only).
- Orb-passive relic interplay (RUNIC_CAPACITOR / INFUSED_CORE /
  SYMBIOTIC_VIRUS) is carried by the snapshot but their turn-boundary orb
  passives do not match the engine yet; those three stay unsupported.
- TOASTY_MITTENS: the engine's turn-start draw-pile exhaust makes the CLI's
  end-turn reply lose its public observation (trace rows without post-state);
  blocked on the CLI snapshot builder.
- FENCING_MANUAL's Forge applies hand-affecting card semantics at combat
  start that the shadow does not model; stays StateCapturedOnly.
- POLLINOUS_CORE's 4-turn draw bonus fires on the round 3->4 transition,
  which is blocked by the enemy Buff-intent gap; stays StateCapturedOnly.
- RESOLVED via the teacher-side export: the CLI teacher snapshot now includes
  each relic's use-state (ActivatedThisCombat etc. via reflection) and the
  ShadowDiff consumes it plus the teacher draw pile for play ordinals.
  GAME_PIECE, PERMAFROST and LOST_WISP are verified through this path.
- CHARONS_ASHES / BURNING_STICKS: the teacher snapshot disambiguated the
  engine — a self-exhausting card (TREMBLE) resolves its exhaust ASYNC (the
  teacher exhaust pile reads 0 right after the play) and the Burning Sticks
  hand copy uses an engine-generated instance id (card:TREMBLE:012), neither
  of which is reproducible from public observations. Both stay
  StateCapturedOnly (handlers implemented, verification blocked).
- HELICAL_DART: the Shiv-play trigger needs generated-card identity; Shivs
  are engine-generated (not present in any observable pile), so the hand-id
  comparison cannot match. Stays StateCapturedOnly (handler implemented).
- Status cards with "vanish" semantics (v0.111 Dazed leaves play without
  exhausting or discarding) are unmodeled; TEA_OF_DISCOURTESY stays
  StateCapturedOnly.
- Card self-HP-loss effects (e.g. HEMOKINESIS, INFERNO) are not exported in
  the public card stats, so DEMON_TONGUE's first-loss heal has no observable
  shadow-side loss to key off; DEMON_TONGUE stays StateCapturedOnly.
- RESOLVED: the CLI now emits a terminal public observation for actions that
  end the combat (RunSimulator.TryBuildTerminalCombatObservation + terminal
  attach in Program.cs), and the ShadowDiff runs a reduced terminal comparison
  (player HP, relics, terminal flag only — the engine tears down piles/hand/
  enemies/powers at combat end). BLACK_BLOOD and MEAT_ON_THE_BONE are verified
  through this path; remaining combat_end relics are blocked on their own
  semantics (cross-combat counters, upgrades, orb counts), not on the CLI.

## Repeatability

`training/verify_repeat_runs.py` derives an allow-listed report manifest from
both probe registries, rejects unexpected report files, records the version
lock and group counts, and stores each report's raw repeat SHA-256 in
`repeat_sha256`. The latest run is:

```text
verdict=pass, report_count=212, different=0, missing=0, added=0, unexpected=0; quality_counts={Reliable:96, Estimated:67, Uncalculable:49}
```

Historical non-matrix reports are retained under `data/legacy-shadow-diff/` and
are not part of the closeout manifest.
