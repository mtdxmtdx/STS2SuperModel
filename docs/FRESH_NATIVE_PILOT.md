# Fresh native pilot admission

`native_pilot_collect` is a distinct opt-in collection operation. It calls the
actual native collector, imports its borrowed boundary through the reviewed
`ImportNativeAsync` certificate path, evaluates the imported root, and writes
`nosl.dataset.native-pilot.v1`. It has no archive-conversion entry point.
Prototype v1/v2/v3/v4 records, raw native sources, and any `trainable:false` row
remain quarantined. The inspected fixed 200-root cohort is unchanged.

A collection requires a new identifier, `seedPrefix` equal to
`nosl-native-pilot/<collectionId>`, `sourceRunPrefix` equal to that identifier,
and an exact protection-registry SHA256. The old inspected source prefix cannot
be supplied to this operation. Purpose is explicitly `engineering-smoke` or
`pilot`; neither means distribution acceptance, formal labels, or training
authorization. The native engineer path is limited to T0, at most 200 roots,
100 runs, 20 floors, 16 evaluation worlds, 300 rollout decisions, 256 posterior
attempts, and 10,000 source-run decisions. These caps do not authorize generation.

Stable reviewed entry, owned-origin reviewed suspended choices, and untouched
native generation-potion entries use their actual certificate/profile. The
new potion profile remains stable-only; its unsupported pending choices and
consumed-generation histories fail closed through the shared importer. Source
seed is audit-only and never conditions the posterior or enters student input.

Each emitted teacher record binds source run/battle identities, all public
history, reachability/provenance, sampler/teacher/continuation/rules/endpoint
versions, seed accounting, all legal candidates, individual outcomes, completion
counts, HP/resource diagnostics, and measured costs. Source run identity derives
from native character/ascension and actual source seed. Battle identity adds
act, floor and encounter. Length-delimited SHA256 is identical in C# and Python.
Caller grouping prefixes cannot disguise a reused run; every raw/invalid record
also contributes these aliases before filtering.

The native admission validator checks settled HP accounting, inventory presence,
endpoint/continuation identity and empirical auxiliary targets against their
actual settled outcomes. Incomplete candidates keep their full allocated mass,
null point targets, false masks and explicit completion/error/truncation counts.
Other useful candidates and empirical heads can be retained. All-masked roots
remain diagnostic. Objective values may still be unresolved; certified pairwise
ranking is unavailable and stays masked. Useful empirical labels do not imply
calibrated values or a certified learned continuation.

## Historical protection

`tools/native_protection.py` exports existing prepared metadata without decoding
any old target shard. It adds inspected native public/source metadata. Explicit
`--native-source-archives` are projected lexically: neither target values nor
outcome-sample values are decoded. `--native-metadata` accepts already projected
rows and rejects target-bearing inputs.

The registry embeds the complete original metadata registry, verifies its hash,
and verifies preservation of every original alias and split owner plus every
supplied native metadata alias. It preserves the source's public-identity
scheme. Ordinary source IDs, complete public identity and old-compatible public
aliases all participate in transitive pre-filter grouping. Fresh native pilot
roots connected to *any* registered component are excluded, including previously
observed train/development sources. A later invalid bridge that connects retained
fresh data to protected history permanently blocks the corpus and its loader.

These checks detect missing/changed metadata under the bound contract. They do
not authenticate an adversary who rewrites the entire history and every hash,
and they cannot establish that an omitted historical collection was supplied.
The caller must provide the complete relevant source history. Certification
establishes a reviewed mechanism family, not a good source distribution.

Use `configs/data_pipeline.native-pilot.v1.json` and `--protection-registry` for
preparation. Generic v2 configuration still rejects the new pilot envelope.
`PreparedDatasetV2` independently verifies the native admission lock, purpose,
bound registry/hash, history closure, source aliases and immutable shards. It
opens only train/validation target rows. Test shards remain opaque. The v2
training fingerprint binds the new admission module and protection builder;
quality acceptance and bounded fit authorization remain separate gates.

Example protection and preparation, using supplied authorized artifacts only:

```sh
python -B tools/native_protection.py \
  --protect-from-prepared artifacts/previous-prepared \
  --native-metadata inspected-native-public-provenance.jsonl \
  --output artifacts/native-protection.json
python -B tools/prepare_dataset.py supplied-fresh-native-records.jsonl \
  --config configs/data_pipeline.native-pilot.v1.json --mode engineering-smoke \
  --protection-registry artifacts/native-protection.json \
  --output-dir artifacts/native-engineering-prepared
```

## Bounded verification

The declared regression namespace was `nosl-native-pilot/fresh-admission-regression`.
The initial one-run check collected three unsupported roots. The same namespace
was then bounded to four runs, eight floors, twelve roots, one root per combat,
one evaluation seed (8801), and 200 rollout decisions. It collected **12 roots
from three runs: two certified and ten explicitly unsupported**, with **18/18
settled action-worlds**. There was no alternate namespace or seed search.

The first test used synthetic historical metadata. A separate exact replay of
the same namespace bound the actual verified registry: all pilot-5000-v4 and
current40-development-protected-final aliases plus all 200 inspected native
source/public metadata records. All **494 original test targets remained
undecoded**; their bytes were hashed under a guard rejecting text reads. The
combined registry contains **2,579 components**. Both certified roots remained
usable and loaded successfully as **one train and one validation root**, including
one auxiliary-only root. Ten raw rejected roots retained provenance. Isolation
passed. See `NATIVE_PILOT_ADMISSION_SUMMARY.json` and the ignored
`artifacts/fresh-native-proof` evidence.

This real end-to-end cohort exercised only the stable reviewed native family.
Choice and generation-potion support use their separately reviewed importer
regressions; this cohort does not establish end-to-end coverage for those two
profiles. Every observed regression root is now a development fixture and must
be protected in future collection. No fresh benchmark, distribution quality,
policy strength, formal readiness, optimizer step or backward pass is claimed.

Focused checks:

```sh
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/fresh-native-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter FullyQualifiedName~NativePilotDatasetTests
python -B -m unittest discover -s tests/data -q
NOSL_NATIVE_WORKER="$PWD/artifacts/fresh-native-build/bin/Nosl.Worker/release/Nosl.Worker.dll" \
  python -B -m unittest discover -s tests/data -p 'test_native_pilot.py' -v
../.venv-nosl/bin/python -B -m unittest discover -s tests/python \
  -p 'test_native_admission_fingerprint.py' -v
```
