# Constructed Hunt evidence at the full-policy boundary

The source-backed finite Hunt producer now has a separate path into
`PolicyDatasetV5` and the actual `train_policy_v5.batch_loss`. The path accepts
newly executed constructed evidence solely for engineering inspection. It does
not make that evidence a natural-native candidate, an admitted corpus, or a fit
authorization.

## Exact record contract

`finite_hunt_policy_v5.adapt_record` consumes the exact fresh C# raw bytes from
the [core producer](FRESH_CONSTRUCTED_HUNT_V5.md). It validates the raw v5 input,
paired outcomes, immutable full anchor, original denominator and measured labels
before constructing the existing `nosl.dataset.full-policy.record.v5.1` envelope.
It does not convert an old v2, auxiliary, or prepared engineering envelope.

The new audit subtype has these exact claims:

- `purpose:engineering-fixture`, `source_kind:constructed_empirical_plan_fixture`
- `trainable:false`, `native_run:false`, `producer_receipt_sha256:null`
- The standard source groups, full public identity, target digest, and exact raw
  artifact/record hashes
- `constructed_evidence` containing the versioned format and unchanged `raw_utf8`
- No `actual_seed`, `source_draw_seed`, or `native_source_run_identity` fields

The genuine declared setup seed remains only inside the raw constructed audit.
It is not renamed or represented as a natural-native seed identity. Every call to
`data_policy_v5.validate_record` for this subtype independently regenerates the
entire normalized record from that exact raw evidence. Changing outer hashes
cannot conceal changed outcomes, targets, source claims, or raw evidence.

Objective identity hashes the actual checked-in candidate specifications and the
C#/Python evaluator sources. Its status remains candidate, calibration is false,
and calibration evidence is null. The evaluation-design hash binds the complete
declared setup, fixed independent draw plan, paired trajectories and public
evidence. The adapter requires those source files and fails closed if unavailable.
These hashes establish consistency, not producer authenticity.

The only enabled targets are the two whole-plan heads. Every action target and
ranking target remains unavailable. A successful TheHunt extra reward retains
its unpriced future utility. Real settled failure has a zero success target;
truncation retains a null target and its complete allocated denominator.

## Admission and execution remain closed

The existing engineering-purpose guard rejects fitting before opening prepared
data or constructing a training session/model. Mutating purpose, source kind,
native status, a producer receipt, objective calibration, or bound raw evidence
does not convert the record into an admissible pilot. Native and production
validators reject it. Untrained inference returns `MODEL_UNTRAINED` and selects
no action, including with the experimental flag.

Fresh means newly executed. It does not assert that a public root is novel or
unprotected by historical aliases. No historical isolation/cohort admission is
performed, no review/authorization receipt is created, and no historical sealed
row is opened. Whole-plan engineering targets do not establish trained policy
strength or safe learned finite-plan continuation.

## Historical and current verification

The original one-backward report at
`configs/student_v5_full_policy_connectivity.json` is retained byte-for-byte.
It remains a historical result for its exact **39-source** snapshot. It is not
relabelled as verification of the expanded current source set.

The current training-source closure contains **41 files**: 37 historical files
are unchanged, `data_policy_v5.py` adds this validation dispatch,
`train_policy_v5.py` adds the two producer modules to its source list, and
`finite_hunt_v5.py` / `finite_hunt_policy_v5.py` are new. Model, loss, schema,
public-identity, evidence-encoding and vocabulary files retain their historical
hashes. The `batch_loss` function's AST is unchanged despite its containing file's
updated source list.

Current integration has forward-only evidence using the exact current config and
the existing 1,573,534-parameter StudentV5. The actual full-policy batch loss is
0.8731998801231384: plan-success loss 0.8081371784210205 and extra-HP loss
0.06506272405385971. Every action/ranking term is zero. Parameter bytes are
unchanged; hard tripwires forbid backward, autograd and optimizer construction.
There are zero new backward calls, optimizer steps, gradients or saved weights.

Ten focused checks cover accepted engineering records, actual batch loss,
inference abstention, corrupt/resealed evidence and rejected admission/execution.
The unchanged full-policy and native-policy suites pass 31 and 17 checks.
These test counts overlap earlier verification and are not independent data.

```sh
python -B -m unittest discover -s tests/data -p test_finite_hunt_policy_v5.py -v
python -B tools/check_finite_hunt_policy_v5_forward.py \
  artifacts/fresh-finite-v5/fresh-finite-hunt-v5-success.raw.json
```

See [the seam report](FRESH_CONSTRUCTED_HUNT_FULL_POLICY_V5_VERIFICATION.json) for
source/evidence hashes and the exact current-versus-historical comparison. The
earlier backward authorization remains consumed and was not rerun.
