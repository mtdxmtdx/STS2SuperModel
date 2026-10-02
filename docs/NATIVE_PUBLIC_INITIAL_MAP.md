# Typed public initial-map conditioning

The Rewards hybrid sampler can now condition the native initial map without the
old second-combat/first-reward certificate. `NativePublicInitialMapCondition`
receives only detached `PublicRunEvidence` and the declared prior. Its necessary
event is the exact first observed map choice slice and chosen coordinate. Native
generation still constructs every map point, topology pass, prune and repair.
The unobserved map remains latent. This is a proposal accelerator, not a new
prior, root filter, content subset or production-data admission mechanism.

## Public certificate and native timing

The certificate requires the declared fresh Silent A10 hybrid execution and one
of its two reviewed outside-combat scripts. The typed prefix must show:

1. The native run start with the RingOfTheSnake starter, then the first complete
   act-zero/floor-one Event owner
2. Its initial visible options and linked first-positive Neow choice
3. Completed Neow, with no intervening gap, other room or map owner
4. The first act-zero/floor-one map owner, an Ancient current node in row zero,
   and the displayed connected Monster destinations in row one, followed by the
   linked chosen coordinate

Nested Neow reward/card-choice owners are allowed. Missing boundaries, a gap
before that map slice, an unknown Neow positive, an observed GoldenCompass, or
an unsupported first map shape disable this certificate. Later gaps do not
erase an earlier certified slice. The entire original root and requested content
catalog stay in ordinary native replay whenever this accelerator is unavailable.

The native `RunState` constructor generates the map before adding any player.
Its generated-map hooks therefore have no player models. The public map callback
comes later, after Neow has resolved. The reviewed Neow positives neither replace
the map nor change point types. SmallCapsule's reward requests only
Common/Uncommon/Rare relics, with Circlet as the empty-pool fallback;
GoldenCompass, the map-replacement relic, is Ancient. LavaRock grants its extras
at the act-zero boss, after this slice. Card and potion grants at this boundary
do not rewrite topology. This source-pinned closure must be reviewed when native
content changes; unreviewed positive IDs deliberately fall back.

WingedBoots changes the offered destinations. On this first step every surviving
row-one Monster is connected to the Ancient parent, so it changes only the
native option enumeration order: the full next row versus `Children`. The
certificate preserves that order and the deterministic source choice. HP cannot
change the first choice because all destinations are Monsters. Later Boots
charges, route choices, encounter types and Unknown room rolls remain native.

The recorder and the predicate share `NativePublicMapSlice.Observe`. It projects
only the current node, offered destinations, and ordinary edges between those
visible nodes. `Unknown` stays `Unknown`; it never reads a resolved hidden room.
No source graph, source seed, private encounter catalog, or reconstructed act
identity is a certificate input. The typed channel publishes act index, not the
content identity of act zero. Therefore both Overgrowth and Underdocks remain in
the candidate law. The accepted candidate's actual native act type is saved only
to validate its constructor replay.

## Joint law and capped work

For each complete candidate, `NativeInitialPrefixCondition.Prepare` redraws a
fresh uniform RunSeed and a fresh oracle over native full-state addresses. It
runs the native three-word ActSelection and the entire StandardActMap component.
Equal addresses within that candidate share their word. All three selected act
words and all selected map words are replayed in exact native order, with the
existing constructor, RNG-address, owner, alias and completion guards.

Let p(x) be the joint native completed-prefix density, E(x) the public necessary
event, b the joint probability of a clean completed miss, and K the fixed trial
budget. The successful capped proposal subdensity is

    q_K(x) = p(x) 1[E(x)] sum(b^j, j = 0, ..., K-1)

The multiplier is constant for the fixed public root because every candidate
redraws the entire seed and prefix. Its reciprocal is both p/q and the same
global correction envelope, so their ratio cancels. This does not assert that
p/q is numerically one, nor estimate a success probability. If every native
trial completes, b = 1 - Z and p/q = Z / (1 - (1-Z)^K).

Holding a seed or earlier native prefix while retrying maps would be invalid:
seed-dependent state aliases can change acceptance and abort probabilities.
Tests enumerate a finite example with native matching seed masses 2/8 and 1/8.
Two whole-prefix trials emit those seeds with masses 26/64 and 13/64, sharing
p/q = 8/13. Held-seed capped retries instead yield the incorrect ratio 12:7.

Failed trial words are auxiliary and never enter the accepted tape. Native
exceptions and cancellation stop the attempt with their identity and partial
work accounting. They are not recast as clean misses. Complete-trial exhaustion
remains computationally inconclusive; it does not contradict the root or remove
its content. No per-trial draw/time cap truncates a completed-map law.

## Replay lifecycle and verification

After the selected initial map completes and its exact `RunState` is attached,
later native map generation proceeds through the ordinary oracle. It must not
replay the consumed constructor prefix. An additional map callback before
attachment, or from a different run, remains an invariant failure. This allows
later acts and native map-replacement effects without broadening the original
conditioning event.

Focused tests cover detached-input eligibility, the original legacy certificate,
both native initial acts, Boots option order, complete clean-miss retries and
exhaustion, exact map/ordered trace replay, retained aliases, finite normalization,
modifier/gap fallback, Unknown icons, actual recorder roots outside the old reward
certificate, and subsequent native act generation plus exact replay. Full public
packet equality is still required by the sampler after every accelerator.

Only the first visible slice is conditioned here. Later public map slices and
path choices remain in full-evidence replay; they are not silently ignored as
evidence. No production collection, training, broad performance claim, or new
prior identity follows from these bounded engineering tests.
