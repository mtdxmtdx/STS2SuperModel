# Completed balanced validation block

The predeclared 40-battle, 160-attempt block completed with **73 unique effective
decision points**, plus eight fully masked diagnostic records. It validates the
bounded generation and audit machinery. It does **not** pass the gate for a larger
training/data stage: resource labels remain missing, posterior timeouts censor
later phases, and there are no confidence-certified preference pairs.

[Machine-readable evidence](../configs/m5_balanced_validation_report.json) binds
the original [plan](../configs/m5_balanced_validation_plan.json), runtime and raw
reports. No new optimizer steps ran; the first experimental trial remains at 497.

## Exact accounting

The attempted source allocation was 16 starter, 16 single-card, four potion and
four relic battles, balanced over four simple enemies. Every source requested its
opening, turn two, turn three and first pending choice once. The 160 indices are
present exactly once; unavailable phases were not replaced to meet a success quota.

| Requested phase | Attempts | Saved records | Whole-attempt timeouts | Phase unavailable |
|---|---:|---:|---:|---:|
| Opening | 40 | 40 | 0 | 0 |
| Turn two | 40 | 27 | 11 | 2 |
| Turn three | 40 | 12 | 14 | 14 |
| First choice | 40 | 2 | 1 | 37 |
| Total | 160 | 81 | 26 | 53 |

The 73 effective roots include 68 with all candidate utility targets, four with
partial utility and one with auxiliary targets only. Eight other records are
wholly masked and remain diagnostic. A strict whole-decision training preparer
can conservatively classify the four partial-utility roots as auxiliary-only;
their individual raw masks are preserved. These are 40 source groups, not 73
independent battles. Thirty-two roots are later-turn decisions, 20 follow net HP
loss, and two are pending choices.

The effective roots use 1,168 root-world draws and 6,880 complete candidate/world
copies. Across **all 81 saved records**, 7,568 allocated copies comprise 7,450
settled outcomes and 118 truncations. The eight diagnostic roots account for 688
of these copies, including all 118 truncations. Another 2,432 requested copies
belong to timed-out, uncommitted attempts. None is converted into a defeat, zero
utility or a completed observation. All 81 raw archive references verify, with
zero invalid records, semantic duplicates, orphan journals or missing indices.

The four processes exited normally after about 34 minutes elapsed. Their summed
service wall time was 4,496.48 seconds, CPU time 4,483.11 seconds, and largest
process peak RSS 162.99 MiB. No memory or outer time guard fired. Reconstruction
and branch preparation consumed 95.29% of accepted teacher time. Assignment by
index modulo four placed one phase on each worker, leaving uneven workload;
aggregate CPU/service time must not be confused with elapsed time. This is a
quality/conditioning bottleneck, not evidence that available RAM is exhausted.

## What sixteen worlds establish

The fixed first-four versus disjoint remaining-twelve diagnostic admits 68 fully
valued roots, 397 actions and 35 source groups. All legal candidates and complete
world mass remain present. Recomputed full-sixteen values match native targets
within 2.28e-13.

Twenty-nine of these roots have a nonzero empirical contrast after excluding
end-turn solely from the descriptive comparison. Across 677 correlated action
pairs there are six strict sign reversals, all from one source group, and no
changed best-action sets. Keeping actual end-turn gives 54 roots with a contrast,
six reversals among 1,001 pairs, and two changed but overlapping best-action sets.
Sample equality is not certified action equivalence.

No strong pairs or equivalent sets are certified. For an action with zero deaths
in sixteen IID conditional samples, even the marginal two-sided 95% exact-binomial
upper endpoint is about 20.59%. This cannot resolve the user's 1% hypothetical
risk tradeoff. Correlated action copies cannot be pooled to claim tighter risk
confidence. All potion-category roots fail the complete-utility comparison gate;
this block cannot demonstrate learned potion preferences.

Uncertified pairs alone do not prohibit an empirical pilot: the implementation
already trains valid empirical mean targets with the appropriate masks. The
decision to pause expansion also depends on absent resource supervision,
computationally selected coverage, the small independent-group count and the
first pilot's measured resource mistakes. It is not a newly imposed requirement
that every regression row have a certified ranking label.

## Version and holdout boundaries

The block uses public continuation v2 and **belief dispatcher v2**, source commit
`399d4103ca9a687d2391d36b7c56c05a053696be`. The later
[Sly posterior extension](CONSTRUCTED_SLY_POSTERIOR.md) is dispatcher v3 and does
not upgrade these records. All effective roots contain DefendSilent, so none
satisfies the narrow v2 cycle certificate at the observed root. Continuation-wide
certificate activation was not instrumented. This report does not attribute the
results to that fix or compare v4 and v5 as if they had identical source support.

The independent isolation audit unions all 81 decisions and 160 attempts before
filtering. It finds zero links to old train, validation or test components. The
old 494 test targets remain unread and byte-identical; protection retains all
524 semantic digests and 244 components associated with that split, including
rejected/diagnostic aliases. All new records have now been inspected for
development. A newly assigned test partition of this small block is consequently
development data, not a fresh blind benchmark.

The [metadata-only cross-version import](../tests/data/README.md) now preserves
those old split aliases without copying or reading old targets. A new development
preparation unions all 160 journals and 81 decisions, retains 73 usable roots
(57 train / 10 validation / 6 development-test), and quarantines eight masked
records. Its manifest SHA is
`adf545cd6ddd13d00a1323ae42318cea4b24797c2d59928b199fb9a3b7ed7db1`.
Zero actual old-test collisions occur in this block; 24 dedicated regressions
exercise synthetic collisions and tampering. An invalid or journal-only later
bridge permanently blocks loading/appending even when both split names are test,
while preserving existing shard bytes.

All 129 data and 165 student engineering tests pass after integration. The exact
original learned bundle still loads with unchanged inference-source hashes.
Changing the loader correctly changes its training-source fingerprint, so the
old training checkpoint cannot silently resume under this new code. No new fit
or optimizer steps ran; old source and checkpoint recovery artifacts remain saved.

## Stage decision

Keep the reusable pipeline and recovery artifacts, and stop automatic enlargement
at this gate. The completed first trial already gives a usable engineering fit
and exposes a specific resource applicability failure. Another fit on these 73
roots would add little independent validation support and would not supply the
missing potion target values. Larger counts cannot remedy that absence.

The [confirmed feedback](CONFIRMED_PREFERENCE_FEEDBACK.md) adds two precise
constraints, with no global risk budget or invented potion price. The supported
next technical steps are separately versioned resource/relative supervision and
targeted conditional-posterior coverage, with a new predeclared validation gate
before scale. Full M3 calibration and broad M5/M6 policy acceptance stay open;
formal training and model promotion have not occurred.
