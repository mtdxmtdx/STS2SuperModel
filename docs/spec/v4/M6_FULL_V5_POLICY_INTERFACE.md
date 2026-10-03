# M6 full-v5 policy engineering interface

This is the additive `nosl.full-policy.v5.1` engineering path. It uses the current
`StudentV5` and complete v5 public schema, including public run evidence, complete
map captures and exact finite-plan anchors. It does not modify or promote the
existing auxiliary `native_v5`, `pilot_v5`, `PreparedDatasetV5` or `InferenceV5`
artifacts. It provides code and contract verification, not a trained policy or a
claim that full-content M6 acceptance is complete.

## Entry points

- `nosl.data_policy_v5.PolicyDatasetV5`: immutable new objective records, explicitly
  limited to train/validation and a declared engineering, unadmitted native
  candidate or bounded-pilot purpose
- `nosl.train_policy_v5.PolicyTrainingSessionV5`: owns the exact inputs, model,
  deterministic one-pass order, optimizer and committed-supervision ledger
- `nosl.train_policy_v5.train_bounded`: usable bounded training loop with checkpoint,
  exact resume, validation diagnostics and unpromoted bundle export
- `nosl.inference_policy_v5.score_public`: full-v5 no-grad diagnostic heads, with
  no action selection or calibration claim, including for random initialization
- `nosl.inference_policy_v5.InferencePolicyV5`: independent bundle reader and
  default-abstaining JSONL action scorer

Neither inference entry point imports a dataset, trainer or simulator. Complete
public validation runs before any inherited mechanics projection. Every head is
checked for shape, CPU float32 dtype and finite values. The returned legal mask
must equal the public input exactly; ranking must equal value with only illegal
slots set to negative infinity. Illegal rows remain present with null predictions.

## New record contract

The exact top-level record fields are `format`, `public_input`, `targets`,
`audit_only`, and `objective`; format is
`nosl.dataset.full-policy.record.v5.1`. `data_policy_v5.validate_record` is the
authoritative constructor contract. Existing auxiliary, legacy, label-free
natural-source, quarantined or engineering-v5 envelopes are not promoted.
Explicit producer adapters regenerate this new envelope from their own exact
source formats.

Targets retain full-v5 nullable masks and action indices. Every action explicitly
accounts for allocated, completed, truncated, error and other worlds. Unresolved
mass cannot be normalized into supervised labels. Positive ranking pairs cannot
use zero-weight targets, and set supervision requires resolved positive-weight
objectives for every legal candidate. Unavailable finite-plan labels remain null
and masked; whole-plan labels require the exact original full-v5 active anchor.

Audit fields bind purpose, trainability, source kind, original run/combat/branch
groups, public state identity, original artifact and record hashes, full public
conditioning and target hashes, native source seed identity, source draw seed and
immutable producer receipt hash (null only for an engineering fixture).
All private provenance stays in the training audit; only `public_input` reaches
the model. The synthetic fixtures explicitly fabricate these fields for contract
tests and carry no real review, authorization or native-label authenticity claim.

The separately validated `constructed_empirical_plan_fixture` subtype omits all
native seed/identity fields and instead embeds its exact newly executed raw Hunt
evidence. It has `native_run:false`, `trainable:false`, an engineering purpose and
no producer/admission receipt. Its entire full-policy record is independently
regenerated on validation. It supplies measured full-v5 plan heads to the real
batch loss without enabling fitting or natural admission. See the
[constructed producer interface](../../FRESH_CONSTRUCTED_HUNT_FULL_POLICY_V5.md).

The objective attestation binds its specification, evaluator source, objective
calibration status/evidence, independent evaluation design, automatic-settlement endpoint,
continuation policy and full-v5 conditioning scope. A run locks one objective
specification/evaluator/calibration/continuation identity across both splits;
per-root evaluation-design provenance may differ. The uncalibrated candidate
profile explicitly has `objective_calibrated:false` and null calibration evidence.
Per-action objective availability is represented by the original masks; a genuine
empirical value need not imply globally calibrated objective coefficients.
This does not infer utility
labels from HP, invent missing resource prices or assert calibrated model outputs.

The additive `native_policy_v5` producer/admission path supplies source-backed
objective records and sealed original byte/attempt/outcome evidence. Unadmitted
`native-objective-candidate` records retain `trainable:false`. An exact externally
reviewed cohort may produce new bounded-pilot normalized records; the original
raw source flags and bytes are preserved. A hand-written record or a checksum is
not evidence of native source authenticity or review.

## Admission, execution and consumed supervision

1. `freeze_inputs` revalidates exact config, records and implementation/runtime,
   requires enabled supervision, and records exact per-record descriptors
2. Each native record must regenerate exactly from its immutable producer receipt.
   The existing metadata-only `protection_v5` machinery unions historical source
   and public aliases with all failed/excluded/unexecuted attempt metadata before
   filtering. The supplied protection registry must match the receipt. All historic splits are protected, and
   train/validation may not bridge a source component. Frozen test labels are
   never parsed or evaluated
3. An external `nosl.training.full-policy.review.v5.1` receipt must bind the exact
   frozen input snapshot excluding the receipt itself and explicitly attest
   objective supervision, full public conditioning and source isolation
4. A separate `nosl.training.full-policy.authorization.v5.1` receipt must bind the
   reviewed frozen snapshot, review hash and bounded-objective-pilot purpose
5. An explicit execute argument is still required; engineering fixtures and
   unadmitted native candidates cannot fit

Hashes establish integrity, not authenticity, human review, user authorization or
promotion. The interface never creates review/authorization receipts. Existing
auxiliary receipts are incompatible. Config, corpus, review, source, runtime or
lifetime budget drift rejects resume. Each bounded run has at most one epoch,
700 optimizer steps and 5,000 roots in each explicitly supplied split.

Supervision is recorded only after a successful optimizer step. The session
records the exact consumed record digests and the model-state digest before and
after the step. Exported coverage is recomputed from those records; caller-supplied
summary counters cannot confer policy eligibility. Duplicate/replayed records,
broken model chains, inconsistent step counts and unbound final weights are
rejected. Failed learning operations poison the session and cannot be exported.

Checkpoints bind model/optimizer tensors, order, cursor, progress, authorization,
RNG and source/config/data identity. Restores validate detached state before
replacing live state. Exhausted/no-progress resumes may reuse only a fully
verified identical existing bundle; collisions and tampering are rejected.

## CLI

Readiness is the default and writes no weights:

```bash
PYTHONPATH=python python -m nosl.train_policy_v5 \
  --student-config configs/student.v5.engineering.json \
  --training-config configs/student.v5.full-policy.json
```

For a new supplied corpus, `--prepared DIR` reads only `train.jsonl`,
`validation.jsonl`, metadata-only `protection.json`, and the sealed
`producer-receipts.json` mapping for native inputs. `--review PATH` and
`--authorization PATH` supply separately obtained receipts. `--engineering-fixture`
permits synthetic forward/loss inspection; `--forward-check` uses no gradients.
`--native-objective-candidate` inspects a sealed unadmitted native cohort for
readiness/forward diagnostics and is incompatible with fitting.

The implemented future fitting entry point additionally requires
`--execute-bounded-policy-pilot --output NEW_DIR`. `--resume` keeps the original
lifetime budget; `--stop-after-steps` limits this invocation without resetting it.
Providing those flags is not authorization to run this task's fitting program.
No fitting run is part of this engineering delivery.

Standalone scoring accepts one public JSON object per line:

```bash
PYTHONPATH=python python -m nosl.inference_policy_v5 --bundle NEW_BUNDLE
```

An untrained bundle returns `MODEL_UNTRAINED`. A trained bundle still abstains by
default. `--allow-experimental` permits only an unpromoted inactive-controller
policy with committed objective supervision covering every legal action kind.
Auxiliary-only, plan-only, zero-weight, unavailable or merely available but
unconsumed objectives cannot enable action selection. Every finite controller
continues to return `PLAN_POLICY_UNVALIDATED`, even if anchor estimates exist.
Unavailable or disabled heads remain null unless their loss consumed actual
supervision. Pairwise-only learning may expose a ranking `score`, but its absolute
`value` is null and explicitly has no expected-utility interpretation. Plan head
estimates are separately masked from consumed plan supervision.
Every bundle also binds the versioned empty-price public resource screen. Legal
or past potion use/discard, gold/max-HP changes against the native combat-entry
anchor, a missing entry anchor, and the listed known permanent-benefit/resource
mechanisms (`Feed`, `TheHunt`, `HandOfGreed`, `Alchemize`, `ChosenCheese`) reject the
entire inactive decision. The screen never removes candidates or chooses a
fallback, and explicitly does not certify full-content applicability. A
net-cancelled resource example in training does not authorize an unpriced fresh
resource action. Broader valued profiles need future explicit review.
No calibrated/promoted bundle format or automatic promotion is implemented.

## Verification boundary

`tests/python/test_full_policy_v5.py` runs under guards that forbid backward,
autograd gradient calls, optimizer construction and gradient clipping. Temporary
roundtrip files contain random initialization only and are discarded. Structural
"committed" receipts in these tests are synthetic in-memory fixtures, never
exported or claimed as learned weights. The separately authorized connectivity
check must use the exact checked-in current v5 config and the real `batch_loss`
path, one minibatch and one backward call, zero optimizer steps/fit loops/saved
weights, and report finite nonzero gradients plus byte-identical parameters.
Its explicit entry point is `tools/check_full_v5_connectivity.py`; it is outside
test discovery and requires separate authorization before invocation.

Passing these checks establishes engineering connectivity and guarded behavior.
It does not establish real training, student performance, calibration, safe
finite-plan continuation, production admission, client-version fidelity or
full-content coverage.

The frozen code at `e898383` passed 31 final core checks, the preceding 87-check
core/legacy aggregate, and the separate 17-check integrated native bridge suite.
The single authorized current-config connectivity check then passed with
1,573,534 parameters, 12 finite/nonzero gradient groups, one backward request and
one underlying autograd entry. Before/after parameter bytes were identical;
there were zero optimizer steps, fitting loops or saved weights in that check.
The authorization is consumed; this evidence does not authorize a repeat.
See the [connectivity report](../../../configs/student_v5_full_policy_connectivity.json)
and [verification summary](../../../configs/student_v5_full_policy_verification.json).
