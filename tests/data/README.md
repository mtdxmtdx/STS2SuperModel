# Data preparation verification

Run the dependency-free synthetic contract tests:

```sh
python -B -m unittest discover -s tests/data -v
```

These fixtures exercise data engineering, not game correctness or natural-state
reachability. No generator or optimizer is run by this suite.

Prepare the bounded engineering smoke in a separate corpus:

```sh
python -B tools/prepare_dataset.py artifacts/teacher-smoke.jsonl \
  --output-dir artifacts/data200-smoke --mode engineering-smoke --shard-size 100
```

Initialize a reviewed pilot corpus from its first generation stage:

```sh
python -B tools/prepare_dataset.py artifacts/pilot-first-stage.jsonl \
  --output-dir artifacts/pilot-dataset --mode pilot --shard-size 1000
```

Append a separately authorized and quality-reviewed generation stage:

```sh
python -B tools/prepare_dataset.py artifacts/pilot-next-stage.jsonl \
  --output-dir artifacts/pilot-dataset --mode pilot --resume --shard-size 1000
python -B tools/prepare_dataset.py --output-dir artifacts/pilot-dataset --verify-only
```

This tool only prepares supplied records. It never starts generation/training,
never synthesizes targets, and refuses formal mode. Stage promotion is explicit
review, not an automatic effect of collecting 200/5k/10k/30k/100k attempts.

## Integrity and statistical units

- Every decision keeps all candidates; invalid records are quarantined whole
- Input attempts, exact unique public roots, new usable roots before holdout
  exclusion, effective retained roots, and duplicate attempts are separate counts
- Full-candidate complete roots have all six target masks valid for every action;
  auxiliary-only roots have some usable supervision with missing heads/candidates
- Complete trajectory accounting is reported separately from value-target
  availability; finite point targets do not imply resolved pairwise ranking
- Independent root world draws differ from correlated action-world copies
- Incomplete/error outcomes retain null targets and false masks, never zeros
- Original source run, combat, branch family, upstream public digest, and computed
  exact public-input digest form transitive connected components before filtering
- Invalid/duplicate rows can still provide provenance edges
- Identical public inputs are never merged into invented pooled labels; first
  valid retained target set is frozen and later attempts are explicitly excluded

## Frozen test and append semantics

Engineering smoke and pilot data cannot share a corpus. First pilot preparation
freezes its test set. Append locks configuration, observation schema, all stable
version fields, and historical split assignment. New test-associated roots remain
assigned to test in the audit state but are excluded from the original holdout and
from training. A bridge connecting historical splits quarantines the new connected
component and permanently blocks readiness; deleting the bridge does not repair
known contamination. A clean rebuilt corpus is required to remove that blocker.

The top-level `manifest.json` references immutable checksummed stage manifests,
JSONL shards, quality reports, and split state. Every append verifies existing
files. The initial test shard descriptors never change. A repeated identical
latest request is idempotent; an interruption after stage publication can recover
without rewriting published shards. A nonblocking process lock prevents racing
writers. Tampering, mixed versions, or changed config cannot silently resume.

Python APIs: `prepare(records, config, mode, state=None, student_config=None)`
returns `(splits, report, rejected)`; persist `report['split_state']` together with
the corresponding immutable files. Prefer `persist_batch` for transactional disk
output. `verify_manifest(output_dir)` checks files and version locks;
`iter_dataset_paths(output_dir, split)` returns verified shards and refuses known
split contamination. Test paths always refer to the initial frozen holdout.

Starting a new training experiment should bind the exact top-level manifest SHA;
appending data changes that identity and is not a resume of the same experiment.

## New teacher versions with old split protection

A new teacher, continuation or sampler version requires a **new output corpus**.
Do not change version fields or append incompatible labels to the old corpus.
Import its complete split aliases without importing its labels or deduplication
history:

```sh
python -B tools/prepare_dataset.py new/shard-*/decisions.jsonl \
  --output-dir artifacts/new-version-development --mode pilot \
  --protect-from-prepared artifacts/pilot-dataset \
  --provenance-journals new/shard-*/attempts.jsonl
```

The source must pass immutable manifest, state, shard and public-identity checks.
Old JSONL shards are streamed only for SHA-256 integrity checks; old targets are
never parsed or copied. All source components are imported, including aliases
from rejected and diagnostic roots. The portable `nosl.dataset.split-protection.v1`
registry contains source manifest/state hashes, source version metadata, frozen
test shard hashes and complete component/token/split metadata. It contains no
old `seen_public_digests` set, so intentional new-version labels on old training
public roots remain eligible.

All incoming decision rows and supplied journal rows join provenance before any
validity, usability or duplicate filtering. Journals can supply explicit aliases
and semantic digest metadata. For existing generator-v7 journals lacking aliases,
the neighboring `generation_config.json` must match the exact local generator
hash, declared generation identity and source indices; the pinned generator's
run/combat/family aliases are then recovered, including failed attempts. These
sidecar files are included in the immutable request hashes. Unsupported or
inconsistent journal metadata fails closed. Supply every relevant journal; the
tool cannot discover missing inputs outside the supplied paths.

Every old-test-connected component is excluded from **all** new target shards,
even in the new corpus's first stage. Old validation and training components keep
their assignments. Newly discovered aliases persist in the state. Cross-split
bridges quarantine the connected rows, save the full conflicting alias set,
permanently block readiness and prohibit further protected appends. Reports
include the full protected alias closure, incoming component aliases and counts.
If a later bridge connects an already retained new-test root to an old protected
test, it also commits a permanent blocker even though both split names are test.
The existing shard bytes remain immutable; the loader refuses the now-contaminated
corpus. A separate version-mismatch row cannot prevent this blocker from being
saved: incompatible labels stay quarantined under the existing version lock.
An initial invalid-only conflict produces a blocked metadata corpus with no label
version population. None of these conflict cases can be consumed or resumed.

The registry SHA is bound in the corpus lock and its immutable first-stage file.
Both the preparer and independent student loader enforce registry schema,
checksum, binding, alias retention and inherited splits. Subsequent `--resume`
automatically uses the bound registry even when `--protect-from-prepared` is
omitted. A changed registry or adding protection to an existing ordinary corpus
is rejected; missing/corrupt protection cannot silently fall back to normal
preparation. A moved protected output can resume without the original corpus.
Interrupted first-stage publication retains or fails closed on its registry.
Same-version append checks, original frozen test bytes and formal-mode blocking
remain in force.

APIs add keyword-only `protection` and `provenance_records` to `prepare` and
`persist_batch`. Construct protection with `export_split_protection(old_dir)`.
For pure in-memory resumes, explicitly pass the same validated registry; the
transactional disk API reloads it automatically. Pure API dictionaries are
caller-owned inputs, not independently authenticated historical evidence.

Legacy v3 corpora without protection remain supported without a format/version
bump. Registry enforcement lives in `python/nosl/data.py`, so its existing
training-source fingerprint covers the complete implementation. This targeted
loader revision correctly rejects resuming the original training checkpoint.
All inference fingerprinted sources remain unchanged, and the original learned
bundle still loads. Future training requires a fresh reviewed experiment; this
feature never authorizes or starts training.

The October 2 completed 40-battle cohort was already inspected. Its protected
development copy is not a fresh unseen benchmark, including its new test split.
It preserves the actual continuation-v2 / posterior-dispatch-v2 labels; it must
not be presented as newly generated dispatcher-v3 evidence.

## Explicit student/public v2 pipeline

Use `--config configs/data_pipeline.v2.json` with the same durable preparation
entry point. It locks `nosl.dataset.prepare.v4` and the distinct
`nosl.public-identity.student-v2.v1` semantic identity. Legacy defaults remain
unchanged. V2 registries can protect either verified v1 or v2 source corpora;
the source identity is retained, not rewritten. Incoming roots add conservative
legacy aliases for context-free current observations and embedded anchors before
filtering, so changing/removing new context cannot bypass old test protection.

V2 journals may include public_input and legacy_public_input_digests in addition
to source aliases and a versioned public_input_digest. Their data is used only
for provenance. Supply every attempt journal, including failed attempts. Plan-only
roots keep null ordinary candidate labels and zero candidate rollout allocations;
paired-policy accounting is reported separately. Native development remains
quarantined until a separate admission contract exists.

`test_prepare_v2.py` uses declared synthetic independent contract groups and tiny
real bounded finite-Hunt audit fixtures. It covers protected old aliases,
invalid bridges, frozen append, mixed-version refusal, malformed provenance,
recursive identity and shard tampering. `PreparedDatasetV2` loads only training
and validation, verifying frozen test bytes without parsing their targets.

The v2 throughput population includes structurally valid diagnostic-only roots,
including incomplete plan attempts. Unusable labels remain excluded. Paired-policy
completion/error/truncation accounting is separate from candidate-action counters;
missing cost timings remain null. A v2 cross-split bridge commits a permanent
isolation blocker even without an imported registry and even when an unrelated
incoming row has incompatible label versions.

No-fit verification also covers positive batch size and learning rate, finite
nonnegative loss weights, zero-step optimizer/RNG restore, lifetime budget
expansion refusal and bundle action-policy abstention when only plan or auxiliary
supervision has actually been consumed. The dedicated pipeline acceptance test
performs exactly one backward on a two-root engineering batch and verifies every
weight is unchanged; it never calls optimizer.step or produces trained weights.
