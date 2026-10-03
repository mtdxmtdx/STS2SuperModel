# Independent Map primitive law

`nosl.native-map-rewards-state-tape-prior.v1` is a new label-only law. It requires
`nosl.public-map-complete-graph.v1` and `nosl.public-run-evidence.v2`. It does not
rename, migrate, or change `nosl.native-rewards-state-tape-prior.v1`, its persisted
serialization, its Rewards/full-state aliases, or its corpus.

## Three primitive partitions

The ideal random object consists of three mutually independent collections of
uniform 64-bit words:

1. Map cells indexed by `(actIndex, effectiveInitialSeed, rawCursor)`
2. The existing Rewards cells indexed by `(originFamily, effectiveInitialSeed,
   rawCursor)`
3. The existing full-state cells indexed by the complete native predraw state

Only `StandardActMap.CreateRng` binds Map provenance, at native seed construction.
The effective seed includes the unchanged native `act_N_map` name hash. Act
lineage is also explicit: future acts whose effective seeds happen to collide
still own different cells. Creating an incidental RNG inside map generation,
or merely choosing the same name, does not give it Map provenance. Native
generation order, distributions, conversion arithmetic, wrapper counters and
underlying Xoshiro state advancement are unchanged.

Clones and same-seed/same-act recreations share the declared cells. Reseeding
preserves act lineage and changes the effective initial seed, resetting the raw
cursor. Snapshot metadata records the law, source partition, Map origin, act,
seed and raw cursor; restore never attempts to recover lineage from numeric
state or wrapper Counter. Missing, incompatible or malformed metadata fails
before that individual generator's native state mutation. This is not an
atomic multi-stream load guarantee for a whole RunRngSet or PlayerRngSet.
Counter can advance on a rejected wrapper draw
while no raw word is consumed, so it is not an oracle address.

An existing hybrid generator cannot restore or reseed into the other hybrid law,
even when a different ambient scope would otherwise authorize the loaded
snapshot. Explicit hybrid scopes cannot nest across laws. Generic word-forcing
scopes still inherit their enclosing provenance law; same-law hybrid scopes may
override callbacks without changing source lineage.

`NativeMapOracle` uses a distinct SHA256 address namespace for reproducibility.
SHA256 outputs are a deterministic instantiation of the declared ideal oracle;
tests of finite recipes or seeds do not prove independence or exact posterior
sampling for that deterministic implementation.

## Why the initial map factor is constant under this law

For the supported act-zero constructor path, `RunState` creates the map before
players are added. The native Overgrowth and Underdocks map kernels both have
15 rooms and the same randomized point-count generation. Holding ascension and
the second-boss flag fixed therefore gives the same native semantic-map kernel
for either act-zero identity. Player relics cannot alter that constructor map.

Fix any run seed and either supported act-zero identity. Every successive raw
Map draw has a fresh cursor in that Map origin, so under the ideal product law
the kernel sees the same IID uniform-word sequence distribution. Changing the
held seed changes addresses but not their joint distribution. The semantic-map
pushforward is thus one common law `Q`, independent of the held seed and of the
Rewards/full-state primitives. In particular, `Q(observed map)` is a common
factor when conditioning the remaining hidden world on a complete visible map.

This statement is about the full semantic graph: coordinate/type nodes,
coordinate edges, start and boss roles. Private object creation order and
presentation-independent traversal indices are not part of that event.

Direct installation of a supported, reachable complete graph may marginalize
this unused initial Map component: for fixed public graph `g`, the joint mass
is `Q(g)` times the remaining world's mass, and this positive common factor
cancels on normalization. Reachability and all other public observations still
need independent validation. This law alone does not certify an arbitrary
graph as native-reachable.

The retained private map RNG is behaviorally dead after native construction:
native code uses it only within the construction/generation passes. Marginalizing
its unobserved terminal cursor is valid only for the audited continuation that
never reads it or regenerates the same act's map. A future consumer of that RNG,
a same-act regeneration, a new act-zero map kernel, or any map modifier at the
constructor boundary requires re-review. API recreations still share their
declared Map cells; marginalization does not authorize inventing different
sharing semantics for those recreations.

## Why old laws cannot delete this factor

Under the original native seed law, a map is a deterministic function of the
run seed. Observing it can constrain that seed. Under either old full-state
tape law, Map and an untagged generator with an equal native state read the same
cell. Their joint observations can therefore be coupled. For example, create
the native act-zero Map RNG and a plain RNG whose seed equals its effective
named seed: their first full-state cell is identical. Across acts, adjusting
the run seed by the difference between map-name hashes also produces an exact
state collision. The new Map partition breaks these cross-origin aliases;
the old laws intentionally retain them. Rejection improvements under the old
law do not establish the common-factor argument above.

## Verification boundary

`NativeMapProvenanceTests` exhaustively enumerates a finite four-valued product
oracle across two Map seeds, a later-act Map, Rewards and full-state partitions. It
checks the deliberate effective-seed collision, exact clone/recreation aliases,
the retained old-law counterexample, snapshot/restore failures, reseeding,
raw-cursor/Counter divergence, and unchanged Rewards oracle output. A native
trace test feeds the ordinary native raw words through the new Map partition
and compares the complete semantic graph, primitive addresses, counters and
native state. A coupled-word fixture checks the identical act-zero semantic
kernel across different held seeds and both acts. It also checks that construction occurs
before players without dynamically tagging incidental RNGs.

These are law/mechanism checks and native behavioral fixtures, not source-seed
recovery, production generation, fitting, or empirical proof over finite seeds.
