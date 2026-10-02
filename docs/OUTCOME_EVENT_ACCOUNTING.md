# Committed outcome diagnostics

The optional native observer records committed player HP and potion inventory
changes. It does not execute rules, schedule hooks, consume RNG, or publish new
policy history. Native outcomes and RNG streams remain the authority.

## HP accounting

`Creature.LoseHpInternal`, `HealInternal`, `SetCurrentHpInternal`, and
`SetMaxHpInternal` notify the player-bound sink after committing their native
values. The private ledger retains the operation and before/after HP and maximum
HP for every transition. Constructor/restore and clone reconstruction mutations
occur before binding and are outside the combat interval.

`CumulativeHpDamage` is actual HP removed by the loss mutator, including lethal
loss but excluding excess overkill. `HealingReceived` is actual positive healing,
excluding overheal. `OtherHpAdjustment` is the signed HP delta from direct sets,
maximum-HP caps, and a negative heal mutator call. Completeness requires an
uninterrupted ledger and exact reconciliation:

`start HP - damage + healing + other adjustment = settled HP`

The objective still uses fixed start HP and settled HP. No diagnostic quantity
is an additional reward or cost. Missing legacy adjustments remain null, and
legacy incomplete records and saved archives are not relabeled.

## Potion accounting

Notifications occur only after successful slot additions/removals. Native
`PotionCmd` supplies the removal cause, so manual and automatic use both record
consumption, including consumption followed by an effect exception. Discarding
records discard; unsuccessful procurement or removal emits no movement.
`generated` denotes actual acquisition into inventory during the interval; it
does not claim a specific generator or inspect unclaimed reward offers.

An unclassified direct removal is retained as `removed` and makes resource
provenance incomplete. Otherwise, each prefix must have nonnegative quantities,
and start inventory plus acquisitions minus consumptions/discards must equal
settled inventory. These movements are diagnostics; start/end inventory remains
the sole input to inventory valuation.

## Lifetime and branches

Bind immediately after the fixed start snapshots and before room setup effects.
The binding lives on the player independently of `Creature.CombatState` and
survives native combat detach. Copy accumulated immutable entries and running
totals into each branch, then bind only after native graph cloning has finished.
Native `Player.CloneForCombat` never copies the observer reference.

The session seals its ledger at its automatic-settlement boundary. The natural
driver notifies before reward choices; no-reward forced combats notify after
their automatic event return. Recording stops before optional reward claims.
Source collection and session disposal detach in `finally`; interrupted tracking
cannot become complete merely because its end snapshots happen to match.

The remaining unobservable facts are counterfactual/future outcomes and identities
of unclaimed hidden offers, none of which this ledger needs. HP changes and owned
potion movements were previously uninstrumented, not intrinsically hidden.
Unknown valuation/calibration is independent of event provenance completeness.

## Verification

Focused tests exercise lethal overkill and Fairy revival, capped healing and
post-detach victory healing, direct HP/max-HP changes, Entropic Brew generation,
discard, committed consumption followed by failure, native source and conditional
choice continuations, independent clone prefixes, unknown removals, and matching
native outcomes/recorder output/RNG with the observer enabled or disabled.
