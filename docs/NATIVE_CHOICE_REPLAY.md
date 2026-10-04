# Native suspended choices from owned stable origins

The unchanged 200-root native cohort now admits **150 roots**, up from the frozen
v2 result of 145. The five newly admitted suspended choices contain **19 legal
candidates and 38/38 settled action-world continuations**. The complete bounded
run settled **1,844/1,844 worlds**. All 200 public inputs and the existing 145
supported target payloads are identical to the frozen v4 evidence. The other 50
roots remain present and unlabelled. No formal labels, training, new seed search,
corpus promotion, generated-card closure expansion, or vendor changes occurred.
See [the machine-readable evidence](../NATIVE_CHOICE_REPLAY_SUMMARY.json).

| Actual collected choice | Encounter | Candidates | Settled worlds |
|---|---|---:|---:|
| Acrobatics | FuzzyWurmCrawler | 5 | 10 |
| Survivor | VineShambler | 3 | 6 |
| Survivor | SludgeSpinnerWeak | 3 | 6 |
| Prepared | HauntedShipNormal | 4 | 8 |
| Survivor, with Reflex available | SeapunkWeak | 4 | 8 |

The two other suspended roots are Survivor choices in CorpseSlugsWeak and
TwoTailedRatsNormal. Their encounter memory is still unreviewed, so the choice
path rejects them at the prior boundary. Remaining blockers are 24 generation
potion carry-ins and 26 unreviewed encounter roots in total.

## Prior, conditioning, and ownership

The prior is still the explicitly declared conditional permutation of unknown
cards and independent future RNG at a reviewed stable boundary. It is not a
posterior over the actual finite native run seed. The existing stable native v2
certificate and its entire card/relic/potion/monster/power closure are unchanged.

When the native collector is about to execute a reviewed choice-producing play,
it certifies and detaches that stable source boundary. It records every selected
public action and ensuing exact decision packet until the next stable boundary.
Capture does not depend on whether the stable origin itself is among the roots
selected for export. The reviewed entry actions are Survivor, Prepared,
Acrobatics, ThinkingAhead, and DaggerThrow, shared with the existing constructed
conditional-choice path. Potion and arbitrary setup choices cannot acquire
provenance through this path.

A suspended import forks that stable snapshot and re-executes the observed suffix.
It verifies every packet, including the origin and the current root, and creates
an independent suspended coroutine. It never clones the current suspended native
state or starts a fresh Scenario. The source run's seed is audit metadata only.
Missing origins, unsupported origins, incomplete history, or mismatching packets
fail closed. The synchronous `ImportNative` remains the stable-only entry point;
`ImportNativeAsync` dispatches both stable imports and owned-origin choice replay.

For a posterior proposal, the sampler canonicalizes and resamples unknown draw
order and all future streams **at the stable origin, before the observed suffix**.
The full public suffix determines acceptance. Accepted draw order, RNG state,
monster memory, and other effects are retained without reseeding at the pending
choice. Student continuation actions and terminal results are never conditioning
evidence. A continuation fork replays only its own accepted world's origin.
Repeated posterior sampling still uses the declared stable-origin prior.

Public known physical positions, revealed candidate order, ordered selections,
Sly discard/autoplay semantics, and native reshuffling remain in the shared
execution path. Every branch has an independent origin and coroutine; it survives
disposal of its parent or the collector's borrowed origin. Raw source packets,
physical draw lists, source RNG streams, and the complete subsequent source run
remain unchanged by collection, import, sampling, or teacher evaluation.

The exact outcome ledger still begins at combat entry, copies already-accounted
prefix scalars, binds only after `CloneForNosl`, and seals at automatic native
settlement before postcombat decisions. Origin and branch disposal detach their
own observers. All 1,844 measured outcomes have complete HP and resource event
diagnostics as well as verified settlement.

## Versions and raw metadata

The audit-only dispatcher is `nosl-belief-dispatch-v5`. Stable roots retain
`native-public-entry-reviewed-memory-exchangeable-v2`; suspended roots use
`native-public-entry-reviewed-memory-conditional-choice-v1`. Native development
records/reports use `nosl.native-belief-prototype.v3` and
`nosl.native-belief-prototype-report.v3`. The report lists both `posteriorProfiles`
and its `posteriorImplementation`; each record states its actual profile and
whether import used a stable clone or stable-origin replay.

The public student schema stays v2 and receives no sampler profile, origin,
private RNG, source seed, or audit metadata. Existing frozen v2/v4 artifacts,
old datasets, checkpoints, and binaries are preserved. This evidence remains
`trainable=false` and `formal_labels=false`. All 150 rankings are masked for lack
of certified utility support; the 922 action targets split into 461 empirical
objective values and 461 unresolved objective-value masks.

Raw collection now explicitly says `posterior_evaluation=not_evaluated` and
`posterior_reason=native_posterior_not_evaluated_by_raw_collector`. Its conservative
`posterior_supported=false` means no certificate was established by that exporter.
An attempted prototype import instead emits `posterior_evaluation=unsupported`
with the actual rejection reason, or `supported` with the accepted profile.
A raw record no longer claims an implemented native mechanism does not exist.

## Verification and reproduction

`NativeChoiceReplayTests` uses the same actual collected roots, compares the full
source report against collection without boundary consumers, tests malformed or
missing replay history, erases hidden origin order and RNG while keeping public
facts fixed, checks same-seed coupling and resampling, verifies disposal lifetimes,
and settles every newly supported candidate in two independent worlds. A real
native Seapunk Survivor choice selects Reflex and verifies its two Sly draws.

The collected FuzzyWurmCrawler Acrobatics choice draws the two remaining Strikes,
reshuffles eight public discard cards, and observes a third Strike. Exact multiset
enumeration has 1,120 distinct prior orders, 420 accepted orders, and acceptance
mass 3/8. The test checks accepted residual orders, first-card marginals, and
one-attempt rejection mass over 128 independent sampler seeds. Rejected proposals
remain computationally inconclusive. A teacher with a known rejected one-proposal
budget retains every candidate as `ComputeTruncated`, with no fabricated loss.
Existing constructed known-position, reordered/multiple selection, Sly, and
conditional replay tests exercise the same shared machinery.

Build and test with .NET 9 in the isolated checkout:

```
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/native-choice-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false
```

The fixed evidence request, response, individual records, report, and verification
logs are in ignored `artifacts/native-choice-proof-200-v5`. Its request is copied
unchanged from the frozen v4 run: prefix `nosl-m5-natural-proof-20261001`, up to
100 runs, 12 floors, 200 roots, eight roots per combat, T0, evaluation seeds 101/102,
and a 200-decision rollout cap. The isolated worker binary is
`artifacts/native-choice-build/bin/Nosl.Worker/release/Nosl.Worker.dll`.
The separate-process protocol smoke uses these isolated worker/policy binaries.
The final complete NOSL suite passed **985/985**, and that protocol smoke passed.
