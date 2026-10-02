# Native conditional random-tape sampling

The `native_tape_replay` operation introduces an explicit, separately versioned
label prior. It addresses the rare initial-hand rejection factor while retaining
the upstream native run, event owners, card choices and terminal settlement.
The existing `owned_native_replay` operation and all frozen data keep their old
distribution. New tape records are development-only and rejected by the existing
production loader.

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

The first predeclared eight-root probe completed all eight attempts in274.10 seconds
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
Next engineering work addresses startup hooks that are irrelevant to the draw
prefix and public initial HP/intent constraints, with fresh distribution checks.

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
