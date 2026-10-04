# Fresh complete-map raw candidate API

`native_complete_map_candidates` is a separate, bounded v5 collection operation.
It generates each source from a declared `nosl.native-map-rewards-state-tape-prior.v1`
using the native producer, disposes the original world after detaching its complete
public packet, and passes only that packet and the declared prior to inference.
There is no request field for a saved row, source graph, packet, hidden state or
source reachability assertion. Unknown and duplicate request keys are rejected.
A plausible arbitrary graph is not a certificate of native support.

The existing `native_tape_replay` operation remains a quarantined diagnostic
export, with its previous record/report shape. It has no candidate-mode option.
Persisted diagnostic rows cannot be submitted to the new endpoint or promoted.
The shared collection/evaluation implementation preserves the fixed source draw
list, all source attempts and one outcome per independent evaluation seed per
candidate, including errors, truncations, absent roots and unexecuted draws.

## Request and limits

First call the read-only operation:

```json
{"op":"native_complete_map_runtime_identity"}
```

It returns `upstream_commit`, `worker_assembly_sha256`, `core_assembly_sha256`
and `runtime_version`, without filesystem paths. Add `wrapper_source_sha256`
and `vendor_source_sha256`, then serialize exactly those six keys as a JSON
object. Pass the resulting exact JSON string as `build_receipt_json`:

```json
{
  "op": "native_complete_map_candidates",
  "purpose": "bounded-pilot",
  "build_receipt_json": "<exact six-key build receipt JSON string>",
  "options": {
    "prior": {
      "schemaVersion": "nosl.native-map-rewards-state-tape-prior.v1",
      "eligibleCombats": 1,
      "eligibleDecisionsPerCombat": 1,
      "execution": {
        "maxFloors": 1,
        "sourceDecisionHorizon": 64,
        "sourcePolicyId": "nosl-public-rules-v2",
        "outsideCombatScript": "nosl-natural-public-script-v3",
        "publicContextProfile": "nosl.public-run-context.v1",
        "publicEvidenceProfile": "nosl.public-run-evidence.v2",
        "publicMapObservationProfile": "nosl.public-map-complete-graph.v1"
      }
    },
    "sourceDrawSeeds": [11001],
    "collectionId": "fresh-api-contract-fixture",
    "wallBudgetSeconds": 30,
    "eventPermutationMaxTrials": 1
  },
  "teacherOptions": {
    "mode": "T0",
    "continuationPolicyId": "nosl-public-rules-v2",
    "evaluationSeeds": [402, 401],
    "explorationSeeds": [],
    "maxPosteriorAttempts": 1,
    "maxDecisions": 1,
    "formalLabels": false
  }
}
```

The mode, receipt and explicit prior/profile are validated before source generation.
Only conditioned T0 with the reviewed continuation, complete combat history and
v3 source script is accepted.
`publicCombatHistoryMode=unavailable` is rejected before source generation.
No exploration worlds, reference comparison or formal labels are allowed. Existing
bounds remain: at most 16 source draws, 16 evaluation worlds, 4096 posterior
proposals per world, 300 continuation decisions and 900 wall-clock seconds.
Source and evaluation seeds must be unique and disjoint.

A receipt must contain exactly six nonempty string fields; four hashes are lowercase
64-character SHA256 values. Its upstream commit must be
`5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`. Before collecting, the worker checks
Worker/Core assembly-file hashes at the loaded assemblies' locations and the actual
`Environment.Version.ToString()` against the receipt. The receipt SHA256 hashes the
exact UTF-8 string, including whitespace. Source hashes remain declared build
assertions; this receipt does not prove the binaries were compiled from those
sources, certify authenticity, establish quality admission or authorize training.

## Output and adapter boundary

The report schema is `nosl.native-complete-map.raw-report.v1`, with status
`bounded_fresh_candidates_require_separate_quality_admission` and
`selection=predeclared_all_attempts_no_replacement`. It retains the normal bounded
collection counters/options and adds `purpose`, exact `build_receipt_json`,
`build_receipt_sha256` and `runtime_dependencies`.

Each raw record has exactly `schema_version`, `record_kind`, `public_input`,
`targets`, and `audit_only`. Its schema is
`nosl.native-complete-map.raw-candidate.v1`; its kind is
`native_complete_map_raw_candidate`. Public input remains the full
`nosl.student.public.v5` packet with v2 evidence and complete map channel.
All source seeds, grouping identities, runtime dependencies and receipt hashes
stay in audit/report fields, never student features.

Audit records include the source draw and selected combat/decision indices,
`collection_id`, `draw_id`, declared prior/identity, original public-state digest,
source provenance, exact runtime dependency/receipt binding and a complete version
set. The current sampler revision is
`nosl-native-map-rewards-tape-marginalized-v3-public-evidence-v2`, while the existing
posterior law profile remains
`owned-native-map-rewards-tape-marginalized-v1-public-evidence-v2`; these are recorded
as their actual distinct identifiers. No version is fabricated to align them.

`outcome_samples` contains `{action_index, world_seeds, outcomes}` for every legal
action. `world_seeds` is the exact requested evaluation order, including failure
slots. Costs, masks, errors and unresolved mass are retained. `trainable`,
`formal_labels`, `objective_calibrated`, and `source_seed_conditioning` remain false;
`independent_final_evaluation` and `native_run` remain true. `purpose=bounded-pilot`
expresses the requested collection purpose, not a passed pilot quality gate.

Every attempt has `draw_id` (`collection_id + "/draw:" + zero-based index`),
`source_draw_seed`, `status` (`recorded`, `absent`, `failed`, `not_executed`),
`raw_record_index` (or null), `detail` and `provenance`. Detail preserves source
status, recipe and timing as JSON text. Provenance contains known native source
identities even when no root exists; after a root is detached it also contains its
full public input, source combat/branch grouping and public-state digest. The
adapter can bind each recorded index to its canonical candidate digest without
dropping failed or absent draws from protected source history.

Python owns portable canonical public-input/prior digests, exact raw artifact
binding and separate admission. This API does not duplicate semantic identity
normalization, perform fitting, change historic quality failures or grant permission
to launch a larger cohort. The regression tests are tiny API contract checks,
including one source-cleanup
failure fixture, not evidence that the current fresh-eight quality failure has
been resolved. `existingRoots` counts successfully observed/captured roots before
source cleanup, including a root whose subsequent cleanup fails. Such a draw
retains its public/source provenance as `failed` with no raw candidate. This also
corrects the diagnostic report's previously undercounted root denominator on
cleanup failure; its schema and quarantined labels are unchanged.
