# Distribution review before the next data stage

The first pilot is a bounded constructed-source engineering experiment. Neither
its root count nor its retained completion rate establishes broad interaction
coverage. This review is based on the stopped, recovered v4 snapshot at
`artifacts/reports/m3-m6/pilot-v4-recovered-2742.json`: all 14 recorded decision and
attempt byte-prefix hashes were verified before inspecting those prefixes.
Later appended records are outside these counts.

## Observed support and missingness

There are 2,856 saved records, including 114 diagnostic-only records. The 2,742
usable records contain four cross-shard duplicates, leaving 2,738 globally unique
effective roots from 1,390 source battles. All saved raw references verify, and
there are no invalid or orphan records. Seven interrupted attempts are recorded
with unknown or partial timings, rather than converted to game losses.

- 2,514 roots have values for every legal candidate; 198 are partially valued and
  26 have auxiliary targets only
- Of the complete roots, 2,512 have at least two legal candidates and 2,008 have
  nonzero empirical utility spread
- 960 complete roots still have nonzero spread among the remaining candidates
  when end-turn is removed from the contrast: 480 starter, 440 single-card and
  40 relic roots. This is an additional support diagnostic, not a rule to remove
  end-turn from policy evaluation
- All 209 potion-category roots lack complete legal-candidate value coverage
- There are zero certified strong pairs and zero certified equivalence sets.
  Unequal four-world means are noisy regression evidence; zero strong pairs
  does not mean actions are equivalent

All 723 timeouts are nonstarter cases; 712 are later-phase requests. The journal
shows that teacher labeling had begun, but does not identify the internal stage
at which each timeout occurred. These are separate from 1,932 cases where the
source combat ended before the requested public phase.

| Requested turn-three category | Attempts | Saved records | Timeouts | Ended before phase | Reset failure | Interruption |
|---|---:|---:|---:|---:|---:|---:|
| Potion | 139 | 12 | 87 | 40 | 0 | 0 |
| Relic | 139 | 2 | 69 | 52 | 15 | 1 |

Saved records can include diagnostic-only rows. The next report must show each
stage separately: planned, attempted, phase reached, saved, globally unique
usable, fully valued, and confidence-certified ranking support.

## Deterministic allocation coupling

The v4 recipe derives category from `index % 10`, enemy from `index % 4`, and
focus/inventory IDs from the same block counter. Consequently:

- Potion cases use only TwigSlimeS or Nibbit; relic cases use only LeafSlimeS or
  TwigSlimeM. Each potion remains tied to one enemy across its 64-item cycle
- Potion and relic focus cards occupy even and odd catalog positions,
  respectively, permanently excluding half of the 86-card catalog in each group
- Six focus slots advance only four card positions per ten-battle block, causing
  overlap between item focuses and the next block's plain-card focuses
- Upgrade draws share a stream with HP draws; entering the low-HP branch changes
  the later upgrade draw. This is avoidable implementation coupling, not proof
  that every resulting marginal is biased

The current report exposes category by current observed enemy composition.
That composition is not asserted to be the initial encounter. Future allocation
reports also need declared primary item/card, enemy, upgrade and requested phase.

## Proposed next recipe, not yet executed

Keep the 40/40/10/10 category mixture, existing four enemies, public stopping
times, teacher, objective and compute budgets initially fixed. Use a separately
versioned recipe with deterministic, domain-separated catalog permutations.
For a category's ordinal `q`, let its primary catalog be the starter marker,
86 cards, 64 potions or 293 relics. Permute that catalog once for the recipe;
write `epoch, position = divmod(q, catalog_size)`. Select the primary at that
position and the enemy at `(position + epoch) % 4` in a separately permuted
enemy list. Every primary then sees all four enemies in its first four occurrences.

For single-card sources, the current cards all have maximum upgrade level one.
An upgrade schedule `(epoch // 4 + epoch % 2 + card_hash_bit) % 2` covers all
eight enemy/upgrade combinations in eight occurrences. The implementation must
assert the supported upgrade domain instead of silently assuming future catalogs
have the same shape.

Supporting cards for potion and relic sources should use their own per-category
permuted 86-card cycles, with a cycle-based upgrade bit. This removes the permanent
parity restriction. It does not promise all item/card/enemy interactions at pilot
scale. HP and enemy HP draws, supporting-card allocation and simulator setup seeds
must use separate named domains. Persist the exact catalogs, algorithm, domains,
seed prefix, budgets and recipe bytes.

Static counting of this proposed schedule gives the following planned coverage.
These are attempted battles and requested phases, never completed effective roots.

| Attempted battles | Requested phases | Card/enemy/upgrade cells | Potion/enemy cells | Relic/enemy cells |
|---|---:|---:|---:|---:|
| 1,250 | 5,000 | 500 / 688 | 125 / 256 | 125 / 1,172 |
| 2,500 | 10,000 | 688 / 688 | 250 / 256 | 250 / 1,172 |
| 7,500 | 30,000 | 688 / 688 | 256 / 256 | 750 / 1,172 |

At a ten-percent relic mixture, scheduling all 293 relics against four enemies
requires at least 11,720 attempted battles, or 46,880 requested phases. Retained
root totals cannot substitute for those cross-tabs or actual mechanism evidence.

## Gates before enlargement

Test exact allocation invariance across restart, process order and one/seven-way
partitioning. Exhaustively count finite prefixes, catalog/upgrade/enemy cells and
planned missing cells. No allocation may depend on outcomes, accepted counts,
worker speed, labels or posterior success.

Predeclare a global attempted-battle block and complete its scheduled phases.
The present per-shard successful-root stopping rule yields uneven source prefixes;
a root target can trigger review, but cannot establish balanced allocation. Never
replace a missing turn-three or choice phase with a convenient earlier root.

Before another 5k–10k stage, audit the bounded block's complete cross-tabs,
failure reasons, successful and failed costs, full-candidate value support,
empirical contrasts, semantic duplicates and provenance components. Keep all
missing timings and incomplete probability mass visible. Preserve the frozen
holdout and reject incompatible generation-config mixing.

Before 30k/100k, show why the next allocation improves useful missing support or
label precision. More four-world roots do not resolve systematic computation
censoring or missing resource prices. Consider the purpose of additional
independent evaluation worlds without stopping selectively when a result becomes
significant. Actual bounded policy evaluation and same-support value/ranking
diagnostics must precede enlargement; auxiliary loss alone is insufficient.

Even a corrected allocation remains a four-enemy constructed curriculum with
frozen-T0 values, selective phase availability, bounded posterior support and an
uncalibrated objective. It does not establish natural reachability, broad target
coverage or formal-training readiness.
