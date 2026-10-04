# P05: a safe, better later potion use

This is a constructed native-simulator existence example for the V4
[same-potion timing requirement](spec/v4/TEACHER_CONTRACT_V4.md#4-9点药水规则).
It compares using one Flex Potion now with waiting safely and consuming that
same potion on the next player turn. It does not compare consumption against
retention, assume a resource price, optimize potion timing generally, change the
frozen continuation, or establish a learned policy capability.

The focused integration is
[DelayedPotionTimingTests.cs](../tests/Nosl.Tests/DelayedPotionTimingTests.cs).
No production source, default policy, objective profile, posterior certificate,
training data, or formal-label permission changes.

## Executed result

Executed 2026-10-02 on base commit `65bdfb3`, with isolated .NET 9.0.303
build output. The new integration passed, and its same-seed evidence-export
reproduction also passed. All 32 allocated pairs completed with both arms
winning and consuming exactly one potion; there were no incomplete outcomes.

| Native outcome | Worlds | Early use final HP | Late use final HP | Late HP advantage |
|---|---:|---:|---:|---:|
| Three Strikes drawn on turn two | 16 | 55 | 60 | 5 |
| Two Strikes drawn on turn two | 16 | 60 | 60 | 0 |

The empirical HP advantage is 2.5. Existing relative cancellation gives an
early-minus-late mean candidate cost of 2.541666667 and fixed-N 95% interval
[1.321256453, 3.762076881], favoring the late arm within this declared fixture
and sampler. Both absolute utilities remain masked; formal labels remain
forbidden. The 16/16 split is an observed count, not an asserted population
probability. The separately seeded replacement `potion-timing-source-49`
matches source `potion-timing-source-0` in its entire public history and
reproduces all 32 paired traces/outcomes despite different true future RNG.

## Declared root and public scripts

The declared setup is Silent A10, player HP 60/70, TwigSlimeS at 20 HP, one
FlexPotion, the native RingOfTheSnake starter relic, three unupgraded
DefendSilent and three unupgraded StrikeSilent. This is an explicit constructed
recipe, not a naturally sampled encounter. All six cards are in the opening
hand. Three ordinary native Defend plays reach the root: 15 block, zero energy,
three Strikes in hand, three Defends in discard, empty draw pile, and the
enemy's visible attack of 5. No HP, pile, intent or RNG is changed in place.

The two root actions are `potion` and `end_turn`. Both then use the identical
test-only continuation `test-public-next-turn-flex-attack-defend-v1`:

1. On turn two or later, use the remaining Flex Potion if present
2. Otherwise play a legal Strike, otherwise a legal Defend, otherwise end turn

Every choice receives only a serialized-and-deserialized public DecisionPacket.
The controller sees no branch object, source seed, sampled seed, hidden order,
counterfactual outcome, gate result or objective score. Its turn-two potion use
is a declared finite script, not a conclusion inferred from evaluating worlds.
It immediately attacks after later use; neither potion eligibility nor a
threshold rule forces an end turn. Both branches consume exactly one potion.

## Mechanics supporting safety and bounded outcomes

Native [FlexPotion](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Potions/FlexPotion.cs)
grants five Strength through
[TemporaryStrengthPower](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Powers/TemporaryStrengthPower.cs),
which removes that change after the player's side ends. The early arm has no
energy and cannot attack before expiration. The late arm retains the potion
through the first enemy turn; 15 existing block absorbs the visible 5 damage.
[TwigSlimeS](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Monsters/TwigSlimeS.cs)
only repeats that five-damage attack and has no retaliation/death hook.

The next native draw reshuffles six cards and draws five, necessarily including
two or three Strikes. With late Flex, two Strikes deal 22 and win on turn two,
with no HP loss. With early Flex expired, two Strikes deal 12 and leave one
energy for a Defend, or three deal 18 and leave no block. Consequently the early
arm takes zero or five damage on the second enemy turn. In the two-Strike case,
the third Strike is the remaining draw card, and the following four-card draw
contains at least one more Strike; the next turn necessarily has enough damage
to kill the remaining eight HP. In the three-Strike case only one Strike is
needed to kill the remaining two HP on turn three. These arguments cover every
shuffle order, not just observed outcomes.

The reviewed recipe has no healing, max-HP change, permanent gain, extra reward,
potion generation, or relevant extra callbacks. The settlement boundary is the
actual automatic native endpoint before the first postcombat decision. Each
outcome must have complete HP/resource diagnostics, unchanged persistent
assets, one consumed FlexPotion, and no selected reward. The whole-combat
anchor stays at HP 60 and one starting potion throughout; it is never reset at
the comparison root or at potion use.

Thus the paired HP saving has support [0, 5]. With the unchanged candidate
objective, early-minus-late relative cost has support [0, 61/12], because a
five-HP loss costs `5 + 0.2 * 25 / 60`. The same unknown potion term cancels
through the existing CommonResourceObjective using the actual complete
resource ledgers and exact unchanged persistent-asset snapshots. Neither
absolute utility becomes available; no price, zero-value assumption, empty
inventory substitute certificate, or new calibration answer is introduced.

## Sampling, isolation and interpretation

The script, 32 evaluation seeds 9420000–9420031, and mechanics support are fixed
in the test before sampling. FlexPotion is outside the existing fast
exchangeable sampler's declared potion set, so the test uses the existing
whole-setup rejection sampler conditional on the complete public prefix,
with at most 1,024 attempts per assigned world. Acceptance uses public history
alone. Each accepted independent world is forked into two separately owned
native replay branches. The test checks identical starting snapshots, distinct
state/room ownership, the untouched paired arm after running its counterpart,
and unchanged sampled world and source snapshots.

A second ordinary source seed is selected solely for an identical public
opening, before observing comparison outcomes. It executes the same prefix and
has an identical full public history but different true future RNG. The same
32-seed paired evaluation must reproduce every action, observation and settled
outcome exactly. It is a hidden-source invariance check, not 32 additional
independent evaluation worlds.

All 32 allocated pairs remain in the report, including ties. Sampling failure,
unexpected choice, a decision cap or missing endpoint fails the integration;
no complete-case average or loss imputation is allowed. The one predeclared
relative-cost comparison uses a fixed-N Hoeffding interval with alpha 0.05 and
the mechanics support above. It does not infer support from sample extrema.
The evidence is empirical under the declared sampler, not exhaustive
enumeration of future random streams. The existing exact draw-pile enumerator
does not enumerate a future reshuffle from this empty-draw root, so no exact
population probability is assigned by hand.

The nine-HP rule still returns `EligibleNotMandatory` at exactly nine. This
timing contrast is not a use-versus-retain valuation and the nine-HP gate is
not applied again to the difference between two eventual-consumption arms.
The example establishes that safe delay can preserve useful potion timing;
resource-aware general continuation, timing optimization and learned
supervision remain separate open work.

## Reproduction

From this checkout:

```sh
source ../.dotnet-nosl-env.sh
NOSL_POTION_TIMING_EVIDENCE_DIR="$PWD/artifacts/reports/delayed-potion-timing-v1" \
DOTNET_PROCESSOR_COUNT=1 dotnet test tests/Nosl.Tests/Nosl.Tests.csproj \
  -c Release --artifacts-path artifacts/potion-timing-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter FullyQualifiedName~DelayedPotionTimingTests \
  --logger 'console;verbosity=detailed'
```

This only builds isolated artifacts and runs the new integration. The optional
native-paired-timing.json contains the declared recipe, public root/prefix,
fixed plan, every paired public trace and actual settled outcome, relative
result, replacement source identity and invariance checks. Repeated test runs
are reproductions of the same fixed seeds and must not be pooled as fresh
evidence. There is no optimizer, fit, bulk generation or publication step.

The executed local report is under
`artifacts/reports/delayed-potion-timing-v1/` (ignored by Git), with SHA-256
`40322f8167c33dc95bea1da5b0bac61cb4c1bfa53920e2e1d7281c2db5afc951`.
The adjacent source-runtime-manifest.json records the tested source/runtime
hashes as a post-run audit. The test source SHA-256 is
`db717b601378a1ba762642728226fb279c737a9d46331b9f55eff60f9f616b44`.
