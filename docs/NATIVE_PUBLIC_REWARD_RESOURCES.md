# Public past-combat resource proposal

This development-only helper conditions already displayed gold and potion offers in
an owned hypothetical run under the explicit hybrid Rewards-provenance tape law.
It does not change resource utility, price gold or potions, predict future bonuses,
use a source seed/graph, or change production-generation or training admission.
The whole detached public prefix still has to match before accepting the world.

## Owner and scope

`NativePublicRewardResourceCondition.Create(evidence, cards)` revalidates the
existing `NativePublicRewardCondition` combat-index / first-offer mapping. Only
that first displayed snapshot of each certified primary card reward contributes
resource factors. It reads primary keys `gold` and `potion`; absent keys mean no
positive gold / no displayed potion. Refreshes, rerolls, appended extras, later
owners, custom potion-only rewards, and every other recorded fact stay in the
complete public equality check. No extra owner is inferred from a floor number.

`NativePublicRewardResourceProposal.BeginCombatReward` enters an optional resource
callback scope alongside the card proposal. Dispose its returned scope before the
card boundary completion. The owning tape must use a separate auxiliary proposal
RNG, reconstruct both helper and proposal for exact replay, validate completion,
and compose `AcceptCorrection` with its other factors. If `HasFailed` is true,
abort the dependent active card boundary before disposing it: a resource rejection
before card generation must keep its original exception rather than be replaced by
a missing-card-slots validation error. The failure flag survives resource cleanup. `AbortActiveBoundary()` marks an
active resource boundary failed when its owning native reward generation aborts.
If resource boundary creation throws after card boundary creation, abort/dispose
the card boundary in that creation catch and rethrow the original failure.
The caller must retain fatal errors,
zero-support public nonmatches, and computational null outcomes in their existing
separate accounting. This module supplies no retry or admission policy.

## Exact probability factors

Let D = 2^53, the native high-word domain; all unused low 11 bits remain uniform.

* Potion presence uses the actual native float threshold after the force hook.
  Its strict comparison is counted exactly after double-to-float conversion.
  Forced presence consumes no word and leaves native pity unchanged. Other
  presence draws execute normally, including their native +/-0.1f pity update.
* Potion rarity uses native inclusive comparisons `<= 0.1f` and `<= 0.35f`.
  These preimages differ from the card proposal's strict rarity boundaries,
  including all values that round to exactly a boundary. Index buckets use the
  unchanged native double conversion, pool order, and candidate multiplicity.
* The character-plus-shared unlocked potion pool is root-constant. Each native
  runtime pool is checked against that ordered catalog; the rarity-weighted
  identity mass is therefore a valid constant envelope factor for a displayed
  potion. The native rarity and identity draws still run. Empty rarity pools
  return null and consume only the rarity draw, with no invented fallback/index.
* For displayed potion identity x, the joint likelihood is p*z(x), where p is
  the latent presence probability and z(x) is the catalog identity mass. The
  proposal uses bound z(x), leaving p in the rejection correction. Bound one for
  latent presence deliberately forgoes any unproved pity-history speedup.
* An absent potion can mean no reward, or a present reward whose rarity pool is
  empty. If z0 is the null-identity mass, the proposal retains both native paths,
  with weights (1-p) and p*z0. Its total native/proposal ratio is
  (1-p)+p*z0. Native pity follows the sampled presence path, preserving later
  likelihoods. The absent-potion envelope is one.
* Gold uses the actual native inclusive range after Poverty adjustment, encounter
  override, float proportion multiplication and banker's rounding. The proposal
  samples the exact integer preimage. Missing displayed gold means Amount <= 0.
  An omitted zero-proportion slot consumes no draw; fixed and equal-bound rewards
  still consume their native one-word draw. The actual range determines density,
  never the root-wide envelope by itself.

Every forced word must address a fresh tagged native Rewards cell. A prior read,
changed replay override, untagged RNG, incorrect native draw count, pool drift or
uncompleted target remains an explicit proof failure. Callbacks reuse the native
`LabelRandomScope` interception guard so auxiliary RNG draws and nested native
calls made inside a callback cannot accidentally consume hypothetical tape cells.
No native effect, mutation, pity update, upgrade roll, card order, or reward order
is rewritten.

## Gold envelope certificate

The fallback bound is one. It is valid for event rewards, fixed payouts, omitted
rewards, unknown map nodes, or contexts without an adequate detached certificate.
It moves native range rejection into density correction and by itself promises
no aggregate acceptance improvement.

A tighter certificate applies when a public Monster/Elite/Boss map selection is
immediately followed, apart from owner-end records, by a top-level combat owner
on the next floor in the same act. The existing card owner certificate establishes
its observed Victory/first-reward boundary. Pinned `RoomFactory` maps those visible
node types directly to ordinary combat rooms with no event fixed-gold override.
The certificate checks every possible encounter catalog for that act: both
Overgrowth and Underdocks for act zero, Hive for act one, Glory for act two. Native
encounter bounds must retain defaults. The only pinned custom proportion,
GremlinMercNormal, is in {0, 1/2, 1}; default proportions are 1-escaped/spawned.

Victory does not prove that no creature escaped. Instead, the certificate covers
every IEEE float proportion in [0,1]. It binary-searches all transitions of each
native rounded bound over ordered positive float bit patterns and enumerates the
resulting finite set of (min,max) pairs. Its envelope is the largest exact bucket
mass of the public amount across that entire set. Thus neither the current
hypothetical encounter nor its observed runtime bounds select the envelope.
Runtime boundary/range guards check certificate assumptions before use. The
fallback remains available wherever this detached direct-map certificate is absent.

## Validation

Focused tests exhaust small finite presence/rarity/index and gold domains, retain
latent pity branches including present-but-null potion rewards, compare inclusive
float boundary neighbors through the actual native RNG, and cover omitted,
equal-bound, fixed, negative/zero, Poverty and scaled-encounter gold ranges.
Native RewardsSet execution composes resource and card proposals for
StrengthPotion, ColorlessPotion, WeakPotion, forced WhiteBeastStatue presence,
absent potions, fixed60, omitted0, boss75, and half-scaled encounter gold. Exact
replay checks all cards, upgrades, potion/gold output, both pity states and stream
counters. Detached real native evidence exercises the direct-map certificate and
first-snapshot mapping. Guard tests reject reused Rewards cells and wrong
provenance, and verify callback interception suppression/restoration.

These establish the ideal-oracle proposal law and native execution contracts.
Domain-separated finite seeded generators remain an implementation of that law,
not a new proof of exact inference under a finite-seed distribution. Owning-tape
whole-prefix/continuation checks belong to integration; sampled completion rates
must retain their predeclared source-root and world denominators.
