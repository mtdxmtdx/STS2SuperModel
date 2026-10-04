# Immutable public-evidence snapshots

`PublicDecisionSnapshot.Copy` keeps a validated immutable `PublicRunEvidence`
prefix while JSON-detaching the rest of a `DecisionPacket`. Native owned replay
and natural collection use it at their existing public packet boundaries. The
change removes repeated decoding and validation of the complete cumulative
evidence prefix. It does not cache or copy a native run, change any simulator
rules, or replace independent native continuation replay.

## Measured motivation and limits

The frozen map-public-v6 inspected-eight report recorded 159.779 seconds of
teacher work: 71.346 seconds in the combined sampling/fork bucket and 88.433
seconds outside it. Proposal audit elapsed times sum to 23.346 seconds; subtracting
those leaves 48.000 seconds of fork/preparation residual. The inspected-twelve
report recorded 349.661 seconds: 79.656 seconds of proposal audit elapsed,
93.611 seconds of fork/preparation residual, and 176.394 seconds outside the
combined bucket. Settlement snapshot hooks total only 0.026 and 0.031 seconds.

These residuals are not a CPU profile. Proposal timers exclude some setup and
cleanup, so their subtraction does not measure pure forking. Rollout/other
includes every teacher operation outside that bucket. Source construction and
protocol overhead sit outside teacher elapsed time.

The twelve-root probe has separate proposal bottlenecks: root 24108 spends
37.412 seconds on 91 density rejections and root 24107 spends 21.159 seconds on
24 evidence mismatches. Root 24103 nevertheless takes 60.200 teacher seconds
with only two proposals, both accepted, costing 3.645 seconds. Its public input
has 378 evidence events and approximately 741 KB of compact JSON. The evidence
prefix is repeatedly serialized, parsed, reconstructed, and validated at each
decision and replay boundary. This implementation removes that repeated internal
detach work. The separately approved fixed-eight comparison below measures its
effect within the inspected fixture scope.

The older `CLONE_PROFILING.md` measurements concern legacy `CombatSession`
fixtures and repeated standard-map generation. Their student-v1 fixtures do not
exercise the complete-map evidence channel and cannot establish this change's
v6 performance or equivalence.

## Shared-state boundary

Only `DecisionPacket.PublicEvidence` is shared. The packet's observation, actions,
history, choices, arrays, dictionaries, and public run context still pass through
the same JSON round trip. Actions and terminal outcomes retain their existing
copies. No native object is reachable through the evidence types.

The complete reachable public evidence graph was reviewed:

| Reachable state | Ownership contract |
|---|---|
| `PublicRunEvidence`, event ordinals, payload base | Sealed envelope/event types; getter-only properties; closed payload constructors and JSON-derived-type list |
| Run/owner starts and ends, gaps, choices, options, scalar combat facts, damage | Sealed objects containing scalar values, strings, or other reviewed immutable objects |
| Event, offer/group, option, selection, intent, potion, map collections | `ImmutableArray<T>`; constructors copy borrowed backing arrays through `EvidenceGuard.Array`; elements are immutable |
| `PublicIntent` | Sealed record containing only string/nullable integer values; init-only properties |
| Complete-map capture, map nodes/edges/options/coordinates | Sealed objects; getter-only scalar/coordinate state and defensively constructed immutable collections; graph validation is unchanged |
| Evidence assets, offered cards/relics, card choices, combat cards/choices, pre-settlement cards, combat decision observation/actions, taken action | Private legacy DTO storage is deep-copied on construction and every public getter; nested card keywords/modifiers/effects/state dictionaries, relic dictionaries/cards, choice bundles, observation arrays, and action selections cannot expose it |
| Envelope validation state | Private immutable dictionaries/records; `Append` returns a new envelope and validation state without changing a saved prefix |

The proof concerns supported public APIs, not reflection or unsafe extraction of
`ImmutableArray` backing storage. Constructor borrowing is covered explicitly:
inputs manufactured with `ImmutableCollectionsMarshal.AsImmutableArray` are
copied before entering the evidence graph.

`PublicDecisionSnapshotTests` traverses every registered payload type and all
reachable property types. A newly exposed mutable property or extensible type
requires review; the permitted defensive DTO getters are an explicit list.
The adversarial fixture contains every registered payload, and recursively
corrupts every mutable array/dictionary exported from it, as well as the borrowed
constructor input arrays. Saved evidence and sibling packet bytes must survive.
If any future payload cannot maintain this contract, retain full JSON cloning
for that payload/channel until its immutable boundary is established.

## Unchanged exactness and failure behavior

- External packets still use the strict evidence JSON decoder, including unknown
  fields, duplicate properties, version checks, gaps, and complete-map validation
- Full serialized public packet comparisons in native replay and posterior
  acceptance are unchanged; prefix event comparison is unchanged
- Every fork still owns a separate `RunState`, player, room, task, and hypothetical
  label tape, replaying the accepted world's setup and transcript
- Source policy, schemas, prior identity, proposals, density corrections, RNG
  words, allocation, settlement boundaries, cancellation, and cleanup are unchanged
- A saved evidence prefix is stable after later appends and independently stepped
  branches; mutable copies returned to a policy cannot affect either world

## Validation and performance gate

Focused tests compare the helper's exact serialized result against the original
full JSON round trip for absent, v1, and complete-map v2 evidence, covering stable,
pending-choice, and terminal packets. Native v1/v2 same-recipe fixtures check
full public equality through settlement, saved-prefix stability, mutation
isolation, and distinct native ownership.

The isolated Release build passed 12 snapshot tests and 124 existing focused
regressions: `PublicRunEvidenceTests`, `NativePublicRunEvidenceTests`,
`NativeEarlyPublicPrefixTests`, `NativeRunWorldTests`, `NativeTapeReplayTests`,
`PublicCompleteMapObservationTests`, `NativePublicMapV4IntegrationTests`,
`NativeHybridV6CompositionTests`, `NativeConditionedWordFailureTests`,
`NaturalSourceTests`, `NativeChoiceReplayTests`, and `ForcedEventLifecycleTests`.
These are targeted correctness results, not a full-suite or throughput claim.

Before claiming performance or adopting a new frozen runtime, run the relevant
evidence, public-prefix, native-world, choice, complete-map, conditioned-failure,
and v6 composition suites, followed by an independently reviewed frozen-eight
paired comparison. Compare all non-timing public inputs, targets, action outcomes,
ranking fields, recipes, proposal statuses/counts, density fractions, tape/cell
audit fields, and public/action/terminal traces. Check source/sample/settled
RNG/tape state and unchanged source snapshots as available. The same completed
work must be compared before interpreting wall-budget gains; a faster runtime
may execute additional assigned work after the old runtime's timeout.

Keep old report inputs and binaries frozen. Repeated fixtures are engineering
checks, not new population evidence or training examples.

## Fixed-eight comparison after independent review

One optimized invocation used the original eight source draws, their order,
evaluation seeds 501/502, 360-second wall budget, and all other exact original
request bytes. The request SHA256 is
`1055ce701b921300949167b16f92f6e1f3d0f0e83802d8948dce5183911d6e6e`.
The runtime source is `8aab8d9e478f28dd301a346f3280243136d9fded`; the original
baseline is frozen `28cccddc44b3cbc4e91eabc7aaf5dfe48dff04a7`. Runtime sources
differ only in the helper and its two callsite files. The optimized Worker DLL
is `b05e856f087358a12718829c6fb3eb2e80cb66f69c8278bfcdf4a0c9d335095c`.

| Measurement | Frozen baseline | Snapshot variant |
|---|---:|---:|
| External process wall seconds | 167.927 | 22.432 |
| Worker report elapsed seconds | 167.704 | 22.239 |
| Summed teacher elapsed seconds | 159.779 | 18.048 |
| Sampling/fork bucket seconds | 71.346 | 7.833 |
| Proposal audit seconds | 23.346 | 2.313 |
| Fork/preparation residual seconds | 48.000 | 5.521 |
| Rollout/other seconds | 88.433 | 10.215 |

Both reports complete all 8 roots, 16 posterior slots, and 98 candidate branches,
with the same 1,229 rollout decisions and 32 proposals (16 accepted, 16 exact
density rejections). The entire response matches after removing exactly eight
predeclared timing/RSS paths; no build/version field or other audit value was
removed. Public inputs, recipes, proposal statuses/counts/correction fractions,
targets, outcomes, rankings, and all remaining audit fields match. Both normalized
response SHA256 values are
`09170db0024f1c865ac15157a73b2a4d9d867e5979462eb38e9a27e5fdb47d9c`.
An independent comparison parses every non-integer JSON number as exact
`decimal.Decimal` and also passes with those same eight exclusions, ruling out
binary-float rounding as the reason for equality.

This is a roughly 7.54-fold worker-time improvement on an already inspected
development diagnostic. The baseline was recorded earlier on a shared cloud
CPU, rather than in an interleaved repeat. The result does not establish fresh
coverage, posterior admission, population-average speed, or training readiness.
The exact semantic comparison, not completion counts alone, is its correctness
gate. The guarded runner and explicit excluded paths are in
`tools/run_public_evidence_snapshot_probe.py`; detailed frozen files are in
`artifacts/reports/map-public-v6-snapshot/`.

## Separate paired private-state traces

The same `Nosl.MapAcceptanceBenchmark.dll --snapshot-trace` harness was run
against each frozen dependency set after the timed invocation ended. Four fixed
inspected source recipes (24001, 24002, 24007, 24008) each execute one direct
continuation and its independently owned native fork: eight fixture trajectories
per binary. The 44 paired decision frames and four paired settled fixtures have
byte-identical diagnostic output, SHA256
`33b5874e1f998d8ed783f68ea808b73aaaadab4b9c89b8dd70ac0830ef06acc6`.

Each frame records exact public-packet and source-trace SHA256 values, the exact
chosen action, native combat-state digest, complete run/player/monster RNG
snapshot hashes, and a tape-state hash over recipe, visited cells, forced words,
and relevant counts. The diagnostic reads the existing tape cell collections
through explicit reflection and never writes them, consumes a random word, or
passes them to a policy. Native ownership and source stability across fork
creation/advancement are checked. The terminal records include full outcomes.

These private-state traces cover eight deterministic fixture paths, not all 98
posterior candidate branches. The full response comparison supplies all 98
outcomes and audit records; the snapshot byte/mutation tests supply the general
ownership argument. No claim of complete serialized native heaps is made: the
private checks use the simulator's existing combat-state digest plus the stated
RNG/tape projections.

The declaration is `configs/public_evidence_snapshot_trace.json`; the diagnostic
implementation is `tools/Nosl.MapAcceptanceBenchmark/PublicSnapshotTraceDiagnostic.cs`.
Build that existing benchmark project into `artifacts/snapshot-trace-build`, then
use `tools/run_public_evidence_snapshot_trace.py` with the preserved frozen Worker
sets. Both runners refuse to overwrite existing invocation artifacts. Trace
instrumentation costs are excluded from the throughput comparison. The compact
machine-readable result is `PUBLIC_EVIDENCE_SNAPSHOT_SUMMARY.json`.
