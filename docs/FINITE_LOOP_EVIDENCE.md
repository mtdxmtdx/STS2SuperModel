# Bounded native loop execution evidence

Date: 2026-10-01. Runtime baseline: `5ef76a558afd54bc38a78ff6635c3b50ce3c0c6f`.
Pinned simulator: `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`.

This report covers six real simulator integration cases in
[`FiniteLoopTests.cs`](../tests/Nosl.Tests/FiniteLoopTests.cs). The change adds
tests and this document only. It implements neither a loop detector nor a macro,
and does not change the frozen continuation policy, simulator, objective, data
schema, or worker binaries used by the running corpus job.

## Contract and scope

V4 [plan §1.1 and M4](spec/v4/PLAN_NOSL_FULL_COMBAT_V4.md) permits a legal
repeatable sequence that actually ends combat. Action count is not a penalty.
The [teacher contract §§2, 3, 6](spec/v4/TEACHER_CONTRACT_V4.md) requires actual
terminal settlement, retained incomplete probability mass, and no action-count
reward. A budget is a compute limit, not a game defeat. Macro acceleration needs
its own semantic proof and expansion equivalence.

These are declared constructed stress fixtures: Silent A10, two identical cards,
one native TwigSlimeS with declared 65 HP, default 70 player HP, no added potion,
relic, enchantment, or affliction. They are not evidence of the frequency of these
decks in natural play or general interaction coverage. The engine, not the tests,
applies damage, draw, shuffle, block, playability, and terminal rules.

## Actual successful attack cycles

The pinned native [FlashOfSteel](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/FlashOfSteel.cs)
is a zero-cost attack with draw. Its
[GeneratedCardModel](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/GeneratedCardModel.cs)
executes the native attack and draw commands. With two copies, one physical card
can be drawn back after the other has been played and discarded.
[CardPileCmd](../vendor/sts2-sim/src/Sts2Sim.Core/Commands/CardPileCmd.cs) performs
the actual reshuffles and stops draw/shuffle when combat is ending. No test
predicts damage or changes HP, energy, piles, or RNG during execution.

Observed results, reproduced by the final affected regression run:

| Two-card deck | Atomic plays to true win | Recorded draw events | Recorded shuffle events | Settled player HP | Objective cost |
|---|---:|---:|---:|---:|---:|
| FlashOfSteel | 13 | 13 | 11 | 70 | 0 |
| FlashOfSteel+ | 9 | 9 | 7 | 70 | 0 |

Both physical copies were used repeatedly, all plays occurred on player turn 1,
and energy remained 3 at every active decision. Every play was selected by the
unchanged public-packet-only `PublicRulePolicy`. The tests require the real
`terminal_settled` endpoint and engine result `win`, idempotent settlement, and
zero postcombat reward selections.

Each complete atomic action transcript is replayed into a second independent
native continuation. Tests compare every public packet, the complete terminal
facts/events, serialized run and player RNG state, and final persistent asset
snapshot. Every submitted action also appears verbatim in the native public
action ledger. This is exact ordinary execution replay for these fixtures, not
an accelerated macro or proof of equivalence for arbitrary loops.

Each long fight is also compared with an actually executed one-play win using
the same two-card setup and an enemy with 1 HP. Both terminal outcomes receive
cost 0 despite different action counts. The tests use the production shared
`RolloutRecorder` and `ObjectiveEvaluator`; they do not reconstruct an objective
or add an attack/draw bonus in the test.

## Teacher accounting, independent worlds, and budget sensitivity

T0 and T1 each evaluate all three legal root candidates: first copy, second copy,
and end turn. Two fixed final belief seeds, 101 and 102, produce six allocated
candidate-world outcomes per evaluation. T1 uses separate exploration seed 7
and public tree depth 2. Both use a maximum of 64 decisions per rollout.

| Root action | Final-evaluation action counts, T0 and T1 | Wins / allocated | Expected candidate cost |
|---|---|---:|---:|
| Play first FlashOfSteel | 13, 13 | 2 / 2 | 0 |
| Play second FlashOfSteel | 13, 13 | 2 / 2 | 0 |
| End turn, then continue | 14, 14 | 2 / 2 | 5.071428571428571 |

The extra cost after end turn comes from the actual settled HP loss, not one
extra action. Observed total rollout decisions are 80 for T0 and 120 for T1;
T1's total includes exploration. All six final worlds settle, and all raw
outcomes retain the settlement and continuation identities. Formal-label
permission remains false under the uncalibrated candidate objective.

FlashOfSteel is outside the reviewed exchangeable-posterior subset. These tests
assert `whole-setup-rejection-v1`, exercising the ordinary independent native
setup replay sampler without adding a content shortcut. Two source scenarios
with identical full public roots but different actual seeds and private run RNG
states produce identical candidate outcomes and frozen continuation identities
under the same independent final seeds. Both sources remain unchanged. Because
the two-card opening hand is fully visible, this is future-RNG/source-seed
independence evidence, not a heterogeneous hidden-draw-order test.

For T0, all six candidate/world continuations are separately expanded through
ordinary atomic execution and their complete recorded raw outcomes are compared
with the teacher's records. Reducing the teacher decision budget from 64 to 2
preserves all six records as `ComputeTruncated`, with no win/loss, utility,
strong pairwise ranking, or equivalent-action claim. Thus the same legal
winning mechanism is not relabeled a game loss when the engineering budget is
too small.

## Draw-only and defensive loops

Two native [Impatience](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/Impatience.cs)
cards provide the draw-only case. The pinned native implementation draws when
the hand contains no attacks. Two native
[Finesse](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/Finesse.cs) cards provide
a separate block-and-draw case.

After 24 explicit plays selected by the unchanged frozen rule policy:

| Deck | Player HP | Enemy HP | Block | Player turn | Energy | Actual status |
|---|---:|---:|---:|---:|---:|---|
| Impatience × 2 | 70 | 65 | 0 | 1 | 3 | player_decision |
| Finesse × 2 | 70 | 65 | 96 | 1 | 3 | player_decision |

Both actually reshuffle. Their histories and play/draw counters change, and an
end-turn exit remains legal. Finesse also changes block. Stable HP, energy, or a
repeated hand shape is therefore not asserted to be full semantic-state
repetition. Calling settlement at this live boundary is rejected.

For each deck, the teacher keeps all three root candidates and both independent
final worlds. All six allocated records are `ComputeTruncated`, zero are true
losses or `PolicyNonterminating`, completion rate is 0, and win/loss mass bounds
remain [0, 1]. Every affected dataset target mask is false; utility and win
targets are null, and pairwise/equivalent sets are empty. No unfinished mass is
dropped or normalized into a favorable label.

The Finesse result exposes a current limitation: the generic continuation keeps
playing after block exceeds the published incoming attack preview. This is
honest accounting of excess defensive cycling, not evidence that the policy
knows when enough defense has been gained.

## Acceptance interpretation and remaining work

| V4 catalog item | Evidence here | Remaining limitation |
|---|---|---|
| C01: legal winning infinite | Two native repeatable free attack/draw fixtures reach true settled wins; long and short wins have equal utility | Bounded fixture evidence only; no general infinite solver |
| C02: stop useless defense, continue useful actions | Native Finesse exposes continued excess blocking and correct truncation | Not closed; continuation still needs principled public-state exit behavior and mixed useful-action tests |
| C03: safe finite delay for real benefit | No new positive acceptance claim | Draw-only fixtures earn no verified benefit; general safe-delay planning is outside this change |
| C04: macro equivalence | Exact ordinary atomic transcript replay | Not closed; no macro exists in this change |
| C05: do not mistake a public repeat for semantic nonprogress | No repetition heuristic declares these changing histories/counters a defeat or proven nontermination | No exhaustive hidden-progress detection or full-state repetition proof |
| D01: world-count conservation | Every root candidate keeps every allocated final world for complete and truncated executions | Does not establish coverage beyond these fixtures |

No acceptance-catalog status is edited. These tests do not certify all legal
loops, the optimality of T0/T1, arbitrary risk preferences, natural source
representativeness, or policy convergence. No weights are fitted and no labels
become formally calibrated.

## Reproduction and isolation

Use the configured .NET 9 SDK. The run restored only already cached packages
with an empty package-source configuration, and placed all build products in
`/tmp/nosl-finite-loop-build` rather than the default project bin/obj directories:

The exact successful six-case command, after that isolated restore, was:

```sh
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path /tmp/nosl-finite-loop-build --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~FiniteLoopTests' \
  --logger 'console;verbosity=detailed'
```

Result: 6/6 passed, 9.4896 seconds. After adding the explicit run/player RNG and
asset replay equality assertions, the final affected regression command was:

```sh
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path /tmp/nosl-finite-loop-build --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~FiniteLoopTests|FullyQualifiedName~TeacherTests|FullyQualifiedName~ObjectiveTests' \
  --logger 'console;verbosity=detailed'
```

Result: 50/50 passed, 20.3185 seconds: all 6 finite-loop cases, 23 teacher cases,
and 21 objective cases. This was a focused affected regression run, not a full
repository suite. Local execution logs are `/tmp/nosl-finite-loop-test.log` and
`/tmp/nosl-finite-loop-regression.log`; each new case emits its observed counts
and outcomes to the detailed test output.

Before and after execution, SHA-256 and modification timestamps matched for all
856 existing DLL files found under this checkout's `bin` directories. The seven
active corpus workers' existing binaries were not rebuilt. Runtime source files
were not modified by this change.
