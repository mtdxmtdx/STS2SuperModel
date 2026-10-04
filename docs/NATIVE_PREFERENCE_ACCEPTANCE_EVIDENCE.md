# Native constructed P01–P05 / B01–B02 evidence

`NativePreferenceAcceptanceTests` exercises the pinned C# mechanics through legal
public actions, independently owned exact continuation branches, and actual
automatic settlement. The policy callbacks receive detached `DecisionPacket` DTOs.
No production policy, objective, sampler, training record, or simulator rule changes.
These are explicitly constructed decks and enemy-HP settings, not natural sources
or evidence of a trained student's ordering competence.

## P01 / P02: exact 0/30 lottery against certain 8 and 3

A declared constructed opening uses `MechaKnight` at 5 HP, player HP 60/70, and a
twelve-card deck: Prepared+, two Deflect+, Defend+, Defend, four Wounds and three
Strikes. The first native setup with the required **public** opening composition
is retained: Prepared+, both Deflect+, both Defends and two Wounds. Selection does
not inspect hidden order or any continuation outcome. Its unknown draw multiset
is three Strikes and two Wounds, with no publicly known positions.

The declared exchangeable law has exactly `5!/(3!2!) = 10` distinct orders, each
with rational mass 1/10. The test uses the existing `ForkExact` primitive to own
each world and rearranges only that clone's existing unseen draw-card instances.
It preserves card values, ownership, all RNG states, HP, energy, visible piles and
the complete public history. No native rule or replay/sampler seam is added.
This is a constructed finite law, not the native setup-seed posterior. All ten
classes execute; no class is selected, removed or reweighted by its outcome.

Each class has three independently owned native continuations whose policies see
only detached public DTOs. These frozen acceptance scripts are not claims of
optimal play. In particular, the lottery script ends without defending after a
miss even though defensive cards remain available; a better continuation could
use them. The evidence concerns scoring the requested native outcome
distributions, not selecting the best available root policy:

- Lottery: play Prepared+, execute its native draw-two/discard-two choice by
  discarding publicly visible Wounds, then play a drawn Strike if available
- Certain 8: play both Deflect+ and Defend+ for actual block 7+7+8 = 22, end the
  turn, then play a Strike on turn 2
- Certain 3: add the remaining base Defend for actual block 27, end the turn,
  then play a Strike on turn 2

MechaKnight's first attack is natively 30 damage. Nine orders expose at least one
Strike among Prepared+'s first two draws and win immediately at 60 HP. The one
order beginning Wound/Wound ends turn 1 with zero block and 30 HP, then draws the
three remaining Strikes and wins on turn 2. Both fixed routes draw the original
five-card pool on turn 2, so their lethal Strike is guaranteed. No later enemy
move, shuffle result or reward identity can affect the measured HP outcomes.
There is no healing, resource consumption or persistent reward change.

| Native route | Exact loss distribution | Expected loss | Production expected cost |
|---|---|---:|---:|
| Lottery | 0 with 9/10; 30 with 1/10 | 3 | 3.3 |
| Certain 8 | 8 with 1 | 8 | 8.213333333333333 |
| Certain 3 | 3 with 1 | 3 | 3.03 |

The unchanged production objective prefers the lottery to certain 8 (P01), and
certain 3 to the equal-mean lottery (P02). Inputs to scoring are all 30 actual
settled native outcomes, never hand-authored loss vectors. All retain the original
60-HP combat-start anchor. For the source's unchanged actual draw order, all three
exact-clone trajectories and outcome records additionally match independently
recreated `ForkForContinuationAsync` native executions byte-for-byte. That is a
fixture-specific projection check, not a claim of all-content clone equivalence.
Formal labels remain disabled, and this does not identify a unique calibrated
risk coefficient or establish learned policy quality.

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

## P05: current use qualifies, but the same bottle is better later

The explicitly constructed `SpinyToadNormal` encounter sets enemy HP to 27,
player HP to 90/100, and the deck to three Strikes and two Defends, with one
FlexPotion and the default RingOfTheSnake. A common legal turn-1 end executes the
native non-attacking spikes move; two legal Defends on turn 2 reach the shared
root with 90 HP, ten block, one energy and three Strikes in hand.

The pinned monster does **not** repeatedly attack for 25. Its deterministic cycle
is spikes (gain five Thorns), explosion (25 attack, then remove five Thorns), and
lash (19 attack). The common prefix and all continuations execute those actual
rules. All five cards are drawn every turn, so future shuffle order does not
change the frozen scripts' available attacks or defense.

The never-use baseline plays Strikes, then Defends, then ends the turn. The two
potion alternatives differ only in their first legal root action: use Flex now,
or end the turn. Both then use the same frozen public continuation, which uses
any remaining Flex on turn 3 before playing Strikes, Defends and end-turn. The
policy takes only detached public DTOs; the separate baseline never uses a potion.

| Actual native route | Victory turn | Settled HP | Whole-combat HP loss | Flex consumed |
|---|---:|---:|---:|---:|
| Never use | 4 | 51 | 39 | 0 |
| Use now | 3 | 70 | 20 | 1 |
| Wait, then use on turn 3 | 3 | 75 | 15 | 1 |

An attack on turn 2 triggers five native Thorns damage against the ten block,
leaving only five block for the 25 attack. With no potion, the enemy survives the
three turn-3 Strikes and executes its 19-damage lash; the turn-4 Strike wins. Early
Flex makes the turn-3 attacks lethal and avoids that lash. Waiting preserves all
ten block against the explosion; Flex then strengthens all three turn-3 attacks,
after the explosion has removed Thorns.

Current use saves exactly `70-51 = 19` whole-combat HP against the declared B0,
so the unchanged nine-HP gate returns `EligibleNotMandatory`. Waiting is safe in
the explicit survival sense: it takes 15 HP at the root end-turn, stays alive at
75 HP and wins next turn. It is not described as a zero-damage wait. The later
route saves five more HP than current use while consuming the same single bottle.

`CommonResourceObjective.Evaluate` verifies the shared-world/root/anchor/frozen
continuation audit, exact unchanged persistent assets, and complete native
resource ledgers. It cancels one unresolved FlexPotion coefficient and returns
positive early-minus-late cost
`5 + 0.2*(20^2-15^2)/90 = 5.388888888888889`, favoring later use. Both absolute
potion-consuming costs stay unresolved; no potion price is invented. All three
outcomes preserve the original 90-HP combat-start anchor, actual damage accounting,
zero healing and zero postcombat reward selections. Sources and run/player/monster
RNG remain unchanged after each independently owned native continuation.

This closes the joint threshold-plus-timing existence example only. It is a
constructed comparison under frozen continuations, not a general timing optimizer,
a learned policy result, or calibration of the potion's retained future value.

## B01: exact joint five-HP / 80-percent boundary

A separate declared constructed opening uses a 5-HP `MechaKnight`, player HP
60/70, Adrenaline, Backflip, TheHunt and nine Strikes. The first native setup with
Adrenaline, Backflip and five Strikes publicly in hand is selected before any
continuation executes. Its unknown draw pool is TheHunt plus four Strikes. All
five declared exchangeable orders receive rational mass 1/5.

The counterfactual baseline is explicitly `test-public-strike-first-v1`, used in
both arms. It plays a legal Strike before any other attack. The attempt is
`test-public-adrenaline-backflip-hunt-or-abort-v1`; it uses one immutable public
anchor at turn 1 and deadline at turn 2. It plays Adrenaline (native zero cost,
one energy gained, two cards drawn), then Backflip (one energy, two cards drawn,
five block). If TheHunt is now public, it plays it. Otherwise it irreversibly
aborts, ends turn 1, and follows the same Strike-first baseline on turn 2.

This is deliberately a frozen attempt/abort script, not the production
`FiniteHuntPolicy`/`PublicRulePolicy` pair or an optimal continuation. In the miss
world, TheHunt is available on turn 2 and the ordinary production attack-order
tie would play it. This test's explicitly declared Strike-first baseline instead
wins with a Strike. No actual incidental Hunt fatal is discarded or relabeled.

Each arm is recreated through `ForkForContinuationAsync` from the unchanged
source before installing its declared unseen permutation. Both remain concrete
native states, preserving TheHunt's actual reward hooks. They are executed directly;
no mutated world is replayed under its original source seed. Existing card
instances, HP, energy, RNG and the full public history remain unchanged by
permutation. This is again an exact declared finite law, not the native setup-seed
posterior.

| Hunt position | Exact mass | Baseline loss | Attempt loss | Actual Hunt fatal / extra offer / deadline success |
|---|---:|---:|---:|---|
| 0–3 | 4/5 total | 0 | 0 | Yes, all three facts, on turn 1 |
| 4 | 1/5 | 0 | 25 | No; public abort, native Strike victory on turn 2 |

The native 30-damage opening attack minus Backflip's five block causes the entire
25-HP miss loss. Every support point survives. Whole-plan expected extra loss is
`25/5 = 5`; unconditional specified success is `4/5 = 0.8`. The unchanged
production anchored gate returns `EligibleNotMandatory` at this exact joint
boundary. It still has one fixed anchor/deadline; the abort does not start a new
budget. Native HP/damage/settlement are recorded, and success flags are derived
only from the actual Hunt-fatal event, extra offer and observed fatal turn.

The four success worlds retain unresolved absolute utility for the unpriced extra
CardReward; the failure world remains an ordinary scored win. A separate
regression feeds the recorded values through the fixed-sample formulas and verifies
that bounds straddling five and 0.8 yield `Unresolved`. Those formula outputs are
**not reported as confidence intervals for these enumerated classes**: exhaustive
support points are not independent random samples. No statistical coverage or
sample-size sufficiency claim is made by that check.

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

All six tests assert legal actions, stable source/public and run/player/monster
RNG snapshots after
each compared route, complete settlement, zero reward selections, and actual HP/resource
facts. P01/P02 additionally check all ten unseen permutations, actual native discard
choices, preserved source card order, and concrete native replay parity. B02
checks native TheHunt fatal events and the actual offered
CardReward in every support class. B01 verifies those same actual facts in four
classes and their genuine absence in the deliberately aborted fifth class. P03/P04 score the actual settled outcomes;
B01/B02 leave unknown future reward value unresolved. P05 verifies the
nine-HP consideration gate and same-bottle relative cancellation jointly, while
absolute potion-consuming values remain masked.

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
The earlier five-test targeted suite passed 5/5; the added P05 focused test passed
1/1. A full repository regression was not part of this bounded change. Broader natural-state acceptance, calibrated reward values and
learned policy quality remain outside these fixtures.
