# Public RoomFullOfCheese card conditioning

`NativePublicEventCardCondition` is a detached public-evidence accelerator for
the eight cards displayed by `RoomFullOfCheese.Gorge`. It does not change which
sources are eligible or create an event identity. The caller still performs full
public-history equality, other component corrections, and ordinary failure and
unresolved accounting.

## Certified native boundary

The condition requires a complete run prefix with no preceding evidence gap, an
immediate completed public map owner with before-assets, a top-level event owner
at the next floor, the displayed `GORGE`/`SEARCH` options, a recorded `GORGE`
choice, and a matching child outside-choice owner. Its public card reveal must
contain all eight ordered candidates, select exactly two, and identify
`RoomFullOfCheese` as the source. Event owner, choice owner, reveal ordinal,
act, floor, source, and before-assets are retained in each target. Chosen cards
are never used to reconstruct missing candidates.

`RoomFullOfCheese.Gorge` calls `CardFactory.CreateForReward` with the character
pool, a common filter, and uniform noncombat options. In the pinned native
implementation, `ForNonCombatWithUniformOdds` already sets `NoUpgradeRoll`;
adding `NoRarityModification` preserves that flag. This event consumes one
identity word per displayed card, with no rarity or upgrade word. Other sources'
ordinary upgrade words and all later ordinary Rewards words remain untouched.

The public inventory closes the native hook listener set:
`RunState.IterateHookListeners(null)` enumerates relics, potions, and deck cards.
The certificate conservatively includes melted relics and requires inherited
no-op room-entry, map-event selection, creation-option, and result-modifier
hooks, including both late-option overloads. Therefore public before-assets
remain unchanged through event entry and all card identities come from one fixed
all-unlocked solo character common pool. This deliberately declines inventories
with unreviewed overrides, even when a particular override might do nothing for
these flags. Declining the accelerator must fall back to ordinary generation;
it must not remove that source from the source catalog.

The owned hypothetical callback also verifies exact public before-assets,
character, act/floor, top-level native `RoomFullOfCheese`, selection index,
flags, uniform branch, RNG provenance, and the ordered remaining pool. It reuses
`LabelRewardCardSelection`; no new engine hook or simulator behavior is needed.

## Exact proposal and fixed envelope

For each slot, native `Distinct` is applied before selection. Every occurrence
of a selected `ModelId` is removed from subsequent slots. This differs from
removing just one reference; the certificate preserves the native rule even
when distinct objects share a ModelId.

Let D = 2^53. At slot s, sum the exact `floor(n * u)` bucket sizes of every
position matching the displayed base identity and divide by D to obtain z_s.
`NativeRewardIdentityMath` samples its full 64-bit preimage, including the
unconstrained low 11 bits. The proposal ratio is Z = product(z_s), including
all eight displayed identities and all certified offers. Since the pool and
exclusions depend only on the detached root and pinned native catalog, B = Z
is a root-wide constant, not a bound learned from a hypothetical world.
Consequently the event-card correction Z/B is exactly one. For every supported
raw-word sequence q(w) Z = p(w).

The callback requires fresh owned Rewards-lineage cells. It refuses already
visited aliases before any forced write, and validates a one-word native cursor
advance after each slot. A failed, skipped, repeated, or incomplete boundary
cannot pass `ValidateCompletion` or `AcceptCorrection`. Missing offers, unknown
hooks, inventory mismatches, changed draw contracts, and execution failures must
remain explicit failures/unresolved work under the caller's existing accounting;
they are not absent roots or successful posterior draws.

## Integration API and verification

Create with `TryCreate(root, prior, out condition, out reason)`. Construct
`NativePublicEventCardProposal` with the owned Rewards oracle and independent
proposal-word source, attach the hypothetical run once, and chain
`BeginSelection` after other disjoint card-selection components. Validate
completion and apply correction alongside existing components. Counters expose
conditioned cards and completed offers; ratio and envelope are exact rationals.

Focused tests enumerate reduced finite-word native laws, including duplicate
positions, native Distinct, full ModelId exclusions, and an unpicked-card
constraint. An actual native event fixture records all eight visible candidates,
conditions an independent Rewards oracle, and replays the complete public event
and an ordinary continuation draw. The fixture explicitly constructs an event
path; it makes no natural-source reachability claim. Negative tests cover
missing/unordered candidates, wrong option, absent before-assets, unsupported
hooks, duplicate targets, legacy provenance, aliases, before-assets mismatch,
and a changed native draw count. No broad generation or training is involved.
