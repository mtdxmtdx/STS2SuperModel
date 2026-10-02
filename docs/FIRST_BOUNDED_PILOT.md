# First bounded pilot: results and remaining gates

The v4 constructed corpus reached 5,000 globally unique effective decision points.
One small experimental fit completed; its fixed one-epoch budget is exhausted.
This demonstrates the real data-to-checkpoint-to-native-evaluation path. It does
not establish full M3–M6 acceptance, formal training readiness or a stronger policy.

## Data and isolation

There were 10,131 attempts over 2,628 source battles. The 5,311 saved valid records
contain 5,007 usable records and 304 all-masked diagnostic records. Seven duplicate
usable public roots across shards leave exactly 5,000 effective roots from 2,513
accepted source battles: 4,585 fully valued, 359 partially valued and 56 with only
auxiliary targets. These last two groups must not be combined and called 415
auxiliary-only roots.

There are 2,438 later-turn roots, 1,233 following net HP loss and 211 pending
choices. The usable roots contain 20,000 independent root-world draws and 116,084
settled candidate/world copies; these units are distinct. Strong preference pairs
and certified equivalence sets are both zero. Every objective profile is still
uncalibrated. All saved raw archives and attempt/record links verify, with no
invalid or orphan records.

The ledger retains 1,178 computational timeouts, 3,522 requested phases that had
already become unavailable, 76 unsupported resets, 36 other reset-context errors,
one then-unsupported Quest schema rejection and seven real process interruptions.
Missing interruption timings stay unknown. The corrected Quest admission is
covered separately; it does not retroactively change the old dataset.

Preparation sealed 3,976 train, 530 validation and 494 test roots under manifest
`4346cb9643900138fe12fa6bd64cacb17b01bd383a178ef3e90b29bac3dbe997`.
Test targets were not used for fitting, diagnostics or model selection. The
plan checked source seeds and initial composition against source audit fields;
byte integrity checking does not imply test-target evaluation.

## Bounded fit and reproducibility

The 976,838-parameter model used seed 1729 on CPU, with maximum 700 optimizer steps,
one epoch and 5,000 train-root budget. It stopped at **497 steps and one epoch**.
The first-step checkpoint was retained and resumed under the same lifetime budget.
An independently captured zero-step validation matches the trainer's initial
validation exactly. This verifies actual resume execution, not an unperformed
uninterrupted-versus-resumed bit-equivalence experiment.

The two training invocations took 274.91 seconds active wall time and 274.53 CPU
seconds in total, with peak child RSS 872.16 MiB. They ran on one CPU affinity with
a 4 GiB virtual-memory cap. Training source is commit
`8ad033030205f064946f7ac42572fe640edf23fc`; the frozen native data runtime is
`5ef76a558afd54bc38a78ff6635c3b50ce3c0c6f`. Python/PyTorch, model source, dataset,
configuration and weights identities are retained. Inference sources remain
unchanged so this bundle remains reproducible. Formal training and promotion
flags are false.

The local retained bundle is `artifacts/experiments/pilot-v4-first-trial`, including
`checkpoint.pt`, `first-step-checkpoint.pt`, `weights.pt`, configuration, manifest
and metrics. Exact file hashes and report paths are in
[the compact evidence manifest](../configs/m5_first_pilot_report.json).
These large experimental artifacts are ignored by Git; their presence is not a
claim that the draft PR contains trained weights.

## Same-support validation

| Metric | Initial | After one epoch | Support |
|---|---:|---:|---:|
| Utility MSE | 72,959.44 | 39,844.98 | 2,936 valued candidates |
| Utility MAE | 91.76 | 99.79 | Same 2,936 candidates |
| HP MAE | 29.83 | 4.13 | 3,073 candidates |
| Death Brier | 0.24343 | 0.03904 | 3,073 candidates |
| Empirical mean regret | 12.17 | 10.54 | 481 complete-value roots |
| Empirical best-mean agreement | 83.16% | 83.99% | Same 481 roots |
| Regret on non-end-turn contrast subset | 32.71 | 28.33 | 179 roots |
| Best-mean agreement on that subset | 54.75% | 56.98% | Same 179 roots |

The last two rows still assess the actual selected action, including end turn,
against all legal candidates. The subset is a support diagnostic, not candidate
filtering. Overall agreement is inflated by ties: 97 roots have all-equal means
and 426 have a best-mean tie. Every estimate comes from four teacher worlds, with
no certified preference confidence.

An independent reconstruction reproduces the initial/final metrics. The 2,653
sampled no-death candidates worsen MAE from 14.12 to 55.97, adding 37.82 to global
MAE. Mixed-death and all-death candidates contribute -7.97 and -21.81, respectively.
Their large squared-error reductions drive the aggregate MSE improvement. This
is a measured tradeoff, not a change in evaluated support or a proof that the
regression is harmless. The fitted model beats a train-only constant baseline on
both MAE and MSE, but still underestimates the negative tail.

Only 34 complete-value roots improve empirical regret, 30 worsen and 417 tie.
Single-card sources improve; starter and the small pending-choice subset worsen.
All 193 valued potion-category validation candidates are play/end-turn actions.
Every one of the 44 potion-use and 44 potion-discard actions lacks utility, and
no potion root permits a complete ranking comparison. That missing supervision
cannot be repaired by pretending a potion has a universal nine-HP price.

## Actual closed-loop outcomes

Before observing learned outcomes, a separate checksummed plan froze eight
constructed scenarios with two independent source seeds each. Baseline and
student each completed all 16 battles: 32 actual rollouts, no unresolved or
invalid outcomes, and 16 wins each. Mean terminal HP was 23.5625 for baseline and
26.375 for student. The student also consumed four additional potions, so the
2.8125-HP difference is not an established net utility advantage.

Both low-HP poison traces immediately consumed SwiftPotion with zero draw cards
and an empty discard pile. The subsequent native packet confirms zero new cards.
This is a concrete resource-use mistake. FirePotion was also consumed in both
poison-guard sources. Comparisons with the different baseline policy do not
identify the causal benefit of those uses; a separately marked diagnostic
intervention is required. Individual HP regressions and discard-choice mistakes
remain visible even though all these small battles were won.

A posthoc intervention subsequently rescored the unchanged complete public inputs
and legal candidate sets, selecting the highest frozen score excluding potion use
and potion discard only at the final selector. This diagnostic is distinct from
the abstaining policy guard. All four already-observed potion sources still won,
retaining all four potions: terminal HP changed 24→21 and 24→24 on the Fire sources,
and 12→12 and 7→12 on the Swift sources. All 92 decisions preserve original inputs
and full candidate scores. The four runs took 6.53 seconds with sampled process-tree
peak RSS 328.66 MiB. There were no new optimizer steps.

These are observed whole-policy tradeoffs, not expected marginal benefits or a
population estimate; policies can consume different RNG after diverging. The raw
model also ranks potion discard above every card at both Fire openings, so a veto
on use alone would not repair the behavior. In the second Swift source, a later
public root shows two energy, incoming 12 damage and both remaining draw cards
known as Defends. Playing Fumes before Backflip leaves no energy for the drawn
Defends; drawing first permits 14 block. Missing resource utility and inaccurate
choice ranking are distinct issues. Training contains one valued use and one
valued discard outside the potion category, so training supervision is almost
absent rather than literally zero.

These 16 sources become observed diagnostic cases after this run. Any subsequent
intervention on them is not new held-out strength evidence. The sealed 494-root
test remains unused.

## Next decisions

The reviewed [balanced v5 recipe](BALANCED_GENERATION_V5.md) and complete attempted
battle blocks are implemented and pass 51 generation and 104 data tests. The new
recipe has not generated a cohort yet. The old periodic category/enemy coupling
and computational censoring are documented in
[the distribution review](NEXT_RECIPE_DISTRIBUTION_REVIEW.md).
Keep attempted, phase-reached, stored, unique, fully valued and confident-pair
counts separate. Further 30k/100k generation requires a reason it improves missing
support or label precision.

The new [experimental policy wrapper](EXPERIMENTAL_POLICY_GUARD.md) now abstains
for legal potion use/discard, explicit prior potion use/discard in public history,
and observed gold changes. It has no fallback, preserves the frozen model and
requires opt-in for diagnostic predictions. All 165 Python tests pass, including
17 focused guard tests; an independent review verified the boundary. Passing the
narrow screen does not certify other decisions. Diagnostic head predictions
cannot supply missing applicability. Preserve explicit HP/death and
potion-consideration evidence without inventing future-resource prices. Investigate
discard-choice and defensive-loop continuations, then obtain more independent
paired teacher evaluations on informative complete legal action sets. A new fit,
if warranted, must be another separately bounded experimental run. No formal
training or model promotion follows automatically from these results.
