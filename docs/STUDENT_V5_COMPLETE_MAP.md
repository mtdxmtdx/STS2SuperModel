# M6 complete public-map engineering

The opt-in map channel has a separate student boundary, encoder and checkpoint
identity. This is **untrained engineering**. A separate [bounded auxiliary data path](V5_NATIVE_DATA_PREPARATION.md) now supports exact-bound future cohort admission and protected preparation/loading. It does not migrate existing labels, promote a policy, certify live-client visibility, or establish end-to-end M3–M6 acceptance.

## Version contract

| Boundary | Version |
| --- | --- |
| Map profile | `nosl.public-map-complete-graph.v1` |
| Public evidence | `nosl.public-run-evidence.v2` |
| Student input/config/model | `nosl.student.public.v5` / `nosl.student.config.v5` / `nosl.student.model.v5` |
| Added map features | `nosl.public-map-complete-graph.features.v1` |
| Conditioning identity | `nosl.public-identity.student-v5.v1` |
| Label-free native source | `nosl.natural-source.v5` |
| Engineering record | `nosl.public-map-complete-graph.engineering.v1` |
| Engineering bundle | `nosl.student.bundle.v5` |

`configs/student.v5.engineering.json` nests the unchanged v4 configuration.
Evidence v2 selects student v5 even before the first map observation. Evidence
v1 and student v4 retain their exact codec, fingerprints, feature dimensions
and checkpoint behavior; their validators reject the new channel. The C#
producer selects student/source versions from the evidence version, not merely
from the presence of a map event. No hidden prior identifier is a feature.

## Closed graph schema

Every v2 map payload has `currentMap`, with exactly `status`, `nodes`, `edges`,
`startingNode` and `bossNodes`. The owning public map event supplies `actIndex`.
No event/encounter identity, RNG state, seed, raw hash, insertion order or alternate
act identifier is accepted in a graph.

- `missing`: all arrays are empty and `startingNode` is null. Partial captures
  cannot imply a complete graph
- `complete`: unique nodes and boss coordinates are sorted by row then column;
  unique edges by source row/column then target row/column. Every edge advances
  a row and joins declared nodes
- The start has `start` or `ancient` type. The nonempty boss list is exactly all
  displayed boss nodes. Every node is reachable from the start and reaches a boss
- The choice slice agrees with graph node types and all graph edges between its
  included nodes. Slice nodes, edges and options are also canonical
- `unknown` remains explicit. Nothing infers unresolved encounters or events.
  Structural validation cannot certify a producer's claim of live-client visibility

`schema_v5.validate_public` checks every event, finite anchor and extension before
model execution. A private v4 projection strips the graph for frozen mechanical
checks/base encoding only. Full graphs remain in semantic identity, prefix checks,
target binding and the added model channel. No record is relabelled/admitted as v4.
Bounds reject captures above 4,096 nodes or 16,384 edges, with existing event and
transport limits also retained; nothing is truncated silently.

## Features and model

`model_v5.public_map_features` produces separate root and finite-anchor channels.
Capture, complete and missing counts distinguish no capture, a missing capture,
and a complete graph. Every graph observation remains in order, bound to public
act and chronological position.

Graph summaries include bounded counts, coordinate ranges and displayed type
counts. Each node entity contains public row/column, start/boss/current/offered
flags, in/out degree and a closed type one-hot. Edge entities bind their two
endpoint vectors. Set pooling removes dependence on native insertion order;
only canonical wire arrays are accepted. No hashes or audit/internal IDs enter
the new graph features. Frozen v4 hashing sees its original choice slice only.

`StudentV5` retains `StudentV4` as its base, pools node/edge entities, encodes the
ordered capture sequence, and adds a residual adapter to the action/plan heads.
The bounded pooling is deliberately lossy; this is no sufficiency or calibration
claim. Strict version/config/fingerprint/state-dict checks reject old checkpoints.

## Targets and inference

`data_v5.validate_targets` retains unavailable-as-null masks and conserved world
accounting. Whole-plan labels require the exact original active v5 anchor,
including the graph. Engineering records bind the full public v5 digest.
Conservative legacy aliases are only for split isolation, never features or labels.
`loss_v5.decision_loss` evaluates masks against full conditioning without adding
a fitting API. Existing engineering and raw-source envelopes always reject production admission. A distinct new candidate envelope can pass an explicit reviewed cohort contract through the separate bounded auxiliary profile; no current diagnostic cohort is admitted.

Standalone `nosl.inference_v5` imports no trainer, data loader or native engine.
Finite controllers retain their full v5 anchors. Untrained weights return
`MODEL_UNTRAINED`; forged training claims remain `MODEL_UNVALIDATED`, without
executing forward or selecting an action.

## Verification

New synthetic checks cover complete/missing/absent graphs, graph/slice rejection,
hidden fields, canonical wire order, JSON property-order identity invariance,
full graph identity sensitivity, anchors, frozen v4 compatibility, quarantined
sources, bundle isolation, deterministic graph-sensitive forward and loss masks.
Model evaluation uses `torch.no_grad()` and one CPU thread.

```sh
OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 PYTHONPATH=python \
  .venv/bin/python -m unittest discover -s tests/python -p 'test_public_map_v5.py' -v
PYTHONPATH=python .venv/bin/python -m nosl.inference_v5 \
  --config configs/student.v5.engineering.json < public-v5-input.jsonl
```

No backward pass or optimizer step is run, and the earlier authorized M6 gradient
check is not repeated. Temporary untrained bundle roundtrips are test-only.
Existing artifacts, datasets, sealed holdouts and experimental weights remain
untouched; no source population or fitting occurs.
