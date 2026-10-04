# Generic owned native replay

The new `owned_native_replay` worker operation executes the upstream native run
from startup through a selected combat decision. It retains the native run,
room/event owners, reward bookkeeping, boss phases and suspended card-selection
coroutines. It does not require the reviewed card/enemy whitelist used by the
earlier detached native imports.

This is a **separately versioned engineering prior**, not permission to reinterpret
the earlier corpora. The older conditional-permutation/independent-future profiles
remain available. They are not silently substituted for whole-run seed inference.
New records are explicitly development-only and cannot enter the existing pilot
loader or start training.

## The declared distribution

`NativeRunPrior` declares the seed law, a fixed number of eligible decision slots,
the source policy, the outside-combat script, the floor horizon and the source
decision horizon. Draw one seed independently, then one slot uniformly from the
fixed range. A slot counts every public stable or card-choice combat packet across
that run. The source-decision horizon counts all native decision-source calls,
including map, event and reward choices. After the selected slot, the teacher's
continuation budget takes over.

If the run ends or reaches its declared source horizon before that slot, the draw
is absent. It is not replaced by the first existing/matching slot. This preserves
the multiplicity of matching roots across runs. The source and posterior proposal
use the same law, with separate independent sampler streams. Finite seed supports
must be declared before collection, with no success-based seed selection.

The inference constructor receives only a detached `DecisionPacket` and this
declared prior. It does not receive the actual source seed, native source graph,
source trace, floor, act or source combat ordinal. Earlier run histories are
marginalized under the source script. Acceptance compares the entire serialized
public packet, including entry assets and current-combat history. Distinct prior
identities remain distinct even if their current public packets happen to match.

A proposal that fails to match can be rejected. An engine error or cancellation
cannot be treated as a nonmatch and retried away. It stops that posterior draw and
leaves unresolved mass. A finite attempt limit returns `ComputeTruncated`, without
claiming impossible content or game loss. Every predeclared source attempt and
every assigned evaluation world remains in the report.

## Ownership and settlement

An accepted world owns its actual `RunDriver`. `StepAsync` resumes its own pending
decision task. A candidate fork starts the same hypothetical recipe again and
replays the owned public action prefix, checking every resulting packet. It does
not clone suspended coroutines or transplant materialized source hidden state.

The shared teacher adapter keeps the original T0/T1 logic and independent final
evaluation. Cleanup is part of committing a sample: a failed disposal cannot add
a second outcome or silently remove an assigned world. Actual source graphs are
disposed before posterior inference starts.

Settlement uses the native automatic boundary before the first postcombat
decision. Reward-enabled forced losses with no actual unresolved offer now finish
their native owner return before the diagnostic completion hook. Reward-present
wins still stop before the reward decision. The hook change does not modify game
effects, RNG draws or their ordering.

Committed HP/healing/resource facts and terminal assets are retained. Ordinary
postcombat reward options remain separate diagnostics, as required by V4; actual
extra and event-return opportunities enter the existing unpriced-benefit ledger.
The reported `settlement_seconds` for owned runs measures snapshot recording only,
because the native completion hook does not expose resolution-start timing.

## Bounded evidence and practical limit

The finite prior `{11,22} × {slot0}` produced one predeclared natural opening root.
Two independent evaluation draws required five proposals; all 24 candidate-world
continuations settled in 3.24 seconds, with about 95 MiB peak worker memory. This
small finite support is an engineering fixture, not a natural-distribution or
policy-quality claim. Both accepted draws select the same one matching recipe;
they are not evidence of two distinct possible futures.

A separate predeclared full-uint64 probe used two opening roots, two evaluation
draws per root and at most 16 proposals per draw. Both roots existed, but none of
the 64 proposals matched. All 30 allocated candidate-world copies are explicitly
truncated, with zero engine errors and no numerical labels. The probe took 4.44
seconds and about 95 MiB. This small sample does not establish a precise acceptance
rate; it provides no evidence of useful broad throughput either. It is a failed
gate at the declared budget, not proof of impossible matching or a measured lower
bound on every larger-budget alternative.

Native tests additionally cover exact replay, a Nightmare pending choice outside
the older import whitelist, actual boss phase change, forced-event loss/return,
timeout and cancellation. Boss/card/event lifecycle fixtures that inject their
setup are explicitly marked constructed; they do not establish naturally sampled
boss coverage. Native bundle selection currently occurs during ScrollBoxes
acquisition, so no unsupported natural in-combat bundle claim is made.

The generic execution barrier is therefore reduced, while the broad posterior
throughput gate remains open. Next viable choices are a structured conditional
proposal with its own distribution validation, a different explicitly limited
prior, or using the existing reviewed pilot scope. A larger sampling-budget study
would need its own fixed acceptance and resource limits; repeating a fit cannot
repair the sampler. No broad seed search,
production corpus expansion, new fit or formal training was run for this change.

## Reproduction

Use .NET 9 with isolated output paths; leave the archived pilot binaries intact.

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/owned-run-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~NativeRunPriorTests|FullyQualifiedName~NativeRunWorldTests|FullyQualifiedName~TeacherWorldAdapterTests'
```

The worker accepts this finite engineering request:

```json
{"op":"owned_native_replay","options":{"collectionId":"owned-native-finite-proof-v1","prior":{"eligibleSlots":1,"finiteSeedSupport":[11,22],"execution":{"maxFloors":1,"sourceDecisionHorizon":64,"sourcePolicyId":"nosl-public-rules-v2"}},"sourceDrawSeeds":[8001]},"teacherOptions":{"mode":"T0","continuationPolicyId":"nosl-public-rules-v2","evaluationSeeds":[101,102],"maxPosteriorAttempts":16,"maxDecisions":200}}
```

For the retained full-prior probe, remove `finiteSeedSupport`, use source draws
`[8001,8002]`, and keep the other bounds unchanged. Repeating these fixtures does
not create new independent evidence. The [machine-readable report](../configs/owned_native_replay_verification.json)
binds exact source snapshots, regression results and retained artifact hashes.
Final runtime replays preserve all initial public packets, targets and outcomes;
the quoted timings above are the initial separate-process measurements. The final
aggregate passes1096 native cases and4480 Core cases with3 existing opt-in skips.
