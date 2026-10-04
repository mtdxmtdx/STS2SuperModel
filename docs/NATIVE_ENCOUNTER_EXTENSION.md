# Native slug, rat and RubyRaider bridge

The unchanged fixed 200-root native cohort now admits all **200 roots**. Its
**2,692 / 2,692** T0 action-world continuations reach automatic settlement, adding
26 roots, 235 candidates and 470 settled worlds to the v6 result. All 200 public
inputs and source traces, and all 174 previously supported target payloads, are
exactly unchanged. See [the summary](../NATIVE_ENCOUNTER_EXTENSION_SUMMARY.json).
This is bounded development evidence, with no formal labels, corpus promotion,
fitting, optimizer execution or new source-seed search.

## Version and supported boundaries

The audit dispatcher is `nosl-belief-dispatch-v7`. Existing profiles retain their
identities and behavior. These three encounter families use
`native-public-entry-reviewed-encounters-exchangeable-v1`, or
`native-public-entry-reviewed-encounters-conditional-choice-v1` for an owned
reviewed-card choice origin. The unchanged native-import markers continue to
distinguish detached stable clones from owned-origin choice replay. Policy DTOs,
source collection, actual Silent/A10 checks and prior semantics are unchanged.

The prior is still conditional draw permutation with independent future RNG,
not inference over a finite run seed. Current published intent and all certified
monster memory are preserved. Hidden draw order, permanent deck order, all future
run/player/monster streams and unobserved reward pity are replaced in coupling
tests. The source graph remains unchanged through sampling and settlement.

`NativeEncounterMemory` deliberately dispatches separately from the frozen older
memory family. It validates every intent publication, including repeated
publications in one player turn without inventing an additional monster move.
Unknown state remains unsupported:

- CorpseSlugsWeak requires its two initial lifetime slots and their public-derived
  starter rotation, complete normal move log, and Ravenous amount five. A killed
  ally during the current player turn publicly determines IsRavenous, STUNNED and
  the saved follow-up. The native transient state is not appended to StateLog;
  its follow-up is the previous log entry, not that move's ordinary successor.
  The bridge checks the unperformed mandatory-stun flag, saved ID and current
  machine binding. Earlier-turn allied deaths and enemy-phase deaths are rejected
  for import because move-start/end telemetry is insufficient to infer their
  timing. The fixed cohort has 16 slug roots, including one current live stun and
  one Survivor choice
- TwoTailedRatsNormal accepts only turn one of the initial three-rat encounter.
  Public A10 attack damage distinguishes Scratch 9 from Disease Bite 7; both have
  the same Attack/one-hit shape. This baseline's native preview reads DamageCalc
  directly, so current Weak/Strength never reinterprets historical damage. The
  bridge checks the starter rotation, one-entry logs, no performed first move,
  countdown two, backup count zero, factory slots third/fourth/fifth, initial
  spawn history and the five-slot future sorting order. Later and summoned-rat
  roots remain unsupported. The cohort has eight roots, including one Survivor
  choice
- RubyRaiders accepts one each of Brute, Axe and Assassin, in the public factory
  ordering. These models have no extra private counters. Complete deterministic
  paths identify Axe's otherwise identical SWING_1 and SWING_2 phases. Other
  RubyRaider archetypes remain unsupported. The cohort has two turn-one roots

## Native continuation and potion closure

Import restrictions do not alter execution inside an already sampled world.
Stuns, enemy death ordering, future summons and settlement remain owned native
engine operations. CombatState.IsLiveCombat uses the live engine flag, including
projections; native rat summons therefore execute normally. Clone state already
preserves the transient slug clone factory, rat scalars, physical slots, spawn
history, creature IDs and encounter sorting order. Public targets retain their
lifetime ordinals when rat summons reorder the physical roster.

Only two read-only Core checks were added: the rat summon-countdown getter and an
encounter-slot sequence predicate. No monster rule, transition probability, RNG,
clone implementation or observer event changed.

RavenousPower uses its public amount and owner; its extra state is the checked
slug memory. FrailPower has no private counter or retained reference beyond the
existing public power state. New power IDs are allowed only in the relevant
encounter certificate. Ordinary previously reviewed held potions stay legal:
the test suite explicitly covers a FirePotion death during player play and a
PowderedDemise death after the survivor's enemy move. Both retain the correct
saved continuation through an exact fork and settle with actual/projection parity.
Held AttackPotion/PowerPotion priors are explicitly rejected for these new
families until their combined descendant interactions receive separate evidence;
none of the 26 fixed roots has such a prior. Generated-potion pending imports
remain blocked everywhere.

## Evidence and reproduction

The fixed request is copied byte-for-byte from the retained v6 recovery request:
100 maximum runs, 12 floors, 200 roots, eight per combat, unchanged
`nosl-m5-natural-proof-20261001` prefix, T0 seeds 101/102 and 200 rollout decisions.
The full response and test logs remain ignored under
`artifacts/native-encounter-proof-200-v7`; the summary records their hashes.

Eleven new targeted cases cover all 26 native imports, both owned Survivor choices,
hidden-state replacement through settlement, source isolation, mutated private
roles/counters/physical slots/logs/stun follow-up, missing and repeated public
intent publications, both potion-induced stun timings, real future rat summons
with post-summon cloning, and the repeated Ruby axe phases. Historical family tests
retain their original assertions by explicitly leaving the new families to this
suite. The complete NOSL suite passes 1,059 / 1,059 cases; the separate
worker/public-policy protocol smoke also passes. An independent source review
and complete retained-artifact comparison found no material defects. The full
vendored Core suite passes 4,480 tests with zero failures and three documented
opt-in cases skipped. Both vendor patches reconstruct all 2,227 tracked Core/test
files and the exact file set from pinned upstream, independently verified. Explicit
negative tests cover generation-potion entries and delayed/earlier slug deaths.

Use the isolated .NET 9 build flags
`--artifacts-path artifacts/native-encounter-extension-build -m:1 -nr:false
-p:UseSharedCompilation=false -p:NuGetAudit=false`. Frozen worker binaries and old
default/corpus artifacts are not modified. All 2,692 outcomes retain complete HP,
inventory, resource provenance, permanent-change and settlement diagnostics. Of 1,346 action targets, 632 have
empirical objective values and 714 remain objective-value masked. Mechanics
coverage does not establish preference sufficiency or authorize training.
