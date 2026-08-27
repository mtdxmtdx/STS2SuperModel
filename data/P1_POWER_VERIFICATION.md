# P1 Power Verification Status (Batch 1)

Updated: 2026-08-27 +08:00
Lane: P1-A "Power 真实行为差分"

Version lock (unchanged):

- Game: `v0.111.0`, commit `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`, trace schema: `1`

## Verdict

**First P1 batch complete: THORNS, ACCURACY, PLATING, POISON, PANACHE are promoted from
`simulator_declared` to `simulator_supported` / `LiveObserved`.** Every Power has a fixed-seed
CLI fixture whose diffed actions produce ShadowDiff reports with `confidence=Reliable` and
`mismatch_count=0`, covering the pre-trigger state, the power-granting play, the trigger play,
and (where turn boundaries are reconstructible) an end-turn projection. All fixtures were run
twice; both runs produced byte-identical reports.

Driver: `training/run_p1_power_probes.py` (`--include-p0` reruns the 21 P0 fixtures as well).

## Catalog statistics change

`data/powers/v0.111/power-coverage.json` summary, before → after promotion:

| Metric | Before | After |
| :--- | :--- | :--- |
| total_powers | 283 | 283 |
| runtime_probed_count | 9 | **14** |
| simulator_supported_count | 9 | **14** |
| simulator_declared_count | 64 | **59** |
| state_captured_only_count | 210 | 210 |
| unknown_count | 30 | 30 |

Promoted entries in `power-catalog.json` flipped exactly:
`simulator_support: simulator_declared → simulator_supported`,
`evidence: HeuristicInferred → LiveObserved`.

## Per-Power results

Report files live under `data/`; fixture commands under `training/fixtures/`.

### THORNS（荆棘）

- Fixture: `training/fixtures/p1-power-thorns-commands.jsonl` (Silent, seed `p1-thorns-fixed`,
  deck ABRASIVE×3 + DEFEND_SILENT×10, SEAPUNK_WEAK).
- Entry path: play **ABRASIVE** ("Gain Dexterity. Gain Thorns.") → end_turn (enemy attacks).
- Observed engine behavior: playing ABRASIVE grants `DEXTERITY_POWER`(1) + `THORNS_POWER`(4);
  when Seapunk's attack lands for 11 (through no block), Thorns deals exactly 4 damage back to
  the attacker (46→42 HP). Structured rows carry owner/player, applier/player.
- Reports: `p1-csharp-thorns-diff-report-0.json` (play_card), `-1.json` (end_turn).
  Both `Reliable`, `mismatch_count=0`.
- RNG streams: zero random consumption on this path (no shuffle: 13-card deck leaves 6 draw
  cards before the round-2 draw of 5); all seven counters matched.

### ACCURACY（精准）

- Fixture: `p1-power-accuracy-commands.jsonl` (Silent, seed `p1-accuracy-fixed`,
  deck ACCURACY×4 + SHIV×6, SEAPUNK_WEAK).
- Entry path: play ACCURACY → play SHIV at the enemy.
- Observed engine behavior: `ACCURACY_POWER`(4) appears with empty dynamic vars; the next Shiv
  preview updates its `stats.damage` to a live 8 and actually deals 8 (44→36). Engine keeps the
  power card transient after play; Shiv exhausts instead of discarding.
- Reports: `-0.json` (Accuracy), `-1.json` (buffed Shiv). Both `Reliable`, `mismatch_count=0`.
- Simulator fixes required: map status `SHIV_DAMAGE_BONUS` ↔ live `ACCURACY_POWER`;
  strip Accuracy from the CLI damage *preview* inside BuildCard so the bonus is not applied twice.

### PLATING（覆甲）

- Fixture: `p1-power-plating-commands.jsonl` (Ironclad, seed `p1-plating-fixed`,
  deck ETERNAL_ARMOR×10, SEAPUNK_WEAK).
- Entry path: play ETERNAL_ARMOR → end_turn → round 2 snapshot.
- Observed engine behavior: grants `PLATING_POWER`(9, dynamic_vars `{Decrement: 1}`);
  end of player turn gains amount-as-Block (absorbs part of the enemy attack: 80→78 HP against
  an 11-damage intent); start of the player's turn decrements to 8 with the structured row kept
  in sync.
- Reports: `-0.json` (grant), `-1.json` (turn boundary incl. block gain, absorb, decrement).
  Both `Reliable`, `mismatch_count=0`.
- Simulator fixes required: PLATING was previously unimplemented — added generic turn-end
  Block gain, turn-start Decrement tick, PowerState sync, and the static `Decrement=1`
  dynamic var.

### POISON（中毒）

- Fixture: `p1-power-poison-commands.jsonl` (Silent, seed `p1-poison-b`,
  deck POISONED_STAB×3 + DEFEND_SILENT×10, SEAPUNK_WEAK).
- Entry path: play POISONED_STAB (6 dmg + 3 Poison) → enemy turn (Poison ticks).
- Observed engine behavior: `POISON_POWER` lands on the enemy with
  owner=`enemy:SEAPUNK:1`, applier=`player`; at the enemy turn start it deals damage equal to
  its amount (40→37) and then decrements to 2 in the same observed state. Poison does not tick
  during the application turn.
- Reports: `-0.json` (apply), `-1.json` (tick at turn boundary). Both `Reliable`,
  `mismatch_count=0`.
- Simulator fixes required: the poison tick now syncs the structured `POISON_POWER` row
  (amount decrease/removal) — previously only the raw status changed while the power list went
  stale.

### PANACHE（神气制胜）

- Fixture: `p1-power-panache-commands.jsonl` (Silent, seed `panache-c`, deck
  PANACHE + SHIV×9, SLIMES_WEAK = three enemies).
- Entry path: play PANACHE → five Shiv plays across the turn.
- Observed engine behavior: `PANACHE_POWER`(10) exposes a live dynamic var `CardsLeft`
  (displayed as 5 right after playing Panache itself; playing Panache does not consume it).
  Each subsequent card decrements the displayed counter (5→4→3→2→1); when the fifth
  post-Panache card resolves the counter hits zero, all enemies take 10, and the counter resets
  to 5 (visible again in the round-2 snapshot). Exhaust pile accumulates each Shiv; the public
  observation exports no exhaust count but teacher snapshots do.
- Reports: `-0.json` (grant), `-1.json` (first Shiv, CardsLeft 5→4), `-4.json` (fourth Shiv,
  CardsLeft 2→1 + cumulative exhaust baseline), `-5.json` (fifth Shiv triggers the AoE and
  resets to 5). All four `Reliable`, `mismatch_count=0`.
- Simulator fixes required: mirror `CardsLeft` into the structured power row via the internal
  `PANACHE_CARDS_LEFT` companion counter; ShadowDiff restores that companion status from the
  pre-state dynamic var and seeds the exhaust pile from the nearest earlier teacher snapshot.

## Repeat-run consistency

Full matrix executed twice back-to-back: all 12 report files were compared by SHA-256 across
runs — identical (`run B == run A`). The `--include-p0` regression run produced 43 reports
(31 P0 ordinals + 12 P1), all `mismatch_count=0`; the full 41 legacy `p0-csharp-*` report set
in `data/` was re-checked and remains all-zero-mismatch.

## Simulation changes made (generic, minimal-root)

Core copy branch `codex/p1-power`:

1. `DeterministicSimulator.LivePowerIdFor`: declared statuses mapped to their live power ids
   (`SHIV_DAMAGE_BONUS → ACCURACY_POWER`, `TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE → PANACHE_POWER`),
   following the existing DEMON_FORM/RUPTURE/AFTERIMAGE precedent.
2. `KnownPowerDynamicVars`: `PLATING → {Decrement: 1}`; new `LivePowerDynamicVars(state, id)`
   mirrors Panache's live `CardsLeft` from `PANACHE_CARDS_LEFT` into the structured power row;
   `SyncPowerState` refreshes dynamic vars on every update, and the counter tick emits a sync.
3. PLATING implemented generically: turn-end Block gain equal to amount; turn-start
   Decrement tick removing the status at zero; structured row kept in sync.
4. `TriggerEnemyPoison` (existing enemy-side poison tick used by the turn projection) now also
   syncs the structured `POISON_POWER` row after each tick.

ShadowDiff tooling (same tree):

1. `BuildCard` translates the new card effect stats (`accuracypower`, `thornspower`,
   `dexteritypower`, `platingpower`, `poisonpower`, `panachedamage`) into EffectSpecs; cards
   with the Exhaust keyword now get `CardDestination.Exhaust`; Accuracy is stripped from the
   Shiv live-preview damage to avoid double counting.
2. `StatusIdForPower` maps `ACCURACY/PANACHE` powers back to their simulator-internal status ids.
3. Differential grounding: exhaust counts compare against the nearest post-action teacher
   snapshot (the public observation has no exhaust counter), and the snapshot baseline seeds
   already-exhausted cards plus Panache's companion counter from real teacher data.

No fixture-specific special cases were added anywhere; confidence was never lowered and no
comparison fields were skipped or removed.

## Evidence trail

- Fixtures: `training/fixtures/p1-power-{thorns,accuracy,plating,poison,panache}-commands.jsonl`
- Traces (generated per run, not committed): `data/p1-power-*-trace.jsonl`
- Reports: `data/p1-csharp-*-diff-report*.json` (12 files, all Reliable / 0 mismatches)
- Driver: `training/run_p1_power_probes.py`
- Catalog artifacts regenerated via `tests/PowerCatalogTests.GenerateAndExportFullPowerCatalog`

## Known boundaries / unresolved items

1. **Turn-boundary hand identity needs a shuffle-free deck**: the public/RNG-counter data cannot
   replay an engine reshuffle (documented P0 limitation). The P1 fixtures therefore size decks so
   the diffed turn boundary draws without shuffling. PANACHE keeps its end_turn snapshot as
   observation only — multi-enemy non-attack intents are not reconstructible either.
2. **Panache reset semantics across turns**: the round-2 display shows CardsLeft reset to 5,
   which matches the engine; long-horizon (>1 turn) counter interactions remain unprobed.
3. Enemy-owned THORNS-style reflect powers were out of scope (player-side verified only);
   enemy thorns remains part of the unsupported combat-hook backlog.
4. `FIRST_SHIV_DAMAGE_BONUS` interactions and Accelerant multi-tick poison are still
   `simulator_declared` (not probed here).
