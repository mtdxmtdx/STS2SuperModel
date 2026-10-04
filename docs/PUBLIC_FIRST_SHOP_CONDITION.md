# Complete public first-shop proposal

This optional accelerator retains the existing Rewards v1 and Map v1 priors. It
uses detached complete public evidence, including every unpicked card, relic,
potion, price, service and leave action. It does not take source native state,
source seeds, source RNG traces or a selected hidden bag. Unsupported histories
keep ordinary rejection; the frozen corpus and declared prior do not change.

## Public closure

The certificate supports the first directly mapped shop in act zero, before
floor 38, in a single-player Silent A10 run. Neow selected PreciseScissors. Earlier
combats are directly mapped normal combats; intervening events choose Gorge.
Earlier relic offers and unreviewed owners are rejected. Each prior map choice
must be Monster followed immediately by a complete parentless Combat owner, or
Unknown followed immediately by a complete parentless Event owner. The latter
owner must separately satisfy the Gorge certificate. A missing room, a next Map
owner after an unknown point, and every other prior node type decline support.
This is conservative source closure, not an assumption that all treasure bag
pulls emit offer evidence or that unchanged owned inventory proves no bag pull.
Pinned TreasureRoom directly grants its relic; there is no native skip action.

The immediately preceding map settlement certifies the complete before-assets,
with RingOfTheSnake and PreciseScissors and no relevant room or merchant hooks.
The complete leave settlement has identical public assets. Nevertheless shop
stock is not behaviorally dead: generating it removes displayed relics from both
bags and consumes persistent RNG streams.

`RelicCmd.Obtain` removes the Ancient PreciseScissors from the shared bag where
applicable, but Ancient relics are absent from the player's four ordinary buckets.
`RoomFullOfCheese.Gorge` generates common cards without touching relic bags.
Cards and potions have no relic insertion/removal paths in the pinned catalog.
Normal combat rewards have no relic offer without a modifier, and the public
inventory plus owner closure excludes those modifiers and earlier consumers.

Early single-player shop eligibility is root-fixed. Audited `IsAllowed` overrides
use the early floor gate or single-player test; MassiveScroll is always excluded.
`IsAllowedInShops` is also respected. An unreviewed override declines the fast
path. All three public relics must be distinct supported identities, with two
ordinary-rarity slots followed by the Shop-rarity slot.

## Whole player-bag law

The label-only boundary in `RunState.CreatePlayerRelicGrabBag` occurs after
ordinary shared-bag construction and before all player-bag shuffles. Native
bucket order, each Fisher-Yates loop, and all later removals are untouched.

For a bucket with n unique relics, m eligible relics and k ordered public back
draws, first sample a uniform whole physical permutation. Keep all ineligible
positions and their order. Delete target identities from the eligible subsequence
and append their reverse draw order. Each compatible permutation has exactly
(m)_k preimages, so the proposal is uniform over compatible whole permutations.
The remaining eligible order and every ineligible interleaving remain random.

Native Fisher-Yates is not assumed exactly uniform. Invert the proposed whole
permutation to its unique descending swap-index sequence; sample each raw word
uniformly from the exact double-to-integer bucket, with unrestricted low bits.
For Q=2^53 and selected native bucket size b_j:

    p/q = 1/(m)_k * product(j * b_j / Q)
    B   = 1/(m)_k * product(j * max_bucket_j / Q)

The existing exact bucket correction accepts with (p/q)/B. B is fixed by the
public root and catalog. Untargeted buckets use ordinary independent raw words.
The shared bag order is unconditioned. Native
`RelicFactory.PullNextRelicFromBack` removes each displayed identity from the
player bag and calls `RemoveFromSharedBag`. No remaining bag is overwritten.
Thus future pulls retain the correctly conditioned complete remaining order.

## Joint stock and prices

The initial `MerchantInventory.Generate` label boundary supplies 28 Shops words:
one discount slot, seven card identities, eight card prices, three relic prices,
six potion rarity/index words, and three potion prices. Seven Rewards cells are
conditioned: character-card rarities at offsets 0,2,4,6,8, and relic rarities at
12,13. Seven native merchant upgrade words remain ordinary. Native generation
still performs every draw, clone, odds lookup, price conversion and bag removal.

The hidden sale slot is sampled proportional to its native slot-index mass times
the joint probability of all seven observed prices. Its discarded full-price
word remains uniform; the sale recalculation still consumes its own native word.
All three distinct potion identities precede all three potion price draws.

Card identity probability sums compatible rolled-rarity fallback branches and
all matching index preimages. Native shop rarity thresholds include the current
rarity offset and do not update it. With no public offset certificate, a safe
root-fixed envelope is the maximum candidate-index mass over rarity branches for
each character slot; the colorless identity masses are fixed. All fixed potion,
relic-rarity, discount and price factors enter the envelope exactly.

When `NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner` certifies
the entire public prefix before the Shop owner, the exact native float offset
is reconstructed from every earlier displayed reward card. The shop certificate
then uses the exact summed native rarity/fallback/index mass for each character
slot, with a runtime equality guard on that offset before stock forcing. Failure
to certify the prefix preserves the conservative envelope. A sampled hidden
offset is never substituted for a root-fixed certificate.

Price preimages include the actual double-to-float range conversion, native float
multiplication, banker's rounding, and sale integer division by two. The full
finite preimage is found by monotone binary search. No price or unpicked offer
is ignored.

## Ownership and integration

Construct `NativePublicShopProposal` using the owning Rewards oracle's
`WasVisited` and `ForceFresh`, an independent proposal RNG, the tape's
`ForcePrefixWords`, and its `CaptureConditionedFailure`. Enter `EnterScope()`
before fresh run/player construction. `AttachHypotheticalRun` belongs after
`AddPlayer`: it verifies the same run completed this proposal's native player-bag
construction. Include `ValidateCompletion` and `AcceptCorrection` in the tape
lifecycle and reconstruct the proposal with its same seed during exact replay.

Full-state and Rewards freshness/alias guards are mandatory. Extra, skipped or
restored draws, foreign owners, incomplete callbacks and disposal errors fail
completion. Native failure aborts dependent validation without replacing its
original exception. Fatal callback errors flow to the tape's sticky failure sink.
Complete public-history comparison remains mandatory after all corrections.

## Verification scope

Finite tests exhaust independent small raw-word permutation domains, including
two back draws from one bucket, two blocked relics, every blocked-relic position
and both blocked-relic relative orders, and verify exact p/q and its fixed bound.
Price bucket endpoints and adjacent words are checked
against actual native `Rng.NextFloat` and
`MerchantCardEntry.CalculateDiscountedPrice`.

The source-24008 lifecycle fixture uses detached public DTOs to condition new whole
bags. It checks all stock and prices, removal from both bags, variation in
remaining order, all 14 native Rewards draws, and an ordinary next Shops cell.
This is same-recipe native lifecycle verification, not fresh posterior acceptance
or production admission. The conservative stock correction was approximately
0.08729955 in the verified baseline; the optional exact public-offset proposal
has mathematical stock correction one, leaving only finite-word bag correction.

Fresh validation on the verified archived-source baseline `0fa856b`, with the
reward-history dependency `bc002aa` (locally cherry-picked as `e22cc9c`), passed
all 19 focused shop tests and 81 native Merchant/RelicGrabBag regressions, with no
skips. These are new results, not inherited claims from the lost workspace.

The additional checks cover pass-through label scopes preserving the complete
native inventory, both bags and RNG snapshots; missing conditioned words,
restored full-state draws, disposal failures and Rewards aliases remaining
fatal; and native draw exceptions aborting each boundary without replacement.
The missing-room fixture removes the complete Event/OutsideChoice pair and
renumbers the remaining Map/Shop transcript, so DTO validation succeeds before
the shop certificate declines it. Old common-seed priors remain unsupported by
this accelerator.

This is component validation. Owning tape/source integration, complete replay
comparison and fresh posterior throughput admission remain separate gates; no
production corpus or learning run is authorized by these tests.
