# Map-only public act prefix

The independent Map sampler may now condition the initial native act selection when both of these public certificates are available:

- Complete initial-map reconstruction is eligible under the declared complete-map producer/profile
- The reviewed opening encounter, retained weak-encounter sequence, or public event permutation establishes one consistent initial act, Overgrowth or Underdocks

Every available act certificate must agree. Map graph shape does not identify the act. Missing, unsupported, conflicting certificates or gaps before the certifying prefix leave ordinary replay in place. Reviewed opening/weak certificates may retain already certified evidence across a later gap. The previous state-tape and Rewards samplers retain their whole-initial-prefix proposal, which resamples its hypothetical RunSeed jointly with its map trace.

## Exact held-seed kernel

The new proposal holds every outer recipe field fixed, including its independently drawn hypothetical RunSeed. It invokes the native `ActDefinition.GetRandomList` and samples exactly its three distinct full-state tape cells, including the singleton Hive and Glory draws. The first act has probability 1/2 at every held seed. There is no actual source-seed input and no direct act-object replacement.

For a fixed complete-trial budget K, the null mass is 2^-K. The successful trace subdensity is p(trace) × 1[public act] × 2(1-2^-K), so p/q = 2^K / [2(2^K-1)]. This ratio is the root-constant envelope; its correction consumes no random word. The complete-map component has an independent, root-constant semantic-map mass under its existing reconstruction contract. Public event permutations keep their existing root-fixed normalizer and independent scratch proposal; their selected words are installed only at the actual native UpFront boundary.

The act budget currently reuses `initialPrefixMaxTrials`, but its audit fields are distinct: `publicActPrefixStats`, `publicActPrefixMaxTrials`, `publicActPrefixCorrection`, `publicActPrefixNullMass`, `publicActPrefixSuccessfulSubdensityRatio`, and `conditionedPublicActWords`. Dataset audit identifies `public_act_prefix_budget_source` as `initialPrefixMaxTrials`. Failed complete trials remain auxiliary and install no live tape cells. Only a clean K-trial exhaustion with exactly 3K words and distinct cells consumes an outer attempt and permits a fresh independent recipe. Cancellation, partial trials, native faults, and invariant failures remain unresolved errors.

## Owned replay and alias invariants

A separate act plan and replay owner preserve the selected three ordered full states. Before any map construction or reconstruction, `BeginMapGeneration` verifies all three words were consumed, the exact held seed and fresh native run boundary, the selected act/Hive/Glory list, and the fresh Map RNG. It binds the constructing run before `LabelMapConstructionScope`, so zero-word reconstructed maps cannot skip completion. Attaching the completed native run must identify that same object.

All later reads and exact recreations retain the selected full-state overrides. A later forced component touching an already visited act cell is a fatal invariant error, including a coinciding UpFront event-permutation cell; it cannot overwrite the act word or become a retryable public mismatch. Unexpected, missing, extra, or reordered act words, a wrong act after a successful plan, repeated/unowned construction, and conflicting replay overrides are likewise errors. Callback failures remain sticky even if native code catches them and later reports absence or a public mismatch. Replay copies reuse the selected plan and overrides with a fresh owned constructor, without re-preparing the proposal.

Raw act states and words remain private hypothetical replay data. Public packets, student fields, and audit output receive no act trace. This change adds no dataset generation, benchmark, training, production-readiness, or throughput claim.

## Focused verification

`NativePublicActPrefixTests` covers both acts, exact complete trials, unchanged seed, zero Map draws, replay copies, recreated full-state aliases, eligibility/fallback, constructor faults, collision invariants, partial trials, and cancellation. `NativePublicActPrefixReplayTests` checks existing native public roots and owned settled continuations, act/event/map composition where eligible, separate source audit/null attempts, legacy fallback, and swallowed-failure classification. `NativePublicActPrefixFiniteTests` enumerates finite bounded kernels and their explicit null outcomes against a brute-force prior, including held-seed aliases and downstream correction. These are correctness checks, not acceptance benchmarks.
