# Bounded pilot implementation and runtime identity

This is an experimental, unpromoted pilot contract. It does not enable formal
training, expand the authorized data or optimizer budget, calibrate probabilities,
or demonstrate student rollout win rate. The engineering tests described below
take **zero optimizer steps**; their temporary weight fixtures are random
initialization, including fixtures that exercise the learned-bundle guard.

## What is frozen

`freeze_inputs()` binds the existing canonical config digest, all split hashes and
root counts, public identity scheme, observation schema, teacher/objective/rules
versions, and per-root continuation associations. It additionally stores an
`implementation` object with format `nosl.implementation.v1`:

- SHA-256 of the exact bytes of `python/nosl/{__init__,train,model,schema,data,
  public_identity,vocabulary,inference,reproducibility}.py`. Public/history feature
  extraction is implemented in `model.py`, so its changes are covered too
- Python implementation and patch version; ordinary string PyTorch version
  including build suffix, Git build ID, SHA-256 of `torch.__config__.show()`,
  machine architecture and PyTorch CPU capability
- Effective deterministic-algorithm and warn-only flags, intra/inter-op thread
  counts, MKLDNN flags, float32 matmul precision, default dtype and default device

Only named package sources and public runtime APIs are read. Metadata uses
relative source names. It includes no absolute checkout paths, environment
variables, credentials or private files. The PyTorch build text can contain
compiler paths, so only its digest is persisted. Converting `torch.__version__`
to an ordinary string also keeps checkpoint loading compatible with
`torch.load(..., weights_only=True)`.

For a pilot, deterministic seeding and thread setup happen **before** the freeze,
so a new run and a fresh resumed process capture effective settings consistently.
The same object appears in `inputs.json`, checkpoint `frozen.implementation`, and
bundle manifest `frozen_inputs.implementation`. Every checkpoint save rechecks the
source/runtime identity against the freeze; resume recomputes it independently
before restoring model, optimizer or RNG state. Passing old frozen metadata to
the restore function does not bypass the current-implementation check. Existing
config, split and lifetime-budget checks remain in force.

Old checkpoints without implementation provenance explicitly fail with
`cannot safely resume`. Do not edit their metadata to manufacture provenance or
silently restart a pilot under an old name. A changed implementation requires a
separately identified experiment with the applicable authorization.

### Frozen-source operating contract

Finish and test source changes before launching. Archive the exact source tree,
config and isolated Python environment identity with the experiment; run a fresh
Python process for each invocation. Do not edit, monkeypatch or reload these
modules while a pilot runs. Byte hashes intentionally reject even comment-only
source edits during resume. They bind disk sources, not an attestation of arbitrary
already-imported or monkeypatched Python code. Keep the source tree unchanged for
the full invocation, including import and the initial freeze.

This guard is a conservative compatibility check, not a promise of bitwise
reproducibility across different hardware, kernels or all possible math-library
environment settings. Preserve the launcher/environment and host class as well.
The real interrupted-training trajectory comparison remains a separate bounded
pilot check; an untrained serialization/RNG round trip does not prove it.

## Archived learned inference

`Inference.from_bundle()` continues to verify config/weight checksums and load a
strict state dict. For `trained: true`, it additionally requires implementation
provenance and checks the Python/PyTorch/CPU build identity plus the exact
inference-relevant sources: package initialization, model/features, schema, public
identity, inference, and the shared reproducibility helper.

This shared helper does not import or read trainer/data/vocabulary modules during
inference, and it does not import any simulator, teacher or search code. Trainer-
only changes cannot change the already-learned forward function and therefore do
not prevent archive evaluation. Effective fit-only flags and thread-pool sizes
are recorded but are not prerequisites for loading an otherwise compatible
inference bundle. This is source/build compatibility, not identical timing or
bitwise results for every inference execution setting.

Learned bundles missing provenance fail closed with `cannot safely evaluate`.
Untrained engineering bundles retain their existing checksum-verified behavior
and still return `MODEL_UNTRAINED`. This change does not grant promotion;
experimental inference still requires explicit `allow_experimental=True` or
`--allow-experimental`.

## Mean-valued loss estimator

`value`, `expected_final_hp`, and `potion_net_change` use squared error. Value and
HP retain the existing `/100` target scaling; potion change retains its original
units. Existing masks, per-action weights, head weights, per-head candidate mean,
and per-root aggregation remain unchanged. BCE probability heads, interpolated
HP-distribution cross entropy, pairwise and equivalent-set objectives are
unchanged.

Squared error elicits the weighted arithmetic mean. Smooth-L1/Huber instead
elicits a robust location and can suppress rare large negative utility outcomes:
95% utility 0 and 5% utility -1000 have expected utility -50, or -0.5 in the scaled
value head. The targeted gradient test verifies that this is the stationary point
of the weighted MSE, while the near-zero Huber stationary point is not. This
matters when death penalties are large and outcome distributions are asymmetric.
It does not remove small-sample teacher uncertainty or make one-step fitting
equivalent to converged mean estimation. Gradient clipping remains unchanged.

Evaluation now records `value_mse` alongside `value_mae`, over the same masked
candidate support. MSE is in **raw utility squared units**, not scaled head units.
Like the existing MAE it is a descriptive unweighted candidate metric; it is not
the full head-weighted/per-root training objective. The before-fit and final
validation snapshots both contain it. Null/masked targets contribute no loss,
gradient or metric observation. Test holdout outcomes still never select weights.

## Recommended isolated one-CPU launcher

Wait for the audited real-data stage and record the concrete wall/CPU/memory
limits before launching. The first proposed bounded trial is one epoch, at most 700 optimizer
steps, and at most 5,000 **training** roots drawn from the audited approximately
5,000-root total prepared corpus. A cap does not guarantee that many train roots
or steps exist after splitting. Existing code rejects excess roots rather than
truncating or resampling them. The listed config must retain `torch_threads: 1`.

Use a pre-existing isolated environment with the pinned CPU PyTorch build. The
following Linux shell template deliberately requires explicit resource values;
it does not install software or establish approval by itself. Run from the frozen
repository root. `PILOT_CPU` is one permitted, otherwise-available logical CPU ID.

```sh
: "${PYTHON:?path to the isolated environment Python}"
: "${PREPARED:?audited prepared data directory}"
: "${OUTPUT:?new pilot output directory}"
: "${RESOURCE_LOG:?external time/resource log path}"
: "${PILOT_CPU:?one allowed logical CPU ID}"
: "${WALL_SECONDS:?approved per-invocation wall-time cap}"
: "${CPU_SECONDS:?approved per-invocation CPU-time cap}"
: "${VMEM_KIB:?approved virtual-memory cap in KiB}"
(
  ulimit -v "$VMEM_KIB" || exit
  ulimit -t "$CPU_SECONDS" || exit
  exec /usr/bin/time -v -o "$RESOURCE_LOG" \
    timeout --signal=TERM --kill-after=30s "${WALL_SECONDS}s" \
    taskset -c "$PILOT_CPU" \
    env OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 \
        PYTHONPATH=python "$PYTHON" -m nosl.train \
      --config configs/student.pilot.json --prepared "$PREPARED" \
      --mode pilot --confirm-pilot --max-roots 5000 \
      --max-epochs 1 --max-steps 700 --output "$OUTPUT"
)
```

The subshell limits apply only to this invocation and its descendants. CPU
affinity restricts execution to one logical CPU; `torch_threads: 1` and the
thread-library settings avoid unnecessary intra-op parallelism. The effective
PyTorch inter-op pool size is separately fingerprinted. The outer `timeout`
includes loading and initial validation; its kill grace can extend the process's
absolute termination time by up to 30 seconds. Account for that grace when
selecting the approved wall budget.

`ulimit -v` limits **virtual address space**, not RSS, and a low value can prevent
PyTorch imports or allocations even when resident usage is low. `ulimit -t` limits
process CPU seconds, not wall time. `/usr/bin/time -v` reports observed peak RSS
and status even if training exits before writing a completed manifest. The
manifest's `peak_rss_mib` is observed process lifetime high-water RSS on Linux,
not an enforced RSS ceiling; `wall_seconds_this_invocation` begins after input
loading/initialization and is not the full launcher wall time. An external RSS
monitor/cgroup is needed if the approved limit is specifically resident memory.

Resume with the same source, environment, inputs and all three lifetime caps,
adding `--resume` to this command and preserving the existing output directory.
Resource limits above are per invocation; keep cumulative wall/CPU accounting
separately if the approved experiment budget spans retries. A timeout is an
interruption, not successful completion or authority to extend limits. The last
atomic checkpoint may be usable; examine exit status and checkpoint consistency
before deciding whether an authorized resume remains appropriate.

## Verification

Run Python tests on one CPU without executing a pilot:

```sh
taskset -c "$PILOT_CPU" env OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
  OPENBLAS_NUM_THREADS=1 PYTHONPATH=python "$PYTHON" \
  -m unittest discover -s tests/python -v
```

`test_pilot_reproducibility.py` covers stable source/runtime fingerprints,
untrained model/optimizer/RNG save-and-restore, every bound source, Python and
PyTorch version/build drift, effective CPU flags, missing old provenance,
in-flight source drift, learned inference checks, training-free inference imports,
unchanged untrained fixtures, weighted mean gradients and nullable/masked targets.
It simulates source drift in memory and does not modify live generator sources.
