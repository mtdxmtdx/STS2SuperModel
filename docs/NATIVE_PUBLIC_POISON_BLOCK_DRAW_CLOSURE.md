# Draw-prefix v11: the next two measured blockers

`nosl.public-first-draw-cycle.v11` extends the reviewed
[five-card v10 closure](NATIVE_PUBLIC_FIVE_CARD_DRAW_CLOSURE.md) only for LegSweep,
NoxiousFumes, and NoxiousFumesPower. They were the next stopping points in the
same already-inspected source recipe 24210. No additional roots were searched.
The separate commit keeps the v10 review and frozen test evidence identifiable.

## Complete native effect argument

LegSweep is sealed. Its two-energy play first calls ordinary powered
`CreatureCmd.GainBlock` for 11/14, then applies already-reviewed WeakPower to the
selected enemy for 2/3. `GainsBlock` is a read-only declaration. It inherits the
ordinary Discard result and base card listeners. The block, Weak application,
damage scaling, duration tick, and removal callbacks stay within the existing
closed listener set; no card selection, draw, generation, upgrade, insertion, or
reordering occurs.

NoxiousFumes is a sealed one-energy Power card. Its entire play applies
NoxiousFumesPower for 2/3; it inherits the Power-card None result. Removing the
played Power card from combat changes the future discard pool, but the existing
public reshuffle witness already identifies that exact pool. It never assumes
all entry cards return to Draw.

NoxiousFumesPower is sealed, stacks its counter, and overrides only
`AfterSideTurnStart`. When the participants contain its owner, it snapshots
`HittableEnemies` and applies already-reviewed PoisonPower to each. It does not
draw, move cards, consume RNG, or select hidden content. Poison's side-start
damage, decrement, and death callbacks retain the existing source-reviewed
closure. The underlying application/stacking/removal commands are unchanged.
Other poison-amplifying powers, including AccelerantPower, remain unreviewed and
stop the certificate before later draw evidence.

The source-pinned files and SHA256 values are:

- `vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/LegSweep.cs`:
  `a6281879620a8fd1367f3f31c421db08319ee92bfa4c32f1b022ab8906d70bb7`
- `vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/NoxiousFumes.cs`:
  `2d6653f3ba1fd87e53698ce9c8e90748e8ae1cbcae43076ba8f0a50425f9de0c`
- `vendor/sts2-sim/src/Sts2Sim.Core/Models/Powers/NoxiousFumesPower.cs`:
  `58566e0ad44288da7f08d665c42cd682f9b73e331c784508f0aa257c963c373a`

The reviewed inherited paths are `CardModel.PlayAfterObserverStartedAsync` and
its result location, `CreatureCmd.GainBlock`, `PowerCmd.Apply/ModifyAmount/Remove`,
WeakPower, PoisonPower, and the engine's side-start/turn draw ordering. The
certificate still delegates every effect to those native implementations.

## Same derivation and failure boundaries

The proof remains preservation of the current undrawn pile between witnessed
ordinary draws. The new plays/listener mutate only scalar combat values or the
played card's already-public combat membership. Initial/reshuffle multisets and
ID/upgrade prefixes still come solely from complete typed public evidence.
No actual source seed, native hidden pile, or source world enters the condition.

The existing shuffle proposal, finite rational likelihood/envelope, correction
draws, source prior, proposal budget, native replay, and final full-public equality
are untouched. Unsupported transitions still stop at the proved prefix without
removing support or public observations. This condition version is distinct;
the integration owner must advance the central sampler implementation identity
before a new declared collection. The existing v7 corpus remains frozen.

## Native verification

`NativePublicPoisonBlockDrawClosureTests` checks both upgrade forms through real
native block/Weak or Power/Poison effects, end-turn draws, and a witnessed
reshuffle. Three independent proposal streams and independent native seeds per
card/upgrade yield 12 exact complete-public replay comparisons. The existing
proposal objects validate completion and execute their unchanged correction
tests. The source packet is checked unchanged.

The NoxiousFumes test checks that Poison is absent immediately after play,
appears at the next owner-side start, ticks during the subsequent enemy side,
and is added again on the following player side. Two stacked powers affect
both live enemies through that actual timing. The reshuffle pools contain 11
cards after one Power play and 10 after two, rather than incorrectly restoring
the removed Power cards. A synthetic Accelerant contradiction retains the
unreviewed-power rejection and discards no evidence.

The same previously inspected 24210 root now reaches:

| Combat | Next blocker crossed | Initial cycle | Full-cycle endpoint |
|---|---|---:|---|
| 0 | LegSweep | 16 cards | combat_owner_ended at event 156 |
| 1 | LegSweep | 17 cards | combat_owner_ended at event 278 |
| 2 | NoxiousFumes | 17 cards | observed_prefix_complete at event 359 |

An owned native replay uses that same diagnostic recipe with independent
proposal randomness, conditions every newly certified initial/reshuffle target,
validates the owning tape, and reproduces every public packet field. This is a
native wiring fixture, not a posterior or throughput experiment. The separate
12 replays vary both private native seed and proposal randomness.

Pinpoint and other entry hooks remain unsupported. No timing cohort, fresh
source search, training data, optimizer step, or production admission is created
by this addendum.

The final isolated Release run passed 280/280 focused cases, including all 45
new v10/v11 cases, existing draw-history/Slime/Fairy regressions, finite shuffle
and unordered-pair correction tests, public reshuffle/native replay suites,
conditioned-word failure guards, and public-prefix checks. The final log is
`artifacts/test-results/draw-eleven-final/draw-eleven-final.trx`. No simulator or
central sampler version file is modified by this addendum.
