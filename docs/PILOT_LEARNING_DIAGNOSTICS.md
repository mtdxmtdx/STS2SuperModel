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
phase population. Joint group keys are JSON `[category, phase]` pairs so sparse
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
source battles; ties, illegal/single candidates; split leakage; hard bounds;
test-byte integrity without test-outcome parsing; read-only execution; and
provenance/source/weight tampering. Temporary synthetic bundles contain random
initialization for loader tests only: no fitting or optimizer step is performed.
