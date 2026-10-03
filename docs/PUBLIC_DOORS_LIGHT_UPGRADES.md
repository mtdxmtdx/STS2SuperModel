# Public Doors LIGHT outcome proposal

This extension recognizes the exact unlocked, unpriced `LIGHT`, `DARK` first page
and conditions the unordered pair of upgrades published by its `LIGHT` settlement.
The input is detached public evidence under the existing independent Rewards or
Map/Rewards tape prior. It does not consume a source seed, recipe, physical-card
identity, private inventory, or hidden event eligibility. Unsupported evidence
disables the upgrade accelerator and leaves ordinary replay available.

## Identity and public certificate

`DoorsOfLightAndDark.GenerateInitialOptions` emits precisely this pair. It is the
sole `LIGHT`/`DARK` first page in the complete pinned Overgrowth, Underdocks and
shared catalogs; the exhaustive competing-page review is recorded in
[the BrainLeech certificate](PUBLIC_BRAIN_LEECH_CARDS.md). Doors belongs to the
Underdocks pool and inherits `EventModel.IsAllowed == true`. The existing event
permutation proposal retains its complete pool, cursor/visited rules, speculative
eligibility masks, bounded-trial law and root-constant correction. The added
signature only extends its fixed necessary public event predicate.

The upgrade certificate requires a complete uninterrupted prefix, the immediate
preceding ordinary map owner choosing an Unknown node, complete before-assets,
a top-level act-zero event, its exact page and selected LIGHT, then its immediate
completed settlement with complete after-assets. Every non-deck asset must be
unchanged. Repeated public Doors visits decline this accelerator: native event
entry recreates the same event RNG, so visits cannot be multiplied as independent
fresh factors. Runtime freshness checks remain mandatory even for one visit.

The reviewed plain-card catalog is StrikeSilent, DefendSilent, Neutralize,
Survivor, Acrobatics, Blur, DeadlyPoison and AscendersBane. Sealed implementations
were checked, including generated Strike/Defend specs and both upgrade branches.
The normal cards have maximum upgrade level one; AscendersBane has maximum zero.
Upgrade changes only the selected card's level, local cost/keywords or numeric
fields. CardCmd.Upgrade/CardModel.Upgrade dispatch no inventory listener or RNG.
The prototype after zero/one native upgrade must exactly match the complete public
card snapshot. Afflictions, enchantments, temporary modifiers, unreviewed cards,
or unknown listeners decline conditioning. The existing reflection-based event
inventory closure excludes room-entry, event-selection, unknown-room and
card-creation modifiers, checking the entire public deck/relic/potion inventory.
RingOfTheSnake's combat draw hook and NewLeaf's already completed pickup action
do not run at this boundary.

The after-deck multiset must be exactly the before-deck with two upgradeable
card signatures replaced by their native upgraded signatures. Already upgraded
and non-upgradeable cards remain present. Deck size, target identities and physical
multiplicities are read from this public proof, not fixed to a source fixture.
Fewer than two upgradeable cards, a non-upgrade delta, or any other changed asset
declines the accelerator.

## Native law and unordered correction

`DoorsOfLightAndDark.LightAsync` obtains every `IsUpgradable` deck card, calls
`StableShuffle` with the event's own RNG, upgrades the first two cards, then finishes.
StableShuffle sorts by native ModelId then current upgrade level, followed by the
original descending Fisher–Yates loop. An N-card pool consumes all N-1 index words;
there are no rarity, card-generation, resource or upgrade rolls. The event RNG
belongs to the full-state tape partition. Runtime checks use that owned native
object, never an inferred or retained source seed.

For selected signatures a,b with multiplicities m_a,m_b, let s be one if a=b,
otherwise two. Under the auxiliary uniform physical-permutation proposal,

    F = s * m_a * (m_b - [a=b]) / (N*(N-1))

The wrapper chooses either distinct signature order with exactly probability 1/2;
identical signatures have only one order. `ConditionalShuffleProposal` then
samples every compatible physical copy and every unobserved suffix. It retains
each original native index bucket and discarded low raw-word bits. For D=2^53,

    native/proposal = F * product(n * bucket_size(n,index_n) / D), n=N..2
    envelope        = F * product(n * maximum_bucket_size(n) / D), n=N..2

The envelope depends only on the public pool and pair, not their sampled order,
physical copies, suffix permutation or hypothetical run. The wrapper scales both
ordered-prefix ratio and envelope by s and reuses its exact rational rejection
correction. F is not asserted to equal the native pair probability. Reduced-bit
finite tests retain the null proposal atom for physical paths with no native
bucket support rather than resampling them away.

For the inspected public example, N=14, with one Blur and five StrikeSilent copies,
so F=5/91. Both Blur/Strike and Strike/Blur and every physical Strike copy remain
supported. This example supplies no production sampler input or special case.

## Ownership, completion and failure

The existing label shuffle callback runs after native stable sorting. It checks
the owned run, event object/RNG, floor, singleton player, linked observed LIGHT
choice, complete native before-assets, and every physical deck card in the sorted
upgradeable pool. The public signature multiset must match and physical references
cannot repeat. The full-state forcing machinery checks every expected RNG state,
freshness, replay consistency and complete word consumption. Native shuffle and
upgrade commands still perform all work.

An optional `IAbortableLabelShuffleBoundary` receives native shuffle failures.
Rng.Shuffle's loop and draw order are unchanged. Only participating label scopes
receive abort notification and protected cleanup, so a secondary abort/disposal
error cannot replace the original native exception. The Doors proposal captures
failure before aborting only its incomplete-word disposal check; it remains
sticky-failed and cannot complete or correct. Normal and no-op scopes preserve
native permutation, generator bytes and counter.

Shuffle completion is insufficient for acceptance. The proposal also waits for
the linked completed public owner and exact full after-assets, observed only after
the existing public-prefix comparison succeeds. All remaining public replay
constraints stay active. Tape recreation carries the same condition; audit-only
diagnostics report eligibility, target count, conditioned upgrades/shuffles and
actual native/proposal ratio/envelope, including failure cleanup.

## Verification boundary

Focused checks cover literal finite native permutations for mixed/same-signature
pairs and duplicate copies, exact corrected densities and low bits, varied native
decks and existing upgrades, missing/changed public evidence, unsupported content,
foreign or repeated ownership, early/later full-state aliases, incomplete draws,
missing settlement, native failure after the last draw, cleanup exception identity,
and ordinary/no-op scope equivalence. A fixed inspected source fixture verifies
event identity admission, exact packet equality and settled continuation/fork.

These are law and lifecycle checks. Frozen v5 binaries, reports and corpus are
unchanged. No new benchmark, untouched-cohort launch, dataset, fit, or production
acceptance claim is part of this change.

The local focused regression passed 96/96 with no skips, and the existing Core
Random suite passed 37/37 with no skips. Logs and TRX files are retained under
`artifacts/validation/doors-light/` (`focused-regression.log`,
`doors-focused-regression.trx`, `core-random.log`, `doors-core-random.trx`). These
counts are focused verification, not a full solution or full Core test result.
