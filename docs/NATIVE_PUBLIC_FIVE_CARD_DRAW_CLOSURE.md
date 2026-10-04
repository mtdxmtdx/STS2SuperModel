# Five source-reviewed manual plays in draw-prefix v10

`nosl.public-first-draw-cycle.v10` adds DeadlyPoison, FlickFlack, Anticipate,
PreciseCut, and Ricochet to the existing manual-play closure. It also adds the
AnticipatePower/DexterityPower listener closure. Only proposal acceleration
changes. The prior, public evidence, native actions, source policy, finite shuffle
proposal/correction arithmetic, and final full public equality remain unchanged.
The central sampler implementation version must be advanced by the integration
owner before any new declared collection; existing v7 data/binaries remain frozen.

## Native source argument

The source base is STS2SuperModel `8ae22da`, using the pinned simulator. All five
cards are sealed, inherit the ordinary card lifecycle and result locations, and
introduce no card-selection continuation. Their complete implementations,
upgrades, inherited listeners, and downstream command paths were inspected:

| Card | Native behavior | Why the undrawn pile is preserved |
|---|---|---|
| DeadlyPoison | One-energy skill applies 5/7 PoisonPower | No immediate attack or draw. Poison's existing reviewed side-start damage/decrement/death path invokes only the same guarded power/death listeners |
| FlickFlack | One-energy Sly attack deals 7/9 to every opponent | AttackCommand uses the live opponent list and damage callbacks. Manual play neither discards other cards nor invokes Sly autoplay |
| Anticipate | Zero-energy skill applies 2/4 AnticipatePower | TemporaryDexterityPower applies Dexterity before initial attachment, adjusts it on its own later amount changes, then removes itself and subtracts Dexterity at the owner's side end. Dexterity changes only powered block amounts |
| PreciseCut | Zero-energy targeted attack deals 13/16 minus two per other card in hand | The native count is a read after the played card moves to Play. No card is selected, moved, generated, upgraded, or drawn by the effect |
| Ricochet | Two-energy Sly attack makes 4/5 random-enemy hits of 3 | AttackCommand selects live targets through RunRng.CombatTargets; this path does not read or consume Shuffle or inspect Draw. All damage/death callbacks remain guarded |

The native `CardModel.PlayAfterObserverStartedAsync` still spends actual resources,
moves the played card Hand→Play, invokes its listeners and OnPlay, and moves its
ordinary result to Discard (or the existing native Exhaust/None override). The
existing certificate rejects an observed Draw/Hand/Play result. Temporary cost
cleanup, Sharp, and the already-reviewed listener set are unchanged. No native
effect is reimplemented in the certificate.

For Anticipate, the sealed subclass inherits the positive
`TemporaryDexterityPower` lifecycle. `BeforeApplied` applies Dexterity first;
`AfterPowerAmountChanged` handles its own subsequent delta; `AfterSideTurnEnd`
removes the temporary power and applies the negative Dexterity delta.
`DexterityPower.ModifyBlockAdditive` is a read-only owner/powered-source check.
`PowerCmd.Apply/ModifyAmount/Remove` uses the existing guarded power listeners;
these two powers add no pile hook or further power type. Other temporary-power
subclasses are not admitted by this change.

Sources are the five `vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/<Card>.cs`
files, `Models/Powers/{AnticipatePower,TemporaryDexterityPower,DexterityPower,PoisonPower}.cs`,
`Models/CardModel.cs`, `Commands/{PowerCmd,CardCmd}.cs`,
`Commands/Builders/AttackCommand.cs`, and `Combat/CombatEngine.cs`.
The exact new card source SHA256 values are:

- DeadlyPoison: `ae95988cb21c7a4e9d1fc4809542c8e3fb28d8d69bfa46ff58ef395a587184ea`
- FlickFlack: `b40513767a863e0b507e42acfa0c9b39bcbe7fce7133c96a8a10161271a76ba4`
- Anticipate: `6fdecc781eae64af4ec1f2725a4f3a4bbed884ee823105dbcc0429e4aaaf4010`
- PreciseCut: `a325835fe22c80aa8f03e6fb8e99aa4d6177fcf21ddc1d420dd1af91de21e798`
- Ricochet: `e43ada465b674a70e20db6f40f4ae6ecdf29011ff5d4688daa6a0552a3dbf076`

The power sources are AnticipatePower
`d507188875bbaff8c245cd71f76740efbcf5b1333bc69e07025c33b04e79ff57`,
TemporaryDexterityPower
`18c49ac9aa8264615ed1d932226686a989b03909616420aceef73d8ba452014a`,
and DexterityPower
`46bc5a53db08a1c2f8ac15de403c60691a3a3b6419a6c6852373b726fb8a9743`.

## Sly and unsupported effects remain closed

FlickFlack/Ricochet are allowed as explicit public manual plays. The existing
Survivor guard still stops before a discard when a Sly card is present. The
existing DaggerThrow explicit/automatic selection guard still stops before a
selected Sly card is discarded and auto-played. Native turn-end hand flush uses
plain `CardPileCmd.Add` and does not call `CardCmd.Discard` or Sly autoplay.

The five cards do not become ordinary draw sources. Unexpected draw facts,
generation, afflictions, unreviewed powers, changed listeners, gaps, and invalid
result piles retain their earlier stopping behavior. Failure preserves the
already-proved prefix and leaves remaining work to exact native rejection.
This does not remove cards or public facts from the prior's support.

## Verification scope

`NativePublicFiveCardDrawClosureTests` exercises base and upgraded forms through
actual native effects, subsequent end-turn draws, and a witnessed reshuffle.
For every card/upgrade case, three independent shuffle-proposal streams replay
the same complete public evidence using independent private native run seeds:
30 complete-public equality checks. The tests use the existing prefix/reshuffle
proposal implementations, validate their completion and correction calls, and
check the source packet remains unchanged. They do not copy the source hidden
pile or use its seed in the production certificate.

Additional native cases check temporary Dexterity stacking/removal, powered
block, Poison death/Ravenous dispatch, multi-enemy random-target versus all-enemy
attacks, unchanged Shuffle state during the five manual effects, Sly
explicit/automatic discard rejection for both upgrades, and failure guards for
injected draw/generation/result/modifier/power contradictions. These are fixed
mechanism fixtures, not new population data or posterior acceptance estimates.

Previously inspected v7 recipes are reproduced only in tests to locate the exact
measured boundary. The resulting full-cycle audit is:

| Recipe/combat | Assigned blocker crossed | New full-cycle stop | First-cycle observed cards |
|---|---|---|---:|
| 24205 / 1 | Anticipate | combat_owner_ended | 13 |
| 24207 / 1 | PreciseCut | observed_prefix_complete | 12 |
| 24210 / 0 | DeadlyPoison | draw_cycle_play_not_certified:LegSweep | 16 |
| 24210 / 1 | DeadlyPoison | draw_cycle_play_not_certified:LegSweep | 7 |
| 24210 / 2 | Ricochet | draw_cycle_play_not_certified:NoxiousFumes | 12 |
| 24211 / 1 | FlickFlack | combat_owner_ended | 13 |

Recipe 24205 combat 2 still fails its unchanged Pinpoint entry-hook certificate.
LegSweep and NoxiousFumes remain unsupported manual plays in this version. These
remaining stops are explicitly retained; crossing an assigned card does not mean
the entire source root is now cheaply sampleable or complete. No throughput
rerun, broad generation, data admission, or training is part of this change.

The isolated Release checks passed 135 focused cases, 44 additional replay/
reshuffle cases, and 130 draw-history/version regression cases. Their union is
272 distinct passing cases, including 37 new tests. Existing version assertions
were mechanically advanced at four sites in three test files; their behavior
checks were not weakened. Logs are under `artifacts/test-results/draw-five-*`.
