# Explicit Rewards hybrid tape

`nosl.native-rewards-state-tape-prior.v1` is a separate hypothetical label prior.
It requires `nosl.public-run-evidence.v1` and its v3 run context channel. Existing
state-tape and sequential native priors retain their identities and behavior.

The new law separates two ideal independent oracle partitions. The native player
Rewards stream uses `(origin, effective initial seed, raw primitive cursor)`;
every other generator retains the old complete pre-draw-state address. Native
state advancement, conversions, counters, generation order and game effects
remain unchanged. Exact clones copy lineage and cursor; same-lineage construction
restarts the same cells; a restore installs the source snapshot's explicitly
versioned partition. The law deliberately removes accidental sharing between
Rewards and other streams, and orbit-offset sharing between distinct Rewards
initial seeds. It does not claim this was the native sequential seed law.

SHA256 expansion has separate namespace constants for the two partitions. Exact
probability arguments concern the ideal oracle, not a finite posterior over SHA
keys. No original source graph, seed or source RNG enters belief construction.
Owned replay recreates the selected hypothetical recipe and explicit overrides.
An already read Rewards cell cannot be overwritten by a conditional proposal;
replay overrides must match. Native state-description hashes omit this sidecar
and are not used as complete hybrid world/cache identities here.

The worker entry point remains `native_tape_replay` with the explicit new prior
schema discriminator. Source generation and every inference/replay copy use
`NativeLabelTape.ForDeclaredPrior`; prior identity, sampler profile, dataset
version and record kind distinguish the new law. New rows use
`nosl.native-rewards-tape-replay-development.v1` and stay quarantined. Optional
Rewards-cell audit counts do not alter old audit serialization. All absent roots,
errors, cancellation and proposal budgets retain their existing mass accounting.

Reviewed non-Rewards shuffle/HP/Neow/encounter/joint-prefix kernels retain the
full-state partition and their corrections. Nested state override scopes preserve
the independent Rewards callback. The old first-reward state-addressed forcing
kernel is explicitly disabled and rejected for this prior. Its public structural
origin certificate may still establish necessary encounter/map constraints; that
does not force any Rewards cells or assign their likelihood a value of one.

The composed proposal now consumes the typed public prefix to condition both
initial Neow positives, every certified earlier combat's startup shuffle/HP,
and the base identities of standard primary combat card offers. Reward rarity
arms remain latent and carry their native probability; a root-wide catalog
bound supports exact rejection correction. Native hooks, typed evidence and
final full-packet equality retain all remaining public observations. A public
prefix contradiction can stop a hypothetical replay at its next stable decision
without spending the rest of that replay budget. Unresolved structural errors
remain distinct from proven public contradictions.

See [primary reward identities](NATIVE_PUBLIC_REWARD_IDENTITY.md),
[historical startups](NATIVE_PUBLIC_COMBAT_PREFIXES.md), and
[Neow public options](NATIVE_NEOW_PUBLIC_PREFIX.md) for each kernel's scope and
finite/native checks. Exact own-hypothesis forks preserve the complete prefix
and continue through terminal settlement; this is a reproducibility test,
separate from acceptance of a source-conditioned world.

The first predeclared full-history development cohort drew eight previously
uninspected source recipes (11001–11008), two posterior draws per root, at most
64 proposals per draw and 200 continuation decisions. All eight selected roots
existed. The run spent 42.35 seconds and about 185 MiB peak worker memory, but
accepted none of 1,024 proposals: all 96 candidate-world copies were explicitly
ComputeTruncated, with zero engine errors. This fails the predeclared broad
throughput gate. Failed proposal budgets do not imply loss or impossible content.
The exact source breakdown, predeclaration, request and response hashes are in
[the verification report](../configs/native_rewards_hybrid_verification.json).

Observed rejection causes were 455 Neow option/pool mismatches, 254 initial
roster mismatches, 289 combat-entry asset mismatches and 26 map-prefix mismatches.
Startup card-grant branches remain native; they can create public entry assets
that are unlikely to match without their own corrected proposal. The v4 implementation now conditions visible curse/pool choices, the public
first map slice and first encounter roster, together with ArcaneScroll,
ScrollBoxes and Kaleidoscope card grants.
These eight roots are now inspected development cases, not a new blind test for
later revisions. Further breadth validation needs a fresh predeclared cohort.

All new rows remain quarantined development records. Production enlargement
and new fitting stay paused until useful full-history throughput and data
quality pass; the original corpus, sealed test split and experimental weights
are unchanged. This checkpoint does not establish complete M3–M6 acceptance.

## Corrected composition v4

The sampler version is `nosl-native-rewards-state-tape-conditional-v4-public-evidence-v1`.
The prior identity remains unchanged. Past primary potion presence, identity and
gold now use explicit native reward boundaries, with latent pity preserved and a
root-wide likelihood envelope. Abort-aware scopes preserve the original native
failure instead of replacing it with missing-card cleanup errors. Fixed-budget
outer retries after complete prefix exhaustion retain the corrected per-attempt
submeasure; see [the retry law](NATIVE_PREFIX_OUTER_RETRY.md). A clock deadline
can still favor faster latent worlds. Failure accounting alone does not establish
an unbiased retained-source population.

The full worker regression passed 1,485 tests. A subsequent test-only change
strengthened the resource-conditioned replay through terminal settlement and
passed all 8 affected checks; production source was unchanged. The fresh Core
suite passed 4,493 tests with 3 existing opt-in skips, and all 2,231 tracked Core
files reconstruct byte-for-byte from the pinned source and local patches.

A predeclared repeat of the same eight inspected roots hit its 360-second cap.
Four roots executed and four remained explicitly unexecuted. There were 277
proposals, 2 accepted worlds, and 16 settled out of 56 allocated candidate-world
copies; the other 40 were ComputeTruncated. All recorded public inputs match
the original cohort. The aggregate test run overlapped the beginning, so the
recorded duration is a resource diagnostic, not an isolated production benchmark.
The main remaining costs were map-prefix rejection, initial slug intents and
later first-cycle draws. No new unique roots, production rows or fitting occurred.
The exact evidence and remaining work are in [the v4 report](../configs/native_rewards_hybrid_v4_verification.json).
