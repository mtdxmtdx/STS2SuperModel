# P0 Verification Status

Updated: 2026-08-27 +08:00

Version lock:

- Game: `v0.111.0`
- Commit: `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`
- Trace schema: `1`
- Training schema: `1`

## Current Verdict

**P0 contract is complete and verified.** The fixed-seed differential matrix covers every
currently promoted simulator mapping plus representative Power transitions, all with real
CLI/Core zero-mismatch evidence. Unprobed semantics remain explicitly classified and are
not promoted to `Reliable` teacher labels.

The remaining 59 declared Powers, 163 known-unsupported combat-hook relics, and 118
unknown relics are the post-P0 semantic expansion backlog. The system can already emit
versioned smoke data and run focused differentials while that backlog is implemented in
batches.

## Requirement Status

### 1. Structured Power State: Infrastructure Complete, Evidence Coverage Expanded

Implemented:

- `PowerState` preserves raw Power ID, owner, applier when available, amount, DynamicVars, internal counters, trigger phases, capture source, source version, support status, and evidence level.
- `LiveCombatSnapshotAdapter` captures every player/enemy Power into the unified Power list; unsupported powers remain present instead of disappearing behind risk-only handling.
- CLI public and teacher observations expose structured Power fields while retaining localized display fields.
- Applying a modeled status in `DeterministicSimulator` synchronizes the corresponding structured Power state.
- **New:** consuming VIGOR (first-attack consumption, e.g. Akabeko) now removes the structured `VIGOR_POWER` entry in sync with the removed status.

Current catalog evidence (`data/powers/v0.111/power-coverage.json`):

- 283 cataloged and structurally capturable.
- 14 behaviorally validated before P1 plus 5 P1 mappings (`THORNS`, `ACCURACY`, `PLATING`, `POISON`, `PANACHE`) with real CLI/shadow zero-mismatch differentials.
- 59 simulator mappings remain declared but not behaviorally probed.
- 30 retain `Unknown` evidence.
- Reflection-derived hook phases are `HeuristicInferred`, not `ILConfirmed`.

### 2. CombatSnapshot Training Contract: Complete

Implemented and covered by Core tests:

- `RelicState` and `PowerState` in `CombatSnapshot`.
- Public/teacher observation view.
- Snapshot provenance and schema/version metadata.
- Relic and Power state copied by `MutableCombatState.Clone()`.
- Effect-relevant relic/Power fields included in `ExactKey()` and `CycleKeyWithoutProgress()`.
- Structured Power transition is maintained when a simulated action applies a status.

### 3. CLI Training Trace Protocol: Complete for Realized Engine Traces

Trace records include:

- trace ID, step, pre/post hash, normalized action ID;
- public observation and teacher snapshot;
- all seven RNG counters before/after an action;
- `chance_branch` with changed streams, counter deltas, realized outcome hash, probability availability, and enumeration status;
- failure message, failed step, recovery-required flag and recovery status;
- per-row version metadata;
- JSONL auto-flush and explicit flush on quit.

Real random-action evidence:

- `p0-chance-entropic-trace.jsonl` records `CombatPotionGeneration +6` for Entropic Brew.
- The trace records `produced=true`, `probability_known=false`, and `branch_enumerated=false`; it does not invent unavailable probabilities.

### 4. Stable ActionCandidate Contract: Complete for Current P0 Interfaces

Implemented:

- Stable card, potion, enemy target and card-choice identities.
- `PlayCard`, `UsePotion`, `Choice`, and `EndTurn` candidates.
- `Choice` includes stable `choice_id` and `selected_card_instance_ids`.
- Stable IDs map to current CLI 0.2.0 indices immediately before execution.
- Card/potion index movement is handled without changing the public CLI action protocol.
- Missing or ambiguous stable IDs fail before an execution command is sent.
- Training conversion rejects missing stable IDs instead of falling back to hand/slot indices.
- Large choice spaces are marked incomplete after the explicit 10,000-candidate export limit.

Evidence:

- Stable card index shift and potion slot shift are exercised against the real CLI.
- Real `card_select` output exposes stable choice/card IDs and candidates.
- **New:** live multi-enemy stable target replay is no longer unit-fixture only. `p0-multi-enemy-targets-trace.jsonl` targets three distinct enemies (`SLIMES_WEAK`) with three consecutive stable-ID differentials, all `mismatch_count=0` (`p0-csharp-multi-enemy-targets-diff-report-0/1/2.json`).

### 5. Training Export and DatasetManifest: Complete as a Smoke Pipeline

Implemented:

- Raw JSONL trace capture.
- Normalized `TrainingDecisionRecord` JSONL.
- Draft 2020-12 schemas and streaming validation.
- Deterministic train/validation/test/challenge split by episode group.
- Source, output and shard SHA-256 values.
- Concrete generator configuration hash.
- Deterministic quality/coverage report.
- PyArrow Parquet conversion with bounded 10,000-50,000 row shards, Zstandard compression, atomic rename, and read-back comparison.

Current smoke artifact:

- 3 decision rows, 17 action candidates.
- 1 Parquet shard.
- 0 Reliable, 0 Estimated, 1 LowConfidence, 2 Uncalculable.
- This validates tooling only; it is not the planned 1,000-state or 100,000-state dataset.

### 6. Real Engine / Shadow Differential: Mechanism Complete, Turn-Boundary Coverage Expanded

The C# differential (`training/ShadowDiff`) uses the real `STS2BestChoice.Core.DeterministicSimulator` and compares:

- player/enemy HP and block;
- energy, hand, draw/discard/exhaust counts (hand card identities when the teacher draw pile is available);
- round transitions;
- structured Power ID, owner/applier, amount, DynamicVars and counters;
- relic IDs, counters and DynamicVars;
- potion identities;
- all seven RNG counters;
- terminal state.

**ShadowDiff extensions added in this round:**

- `end_turn` differentials via `ProjectToNextPlayerTurn`, rebuilding the real draw-pile order from the teacher snapshot so the next-turn hand compares by stable instance IDs.
- Turn-history reconstruction: the public observation does not export turn counters, so attack counts for turn-start effects (Art of War) are rebuilt from the realized trace.
- Live-preview stripping in `BuildCard`: CLI `stats.damage` already includes additive STRENGTH/VIGOR and a charged Pen Nib doubling, and `stats.block` already includes DEXTERITY; the simulator re-applies these modifiers, so the preview portion is stripped to recover base values. Multiplicative effects (Vulnerable/Weak) are not part of `stats` and remain in `damage_by_target`.

**Simulator fixes verified by these probes (all previously divergent, now zero-mismatch):**

- VIGOR consumption now syncs the structured PowerState list (Akabeko probe).
- `ProjectToNextPlayerTurn` now discards the end-of-turn hand **before** the enemy side acts, matching the live engine (Centennial Puzzle probe: HP-loss draws keep the drawn cards in hand).
- Art of War turn-start energy respects attacks played earlier in the turn when the counter is rebuilt from the trace.

Current zero-mismatch reports (41 total, `data/p0-csharp-*-diff-report*.json`):

- Baseline cards/potions: Defend, Strike, Bash, Fire Potion, Energy Potion, Block Potion;
- Anchor + Strike; Bash then Strike with pre-existing Vulnerable; Nunchaku counter transition;
- Powers: VULNERABLE (Bash), WEAK (Neutralize and turn expiry), STRENGTH (Inflame and Vajra), VIGOR (Akabeko → Strike, with consumption), DEXTERITY (Oddly Smooth Stone → Defend), DEMON_FORM (turn-start Strength), BARRICADE (block retention), RUPTURE (HP-loss Strength), AFTERIMAGE (card-play Block);
- Relics: LANTERN (combat-start energy), BAG_OF_PREPARATION (combat-start draw), BAG_OF_MARBLES (combat-start Vulnerable), RING_OF_THE_SNAKE (Silent starting hand), PEN_NIB (counter progression and 10th-attack doubling across four turns), ART_OF_WAR (turn-start energy after a skill-only turn and after an attack turn), HAPPY_FLOWER (turn-boundary counter step), ORICHALCUM (turn-end block), CENTENNIAL_PUZZLE (first HP loss draws, turn-boundary);
- Multi-enemy stable target replay: three consecutive targeted Strikes across three distinct enemies.

This proves the differential mechanism, the turn-boundary projection path, and these fixtures. It does not prove all 283 Powers, 299 relics, random effects, selections, or arbitrary multi-turn semantics.

**Known observation boundaries (documented, not silently ignored):**

- The public observation exposes RNG counters only (no raw state words), so multi-turn hand identities cannot be replayed once a shuffle is required; the single-turn boundary compare uses the teacher draw pile instead.
- Non-attack enemy intents (Buff/Debuff/StatusCard) are not reconstructible from the public observation, so multi-turn differentials that depend on enemy buffs compare only the supported fields.
- Happy Flower's three-turn energy/counter progression was field-verified (`energy` and `relic.counter` matched on every turn) but only the single-turn boundary report is kept as zero-mismatch evidence.

### 7. Version and Data Consistency Gate: Complete

Every accepted trace/training row binds:

- game version and commit;
- assembly SHA-256;
- CLI protocol;
- simulator version;
- semantic database version;
- scorer version;
- feature schema version;
- model version;
- trace/training schema version;
- generator configuration hash where applicable.

The validator executes the five Draft 2020-12 schemas and rejects missing/mixed metadata, public-state leakage, unstructured Power/relic data, invalid stable actions, malformed hashes, and malformed JSON.

## Relic Verification Result

The corrected coverage (`data/relics/v0.111/relic-coverage.json`) is:

- 299 cataloged and structurally capturable.
- 17 simulator mappings declared → **all 17 currently promoted mappings have real CLI/shadow zero-mismatch probes** (the previous 14 plus TOUGH_BANDAGES, TUNGSTEN_ROD and UNCEASING_TOP).
- 1 explicitly classified as not affecting the current turn (`BURNING_BLOOD`).
- 163 known unsupported combat-hook relics.
- 118 unknown.
- 0 relics claimed as IL-inspected by the current exporter.
- 18 currently eligible for Reliable treatment under the corrected report (17 probed + 1 no-combat-effect).

`INCENSE_BURNER` and `SUNDIAL` remain simulator-declared but unverified because the
v0.111 headless CLI rejects those relic IDs during `set_player`; they are not promoted
to `LiveObserved` without a valid runtime probe.

## Latest Verification

- Core Release: **706 passed, 0 failed, 0 skipped** (includes the turn-boundary ordering fix and VIGOR sync; no test regressions).
- Training tools with PyArrow/JSON Schema: **47 passed, 0 failed, 1 skipped**.
- CLI v0.111 combat-scope gate (including stable action fields and public/teacher isolation): **36/36 GREEN**.
- Schema validation: **3 files / 9 rows, 0 errors, 0 public leaks**.
- Chance trace validation: **6 rows, 0 errors**.
- ShadowDiff Release build: **0 warnings, 0 errors**.
- All committed P0/P1 Power and Relic C# differential reports have `mismatch_count=0`; the current P1 batch adds 12 Power reports and 6 Relic reports.
- Probe driver: `training/run_p0_probes.py` reruns the full 21-fixture matrix end-to-end (CLI trace capture + ShadowDiff), currently 21/21 fixtures, 0 failed reports.

Known pre-existing warnings remain in unrelated analyzer/Mod build output. The complete CLI suite has previously contained failures in reward, shop, save/load and full-run flows outside the current combat-turn P0 scope; it has not been declared globally green.

## Post-P0 Semantic Expansion Backlog

## Teacher Smoke Status

- `data/teacher-smoke-100.jsonl` contains 100 deterministic, schema-valid records.
- All 100 records have non-empty `teacher_best_actions` and stable version metadata.
- The current labels are explicitly `Estimated` from the fallback worker because the
  CombatSearchSession/Expectimax evaluator bridge is not yet connected to this Python
  collector; they are not admitted as Reliable policy labels.
- `data/teacher-smoke-100-quality-gate.json` reports `verdict=pass`, with zero version,
  leakage, stable-ID, malformed-line, split, and Parquet-manifest failures.
- `data/teacher-smoke-100-hidden-states.json` contains the public-state aggregation sidecar.
- `training/TeacherEvaluator/` now provides a concrete `sts2.teacher-evaluator.v1` bridge;
  it accepts a full `combat_snapshot` payload and calls `CombatSearchSession`. Raw CLI teacher
  snapshots still need the snapshot reconstruction adapter before they can be passed to it.

1. Expand real CLI/shadow Power probes beyond the 14 validated powers, prioritizing
   the remaining declared mappings.
2. Relic probes now cover 17 simulator mappings; the next tier is the 163 known-unsupported
   combat-hook relics — implement simulator handlers and probes batch by batch instead of
   promoting on reflection alone.
3. Multi-enemy stable target/choice replay is live-verified; extend to a real `card_select` (multi-card choice) live replay when a choice-producing card is exercised against the real CLI.
4. Grow the fixed-seed differential challenge matrix until Reliable coverage is sufficient for Expectimax teacher generation.
5. Consider exporting turn-history counters (e.g. `attacks_played_this_turn`) in the CLI public observation so ShadowDiff no longer needs to rebuild them from the trace.

Large-scale data generation, Expectimax labels, model training, offline RL and ONNX integration remain later milestones defined in `D:\STS2BestChoice\STS2SuperModel\PLAN.md`.
