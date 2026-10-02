# Balanced constructed-source recipe v5 (prepared, not executed)

This opt-in recipe implements the allocation proposed in
[the distribution review](NEXT_RECIPE_DISTRIBUTION_REVIEW.md). It is a future M5
patch for review after the current pilot. Preparing this patch does not authorize
another real corpus or training run. No C#, model, schema, objective, posterior,
resource-price table or runtime binary is changed.

## Allocation contract

Select `--recipe-version balanced-category-v5`. Every global ten-battle block
keeps four starter, four single-card, one potion and one relic sources. The four
enemies and four public phase requests remain unchanged. Only frozen T0 and
`public-phase-v1` are supported by this new recipe. Existing world, decision,
timeout and memory defaults remain unchanged.

Catalog permutations sort IDs by SHA-256 of canonical JSON
`[recipe_version, seed_prefix, domain, ID]`, with ID as the hash-collision
tie-break. A category's own ordinal determines its primary's epoch and position;
enemy position is `(position + epoch) % 4` in a separately permuted enemy list.
Thus every primary sees every enemy in its first four occurrences. For cards,
`(epoch // 4 + epoch % 2 + card_hash_bit) % 2` gives all eight enemy/upgrade
combinations in eight occurrences. The recipe explicitly rejects a catalog
other than 86 distinct eligible cards, each with maximum upgrade level one.

Potion and relic supporting cards each use an independent permuted 86-card
cycle. Their upgrade bit alternates on each cycle. HP, low HP, enemy HP and
simulator setup seed use independent named hash domains. This removes the old
allocation coupling; it does not promise exhaustive item/card interactions.
All allocation is stateless: battle index, recipe, catalog and seed prefix
determine a scenario, independently of partition count, order, speed and results.

## Predeclared attempted-battle blocks

The new mode requires both `--attempted-battle-start S` and
`--attempted-battle-count N`, with `S >= 0` and `N > 0`. It attempts the half-open
global battle interval `[S, S + N)`. Every battle has all four phase slots:
opening, first player turn two, first player turn three, and first pending choice.
Global source index is `4 * battle_index + phase_slot`; each shard handles indices
whose remainder modulo shard count equals its shard ID.

`--target-roots` is an optional diagnostic count in this mode and never stops a
block early. Optional `--max-attempts` must cover every planned phase request in
that partition or the run is rejected before a worker starts. It counts actual
journaled attempts, including recovered failures, rather than absolute indices.
Empty partitions and all-failure partitions have valid empty decision streams.
The existing `PAUSE_REQUESTED` boundary remains resumable and is not completion.

An unavailable requested phase remains a failure; it is never replaced by an
earlier root. Interrupted source/teacher requests remain explicit interrupted
attempts with unknown or partial timing. Existing WAL, fsync, raw-outcome archives,
record validation and crash recovery are retained. Recovery finishes durable
commits and skips journaled requests, including failures, before taking the next
unattempted index in the same immutable block.

The immutable `generation_config.json` contains the full source catalog, exact
permutations, named domains, formulas, phase/partition algorithm, seed prefix,
teacher/source/worker budgets, runtime/validator hashes and the block bounds.
`generation_recipe.py` stores the exact generator bytes and is hash verified.
Each attempt records its requested setup, primary/card allocation and phase;
saved rows carry the same information under audit-only fields.

The stage report's separate `attempted_plan_completion` diagnostic compares
arithmetically counted immutable assignments with validated, globally unique
journal indices per supplied partition. It reports expected, processed and missing
indices, absent partition counts, and `COMPLETE` or `INCOMPLETE` per partition and
block. A block is complete only when every declared partition is supplied and
every assigned index is journaled, including partitions with zero requests.
Failures and interruptions count as processed requests but remain explicit
failures, never usable roots. Missing requests in a running snapshot do not fail
structural integrity or become game losses. Check both structural integrity and
plan completion before enlargement; the diagnostic does not trust `progress.json`.
Legacy success-quota corpora receive `NOT_APPLICABLE`.

Use separate output directories for new blocks. Only the operational block bounds
and execution partition are excluded from balanced label identity. Disjoint
blocks with identical recipe, seed, budgets and runtime can therefore be reported
and prepared together. Overlapping battle indices retain the same source IDs and
are detected by global source/public-input/provenance checks. Changing recipe,
seed, source bytes, catalogs, runtime or labeling budgets changes label identity.
Changing a block or partition in an existing output directory fails immutable
config comparison.

`legacy-index-v4` remains the default and preserves the old scenario allocation
and successful-root stop. This source revision has a new generator fingerprint;
it cannot silently resume a frozen old corpus, even when legacy allocation is
selected. `--recover-only` can still reconcile durable old facts without running
an old recipe or creating new labels. Old and new recipe records cannot mix.

## Dry verification and remaining gates

The stdlib/mock-worker generation tests count finite prefixes and cross-tabs,
check every primary/enemy and card/enemy/upgrade combination, independently
exercise supporting-card cycles, compare one/seven-way and reversed execution,
and test nonzero/empty blocks, success/failure stopping, pause/recovery boundaries,
budgets, incompatible recipes and report/preparation compatibility. No simulator
or optimizer is used. Run with:

```sh
python -B -m unittest discover -s tests/generation -v
python -B -m unittest discover -s tests/data -v
```

Planned support matches the review: 1,250 battles cover 500 card/enemy/upgrade,
125 potion/enemy and 125 relic/enemy cells; 2,500 cover 688/250/250; 7,500 cover
688/256/750; 11,720 cover 688/256/1,172. These are requested allocations, never
completed effective-root counts or evidence of natural reachability.

Before any further real stage, independently review this patch and predeclare
its block. Before enlargement, audit planned/attempted/phase-reached/saved/unique
usable/full-value/ranking support, missing timings, failures and failed costs,
duplicates and provenance. Preserve the frozen holdout. Four-enemy constructed
coverage, selective phase availability, bounded posterior support and uncalibrated
resource objectives still limit claims; this patch does not establish formal
training readiness or resolve those limits.

## Explicit continuation selection

Generator version `nosl-real-pilot-generation-v7` adds
`--continuation-policy nosl-public-rules-v2`. The default remains
`nosl-public-rules-v1`. The chosen identity controls both the public source
continuation and the frozen teacher continuation, and is part of the immutable
configuration. Changing it requires a new output corpus; v2 also carries its own
teacher dataset identity. Recipe balancing and continuation selection are separate
versioned choices.

The generator requires a positive `continuation_policies` response before any v2
source reset, because old workers can silently ignore unfamiliar JSON fields.
It also verifies the returned T0/T1 continuation family and v2 dataset version
before accepting a record. Capability or identity mismatch stops the stage after
one durable failed attempt, with no game-loss label or accepted data. Legacy
source commands remain unchanged. Use a worker built from the reviewed v2 source;
do not rebuild binaries beneath active generators or rewrite old cohort manifests.

The reviewed v2 policy only changes its documented simple cycle family; it falls
back to v1 elsewhere. See [native policy evidence](PUBLIC_CONTINUATION_V2_EVIDENCE.md).
This selector does not justify broader generation or certify resource values.

The timeout now covers the entire source attempt: worker startup, capability check,
reset, all public source-continuation requests and final teacher response share one
deadline. Each request is additionally bounded, and partial JSON lines are read
nonblockingly so they cannot evade the deadline. Oversized responses fail explicitly.
Worker time/RSS limits are frozen for both recipe modes. Durable storage and shutdown
add overhead; a separate declared job-level process guard is still appropriate.
Short real subprocess tests cover stalled partial lines, cumulative fast requests,
expired attempts and complete large responses. A timeout stays computationally
inconclusive and retains its requested-world accounting.
