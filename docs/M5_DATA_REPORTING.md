# M5 streaming stage diagnostics

`tools/report_data_stage.py` reads an existing generator corpus without modifying,
repairing, relabeling, copying, generating, or training data. It recomputes totals
from `decisions.jsonl` and `attempts.jsonl`; it does not sum `progress.json` claims.

## Commands

```sh
# One engineering corpus, with its inline raw outcome facts verified
python -B tools/report_data_stage.py artifacts/data/engineering-200-v2 \
  --verify-outcomes > /tmp/engineering-stage-report.json

# Discover compatible shard-* children and verify their raw gzip references
python -B tools/report_data_stage.py artifacts/data/pilot-5000-v1 \
  --verify-outcomes > /tmp/pilot-stage-report.json

# Explicit shard paths work too
python -B tools/report_data_stage.py \
  artifacts/data/pilot-5000-v1/shard-0 \
  artifacts/data/pilot-5000-v1/shard-1

python -B -m unittest discover -s tests/data -p test_report_data_stage.py -v
```

The Python API is `report_stage(paths, verify_outcomes=False, ...) -> dict`.
The JSON report goes to stdout. Exit code 0 means the requested read-only
integrity checks passed; exit code 2 means incompatible configuration, corrupt or
invalid evidence, incomplete journal reconciliation, or a changing source file.
This is not a readiness, full-content, calibrated-objective, or trained-strength
verdict. Without `--verify-outcomes`, raw evidence is explicitly `NOT_CHECKED`.

Engineering and pilot corpora have different configuration identities and must be
reported in separate calls. Compatible shards must have identical complete
`generation_config.json` contents except `execution_partition`. Partition counts
must match and shard IDs must be distinct. The saved generation recipe hash and
each decision's generation-configuration and stable version fields are checked.
The report identifies current validation code/config hashes separately from the
historical generation fingerprints.

## Statistical units and masks

- `decision_rows` is the input record count; invalid rows never become effective
  roots or contribute effective world totals
- `valid_decision_rows` includes structurally valid diagnostic records with no
  positive-weight usable targets. `diagnostic_only_decision_rows` counts those
  separately; `usable_decision_rows` counts the remaining records
- `unique_valid_public_roots` includes all structurally valid roots and uses the
  same versioned public-only semantic identity as M5 preparation and train/test
  isolation. Numeric 1 and 1.0 are representation-equivalent; additional public
  representation rules are below
- `unique_effective_public_roots` counts distinct identities with a usable record.
  `diagnostic_only_public_roots` counts identities with no usable record anywhere
  in the supplied shards. These two disjoint counts sum to unique valid roots
- Deduplication is global across the supplied shards. The first structurally valid
  record supplies unique-valid diagnostics; the first usable whole record supplies
  effective targets independently. An earlier all-masked record cannot suppress a
  later usable record of the same public input. Labels from repeated records are
  never pooled or fabricated
- `completed_whole_candidate_roots` requires conserved, nonempty, fully completed
  terminal accounting for every candidate
- `full_utility_roots` additionally requires a valid value mask for every action;
  `all_head_complete_roots` requires all six heads for every action
- `partial_value_roots` have some value supervision, but not full-candidate value
  coverage. `auxiliary_only_no_value_roots` have usable auxiliary supervision and
  no valid value head. Both exclude diagnostic-only rows
- `strong_pair_labels` counts emitted pairwise labels. Empirical value heads alone
  are counted separately and do not establish statistically separated rankings
- Root evaluation-world draws and correlated action-world copies are separate
  totals. Multiple decision roots from one source battle are not independent games
- Source runs, battles, and branch families are counted globally by identity.
  Attempted battles use the shared corpus seed namespace and source battle index;
  summing per-shard battle counts is incorrect when shards cover different roots
  of the same battle

Full-utility targets can use the provisional, uncalibrated engineering objective.
A valid target mask is not approval of that objective or proof of optimal play.

`prepare_dataset.validate_record(..., require_usable=False)` disables only the
final usable-target requirement. All public/target schemas, masks, provenance,
version/seed checks, conserved action counts, aggregate unresolved/error counts,
and cost checks still run. Reporting then performs its ordinary generation-config,
source-index, version-equality and optional raw-evidence checks. Default preparation
keeps `require_usable=True` and rejects diagnostic-only roots for training.

Valid diagnostic rows reconcile their accepted journals and contribute measured
resource costs. They do not enter effective throughput numerators, training counts,
phase/health distributions or observed-content coverage. `worlds` is explicitly
scoped to unique usable roots, alongside separately named failed/interrupted
uncommitted budgets. `saved_outcome_accounting` exposes completed, truncated,
engine-error and other action-world mass for all valid records, diagnostic-only
records, and first-record unique valid roots. Record-level accounting includes
duplicate attempts; it is workload evidence, not a count of effective points.
Truncated/error outcomes never become game losses or invented numeric targets.

`source_distributions.unique_valid_roots.posterior_profile` and
`source_distributions.unique_effective_roots.posterior_profile` report the saved
audit field on their respective deduplicated populations. Historical records
without that field remain `undeclared`; runtime paths are never inferred
retroactively.

## Public identity v2 and frozen-state compatibility

`python/nosl/public_identity.py` is the shared pure-stdlib implementation used by
the generator, M5 preparation/reporting, and training split checks. Its explicit
scheme is `nosl.public-identity.v2`:

- Normalize actual finite integral numeric leaves, including negative zero;
  booleans and numeric-looking categorical strings remain distinct
- Parse/canonicalize valid finite JSON payloads only in public history `detail`;
  arbitrary categorical text and other string-valued fields are not parsed
- Treat `unknownDraw` as an unordered multiset, combining identical canonical card
  entries by count; normalize `knownDraw` container order while preserving the
  card-to-position association
- Ignore public candidate revision and action-history revision **only in a fresh
  identity copy**. These anti-stale transport tokens remain intact in every stored
  and executed action payload
- Preserve hand order, candidate order, ordered selections, public history order,
  public turns, controller deadlines and all other meaningful information

Generic configuration/content hashes are unchanged. Generator configurations bind
the scheme and the helper-file fingerprint. M5 preparation is now
`nosl.dataset.prepare.v3`; its immutable lock/manifest binds the identity scheme.
Old frozen prepared states cannot silently resume under the new grouping rules:
rebuild a new prepared corpus from all preserved raw provenance instead of
rewriting the old test holdout. Historical raw corpora can be reported under the
new recomputed identity, with their stored scheme (or its absence) reported
separately. Old stored digests are never declared valid as new-scheme digests.

## Generator durability and explicit recovery

The reporting tool remains read-only. Generator recovery is a **separate mutating
operation**, which repairs only explicitly recoverable commit gaps:

```sh
# Only when recovery is intended and no generator owns this corpus lock
python -B tools/generate_pilot_data.py --output PATH_TO_EXISTING_CORPUS --recover-only
```

This command starts no worker/simulator and executes no saved recipe. It validates
the saved configuration, original recipe hash, record schemas/provenance, and raw
outcome integrity before reconstructing a missing accepted journal from durable
facts. Current code/config changes cannot silently restart an older corpus;
ordinary generation still requires exact configuration equality.

The new generator uses one fsynced `inflight_attempt.json` per locked worker:

1. Save source intent before reset/continuation
2. Save requested teacher-world budgets before the teacher request can start
3. Archive raw outcomes, then save the validated whole record in inflight state
4. Commit the decision row, then a final measured journal intent
5. Commit the attempt journal, then clear inflight state

A crash before a durable result becomes `interrupted_attempt`, with execution
uncertainty explicit. Requested budgets are not completed worlds or losses. A
saved validated result can be committed without rerunning a simulation. A crash
after row persistence but before the accepted journal is repaired idempotently;
a crash after the journal but before cleanup never duplicates that journal.
Storage failures are not rewritten as game failures.

Legacy durable rows without inflight metadata receive explicitly recovered
journals. Missing total elapsed time stays null; real checkpoint durations are
retained as partial lower bounds, and a saved final measured duration can be
replayed as measured. Teacher rollout time is never substituted for missing
reset/replay/attempt time. The report separates measured sums from partial lower
bounds and returns null total service time/throughput whenever any attempt time
is missing. Historical missing budgets stay unknown unless recoverable from the
frozen requested-world configuration and the durable candidate count.

Partial first or final JSONL records are streamed to uniquely named
`.partial.<timestamp>` backups before truncating the incomplete tail. Complete
malformed records are never silently deleted. Recovery cannot invent an entirely
missing decision, raw outcome, or provenance edge discarded by an older generator.

All valid duplicate whole decision records, raw refs, and original battle/run/
branch identities are now retained. Their journals are `accepted` with an explicit
duplicate flag; only the first valid unique public identity counts toward local
unique/effective targets, including resume. This preserves duplicate openings as
transitive links between divergent later battle states for M5 union-before-dedup.
Global cross-shard uniqueness is still recomputed by preparation/reporting rather
than assumed from the sum of worker counters.

## Phase, health, and decision-boundary distribution

`root_state_distribution` is computed only from globally deduplicated usable public
roots. Duplicate attempts, diagnostic-only rows and invalid rows cannot inflate
its histograms or fractions. It contains:

- Player-turn histogram and turn-one/later-turn concentration fractions
- Declared `generation_source_step` histogram, separate actual
  `generation_decision_index` histogram, and a separate public action-revision
  histogram. Missing actual decision indices remain `missing`; no recipe index or
  anti-stale token is silently substituted
- Current HP changed/lost/gained versus combat-start HP, with root counts and
  fractions, plus later-turn roots with net HP loss
- Pending-choice roots and fraction, using the actual public choice object
- Current HP/current max-HP buckets: [0, 10%], (10%, 25%], (25%, 50%],
  (50%, 75%], and (75%, 100%], including net-HP-loss counts within each bucket
- Hand, discard, exhaust, and draw-pile size histograms, root counts, minima,
  maxima, and means. Draw size uses public `drawCount`, so unidentified draw cards
  are included

HP comparisons describe **net HP at the root**. Starting a constructed battle at
low HP does not establish an after-damage state. A root that lost HP and healed
back to its starting amount is not counted as net-loss; these counts are not a
reconstruction of every historical damage or healing event.

Turn/index histograms are exact through 1000 and have an explicit `1001_plus`
overflow display bin. This is not a simulation turn limit. Missing and malformed
audit indices are visible rather than converted to zero.

The adequacy section reports concrete zero-support observations and measured
concentration, such as no later-turn roots, no net-HP-loss roots, or no pending
choices. It always requires task-specific distribution review and applies no
invented universal pass percentage. The HP bins are descriptive summaries, not
mandatory safety/adequacy cutoffs. Passing schema, accounting, or raw-evidence
checks alone does not establish a useful phase/history/health distribution.

For example, the read-only verified/deduplicated `pilot-5000-v2` snapshot at
approximately 15:15 UTC on 2026-10-01 contained 449 roots: 448 on turn 1, one on
turn 2, and none with HP different from combat-start HP or a pending choice.
All 449 raw outcome references passed. Although 52 roots had at most 25% of
current max HP, each started at that same HP; these were not net-HP-loss roots. Its source steps were only 0, 1, and 2. This documents the old
short-prefix recipe's limitation, not a result for any replacement phase-stratified
recipe. The tool now exposes the same evidence on the validated/deduplicated
population directly; use a fresh report for a later or replacement corpus.

## Resource accounting

Journal accepted, failed, and duplicate-attempt elapsed times are separate.
Teacher-record elapsed, clone, settlement, and rollout-decision measurements are
also separate: journal times include reset/replay and protocol overhead.

`clone_seconds` currently includes both `BeliefSampler.SampleWorldAsync` and
`ForkForContinuationAsync`; its fraction describes belief-sampling and branch
preparation, not pure object copying. Clone/settlement fractions are only reported
when the necessary costs are present for every valid decision record.

A four-worker sum is service time, not wall-clock duration. The report leaves wall
clock null because these artifacts do not establish a synchronized run interval.
Peak RSS is the maximum reported process high-water mark in valid records;
failure-only peak RSS is unavailable. Missing timers/counters are reported rather
than silently converted into measured zero.

Failed requested action-worlds are uncommitted work. They are not completed
worlds, game losses, or deaths. Saved `reset:` JSON is parsed to separate an
already-terminal source boundary from explicit unsupported-capability/content or
engine-error responses. A terminal source means the combat ended before the
requested later decision; no defeat is inferred. Malformed or unknown reset
responses stay unclassified rather than being called engine bugs. If some failure journals lack that field, its total
is null and the observed partial sum/missing-row count remain visible. Completion
fractions for committed roots are explicitly scoped to that population.

## Raw evidence verification and bounded memory

`--verify-outcomes` supports older inline `outcome_samples` and independent gzip
members referenced by `outcome_samples_ref`. It checks path confinement, byte
ranges, exactly one complete gzip member, uncompressed byte count, SHA-256,
action alignment, sample counts, and Win/Loss/truncated/error/nonterminating
accounting against each target row. It does not recalculate utility, certify the
sampler, or establish original-client fidelity.

Global root/source identities are indexed in temporary SQLite, with a 4 MiB page
cache and disk-backed temporary work. The implementation holds one JSONL record
and optionally one bounded outcome member at a time. It does not load a 100k-root
corpus into a Python record list. Disk index size grows with identities, while
record working memory is bounded independently of corpus length. Reports contain
aggregate content vocabularies and at most 30 error examples, not all source rows.

Default line and raw-outcome limits are 64 MiB each; `--max-line-mib` and
`--max-outcome-mib` set explicit alternatives. These limits bound individual encoded
inputs, not total Python object RSS. Oversized/malformed/unterminated records are
reported and excluded without truncating or repairing source files. JSONL hashes
and before/after file-state checks identify changes during reporting, including a
final recheck of earlier shards after the whole scan. A live corpus can be read,
but a changed or journal-inconsistent snapshot is explicitly not a clean audit.

No 100k-root end-to-end runtime or memory benchmark is claimed by these tests.

## Observed scope

Content coverage uses actual public root card piles, known/unknown draw-card
multisets, choices/bundles, potions, relics, and enemies. Requested but unobserved
recipe contents do not count. Upgrades are listed separately. Root presence is not
mechanism, interaction, natural-reachability, or full-content verification. Source
kinds and declared curriculum categories remain explicit.

## Read-only snapshot checked on 2026-10-01

These figures describe existing artifacts at the time of this check, not a later
resumed stage. Both reports passed integrity and all available raw facts were
verified: 200 inline records and 666 gzip-referenced records.

| Metric | engineering-200-v2 | paused pilot-5000-v1 |
|---|---:|---:|
| Attempts | 209 | 703 |
| Unique complete-candidate roots | 200 | 666 |
| Full-utility roots | 177 | 590 |
| Partial-value roots | 17 | 65 |
| Auxiliary-only roots without value | 6 | 11 |
| Accepted source battles/runs | 68 | 260 |
| Globally distinct attempted battles | 70 | 267 |
| Timeouts / other failed attempts | 5 / 4 | 17 / 20 |
| Root evaluation-world draws | 800 | 2,664 |
| Correlated action-world copies | 4,164 | 13,804 |
| Emitted strong pairwise labels | 0 | 0 |
| Empirical-value-only roots | 194 | 655 |
| Observed cards / potions / relics / enemies | 35 / 7 / 6 / 4 | 89 / 28 / 19 / 4 |
| Accepted journal service seconds | 1,180.05 | 4,828.76 |
| Failed journal service seconds | 233.62 | 838.90 |
| Belief-sampling/branch fraction of teacher time | 96.92% | 96.25% |
| Settlement fraction of teacher time | 0.15% | 0.18% |
| Maximum reported worker RSS, MiB | 142.02 | 131.51 |

The paused pilot's 37 failed attempts separate into 17 timeouts, 11 already-terminal
source boundaries, 3 explicit unsupported-capability responses, and 6 public-history
validation rejections. It has zero reset responses classified as engine errors.
The engineering corpus's four non-timeout failures are terminal source boundaries.

All these sources are declared constructed scenarios. The paused pilot has 666
roots, not 5,000 completed roots; its directory name is a target, not a result.
No model was trained or evaluated by the reporting tool.

### Completed phase-gate v3 diagnostic reconciliation

A read-only check of `pilot-5000-v3` at approximately 16:25 UTC on 2026-10-01
verified all 215 raw gzip references and all 524 attempt journals, with zero
integrity errors, invalid rows or unmatched accepted journals. This is a bounded
phase-gate corpus; its directory name does not establish 5,000 usable roots.

- 215 distinct structurally valid saved roots: 204 usable and 11 diagnostic-only
- All 204 usable roots have complete whole-candidate accounting: 183 full utility,
  20 partial-value and one auxiliary-only; zero strong pairwise labels
- The 11 diagnostic-only roots preserve 240 action-world copies: 169 completed,
  71 compute-truncated, zero engine-error and zero other outcomes. They supply no
  effective targets; the 169 individual completions do not make those whole roots
  usable
- Effective world accounting: 816 root evaluation-world draws and 4,756 completed
  correlated action-world copies. All valid records together preserve 860 root
  draws and 4,996 action-world copies
- Usable-root turns: 83 in turn 1, 99 in turn 2 and 22 in turn 3; 50 net-HP-loss
  roots and three pending-choice roots
- 215 accepted journals and 309 failed attempts: 85 timeouts, 221 unavailable
  requested phases and three explicit unsupported responses
- Every posterior profile remains `undeclared` in this historical corpus

Schema, conservation and raw-evidence checks pass. Distribution adequacy and
training readiness remain separate decisions.

## Public phase collection

Generator `nosl-real-pilot-generation-v2` supports `public-phase-v1`: four source
slots per battle request the opening, first public boundary in player turn 2,
first public boundary in player turn 3, and first pending card choice. Each rule
is fixed before source play and uses only information already observed at that
boundary. The collector does not inspect future outcomes or sample a favorable
position from a completed trajectory. Every seventh declared constructed battle
starts at 1–12 HP; this is explicitly a low-health construction, not damage that
occurred during combat or natural-run reachability.

`generation_source_step` names the requested slot. `generation_decision_index`
is the actual number of source actions already executed, and
`generation_source_phase` records the fixed rule. If a combat ends before a phase
or the bounded source policy never reaches it, its absence remains in the attempt
ledger. There is no substitution with an easier opening, no invented terminal
label, and no counting the absent point as effective data. A first-choice root may
coincide with another requested phase; global public-state deduplication still
counts it once. Snapshot timing is a public stopping rule, not future-seed evidence.

Reproduce a bounded initial phase-quality gate with the current frozen runtime:

```sh
python tools/generate_pilot_data.py --output artifacts/data/phase-gate \
  --mode pilot --target-roots 200 --max-attempts 1000 --worlds 4 \
  --root-policy public-phase-v1 --roots-per-battle 4 --max-source-decisions 160 \
  --timeout 45 --max-worker-mib 768 --seed-prefix nosl-phase-gate-v1
python tools/report_data_stage.py artifacts/data/phase-gate --verify-outcomes
```

Old `pilot-5000-v1` (666 roots) and `pilot-5000-v2` (449 roots) remain preserved
opening diagnostics, not silently relabeled or combined with the corrected
runtime/recipe. The v2 audit found 448/449 roots in turn 1, no net HP changes, and
no choices. Passing schema/accounting checks alone did not establish adequate
full-combat coverage. The corrected corpus requires its own measured phase audit
before the 5,000-point pilot fit.
