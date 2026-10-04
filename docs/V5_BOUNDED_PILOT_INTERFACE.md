# Full-public v5 bounded auxiliary pilot interface

This implements the additive M6 training/checkpoint interface in
`docs/spec/v4/PLAN_NOSL_FULL_COMBAT_V4.md`. It does not complete M7 or authorize a
formal run. No reviewed real v5 cohort currently exists. Failed diagnostic
captures, the old corpus and synthetic wire fixtures cannot qualify as training
data. No backward or optimizer step was run to verify this implementation; the
previously authorized single-backward check is not repeated.

## Fixed experiment and default behavior

`configs/student.v5.bounded-pilot.json` binds the full existing v5 student config
digest and fixes the CPU seed, batch size, AdamW parameters and maximum budgets.
The student config and v1–v5 model/schema contracts remain unchanged. The budget
is **at most one epoch and 700 optimizer steps per checkpoint chain**, with
bounded train/validation root counts. There is no formal mode, automatic budget
increase, policy export, model selection on test or promotion path.

Run readiness without fitting or writing weights:

```sh
PYTHONPATH=python python tools/train_pilot_v5.py
PYTHONPATH=python python tools/train_pilot_v5.py --prepared /path/to/reviewed-v5-snapshot
```

The first command returns `FIT_BLOCKED` until an exact reviewed snapshot and run
authorization are available. `--forward-check` additionally evaluates validation
records under `torch.no_grad()`. `--engineering-fixture` permits only a synthetic
dry-run; combining it with execution is rejected.

## Future execution requirements

The executable path requires matching `PreparedDatasetV5` train/validation
instances with purpose `bounded-pilot`. Their existing loader revalidates the
reviewed cohort receipt, protection registry, attempts denominator, transitive
source separation, schema, full public graph, auxiliary masks, source versions
and snapshot hashes. The pilot also freezes its own source/runtime, model,
config, receipt/protection and exact train/validation data identities.

The run authorization is a separate operator-supplied JSON record, bound to the
`frozen_inputs_sha256` returned by readiness. It records the user's already
authorized bounded experiment; the program neither manufactures it nor treats
dataset admission as permission to fit. Do not request new chat permission just
because this record is required. Hashes are integrity locks, not proof that a
human actually reviewed the cohort. The execution operator must supply the real
review and approved run record, never relabel a synthetic fixture.

Required exact fields:

```json
{
  "format": "nosl.student.bounded-pilot.authorization.v5",
  "authorized": true,
  "purpose": "bounded-pilot",
  "authorization_id": "<approved bounded experiment identity>",
  "reviewed_by": "<reviewer identity>",
  "approval_kind": "reviewed_real_cohort",
  "frozen_inputs_sha256": "<readiness digest>",
  "admission_sha256": "<reviewed snapshot admission digest>",
  "protection_sha256": "<reviewed snapshot protection digest>",
  "review_evidence_sha256": "<receipt review evidence digest>",
  "pilot_config_sha256": "<fixed pilot config digest>",
  "max_epochs": 1,
  "max_optimizer_steps": 700
}
```

Only after the cohort quality gates pass, a separately invoked bounded run is:

```sh
PYTHONPATH=python python tools/train_pilot_v5.py \
  --prepared /path/to/reviewed-v5-snapshot \
  --authorization /path/to/approved-run.json \
  --output /path/to/new-experimental-run --execute-bounded-pilot
```

An existing output directory is not overwritten for a new run. `--resume` uses
its `checkpoint.pt` and requires identical data, source/runtime, configurations,
receipt/protection, authorization and lifetime budget. A deterministic shuffle
and cumulative cursor prevent epoch or step counters from resetting on resume.
`--stop-after-steps N` limits new work in that invocation while preserving the
original lifetime cap. Exhausted checkpoints perform no further steps.

## Checkpoints and validation

The separate `nosl.experimental.auxiliary-checkpoint.v5` format stores model and
AdamW state, torch RNG, cumulative progress and every frozen identity. It uses
`torch.load(..., weights_only=True)` and rejects legacy schemas, changed locks,
nonfinite state, altered optimizer settings, fabricated progress and promotion
claims. Serialized AdamW parameter IDs/order and raw moment shapes/dtypes are
checked before loading, and loaded moments are checked for finiteness before
mutating the caller's model or optimizer. A zero-step engineering checkpoint roundtrip is supported, explicitly
untrained. It does not grant future fitting permission. As with any local
checkpoint format, validation provides consistency rather than cryptographic
authentication against an attacker who rewrites all data and hashes.

Validation only reads the prepared validation split. Its optional predictions
expose available auxiliary heads with `selected_action: null` and
`policy_ready: false`; masked targets remain unavailable. Probabilities refer to
the pinned teacher continuation and declared prior, not pure-student play.
Utility, ranking and finite-plan targets remain masked and are never used for
action recommendations. Utility and finite-plan head parameters are excluded
from AdamW, preventing weight decay on unavailable heads. Shared encoder updates
can still alter their outputs, which remain unusable as learned policy scores.
Existing `nosl.inference_v5` is unchanged and continues
to abstain even when someone supplies a forged trained claim. No deployment
bundle is produced by this interface.

Only train and validation targets enter the model. The prepared loader checks
its snapshot's test/candidate/quarantine files as opaque bytes; it never decodes
those targets. Historical frozen test protection stays metadata only. This
interface never opens or modifies the frozen 494 target records.

## Verification and remaining evidence

```sh
PYTHONPATH=python:tests/python:tests/data python -m unittest test_pilot_v5 -v
```

Tests install hard guards on `Tensor.backward`, `torch.autograd.backward`,
`Optimizer.step` and `AdamW.step`; positive execution is limited to zero-step
serialization, restoration and no-grad forward/loss diagnostics. Tests cover
input/receipt/protection/source/config changes, lifetime budget expansion,
optimizer/progress corruption, fixture execution rejection, CLI defaults and
inference abstention, including raw moment casting overflow and same-shape
parameter-ID permutation regressions. Synthetic validator objects test authorization field
matching only; they are not an accepted native cohort.

Still required before a future pilot: a fresh real cohort satisfying the
predeclared quality and source-protection gates, its reviewed receipt, and the
matching bounded experiment record. Actual learning, learned-checkpoint resume,
validation improvement, standalone learned performance and full-range readiness
are unverified. Auxiliary learning alone will not establish a policy-ready model.
