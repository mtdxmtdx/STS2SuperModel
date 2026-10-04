# Complete-context v5 bounded data preparation

This is an additive **auxiliary-label data-engineering path**. It prepares one
immutable, reviewed cohort and loads its train/validation shards. It does not
collect sources, fit a model, authorize training, calibrate utility, or promote
learned inference. The existing v5 student configuration and guarded inference
remain unchanged. Existing raw-source and tape-development envelopes remain
quarantined, including the inspected fresh-eight probe and its reruns. No old
5,000-row corpus, frozen494 target, or existing checkpoint is migrated.

## Native-to-Python boundary

The native producer must emit a separate fresh-collection envelope:

- `schema_version`: `nosl.native-complete-map.raw-candidate.v1`
- `record_kind`: `native_complete_map_raw_candidate`
- `public_input`: the complete unchanged `nosl.student.public.v5` input, including
  evidence v2, the initial complete act-zero graph, later explicit Complete/Missing
  captures, all public history and all legal candidates
- `targets`: native teacher auxiliary labels and the complete candidate rows
- `audit_only`: the fields below

`tools/adapt_native_v5.py` accepts only that new envelope. It cannot rename or
promote persisted diagnostic records. The pure Python adapter binds the original
UTF-8 export bytes and each raw record by SHA256. It computes the v5 semantic
conditioning digest with the existing canonical identity implementation, including
encoded public detail JSON and unordered public deck semantics. It also computes
the portable canonical prior digest. The native serialized prior identity is
retained separately; Python does not pretend that canonical JSON reproduces C#
property ordering.

The adapter preserves full public input, outcome facts, all allocated worlds and
all auxiliary target values. This first admission profile explicitly disables
absolute utility, pairwise preferences and equivalence labels. It masks these
fields in a new derivative artifact and retains the source target hash. It does
not fabricate values or infer strong rankings from observed HP differences.

Required raw audit fields:

- Collection/source: `purpose`, `source_kind`, `collection_id`, `draw_id`,
  `source_draw_seed`, `selected_combat_index`, `selected_decision_index`,
  `source_run_group`, `source_combat_id`, `branch_family`, `actual_seed`,
  `native_source_run_identity`, `public_state_digest`
- Public producer: `native_run:true`, `native_default_start:true`,
  `public_map_observation_profile`, `map_marginalization_contract`
- Status: `trainable:false`, `formal_labels:false`, `objective_calibrated:false`,
  `source_seed_conditioning:false`, `independent_final_evaluation:true`
- Dependencies: `declared_prior`, `source_prior_identity`, `runtime_dependencies`,
  `build_receipt_sha256`, `versions`
- Evaluation: `sampler_seeds`, `exploration_seeds:[]`, `n_exploration:0`,
  `n_independent_eval`, `n_error`, `n_unresolved`, `costs`, and
  `outcome_samples:[{action_index,world_seeds,outcomes}]`

The world-seed list must exactly equal the independent evaluation seed list, in
order, for every action. Assigned world IDs cannot be omitted or repeated; outcome
rows cannot be normalized over completed subsets. The adapter supplies each sample's conditioning digest. T0 is
the only reviewed continuation mode in this profile. Selected source coordinates
must match public combat-entry index, action revisions and public action-history
count, and lie within the declared coordinate prior.

`runtime_dependencies` has exactly six keys: `upstream_commit`,
`wrapper_source_sha256`, `vendor_source_sha256`, `worker_assembly_sha256`,
`core_assembly_sha256`, `runtime_version`. The native endpoint checks loaded
assembly hashes against a supplied build receipt. Source hashes remain explicit
build-receipt assertions; these checks do not cryptographically prove compilation
from the asserted sources. Admission embeds the exact build-receipt JSON text,
verifies its byte hash and parsed dependency identity, and binds it to every row.

`versions` pins public/observation/evidence schemas, map profile/support, teacher,
continuation, objective, inactive controller, settlement endpoint, rules, upstream,
source script, prior identity, sampler, posterior profile, dataset and runtime
receipt identity. Sampler and posterior revisions are separate exact dependencies:
a reviewed sampler v2 may retain posterior profile v1. An updated version pair
requires a new exact-bound review receipt; it is not permanently restricted to
sampler v1 or inherited from a row's Boolean claim.

## Candidate and review contracts

Adapted candidates use `nosl.dataset.native-complete-map.candidate.v1` and
`native_complete_map_auxiliary_candidate`. `native_v5.validate_candidate` verifies:

- Full v5 schema/identity, complete recorded run history, an initial act-zero
  complete map capture and candidate alignment
- Exact source, producer, prior, build and runtime dependencies
- Full independent seed accounting, including cohort-wide source/evaluation
  separation at admission
- Every allocated action outcome, terminal kinds, settlement and continuation IDs,
  fixed combat-start HP/max HP, HP event accounting, inventory anchors and resource
  ledger closure, and maximum-HP permanent-change accounting
- Empirical auxiliary labels against those outcomes; null/false masks for unresolved
  candidate mass; all action/error/cost totals

The initial map owner must be act zero and its first observed map capture must
be complete. Later captures retain their explicit `complete` or `missing` status
unchanged. In particular, the current native producer intentionally emits `missing`
for later acts; this does not make recorded run evidence incomplete. Only the
initial producer-certified graph supports Map marginalization, while later maps
retain native generation and exact public-evidence matching. Absent/null map fields,
partial graphs, and false history-completeness claims still reject. No graph is
backfilled. See [producer preconditions](PUBLIC_COMPLETE_MAP_CHANNEL.md) and
[the later-root reconstruction contract](NATIVE_COMPLETE_MAP_RECONSTRUCTION.md).

A new reviewed receipt uses `nosl.dataset.native-complete-map.admission.v1`, profile
`complete-map-auxiliary-pilot-v1`. It binds the ordered candidate digests, full
candidate artifact identity, target-free public/provenance/accounting projection,
complete ordered attempt ledger, student configuration, exact versions and historical
protection. It also supplies predeclared source draws, minimum completion/breadth
criteria and a hashed external review artifact. Python recomputes the thresholds.
A producer's `trainable` or `gate_passed` claim is never an admission mechanism.

The receipt has one explicit purpose:

- `engineering-fixture`: synthetic contract verification only. It must identify
  `synthetic_engineering_fixture` sources and `synthetic_contract_only` review
- `bounded-pilot`: fresh native sources with `predeclared_fresh_cohort_quality`
  review. This conservative first profile requires every recorded root fully
  settled, no engine errors, no deadline selection, no failed/unexecuted draws,
  unique full public roots and positive predeclared later-decision/combat minima

All requested draws remain in the denominator, including genuinely absent roots.
Every attempt must retain its actual source identity even if no public root exists.
No replacement draws or completion quota may be used. Any overlap with protected
history blocks a bounded-pilot cohort; engineering fixtures can report exclusions.
The repository does not supply an accepted real-cohort receipt. A hash proves
integrity under the reviewed contract, not the authenticity of arbitrary caller
assertions or permission to run an optimizer.

## Protection, immutable preparation and loading

`protection_v5` wraps an existing validated metadata-only registry. It preserves
all original aliases and split owners and adds complete v5 identities plus
conservative v4/v3/v2/v1 aliases and native source identities. Invalid/malformed
public metadata still contributes recoverable aliases before any filter; missing
evidence cannot hide a legacy alias. Strict metadata whitelists exclude labels
and outcome arrays. The full v5 digest is the sole semantic label/dedup identity.
Legacy projections are used only to over-group related history for isolation.

`prepare_v5.persist_snapshot` creates a new output directory exclusively. There
is no append, overwrite, or in-place conversion. It saves candidates, the complete
attempt ledger, receipt, protection, target-free metadata, split state, train,
validation, frozen test and quarantine artifacts, then writes the manifest last.
Versions, source fingerprints, configuration, row counts and checksums are locked.
Partial output without a manifest cannot load. A later cohort gets a new directory
and protects every previously observed source, including failed/excluded attempts.

`PreparedDatasetV5(..., purpose="bounded-pilot")` independently verifies the
snapshot, receipt, exact dependencies, metadata closure, component/split ownership,
selected row outcomes and immutable hashes. Synthetic snapshots require the
explicit `purpose="engineering-fixture"` argument. The loader accepts only train
and validation. It hashes candidate, quarantine and test files as opaque bytes;
the reviewed metadata projection supports isolation and admission replay without
decoding test labels. `verify_integrity()` catches changed files or in-memory rows.
Protection export preserves new validation/test ownership and reads no target rows.

The corpus report always records `fit_authorized:false`,
`formal_training_ready:false` and `policy_supervision_roots:0`. Auxiliary admission
is useful preparation but cannot claim full M3–M6 completion or policy readiness.
Broad native throughput/coverage, risk/resource calibration, future utility profiles,
a v5 training/checkpoint implementation, and separately authorized fitting remain
separate work. Neither the existing engineering v5 config nor this data profile
opens learned action selection.

## Verification boundary

All positive automated fixtures are explicitly synthetic engineering data. Tests
cover old-envelope rejection, source/outcome/graph tampering, incomplete mass,
receipt/build/config binding, malformed provenance bridges, historical protection,
immutable shards, opaque test targets and a single no-grad full-graph forward/loss.
No backward call, optimizer step, native collection or historical target decoding
is part of these checks.

Example commands for already-authorized artifacts (these commands do not collect
new sources or create a quality approval):

```sh
python -B tools/adapt_native_v5.py fresh-raw-candidates.jsonl \
  --attempts fresh-attempts.json \
  --output fresh-auxiliary-candidates.jsonl \
  --attempts-output fresh-bound-attempts.json
python -B tools/native_protection_v5.py \
  --base-registry existing-historical-registry.json \
  --metadata all-inspected-development-public-metadata.jsonl \
  --output complete-context-protection.json
python -B tools/prepare_dataset_v5.py fresh-auxiliary-candidates.jsonl \
  --attempts fresh-bound-attempts.json \
  --admission separately-reviewed-cohort-receipt.json \
  --protection complete-context-protection.json \
  --output-dir new-immutable-v5-snapshot
```

For the next cohort, `native_protection_v5.py --prepared-v5 <snapshot>` exports
all prior candidate/attempt provenance and its split ownership. Additional inspected
metadata may be supplied with `--metadata`. Target-bearing metadata is rejected.
The historical registry and output artifacts must be complete inputs to the review;
the tool cannot discover omitted historical collections or authenticate a reviewer.
