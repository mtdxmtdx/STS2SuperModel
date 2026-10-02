# NOSL public student engineering (M6)

This directory contains the public-only student implementation. The first bounded
experimental pilot now completed one epoch and 497 optimizer steps on the audited
5,000-root constructed corpus. Weights are retained separately from Git, remain
unpromoted, and do not establish broad policy strength. See
[the full results and limits](../docs/FIRST_BOUNDED_PILOT.md).

The frozen v1 implementation remains unchanged. An explicit
[v2 public-context engineering path](../docs/STUDENT_V2_ENGINEERING.md) now consumes
finite Hunt anchors and new public native/event facts. It has separate nullable
whole-plan labels, no-optimizer smoke and standalone fail-closed inference;
it is untrained and is not yet integrated with production dataset preparation.

Engineering forward/backward tests still take zero optimizer steps. Formal
training remains disabled. Experimental selection should use the new
[resource abstention wrapper](../docs/EXPERIMENTAL_POLICY_GUARD.md); raw frozen
inference remains available for reproducible diagnostic evaluation.

## Environment and reproducible commands

Python 3.11+; verified with Python 3.12 and `torch==2.6.0+cpu` on Linux CPU. Use an
isolated environment. The official CPU wheel avoids CUDA packages:

```sh
python -m venv .venv
.venv/bin/python -m pip install --index-url https://download.pytorch.org/whl/cpu torch==2.6.0
PYTHONPATH=python .venv/bin/python -m unittest discover -s tests/python -v
PYTHONPATH=python .venv/bin/python -m nosl.train \
  --config configs/student.pilot.json \
  --data tests/python/fixtures/real-teacher-v1.jsonl --mode smoke
```

`pyproject.toml` also supports installing the Python package. The package's
inference import closure contains only Python stdlib/PyTorch and local schema/model
code. It does not import C# simulator bindings, the teacher, search, or dataset tools.
The optional NumPy-not-installed warning from PyTorch does not affect these tests;
this implementation does not call NumPy.

Regenerate the **controlled** vocabulary only from a newly reviewed, pinned adapter
registry, before preparing data (data manifests freeze the student config hash):

```sh
PYTHONPATH=python python -m nosl.vocabulary \
  --registry configs/coverage_manifest.json \
  --base-config configs/student.pilot.json --output configs/student.pilot.json
```

The committed snapshot comes from upstream `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`:
558 card IDs, 114 monster/pet IDs, 64 potions, 293 relics, 262 powers, 21 enchantments,
7 afflictions and 5 orbs. Registry admission does **not** establish complete
interaction/NOSL validation. Unknown IDs and fields are rejected. This is not a
permanent 11-card allowlist. Legacy public v1 observations remain restricted to
their original audited scope because they lack v2 mutable-state fields.

## Public boundary and architecture

`nosl.schema.validate_public(public_input, config)` is dependency-free. Input:

- `schema_version: nosl.student.public.v1`
- `observation`: exact camelCase C# `PublicObservation` (`nosl.public.v2` for broad scope)
- `history_complete: true`, full ordered public history beginning with combat start
- `controller_context: {status: inactive}`
- complete `candidate_actions` and aligned boolean `legal_mask`

All nested structures and JSON-encoded event details have explicit field
whitelists. Card effects/cost timing, power timing/applier, counters, relic state,
gold, orbs/pets and unidentified draw counts are encoded. No metadata dictionary
is merged into features. `audit_only`, labels, continuation IDs, teacher budgets,
seeds, uncertainty weights, and public-state grouping digests stay out of inputs.
The caller remains responsible for truthful history completeness and complete/legal
candidate enumeration; a neural network cannot establish those facts.

The model has 128-dimensional shared entity embeddings, light set pooling,
ordered GRUs for hand/history/selection, two-layer state and candidate MLPs.
Unknown draw entries are a count-weighted unordered set with no hidden-position
encoding. Known public positions are bound nonlinearly to their card; hand,
potion, enemy, choice slots and meaningful multi-selection order remain encoded.
Versioned signed hashing compresses **validated public** detail/history fields;
it is lossy and may collide, not a sufficiency proof for all mechanics.

The current snapshot has 976,838 trainable parameters, including native Quest cards. Output heads:

- one expected-utility/value head used directly as the action ranking score
- independent win and death logits
- expected terminal HP, 65-bin terminal HP distribution over `[0,512]`
- potion net-count change
- deterministic applicability/rejection status at the inference boundary

Auxiliary heads do not redefine the policy. Predicted probabilities refer to the
recorded teacher continuation, not measured pure-student rollout performance.
Brier metrics are reported; no probability calibration is claimed. Expected HP
and distribution heads are distinct regressions and need not perfectly agree.

Explicit present limits: inactive controller context only; specified-bonus plan
heads remain disabled until a public plan contract and labels exist. Limits are
256 entities/positions, 1024 history events, 4096 candidates, and HP support to512.
Inputs exceeding limits are rejected whole, never truncated or silently dropped.
Unidentified draw identities are represented only by their public count.

## Targets and data

`nosl.data.validate_targets(targets, public_input, config)` and
`DecisionDataset(path, config)` validate nullable per-action supervision. All
candidates must have a target row. False masks require explicit `null`, not zero.
Unresolved trajectory mass cannot be removed and the completed remainder
renormalized. Five world-accounting counts are conserved. Objective-unresolved
rows may retain complete outcome heads with value/ranking masked.

Optional per-action `sample_weight` and pairwise weights affect losses only.
Pairwise labels are used only when provided/certified by the teacher pipeline;
empty pairwise/equivalent sets are valid. Value regression still trains the exact
head used to rank actions. Equivalent-set loss optimizes total set probability.
HP histogram interpolation conserves both mass and mean.

`prepared_dataset(directory, split, config, expected_config_sha256)` verifies M5
immutable stage/shard hashes and frozen initial test references. Changes to a
prepared manifest, any split, config, or label provenance reject resume. Train,
validation and test are separately checked for overlap in run, source-combat,
branch-family and public-state groups. No test loss selects weights.

## Standalone inference

```sh
PYTHONPATH=python python -m nosl.inference --config configs/student.pilot.json < public-input.jsonl
```

Valid M6 inputs return `MODEL_UNTRAINED`; no random action is chosen. Invalid,
unsupported, missing history and no-decision inputs have distinct statuses.

The JSONL/API boundary also accepts an optional exact control envelope:

```json
{"decision_status":"terminal","public_input":null}
{"decision_status":"waiting","public_input":null}
```

These return `TERMINAL` or `WAITING`, with no selected action and no model call.
The status is explicitly supplied by the caller, never inferred from an empty
candidate list. Both require null `public_input`. For active boundaries, use
`decision_status: player_decision` or `card_choice` with the existing complete
strict public input as `public_input`; the status must agree with the presence
of a choice. Missing/extra keys, unknown statuses and contradictory envelopes
return `INVALID_INPUT`. Active envelopes with empty candidates are invalid;
plain legacy public input retains its existing `NO_DECISION` response. The
control envelope is never a model feature or a replacement for dataset schema.

A retained experimental bundle consists of `config.json`, `manifest.json`, `weights.pt`.
Configuration/weight checksums and strict state-dict loading are required;
`torch.load(..., weights_only=True)` avoids general pickle loading. Untrained and
unpromoted bundles are rejected by default. A separately authorized offline
pilot may explicitly request `--bundle DIR --allow-experimental`, returning
`EXPERIMENTAL_UNCALIBRATED`, never a promoted/confidence claim. No such trained
bundle is shipped here. No code auto-promotes a model.

## Bounded pilot trainer

The first authorized trial used 3,976 training roots from the audited 5,000-root
corpus, a maximum of 700 steps and one epoch, and stopped after 497 steps. Its
lifetime budget is exhausted. Further trials require their own declared bounded
plan and data-quality gates; the command below is a separate illustrative example. Formal training is disabled, including if a config
flag is changed. Pilot must specify all lifetime caps; current safety ceilings
are 10,000 roots, five epochs, 50,000 optimizer steps.

```sh
# Example only: replace limits with the specifically authorized experiment
PYTHONPATH=python python -m nosl.train --config configs/student.pilot.json \
  --prepared artifacts/prepared-pilot --mode pilot --confirm-pilot \
  --max-roots 5000 --max-epochs 1 --max-steps 625 --output artifacts/pilot-example
# Resume with identical command/config/data/budgets, adding --resume
```

Alternatively supply `--data train.jsonl --validation validation.jsonl --test
test.jsonl`. Engineering-smoke corpora cannot be used for pilot fitting. At least some
positive-weight value supervision is required; partial-value and auxiliary-only
roots retain their explicit masks. Complete-action value support is reported
separately for ranking diagnostics. Objective calibration may remain false, clearly
marked provisional/experimental. Pilot checkpoints are never production-ready.

Each atomic checkpoint stores model/optimizer state, torch/Python RNG, epoch,
shuffle order and offset, lifetime steps/caps, input/config hashes and versions.
Sequential decision microbatches bound activation memory while accumulating a
batch-mean gradient. Resume cannot reset/expand caps or change holdouts. Metrics
include masked Brier, HP MAE, pairwise/equivalent-set agreement, counts and
unresolved actions. Empty metric sets return null. The frozen test is not
scored during fitting; later independent evaluation is a separate stage.

The first real fit resumed its step-1 checkpoint through the same epoch/step
budget. Uninterrupted-versus-resumed parameter bit-equivalence was not tested.
No formal training has run.

## Measured engineering checks

Historical pre-Quest measurements: CPU, one torch thread, 976,710 parameters;
timings are one local engineering
measurement, not a promised full-dataset throughput:

- Real C# teacher root, 7 candidates/10 public events: forward+backward0.104s,
  process peak RSS about249MiB
- Eight repeats of that same root (not independent examples):56candidate rows/
  80events, forward+backward0.527s, peak RSS about258MiB

The actual one-epoch pilot took 274.91 seconds active wall time and peaked at
872.16 MiB RSS. Teacher generation and training costs remain distinct.
