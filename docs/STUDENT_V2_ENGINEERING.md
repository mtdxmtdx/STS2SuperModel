# Versioned public context engineering

This is an **opt-in, untrained engineering path**. It does not change the frozen
v1 configuration, schema, model, inference, training, public identity or inference
fingerprint implementation. The training-only data verifier has an explicit
version parameter; existing v1 defaults remain unchanged and old training
checkpoints correctly reject its changed source fingerprint. Existing v1 records and checkpoints retain their
original interpretation. No optimizer step, formal training, promotion, broad
policy-strength claim or safe learned bonus execution is part of this change.

## Versions and entry points

- Public input: `nosl.student.public.v2`; configuration: `nosl.student.config.v2`
- Model: `nosl.student.model.v2`; bundle: `nosl.student.bundle.v2`
- Finite controller: `nosl.controller.finite-hunt.v1`
- Finite records: `nosl.dataset.finite-hunt.v2`
- Constructed forced-event records: `nosl.dataset.forced-events.v2`
- Config: `configs/student.v2.engineering.json`, containing its own frozen v1-core configuration
- Python modules: `schema_v2`, `model_v2`, `public_identity_v2`, `data_v2`, `train_v2`, `smoke_v2`, `inference_v2`
- C# opt-in worker operation: `finite_hunt_record`, with `sourceRun`,
  `sourceCombat`, `branchFamily`, and optional `HuntEvaluationOptions`

Ordinary constructed records still export public v1. Native entry or forced-event
history requires public v2; the exporter retains the entire history. Native
development envelopes must explicitly retain `trainable:false`. No default
runtime, checkpoint, frozen dataset or existing v1 configuration is replaced.

## Public finite Hunt context

The only supported bonus template is the existing reviewed finite TheHunt policy
and its no-healing mechanics family. Native carry-in Hunt plans and arbitrary
bonus plans are not admitted. The context contains:

- A structured, strictly validated original public anchor snapshot
- The designated TheHunt/enemy target and template/baseline references
- Original player turn, **fixed original turn + 1 deadline**, public HP guard,
  last observed player turn, status/exit reason and observed public event suffix

It never contains teacher values, estimated incremental HP cost, success
estimates, confidence intervals, eligibility, seeds, private order or audit IDs.
Template/baseline references and revision tokens are excluded from learned
features. The HP guard is a public heuristic, not an observed counterfactual
budget. Observed total HP loss is never renamed incremental loss.

One inference/controller instance owns one immutable plan within one combat.
History must extend its anchor and prior observations; player turns cannot move
backwards. The deadline, anchor, target and HP guard cannot be reset. Public
expiry, low HP, spent TheHunt or no available Hunt/draw on the deadline cause an
abort. Aborted/unresolved contexts abstain. An aborted plan cannot reopen, even
if its fallback later happens to obtain a reward.

`finished` cannot be asserted in a live decision packet. Only an explicit public
terminal observation can finish a plan: actual win, observed TheHunt fatal event
by the fixed deadline and an actual offered extra CardReward are all required.
No reward option is selected or assigned an invented future price. A settled
instance cannot receive another decision or terminal observation.

## Real targets and unknown mass

`TeacherDataset.RecordFiniteHunt` consumes actual paired whole-policy evaluator
outcomes. Ordinary candidate action targets remain null/masked because those
counterfactual candidate actions were not evaluated. Separate `targets.plan`
fields are `specified_success_probability` and `extra_net_hp_loss`, each with a
nullable value, independent mask and completion accounting over all allocated
worlds. The cost is the paired whole-plan difference, not per-trajectory total
damage. Success includes actual win, fatal, offered reward and deadline, with
loss/failure contributing zero and unresolved mass retained.

These means are usable only at the exact original active public anchor. Later
states explicitly have `label_scope:unavailable` and null plan targets. They do
not inherit or renew an anchor budget. Eligibility, confidence intervals and
paired audit evidence remain outside model input and outside training targets.
Passing a finite-sample test is never presented as a mathematical safety guarantee.

## New public event facts

The v2 boundary accepts the exact public native entry asset event, plus the five
reviewed forced-event owner/path contexts and actually opened six-item FakeMerchant
inventory. All fields and nested card/relic states use explicit whitelists.
Unopened stock, future reward choices and event-private state are not accepted.
Event positions and full validated details enter a new public feature branch.

The unchanged v1 core receives a compatibility projection. The v2 feature branch
separately consumes every extension event rather than discarding those facts.
Native permanent decks are canonicalized as public multisets; relic acquisition
order, public event order and purchased slot order are retained. Numeric-equivalent
JSON values have equal features. Masked candidates cannot change whole-plan
features or pooling.

The new model conditions an adapter on the legacy candidate head outputs and
the public context embedding. This score bottleneck and feature hashing are
deliberately compact, lossy engineering choices, with no performance claim.

## Validation and explicit remaining limits

The checked fixtures were emitted by real bounded C# execution: complete paired
Hunt outcomes, incomplete outcomes, a later controller boundary, terminal public
events, one native entry packet and all five forced-event owners. Tests cover
strict validation, hidden-source invariance, forbidden-field rejection, real
nullable targets, finite forward/backward gradients without parameter changes,
expiry/abort/terminal transitions, masked candidates and a standalone import
closure without simulator, search, data reader or trainer dependencies.

Untrained standalone inference returns `MODEL_UNTRAINED`; public exits return
explicit `PLAN_ABORTED`/`PLAN_UNRESOLVED`/`PLAN_FINISHED`. Even supplied future
trained weights cannot select an active finite-plan action through this module:
it returns `PLAN_POLICY_UNVALIDATED` because whole-plan anchor labels do not
establish a safe learned continuation or conditional budget management.

`DecisionDatasetV2` remains the bounded compatibility reader. Production
engineering now uses explicit `configs/data_pipeline.v2.json`, the shared
transactional preparer, and `PreparedDatasetV2`. The preparer still requires an
explicit engineering-smoke/pilot mode and refuses formal mode. V2 public identity
is `nosl.public-identity.student-v2.v1`; its pipeline is
`nosl.dataset.prepare.v4`. Existing v1 modes keep their original identity and
admission behavior.

The v2 identity recursively normalizes numeric representations, revision tokens,
anchor snapshots, JSON event payloads and native permanent-deck multisets.
Meaningful action/history/relic/merchant-slot order is preserved. In addition to
its complete new identity, every root contributes old-compatible public aliases:
the current observation with inactive context and extension events removed,
plus the original anchor. These aliases only protect splits, never pool labels.
All invalid, diagnostic and supplied failed-journal rows participate before
filtering. Protected registries preserve their source identity; the new corpus
binds its own distinct identity and the original registry hash. Old label shards
are hashed as opaque bytes and never decoded by registry export.

Production admission verifies public/target schemas, all provenance/version/seed
fields and conserved completion/error/truncation counts. Whole-plan-only roots
are usable without invented candidate supervision: ordinary action counts stay
zero and masks stay false. Separate paired-policy costs preserve their units.
Unknown timings remain null. Native development envelopes and any
`trainable:false` row are quarantined; new context support is no admission grant.

`PreparedDatasetV2` verifies immutable stages/hash chain, frozen test descriptors,
registry binding and alias closure, provenance and per-row digests. It loads only
train or validation. Test labels remain sealed; their complete shard bytes are
verified and bound by the manifest. Appending changes experiment identity.

`train_v2` implements combined masked action/plan losses, masked validation
metrics, exact source/runtime/input fingerprints, atomic model/optimizer/RNG
checkpoints and fixed lifetime budget resumes. It defaults to readiness only.
Fitting requires separate quality acceptance and bounded authorization records
bound to the exact frozen inputs and budget. Engineering corpora and native
nontrainable rows cannot pass the fit gate. Formal training stays disabled.
Zero-step engineering checkpoints/bundles are explicitly untrained and compatible
with `InferenceV2`. Corpus coverage and supervision actually consumed by committed
optimizer steps are separately bound. Inactive action selection abstains without
objective action-policy supervision and observed legal action-kind coverage;
plan-only or auxiliary-only fitting cannot enable an untrained ranking head.
Whole-plan learned policy execution still abstains pending its separate safety
acceptance. No fit or optimizer step was run for this engineering.

Example preparation (supplied records only, no generation):

```sh
python -B tools/prepare_dataset.py supplied-v2-records.jsonl \
  --config configs/data_pipeline.v2.json --mode engineering-smoke \
  --output-dir artifacts/v2-engineering \
  --protect-from-prepared artifacts/old-prepared \
  --provenance-journals supplied-attempts.jsonl
PYTHONPATH=python python -m nosl.train_v2 \
  --config configs/student.v2.engineering.json --prepared artifacts/v2-engineering
```

Run the bounded check with the CPU environment:

```sh
PYTHONPATH=python python -m nosl.smoke_v2 \
  --config configs/student.v2.engineering.json \
  --data tests/python/fixtures/finite-hunt-record-v2.jsonl
PYTHONPATH=python python -m nosl.inference_v2 --config configs/student.v2.engineering.json
```

The v2 bundle loader binds format, model/public schema, full configuration and
weight checksums, v1-core/v2-inference source hashes and runtime identity. V1
bundles are rejected rather than silently upgraded.
