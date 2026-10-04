# Opt-in public continuation v3: validated native run context

Date: 2026-10-03. Source base: `561f9ab`. Pinned simulator:
`5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`.

## Bounded change

The [v2 certificate](PUBLIC_CONTINUATION_V2_EVIDENCE.md) accepts only
`nosl.public.v2`. Consequently an otherwise identical native-run observation
using `nosl.public.v3` falls back to v1, including its Finesse/Impatience stalls.

[ContextualReviewedPublicRulePolicy](../src/Nosl.Contracts/ContextualReviewedPublicRulePolicy.cs)
adds the separately selected `nosl-public-rules-v3` policy. It accepts an ordinary
v2 observation without run context, or v3 with a context accepted by
`PublicRunContext.Validate()`. For valid v3, a local DTO view is passed to the
unchanged v2 policy with the v2 schema and no context. The caller's packet and
legal action objects are preserved. Invalid/missing context, inconsistent schema
and context, and unknown schemas retain v1 behavior.

The original v2 mechanic certificate and scoring code are unchanged. All its
card, draw-count, enemy, intent, relic, power, cost, enchantment and affliction
guards still apply. Both complete history and explicitly unavailable history
can validate: the combat-only certificate does not need a combat-entry count.
No counter is inferred and no private native state enters the policy.

This closes one schema-boundary gap in the reviewed cycle family. It is not a
general loop solver, a proof of optimal termination, all-game C02 acceptance, a
macro-action mechanism, or evidence of natural-frequency coverage. End turns
remain ordinary simulator actions. Only actual native settlement declares
win/loss; budget exhaustion retains unresolved mass and masked targets.

## Identity and rollout boundary

- Continuation: `nosl-public-rules-v3`
- Frozen tree: `nosl-public-uct-frozen-v3-rules-v3:<digest>`
- Exploration: `nosl-public-uct-exploration-v3-rules-v3`
- Teacher dataset lock: `nosl.teacher-data.public-rules-v3.v1`

The new dataset lock is used for explicit v3 continuation records, including
public-run-context observations. Observation/student/evidence versions remain
recorded separately. Thus neither a matching frozen-tree digest nor matching
targets can hide a different continuation behind an old version. The existing
Python version lock rejects mixed v2/v3 records.

The worker advertises v3 through the existing continuation capability protocol
and accepts it in explicit policy selections. The v2 reproduction tool now
requires the presence of v1/v2 capabilities without rejecting extra versions.

No defaults, existing policy IDs, serialized prior definitions, existing data,
corpus manifests, model files, sampler identities, or probe configurations were
changed. `TeacherOptions` and `NaturalSourceOptions` still default to v1;
`NativeRunExecutionOptions` still defaults to v2. Current map probe gates and
specialized conditioners that require v2 remain unchanged and are not certified
for v3. Production generation CLI selection is unchanged. A future rollout must
declare a new source/teacher configuration and validate its applicable sampler
composition before changing any frozen probe or source law. No training,
publication, or promotion is part of this change.

## Native checks

[ContextualReviewedPublicRuleTests](../tests/Nosl.Tests/ContextualReviewedPublicRuleTests.cs)
uses `NativeRunWorld` with explicit test-only lifecycle setup. It replaces the
opening deck/enemy before native combat setup; it does not transplant hidden
card order or construct a fake decision packet for the positive cycle tests.
Both channels use the native observer, engine transitions, owned replay forks,
and native automatic settlement. Injected lifecycle history remains explicitly
incomplete. These are constructed mechanic fixtures, not natural posterior
samples; the fixed-fixture teacher adapter makes no distributional claim.

- Paired v2/v3 Finesse ×2 and Impatience ×2 packets are equal after removing only
  run context/schema. Each takes 14 legal end turns to the same actual loss
- All six root candidate copies per pure fixture settle. Cap 2 retains all six
  as `ComputeTruncated`, with null utility, false target masks, and no ranking
- Mixed Finesse/Impatience retains useful defensive draws to block 8, then plays
  BladeDance and three Shivs to a 70-HP native win against the 12-HP fixture
- FlashOfSteel and FlashOfSteel+ retain their profitable attack/draw cycles,
  winning in 13 and 9 native plays with zero objective cost
- A native unknown draw containing BladeDance remains drawable after enough
  block; native Survivor choices remain legal and preserve v1 choice handling
- Invalid contexts and every tested existing mechanic rejection retain v1;
  native LetterOpener is outside the certificate and retains cap-failure mass
- T0/T1 use the new identities. Identical v2 mechanics give the same T1 digest
  under distinct version prefixes. Existing frozen observation/prior byte tests
  and old teacher-audit shape checks remain passing

Focused checks passed **33/33**. A separate real worker protocol check confirmed
explicit v3 ends the pure cycle turn, while omitted selection still plays a
card. A paired constructed v2/v3 teacher record check completed **6/6 copies per
policy**, with identical public inputs and targets; both records passed the
engineering-smoke validator and mixed preparation rejected one record with
`append_record_versions_mismatch`. It used new temporary files only.

The historical v1/v2 fixture reproduction also passed: v1 completed **16/48**,
v2 completed **48/48**, with its original public/candidate and version checks.
These are completion/version-separation checks, not preference improvement.

## Reproduction

Use the configured .NET 9 SDK, cached restore sources and a new artifacts path:

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path /tmp/nosl-public-v3-build \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~ContextualReviewedPublicRuleTests|FullyQualifiedName~ReviewedPublicRuleTests|FullyQualifiedName~PublicRunContextTests|FullyQualifiedName~TeacherDatasetProvenanceTests'
python3 -B tools/verify_public_continuation_v2.py \
  --worker /tmp/nosl-public-v3-build/bin/Nosl.Worker/release/Nosl.Worker.dll \
  --output artifacts/public-v3-preserved-v2-fixture-new
```

The normal full worker test suite is the integration regression gate. No
full-suite claim follows solely from the focused result above.

## Completed integration verification

The full Worker suite passed **2,185/2,185**, zero failures/skips,5m12s on source`f7baa66f5048634c40b5dbde2ac8d6df4d315d8c`. Independent read-only review approved this snapshot. Retained focused/aggregate logs and paired protocol/version records are in`artifacts/reports/public-v3-cycles/`, with payload hashes in its manifest. These counts overlap existing suites and are not additive.
