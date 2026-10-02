# Bounded offline pilot policy evaluation

This is an evaluation tool, not training, deployment or model promotion. It
compares **actual closed-loop, automatically settled simulator outcomes** against
the frozen public-rule continuation. Predicted teacher head values are never
substituted for game results.

Implementation: `tools/evaluate_pilot_policy.py`.
Tests: `tests/python/test_policy_evaluation.py`.

## Current evidence and boundaries

- Synthetic orchestration tests cover the public-input-only student boundary,
  paired source seeds, actual-versus-predicted HP, baseline action recovery,
  refusal without fallback, invalid model actions, resource/decision limits,
  failed settlement, source counts, fixed recipes and manifest integrity
- A single real baseline-only protocol smoke is allowed and recorded separately
  below. It is not a policy-strength experiment
- No learned bundle has been evaluated by this implementation task; no fitting,
  optimizer step, new weights, promotion or publication is performed
- The scenarios are explicitly **constructed**, with finite low-HP, difficult,
  multi-card and multi-enemy cases. They do not establish a natural-run
  distribution, complete content coverage or a statistically significant gain

## Freeze the source battles before a learned run

For the later explicitly authorized trial, after M5 preparation is frozen:

```sh
python tools/evaluate_pilot_policy.py freeze \
  --prepared artifacts/prepared/APPROVED-PILOT-CORPUS \
  --output artifacts/reports/APPROVED-POLICY-PLAN \
  --seed-prefix NOSL-OFFLINE-HOLDOUT-v1 --seeds-per-case 2
```

Eight fixed recipe classes times two fixed seeds produce16 source battles.
The cases include starter, low-HP basics, poison/guard, discard/draw, shivs/relic,
low-HP poison, multiple enemies and block carry. No rollout executes while
freezing. Seeds are not searched, replaced or selected using outcomes. A scenario
that fails later remains in the declared-source denominator.

Freeze reads the prepared corpus's immutable manifest/shard integrity and the
`audit_only.scenario_recipe` of source records. It uses no teacher targets for
scenario selection. The source seeds must be disjoint from train, validation
and frozen-test sources. Initial deck/potion/relic multiset novelty is recorded;
at least one declared combination must be a new initial setup. This does **not**
prove that related generated-card interactions never occurred later in training
rollouts. Missing source recipe evidence blocks learned evaluation.

The output contains `plan.json`, `plan.sha256` and an exact archived
`evaluator-source.py`. It binds the scenario list, source counts, resource caps,
prepared-corpus hash, evaluator source and compiled worker runtime hashes.
Existing directories are not overwritten. Changing source, runtime or plan
invalidates the frozen plan; freeze a clearly new experiment before observing
its results rather than silently changing an evaluated plan.

## Run a future bounded experimental comparison

Only after the bounded trial and its evaluation are authorized:

```sh
PYTHONPATH=python .venv/bin/python tools/evaluate_pilot_policy.py run \
  --plan artifacts/reports/APPROVED-POLICY-PLAN \
  --output artifacts/reports/APPROVED-POLICY-RESULT \
  --bundle artifacts/APPROVED-EXPERIMENTAL-BUNDLE --confirm-experimental \
  --dotnet /path/to/dotnet
```

The bundle must be trained, `EXPERIMENTAL_UNPROMOTED`, checksummed by the
standalone inference loader, and tied to the exact frozen prepared corpus.
Smoke-only/unverified plans cannot run learned weights. The tool never changes
formal-training gates, model configuration, student schema, default worker
binaries or the upstream simulator.

Each source battle is run with the identical declared private setup seed for
baseline and student, each in a fresh sequential worker. The seed and scenario
are sent only to worker reset and retained in evaluation audit. The student's
only input is the existing strict `public_input` assembled from a public
DecisionPacket; it receives no source ID, scenario, actual seed, audit metadata,
teacher, search tree or simulator handle. Public histories and action tokens
remain available. The model's selected token must match a current legal
candidate; refusal or invalid output does not trigger a stronger fallback.

The baseline uses worker `continue`, which invokes `nosl-public-rules-v1`.
Its selected token is recovered from the newly published public action event,
including terminal event history. Missing/ambiguous action evidence is an
engineering failure, explicitly marked as incomplete trace.

## Bounds and failure accounting

Defaults frozen in the plan:

- One CPU affinity for the driver and its one sequential worker; torch and
  native-worker thread settings limited to one
-256 decisions per battle,30 seconds per battle,600 seconds per job
-1024MiB combined observed driver/worker RSS
-256MiB public trace output,64MiB single worker response
- At most32 declared sources; no concurrent evaluation workers

The Python freeze API accepts finite tighter/alternative limits, bounded by
1024 decisions,120 seconds per battle,3600 seconds per job,2048MiB RSS and1024MiB
trace data. Limits are validated again when loading a plan. Bundle files have
separate size caps before loading.

Wall alarms interrupt Python orchestration/inference, and nonblocking worker
reads check budgets every100ms. RSS is sampled at boundaries and while waiting;
this is **not a kernel-hard RSS quota**, and native calls can delay Python signal
handling. Worker cleanup may require a two-second termination grace. These
limits and caveats are included in the report, rather than promising exact
hard real-time/memory containment. A production resource sandbox is outside
this pilot tool's scope.

Game `WIN` and `LOSS` require the declared automatic-settlement boundary and zero
post-combat reward choices. `COMPUTE_TRUNCATED`, `ENGINE_ERROR`, `POLICY_ERROR` and
`POLICY_REJECTED` remain separate. They are not converted into deaths or discarded
and renormalized away. Source battles not reached before the job deadline remain
in declared totals and are shown as unresolved/NOT_RUN in paired comparison.

## Outputs

- `frozen-plan.json`: exact evaluated source plan
- `*.trace.jsonl`: every public boundary and selected action, responses, policy
  refusals and complete settled terminal facts when available
- `*-stderr.log`: worker diagnostics
- `outcomes.json`: durable per-source/per-policy results as work completes
- `report.json`: declared/attempted/completed source counts, actual win/loss/death
  counts, HP/net-loss/potion endpoints, unresolved counts and paired differences

Both all-declared and completed-game denominators are exposed. A win count divided
by declared sources is a lower bound when outcomes are unresolved. HP summaries
on completed battles always report that population. Paired completed counts and
unresolved counts accompany the paired HP comparison; no confidence or significance
claim is made from this small sample.

Current TerminalFacts exposes settled HP/maxHP and potion slots. Final gold and
other permanent-resource endpoints are **unavailable**, retained as null with
an explicit limitation; they are never assumed unchanged or given value zero.
This evaluator therefore does not establish a complete economic/resource-value
comparison for mechanics requiring those endpoints.

## Baseline-only protocol smoke

The engineering-only command does not import or evaluate a learned model:

```sh
python tools/evaluate_pilot_policy.py freeze --smoke-only --seeds-per-case 1 \
  --seed-prefix NOSL-M6-BASELINE-PROTOCOL-SMOKE-v1 \
  --output artifacts/reports/pilot-policy-baseline-plan-v2
python tools/evaluate_pilot_policy.py run --baseline-only \
  --plan artifacts/reports/pilot-policy-baseline-plan-v2 \
  --output artifacts/reports/pilot-policy-baseline-smoke-v2 \
  --dotnet /workspace/scratch/b3c7487cea34/.dotnet-nosl/dotnet
```

Inspect that report for the actual smoke result. It is one predeclared source,
not an independent policy-quality benchmark. This plan is intentionally ineligible
for a learned comparison because it lacks a frozen corpus holdout audit.

The final logging-complete implementation repeated the identical declared smoke
seed once after adding failure-response trace retention. This was a code
verification retry, not a new independent source or outcome-based seed choice.
Both smoke plans/results and their exact evaluator snapshots are retained.
