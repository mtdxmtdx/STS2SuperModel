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
