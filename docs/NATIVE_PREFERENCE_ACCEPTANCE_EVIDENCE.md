# Native constructed P03 / P04 / B02 evidence

`NativePreferenceAcceptanceTests` exercises the pinned C# mechanics through legal
public actions, independently owned `ForkForContinuationAsync` branches, and actual
automatic settlement. The policy callbacks receive detached `DecisionPacket` DTOs.
No production policy, objective, sampler, training record, or simulator rule changes.
These are explicitly constructed decks and enemy-HP settings, not natural sources
or evidence of a trained student's ordering competence.

## P03: safe longer battle

At 60/70 HP against `TwigSlimeS` with 20 HP, the five-card deck is Footwork, two
Strikes, and two Defends. Both alternatives legally play Footwork first. Its native
Dexterity makes each Defend sufficient against the five-damage attack.

- Attack-first route: two Strikes on turn 1, five HP lost, victory on turn 2
- Safe route: defend before attacking, zero HP lost, victory on turn 3
- Actual final HP: 55 versus 60; actual total actions: 6 versus 10
- Production objective costs: 5.083333333333333 versus 0

Every safe end turn preserves 60 HP. The entire remaining deck is drawn each turn;
shuffle order cannot change these DTO-only scripts' damage or defense capacity.
The production objective prefers the longer, more-action route because the settled
HP is better; no turn/action penalty is added.

## P04: whole battle, not just immediate defense

At 60/70 HP against `CubexConstruct` with 26 HP, the deck is three Strikes and two
Defends. A common legal end turn executes the native non-attacking charge move.
The turn-2 root still has 60 HP, so the comparison retains the original combat-start
HP anchor. The public intent exposes base damage 8; public Strength is 2 at this
root and 4 on the next turn. Actual committed HP changes establish damage 10 and
12 without treating the base intent field as the modified total.

- One Defend plus two Strikes on turn 2 loses 5 HP, then the unchanged ordinary
  `PublicRulePolicy` continuation wins on turn 3 at 55 HP
- Two Defends plus one Strike loses 0 HP on turn 2; the same continuation plays
  three Strikes on turn 3, leaving 2 enemy HP and no energy/block, then takes 12 HP
  and wins on turn 4 at 48 HP
- Production objective costs: 5.083333333333333 versus 12.48

The five-card deck is fully drawn every turn. The first route wins before the
larger attack. The second route's apparently better current turn yields the worse
actual whole-combat result. Neither route consumes a potion or earns/changes a
persistent resource.

## B02: exact declared five-position law

This is an exact **declared exchangeable draw-order experiment with native
outcomes**, not an exhaustive distribution of the native 31-bit setup seed or all
future RNG streams. This limit matters: `EnumerateDrawPosterior` enumerates the
bounded public draw law; it does not establish equivalence to a whole-setup seed
posterior. Do not promote this result to an all-native-prior probability claim.

The constructed opening is at 60/70 HP against a 5-HP solo `Nibbit` with its native
13-damage intent. The twelve-card deck contains one TheHunt, one Backflip and ten
Strikes. The common public opening hand contains Backflip and six Strikes. Its
five unknown draw cards contain TheHunt and four identical Strikes.

The test enumerates all five distinct orders. Each receives rational mass 1/5,
kept as numerator/denominator and reduced using integer totals before division.
For each class, an ordinary native setup seed supplies a concrete execution
witness with the identical full public opening. Witness discovery happens before
either policy executes, uses the first witness for every class, and fails if any
class is missing. Seed counts never determine probabilities, and no outcome is
used to select, reject or reweight a witness.

Both arms are forked from the same witness. The baseline is the unchanged
`nosl-public-rules-v1`: one Strike wins immediately with zero loss. The plan is the
existing `FiniteHuntPolicy` with its immutable turn-1 anchor and turn-2 deadline.
It plays Backflip, then TheHunt when publicly available.

| TheHunt draw position (zero-based) | Exact mass | Plan HP loss | Fatal turn | Actual extra CardReward offers |
|---|---:|---:|---:|---:|
| 0 | 1/5 | 0 | 1 | 1 |
| 1 | 1/5 | 0 | 1 | 1 |
| 2 | 1/5 | 8 | 2 | 1 |
| 3 | 1/5 | 8 | 2 | 1 |
| 4 | 1/5 | 8 | 2 | 1 |

This support is complete for the declared experiment: Backflip draws the first two
cards and gives five block. If neither is TheHunt, the 13-damage attack loses eight
HP and the next five-card draw necessarily includes TheHunt among the remaining
three original draw cards. The additional two reshuffled cards cannot affect the
plan's Hunt-first choice, available energy, native fatal, or HP. Nibbit's first
attack has no random branch, no healing occurs, and victory precedes its next
attack. Reward option identities are neither inspected nor valued; only the
actual additional offer count matters.

Thus exact extra loss is `(0+0+8+8+8)/5 = 24/5 = 4.8`; unconditional actual fatal,
reward and deadline success is `5/5 = 1`, and all support points survive. The
production anchored gate returns `EligibleNotMandatory` despite three individual
worlds exceeding five HP. This establishes that the threshold applies to the
whole-plan expectation, not each trajectory. The unpriced extra reward keeps
absolute utility unresolved; eligibility is not a required action or a policy
ranking.

## Checks and reproduction

All three tests assert legal actions, stable source/public-RNG snapshots after
both arms, complete settlement, zero reward selections, and actual HP/resource
facts. B02 additionally checks native TheHunt fatal events and the actual offered
CardReward in every support class. P03/P04 score the actual settled outcomes;
B02 leaves unknown future reward value unresolved.

With the repository's .NET 9.0.303 environment:

```sh
NOSL_NATIVE_PREFERENCE_EVIDENCE_DIR=artifacts/native-preference-evidence \
  dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/native-preference-build \
  --filter FullyQualifiedName~NativePreferenceAcceptanceTests \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
```

The optional output contains each public root, native paired traces/outcomes and
B02 rational masses. It is development evidence, not production data or labels.
The targeted suite passed 3/3; a full repository regression was not part of this
bounded change. Broader natural-state acceptance, calibrated reward values and
learned policy quality remain outside these fixtures.
