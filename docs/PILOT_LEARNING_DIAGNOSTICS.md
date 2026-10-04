# Read-only pilot learning diagnostics

`tools/report_pilot_learning.py` reports descriptive evidence from a **completed,
bounded, experimental unpromoted bundle** and the exact immutable M5 prepared
corpus used by that bundle. It never trains, constructs an optimizer, chooses a
checkpoint, generates battles, promotes a model, or edits its inputs. No formal
training, calibration, readiness, or battle-strength claim follows from a pass.

```sh
PYTHONPATH=python ../.venv-nosl/bin/python tools/report_pilot_learning.py \
  --prepared artifacts/prepared/SEALED-PILOT \
  --bundle artifacts/experiments/COMPLETED-PILOT \
  --max-records 10000 --max-loaded-mib 512 --max-line-mib 8 \
  > /tmp/pilot-learning-report.json
```

The paths are placeholders. The tool refuses an absent/incomplete bundle or an
empty train/validation split. A changed config, weights, implementation/runtime,
prepared manifest, shard, provenance, split identity, or validation denominator
is a rejection, not a reason to silently pick another model or easier subset.
Run in a fresh dedicated CPU process. Both PyTorch thread pools are set to one;
no simulator or C# build is invoked.

## Frozen input and test boundary

- Loading reuses `nosl.data.prepared_paths`, including its immutable stage-chain,
  shard checksums, frozen-test references, and split-state checks. This shared
  integrity routine **hashes sealed test bytes**. Hashing bytes is not evaluating
  or selecting from test outcomes.
- Only train and validation JSONL records are parsed. Test targets never enter
  a metric, support filter, constant fit, or checkpoint decision. Raw outcome
  archives/references are not opened. The report records zero test target records
  parsed and explicitly discloses test-byte integrity hashing.
- Train/validation isolation is independently checked by recomputed public input
  identity, source run, source battle, branch family, and audit public-state ID.
- `Inference.from_bundle(..., allow_experimental=True)` verifies the inference
  bundle. Reporting additionally requires the full frozen training/evaluation
  source hash map to match, then recomputes final validation using `nosl.train.evaluate`.
- Manifest, config, weight, reporter-source, implementation/runtime, and parsed
  shard hashes accompany the report. Per-root support includes public-input and
  target hashes, source groups, legal indices, value-mask indices/weights, and
  ranking eligibility. Audit metadata never becomes model input.

Checksums establish consistency with the supplied artifacts, not a signature
from an external authority. The report does not reconstruct or independently
authenticate the historical random initialization.

## Learning comparisons

`before_fit_vs_final_same_validation_support` compares the bundle's stored
zero-step `validation_before_fit` with final metrics recomputed from its verified
weights. Every metric's count, root population, unresolved-action count, and
ranking population/semantics must match. Recomputed final values must also match
the stored final values within float tolerance. Missing before-fit metrics are
rejected rather than regenerated from fitted weights. Deltas are final minus
before-fit: smaller regression/Brier/loss values are better; larger agreement is
better. There are no before-fit strata unless those were separately recorded.

Validation is additionally broken down by source category, declared source phase,
actual observed turn, posterior profile, source battle, and the joint category ×
phase and category × observed enemy-ID composition populations. Phase joint group
keys are JSON `[category, phase]` pairs so sparse
later nonstarter populations cannot disappear inside marginal totals. Declared phase and
actual turn are distinct fields. Every stratum includes its total roots, distinct
source battles, source-run groups, branch families, candidate counts, pair-label
support, and all four mutually exclusive utility-availability classes:

1. `full_utility`: every legal candidate has an available utility target
2. `partial_utility`: some but not all legal candidates have utility
3. `auxiliary_only`: no utility, but at least one auxiliary target is available
4. `missing_utility_and_auxiliary`: no usable target masks at all

Zero-weight utility candidates are counted explicitly. `no_value_roots` includes
both classes 3 and 4; `missing_value_candidates` also includes partially labeled
roots. Train support is shown separately with the same strata and counts.
Distinct battle IDs expose the number of source groups; multiple roots from a
battle are correlated and battles may share source-run groups. These counts are
not a claim of statistical independence or a basis for invented confidence bounds.

`source_category_and_observed_enemy_ids` uses JSON `[category, sorted_enemy_ids]`
keys derived only from `public_input.observation.enemies`. Sorting removes list
order but preserves repeated IDs, so two copies of an enemy remain distinct from
one. This is the **current observed composition**, not an inferred initial or
naturally encountered battle; dead, spawned, or transformed enemies can make
those differ. The slice includes all the existing support and metric fields.

The first pilot's constructed source recipe uses `index % 10` for category and
`index % 4` for enemy allocation. Potion sources therefore use only `TwigSlimeS`
or `Nibbit`, and relic sources only `LeafSlimeS` or `TwigSlimeM`. Category or phase
marginals alone hide this construction constraint. Before scale-up, a separately
versioned generator needs independent enemy allocation and explicit checks of
item/category × enemy coverage. This diagnostic does not change the frozen
generator, repair missing coverage, or treat this first pilot as representative
of natural encounters.

The first 203-root generation snapshot reported during development had 187 full,
14 partial, 2 auxiliary-only roots, zero strong pair labels, and all 15 potion
roots lacked complete utility. Those are historical **whole-generation** counts,
not hard-coded expectations for a later prepared train/validation split. The
report computes each actual population and preserves zero and missing support;
a zero-eligible potion ranking stratum remains visible with null ranking metrics.

## TRAIN-only constant utility regression

The descriptive constant predictor uses only training value targets. Let `n_r`
be the number of value-masked candidates at root `r`, including any zero-weight
rows, `w_ri` its sample weight (default 1), and `y_ri` teacher utility. The constant
is `sum_r sum_i(w_ri * y_ri / n_r) / sum_r sum_i(w_ri / n_r)`.

This minimizes the value head's per-root mean of weighted candidate squared
errors, matching the mean-valued training objective's masks and denominators.
Unavailable values contribute nothing and are never replaced with zero. The
value-head `/100` scaling and fixed loss coefficient do not change this minimizer.
No positive-weight training utility support is a hard rejection. Validation
labels cannot change this constant.

The learned predictor and constant are evaluated on identical validation masks:

- `value_mae` / `value_mse`: unweighted candidate means and counts, matching the
  stored validation metrics. Units are teacher utility and squared utility
- `normalized_root_weighted_*`: errors weighted by `w_ri / n_r`, normalized by
  their summed coefficient, with that coefficient sum reported
- `root_mean_weighted_value_mse`: `sum_r sum_i(w_ri * error^2 / n_r) / root_count`,
  including zero-contribution roots, in raw utility units. Divide by 10,000 and
  apply the configured value loss weight to match the value term's scale

This constant is a regression reference, not an action-selection policy, rollout
win-rate baseline, or estimate of playing strength.

## Empirical utility ranking, and its missing support

The report uses the actual inference `selected_index`, selected by the highest
predicted legal utility. Mean regret, best-action agreement, and selected-action
rank are computed **only if at least two legal candidates exist and every legal
candidate has an available value**. It never substitutes the best available
subset or removes an unresolved competing action. Illegal candidates do not
create missing-label exclusions; single-action roots do not inflate agreement.

- Regret: maximum legal empirical teacher mean minus the selected action's mean
- Best-action agreement: selected mean within `1e-9` of the best empirical mean
- Rank: 1 plus the number of legal means exceeding selected mean by more than
  `1e-9`; tied best actions share rank 1

All are small-N descriptive quantities. Selecting the largest sampled mean can
be optimistic. No uncertainty correction, certified confidence, new strong
pairwise label, or student rollout result is implied. Pair-label counts stay
separate. Incomplete-root and single-action exclusions are reported in every
stratum, along with all incomplete roots even when a root has only one action.

Every train and validation summary also includes `empirical_ranking_label_support`
on that same complete, multi-action population:

- `eligible_roots` and `legal_candidate_count_distribution`: the population size
  and histogram of legal candidates per eligible root
- `utility_spread`: minimum, mean, maximum, and count of per-root maximum minus
  minimum empirical utility; absent support gives null values and count zero
- `all_equal_mean_roots` and `nonzero_spread_roots`: spread at most `1e-9`, or
  greater than `1e-9`, respectively
- `roots_with_best_mean_ties` and `best_mean_candidate_count_distribution`: roots
  with multiple means within `1e-9` of the maximum, and a histogram of the number
  of such candidates (including one for a unique best mean)

Histogram keys are string candidate counts and values are root counts. These
descriptive fields are present overall and in every existing stratum, even when
there are no eligible roots. Incomplete roots contribute neither spread nor tie
counts, even if their available subset is flat or has a large spread.

All-equal empirical means make every legal choice agree with the sampled best
and yield zero regret (or at most `1e-9` under the tolerance). High agreement on
these roots does not show action-dependent learning. The original ranking metrics
and their denominators are preserved. Additional validation fields
`nonzero_spread_empirical_teacher_mean_regret` and
`nonzero_spread_empirical_teacher_best_action_agreement` use only eligible roots
whose spread exceeds `1e-9`; they report their own counts and null values for no
support. Tied best actions still count as agreement in this view when other legal
actions have lower means. Neither flat nor nonzero spread establishes true action
equivalence, certified preferences, or learning; no label or target is changed.

`non_end_turn_empirical_contrast` exposes a narrower descriptive root slice in
every train/validation summary. It first requires available values for **all legal
candidates**, including `end_turn`, and at least two legal candidates whose public
action kind is not `end_turn`. A single play plus `end_turn` does not qualify.
Other action kinds are retained; this is not a forced-card or play-only policy.

- `complete_roots_with_two_or_more_non_end_turn_candidates` counts this population
- `non_end_turn_utility_spread` gives minimum, mean, maximum, and count of the
  empirical spread among those non-end-turn candidates, including tied roots
- `all_equal_non_end_turn_mean_roots` counts spread at most `1e-9`;
  `nonzero_non_end_turn_spread_roots` counts spread greater than `1e-9`

Within that block, validation's `full_policy_empirical_teacher_mean_regret` and
`full_policy_empirical_teacher_best_action_agreement` cover only the roots with
nonzero non-end-turn spread. They retain the actual full-policy `selected_index`
and compare its mean against **all legal candidates**, including `end_turn`.
For example, with non-end-turn means 5 and 1 and an end-turn mean of 7, selecting
end-turn has regret 0 and agreement 1; selecting the action valued 5 has regret 2
and agreement 0. If end-turn instead has mean 0, selecting it has regret 5.
There is no replacement selection or removal of end-turn from policy evaluation.
No qualifying roots yields null metrics with count zero. This slice preserves
the original eligibility, metrics, and labels, and does not certify that the
sampled non-end-turn contrasts are real preferences or evidence of learning.

## Bounds and tests

The report holds at most 10,000 train-plus-validation records. Defaults are 512
MiB total parsed input bytes and 8 MiB per line; hard maxima are 1 GiB and 32 MiB.
Exceeding any bound rejects the entire report, never truncates or samples. JSON
objects consume more memory than their encoded bytes; use the enclosing pilot's
external memory/time cap where needed. Shared integrity hashing is streaming and
may read more bytes than the train/validation parse cap. Results go to stdout;
shell redirection alone creates the requested report file.

```sh
PYTHONDONTWRITEBYTECODE=1 PYTHONPATH=python ../.venv-nosl/bin/python \
  -m unittest discover -s tests/python -p test_pilot_reporting.py -v
```

Tests cover masks, weights and constant means; exact same-support errors; all
availability classes and missing potion ranking support; declared/actual phases;
source battles; flat/distinct means, best-mean ties and tolerance boundaries;
candidate-count distributions; current enemy composition, order and multiplicity;
non-end-turn contrast support with actual full-policy end-turn selections;
incomplete-root exclusions, illegal/single candidates; split leakage; hard bounds;
test-byte integrity without test-outcome parsing; read-only execution; and
provenance/source/weight tampering. Temporary synthetic bundles contain random
initialization for loader tests only: no fitting or optimizer step is performed.
