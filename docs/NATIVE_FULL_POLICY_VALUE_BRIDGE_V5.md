# Native v5 empirical-value bridge

`python/nosl/native_policy_v5.py` is a separate producer boundary between fresh
native complete-map candidates and the full-v5 policy record contract. It does
not change `native_v5.py`, the existing auxiliary snapshots, old raw exports,
native outcome facts, or historical split protection.

The bridge accepts three original byte artifacts: a raw candidate JSONL export,
the complete raw attempt array, and the original native report. The report must
use `nosl.native-complete-map.raw-report.v1` and bind the same records, attempts,
ordered predeclared draw list, teacher evaluation seeds, prior, runtime
dependencies and exact build-receipt string. The old diagnostic operation is
not an input format. An empty candidate export is valid only with a complete
report/attempt ledger; it still preserves failed-source provenance.

## Value and information preservation

The adapter passes owned copies through the strict auxiliary validator to reuse
its complete public-v5 validation, fixed combat-entry anchors, independently
ordered world ledger, terminal HP accounting, inventory closure, permanent
change accounting and error/truncation conservation. It then retains the
original public input and targets in the new record. The auxiliary adapter's
intentional erasure of value targets is never copied back to these original
targets.

Every original enabled value must equal the negative mean of the complete
native outcome costs. The implementation pins the actual C#
`ObjectiveEvaluator.cs`, `RolloutOutcome.cs` profile, and `TeacherDataset.cs`
source hashes. Its supported profile is exactly the development Candidate:

- Defeat cost 1000, downside coefficient 0.2, fixed combat-start HP
- No inventory prices and no permanent future-value prices
- Unknown inventory values cancel only for zero net loss-aware coefficients;
  inventory retained after a loss has zero future value
- Winning permanent changes require zero net amount for each unpriced kind;
  loss has zero retained permanent future value
- Incomplete worlds remain incomplete, and the full assigned mass is retained

There is no potion-price inference from the separate 9-HP consideration
threshold. A false source value mask stays false, including a deliberately
masked but arithmetically computable target. Unknown objective components keep
null values. The source's pairwise and equivalent-action sets must be empty;
the native source has no certified ranking support. No ranking pairs,
equivalence, or finite-plan labels are synthesized from empirical means.

The objective attestation explicitly records `objective_profile_status=candidate`,
`objective_calibrated=false`, and `calibration_evidence_sha256=null`. Its
per-record evaluation design binds the original outcomes, versions, runtime,
prior and ordered seeds. Source/evaluator hashes establish integrity and scope;
they do not establish authenticity or show that a declared binary was built
from its declared source.

## Sealed receipt and historical isolation

Every normalized record binds `producer_receipt_sha256`. The detached producer
receipt contains the exact original UTF-8 bytes and SHA256/length of all three
source artifacts, a versioned objective contract, implementation fingerprint,
per-outcome recomputation, the imported metadata-only protection registry and
its hash, every attempt, and the complete provenance closure.
Each normalized `source_record_sha256` hashes the original nonblank JSONL line's
exact UTF-8 bytes, excluding its line terminator. The receipt also retains a
separate canonical source-record digest and exact target-object digest. The
whole-artifact hash still includes blank lines and line terminators.

All raw record metadata and all attempt metadata enter that closure before any
eligibility decision. Failed, absent and unexecuted draws retain their source
identity, source-draw aliases and any captured public root. Historical split
owners and conflicts are preserved; no held-out target files are read.
`validate_policy_producer_receipt` reconstructs the receipt from the original
artifacts, rather than trusting re-signed derived fields.

The full policy loader must receive a mapping from each receipt digest to its
receipt. `validate_policy_record_source(record, receipt, config, protection)`
checks exact record membership, purpose and admission state, requires the same
imported protection, and returns the entire detached isolation metadata list.
The equivalent lower-level functions are
`validate_policy_record_producer`, `validate_policy_producer_receipt` and
`policy_producer_metadata`. Their output never goes into model public inputs.

## Three separate decisions

1. Adaptation produces `nosl.dataset.full-policy.record.v5.1` records with
   `purpose=native-objective-candidate` and `trainable=false`. They can be
   validated or inspected without admitting a training cohort.
2. `admit_native_policy_cohort` accepts a separately supplied exact-cohort
   review. It creates a new receipt and new `bounded-objective-pilot` records;
   the original candidate receipt and raw `trainable=false` declaration remain
   intact. Review binds the original receipt digest, reviewer/evidence identity,
   complete source selection, objective/public/isolation scope and explicit
   breadth gates. It rejects budget-expired or failed/unexecuted cohorts,
   incomplete outcomes, duplicate roots, protected aliases, historical bridges,
   and cohorts with no complete positive-weight action-value policy root.
3. Training authorization and frozen training-input review are separate gates
   in the full policy path. Neither this producer nor its cohort review invokes
   a fit, optimizer, backward pass, deployment, or formal-label promotion.

Minimum reviewed breadth requires at least one complete root, one positive
decision root, and one later-combat positive decision root. A single diagnostic
source chosen for interface coverage does not establish fresh cohort breadth or
justify admitting production data. Current real native v5 finite-plan labels
remain unavailable; old v2 finite-plan fixtures are not relabeled.

## CLI

```sh
python tools/adapt_native_policy_v5.py fresh-raw.jsonl \
  --attempts fresh-attempts.json --report fresh-report.json \
  --protection historical-metadata-protection.json \
  --config configs/student.v5.engineering.json \
  --output new-native-policy-candidates
```

The output directory must be new. `records.jsonl` contains the new full-v5
records; `producer-receipts.json` is the receipt mapping consumed by the full
policy loader. `summary.json` is written last and records file checksums,
enabled native value counts, cohort-admission state, and explicit false fitting
and formal-label flags. Source bytes are rechecked before writing. The optional
`--review exact-review.json` applies only an already supplied cohort review and
does not grant training authorization.

Synthetic tests in `tests/data/test_native_policy_v5.py` exercise this wire
contract and its rejection paths. Their values, review receipts and native-like
identities are synthetic test inputs, not authentic native calibration evidence.

## Verified single-source native interface

The [verification receipt](NATIVE_FULL_POLICY_VALUE_BRIDGE_V5_VERIFICATION.json)
records 17/17 guarded tests and one predeclared fresh raw API invocation against
the frozen native runtime `28cccdd`. Source draw `24004` was previously inspected
and chosen specifically to exercise existing empirical values. Independent
evaluation seeds were fixed at `1300501,1300502`; there was one invocation, no
replacement source, and no parameter tuning.

That invocation completed in 28.43 seconds: one root, seven legal candidates,
14/14 completed worlds and seven genuine enabled value targets. The adapter
preserved the complete public input, every original target/mask, and all raw
outcome data. Its independent Candidate-profile computation matched the native
values. The full-policy record validator, producer-receipt membership verifier,
and immutable loader accepted the candidate representation. Pairwise and
equivalent-action sets remained empty, with no finite-plan labels.

The complete historical protection registry was unavailable after a rollback.
This fixture therefore uses a separately marked incomplete diagnostic subset
containing the already inspected source-draw alias. It is not a replacement
historical registry and does not reconstruct frozen test seals or original split
ownership. Isolation correctly rejects this inspected source with
`previously_observed_source_or_public_alias`. The output remains an unadmitted,
uncalibrated `native-objective-candidate`, with `trainable=false`; no fitting or
cohort admission was performed. Complete historical protection remains a real
prerequisite for any future fitting work.

## Serializer integration version

Adapter `nosl.native-full-policy.adapter.v5.2` updates the pinned TeacherDataset
source hash after the opt-in public-v3 continuation added its separate dataset
identity. Objective arithmetic, the default v2 record branch, raw candidate
validation and admission gates are unchanged. The full-policy receipt fingerprint
changes with the adapter; a v5.1 receipt is rejected instead of silently rebuilt
under current code. Historical native-interface/one-backward reports and source
artifacts remain immutable. Any new adaptation writes a new record/receipt and
does not imply new simulation, calibration, fitting or admission.
