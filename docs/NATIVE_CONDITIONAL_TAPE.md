# Native conditional random-tape sampling

The `native_tape_replay` operation introduces an explicit, separately versioned
label prior. It addresses the rare initial-hand rejection factor while retaining
the upstream native run, event owners, card choices and terminal settlement.
The existing `owned_native_replay` operation and all frozen data keep their old
distribution. New tape records are development-only and rejected by the existing
production loader.

The second iteration passes the same eight-opening diagnostic gate: 16 independent
worlds accepted and 160 candidate branches settled from 249 proposals in 24.47 seconds.
The later-combat probe instead exposes an outside-combat event-controller loop,
which remains explicit engine-error mass while a versioned policy fix is prepared.
This is not broad production admission. Current evidence is in the
[v2 report](../configs/conditional_native_tape_v2_verification.json).

## Distribution and reproducibility

`nosl.native-state-tape-prior.v1` draws an independent native run seed and a
hypothetical tape, plus uniform indices from declared fixed ranges of combat and
local decision indices. A selected combat or local decision that never exists is
an absent source draw. It is not replaced by a convenient existing decision.
Both source generation and inference use this same declared law. Native source
horizons, the source policy and the outside-combat script are part of its identity.

The ideal tape assigns an independent uniform 64-bit word to each distinct full
pre-draw `MegaRandom` state. Equal states share a word, preserving exact clones and
recreated equal-seed generator aliases. The generator still advances once, and
native integer/float conversions, counters, hooks and game effects run unchanged.
This changes the original deterministic correlation between different states and
streams derived from one run seed. It is not the posterior over the old native
run-seed prior, nor the upstream semantic-key mode.

SHA256 expansion from an independent tape seed and state address implements the
ideal oracle reproducibly. Conditioned cells are explicit proposal-owned overrides
retained by replay/forks. Exact proposal correction is a statement about the ideal
independent-word law; it is not a proof that outputs over a finite SHA256 seed
family are literally independent. The finite validation below enumerates primitive
words directly, rather than treating a seed sweep as such a proof.

## Exact conditional shuffle proposal

The public certificate checks the published entry multiset and the uninterrupted
initial draw history. It rejects Innate/enchantment/affliction reordering and
unreviewed entry hooks, and checks a source-audited closure of ordinary startup
monsters. Later stable or card-choice roots can retain that same initial history.
The certificate is an accelerator gate, not an encounter or card support gate:
ineligible roots use plain native tape rejection under the same prior.

For an eligible root, the actual target-combat entry must match before proposing
its first shuffle. The proposal samples a uniform physical-card permutation
conditional on the published ordered card IDs, preserving multiplicities among
identical cards. It inverts native Fisher–Yates and samples each forced primitive
word uniformly from the exact integer-output bucket. Bucket boundaries use the
actual floating-point conversion, including rounding at boundaries.

Let `c` be the uniform-permutation prefix probability, `n` the deck size, and
`p_i(j)` the native probability of index `j` at Fisher–Yates step `i`. Then the
native/proposal density ratio is `c × n! × product(p_i(j_i))`. A root-constant
envelope is `c × product((i+1) × max_j p_i(j))`. The final rejection correction is
therefore `product(p_i(j_i) / max_j p_i(j))`. The implementation uses exact integer
Bernoulli factors, with no floating-point acceptance comparison.

The entire public packet is still compared after native execution. The fixed
entry multiset and observed prefix make the envelope identical across compatible
run histories, physical card input orders and root indices. Cells already used
before the forced shuffle cannot be overwritten: alias conflicts are unresolved
computation errors. They are never retried as observed nonmatches.

## Additional v2 conditioning

The local decision coordinate is already public: every native action revision
equals the number of logged combat actions, including pending choices. After
validating that relationship and the declared range, inference conditions this
coordinate directly. Its prior factor is constant for the root and cancels; the
global combat index is never inferred from audit fields.

For an unmodified opening roster with unique monster types, HP proposals use the
native unique-HP draw itself. The public target values fix a conservative maximum
bucket size for each monster across all possible earlier creation orders. The
proposal forces one word in the desired available-value bucket, then corrects by
its actual bucket size divided by that public-root maximum. Earlier HP values of
all monster types count, and exhausted-range fallback retains native semantics.
The 54 audited A10 range entries are checked against native metadata; the table
certifies acceleration and never assigns gameplay HP outside the original method.

Only the explicit first-combat/one-floor prior also admits Neow conditioning.
Fresh Silent starts with RingOfTheSnake, the public source script takes the first
unlocked positive option, and the first map row is a monster. An exact two-relic
entry identifies the selected Neow positive. The proposal conditions Neow's own
native Type shuffle, checks its 52 valid pool shapes, and retains the varying pool
sizes 14–16 in one exact rational envelope. Cancelling each pool's own prefix
probability would bias the hidden curse/coin contexts; the finite tests detect it.
Other priors retain normal Neow generation.

Neow, HP and combat-shuffle corrections multiply because each envelope is constant
for the same public root. Every required native interception must be complete
before packet rejection or any random correction draw. Missing hooks, duplicate
interceptions, changed replay words and shared-cell conflicts are computation
errors, not rejection opportunities.

## Privacy, accounting and validation

Inference receives only a detached public packet and prior settings. The actual
source graph is disposed before sampling; its seed, tape and audit trace are never
inputs to the inference constructor. Branches replay their own hypothetical run
and forced tape, then resume only their own native coroutine. Policies continue
to see only public packets.

Every predeclared source draw remains in the report, including absent, cancelled
and failed draws. Each assigned teacher world/action retains its result or explicit
unresolved status. Errors, cleanup failures and alias conflicts cannot be silently
retried. Reference-comparison failures do not erase a completed source record.
Resource masks and existing objective calibration remain unchanged.

Validation includes exact enumeration of 32,768 primitive tapes with duplicate
cards and unequal native bucket masses; native forced-shuffle replay through
settlement; fixed combat/local-decision coordinates; absent mass; equal-state alias
failure; public-input freezing; and cancellation accounting. Ordinary sequential
Core reference vectors also pass. The bounded native benchmark is separately
predeclared in `artifacts/reports/conditional-tape/opening-benchmark-predeclaration.json`.
Its opening scope does not establish later carry-in throughput or production quality.

The first v1 predeclared eight-root probe completed all eight attempts in 274.10 seconds
with about133 MiB peak worker memory. Two roots qualified for the first certificate;
one of them accepted both assigned independent worlds, and all14 of its candidate
branches settled. The other146 allocated branches remained explicitly truncated.
The unconditioned reference accepted0/512 proposals under the same new prior.
There were no engine errors. The declared gate required four fully settled roots
and failed with one, so production expansion remains paused. These unequal proposal
budgets do not support a measured speedup-factor claim.

All eight records were independently checked against the production loader and
rejected. The full native suite passes1132 cases; Core passes4489 with3 existing
opt-in skips. The [verification report](../configs/conditional_native_tape_verification.json)
retains exact requests, artifact hashes, per-source exclusions and unresolved mass.
The v2 iteration keeps those same public roots, adds safe startup coverage and the
corrections above, and settles all 160 branches. Replaying after the completion
guard fix preserves every public packet, target and outcome. The final replay took
24.81 seconds; it is a repeatability check, not new independent data or a speedup
estimate. All 12 records from the opening and later probes fail production admission.

The separately predeclared later probe drew four roots from three combats and eight
local decision positions. All four existed. Each of its eight posterior calls
terminated on a proposed run that exceeded the native event-choice limit, retaining
22 `EngineError` branches and all 128 proposal attempts. This is a recoverable
outside-combat policy problem, not an estimate of posterior acceptance probability.
The existing script and its artifacts remain versioned while the fix is developed.

The final native suite passes 1191 cases. The current Core suite reports 4493 passed
and 3 existing opt-in skips; original log/TRX were not saved for that run, and the
vendor evidence explicitly records this limitation alongside verified source and
assembly equivalence. No new training, production corpus or model promotion occurs.

## Reproduction

Use .NET9 and isolated outputs, leaving frozen pilot binaries intact:

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/conditional-tape-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~NativeTapeReplayTests|FullyQualifiedName~ConditionalShuffleProposalTests|FullyQualifiedName~NativeInitialShuffleConditionTests'
```

The worker accepts a bounded request with `op: native_tape_replay`, a declared
`NativeTapeCollectionOptions` object and the ordinary teacher options. It enforces
at most16 source draws,16 evaluation/exploration worlds,4096 proposals per draw,
300 continuation decisions and900 wall seconds. These are engineering bounds,
not permission for production admission or training.
