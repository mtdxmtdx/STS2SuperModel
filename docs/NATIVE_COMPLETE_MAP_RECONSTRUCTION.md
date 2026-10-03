# Complete public native map reconstruction

This is an opt-in label-only optimization for
`nosl.native-map-rewards-state-tape-prior.v1`, the
`nosl.public-map-complete-graph.v1` observation profile, and
`nosl.public-run-evidence.v2`. Existing full-state and Rewards/full-state laws
are unchanged and are not valid for this replacement.

## Producer and support contract

The support contract is `native-standard-act0-complete-map-producer-v1`.
It means the declared, reviewed natural native source script and policy emitted
an actual complete current StandardActMap at the initial act-zero map owner.
The map was generated before players, with the native A10 act-zero inputs for
Overgrowth or Underdocks. The first map remains unchanged during this source's
act zero: GoldenCompass is not available even transiently through its supported
acquisition paths. SpoilsActMap, quests, FurCoat annotations, unknown producer
versions, and unaudited external/live captures are outside the certificate.

The public record contains only final displayed coordinates, icons, directed
paths, and explicit special-node roles. Unknown remains an Unknown icon. It
contains no encounter or event identities, source seed, native generation
trace, private RNG state, insertion order, pruning state, or quest metadata.
The graph already includes native coordinate postprocessing. Screenshots that
show map icons and paths do not establish universal live visibility or absence
of fog; any other producer must separately establish complete visibility and
its versioned observation contract.

Malformed graph structure is rejected. However, a plausible graph is not a
proof that its probability is positive. Positive support comes from the
trusted native-producer/version contract, with structural guards as defense
against corrupted imports. The execution declaration is that trust boundary;
it is not a learned feature or a caller-supplied authenticity Boolean.
Old sliced records cannot be backfilled, and Missing disables the optimization.

## Factorization

Let X contain the held run seed, selected act, every non-Map latent variable,
and root indices. Let U be the fresh act-zero Map component under the new
independent origin/act/seed/raw-cursor partition. At the before-player boundary,
both supported first acts pass the same inputs to the native map generator,
so its semantic result is M = F(U). The law Q of M is the same for every X.
Variable generation lengths and private final Map cursors are marginalized.

For a fixed completely observed map m of positive support and any remaining
public observation e, the joint likelihood is

`P(X=x, M=m, E=e) = P(X=x) Q(m) P(E=e | X=x, M=m)`.

The same positive Q(m) multiplies every retained candidate. It cancels from
the conditional distribution of X. Reconstruction therefore pins the semantic
value m while sampling all remaining components from their declared laws;
the ordinary final public replay equality still enforces e. No numeric Q(m),
per-world weight, source-seed search, map rejection retry, or probability bound
is invented. This holds when the eventual target root is in a later act too:
the original map remains part of public history, but its private generation
state is dead. Later maps use their own native act component.

For a finite check, take any masses p(x), a finite independent uniform map
ticket u, and any map function F shared across x. Counting accepted `(x,u)`
pairs with F(u)=m gives `p(x) * count(F^-1(m))/count(U)` before the remaining
public predicate. Removing this common factor gives exactly the same posterior.
The independent-prior tests also retain an old-law alias counterexample: if a
Map draw aliases a non-Map state for only some held roots, Q(m | x) differs and
direct replacement changes root odds. Merely adding a public field to that
old law cannot fix the problem.

## Runtime sufficiency and boundary

`StandardActMap.ReconstructForLabels` constructs the real native class, its
grid, parent/child membership, start set, and separate starting/Boss nodes.
It performs no generation, pruning, postprocessing, or RNG draws. Its retained
RNG and placeholder point counts are behaviorally dead outside the generator.
This explicitly does not recreate their hidden conditional distribution.

The retained semantics suffice for the reviewed continuation: the source
controller sorts choices by visible attributes and coordinates; the public
profile uses canonical option order; normal travel and WingedBoots use edges
or grid rows; Unknown-room shop exclusion uses child types/membership; and
Planisphere reads the current icon. Native HashSet insertion order therefore
does not enter this continuation's observation or action law. Generation-only
fields are not used by these consumers. Adding a consumer of omitted state
requires reviewing and versioning the certificate.

`LabelMapConstructionScope` is an explicit label-only seam in RunState directly
before ordinary map construction. Null preserves the complete native path.
The replacement occurs before the existing ModifyGeneratedMap and
AfterMapGenerated hooks, whose timing is unchanged. The Worker proposal checks
the exact fresh, before-player, act-zero boundary, independent Map provenance,
owned RNG derivation, and expected native acts. It binds the resulting map to
one hypothetical run. A replay/fork needs its own proposal and map objects.
Later acts' owned map construction falls back to native behavior. Act-zero
regeneration is an unresolved contract violation and fails closed, including
completion after a caught exception: its reused Map component would require
the conditional private trace that was marginalized. Foreign runs, wrong
boundaries, missing completion, and use before attachment also fail closed.

The initial-map structural validator is reused only on a detached v1 slice-only
projection with the legacy declaration. No old-law probability plan or trace
is reused. The original v2 complete graph remains the sole reconstruction input
and the original evidence remains the final replay target.
