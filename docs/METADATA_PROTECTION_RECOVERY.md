# Historical protection metadata recovery

The original first-5,000 prepared protection is restored. Complete historical
protection remains **incomplete**, so new admission and fitting remain blocked.
This recovery restores identities, original split ownership, and frozen-test
seals; it neither restores nor inspects target rows.

The compact [recovery receipt](../configs/protection_metadata_recovery_20261003.json)
binds provenance, counts, hashes, scope, and missing history. Multi-megabyte
registries and archives remain ignored local artifacts under
`artifacts/recovery-protection-metadata/`.

## Restored and verified

The three standalone user-supplied ZIP volumes were inventoried independently.
All 621 recovery-manifest references exist and their sizes match the central
directories. Whole-volume SHA256 and selected metadata CRC32/SHA256 checks passed.
No decision, train, validation, test, quarantine, outcome, or checkpoint payload
was opened, decompressed, extracted, or separately hashed. `testzip` was not used.

The [original registry](../artifacts/recovery-protection-metadata/first5000-base-protection.json)
preserves exactly 2,533 components and 18,207 tokens from the original split state:
2,022 train, 267 validation, and 244 test components. All 5,304 public aliases
remain, including 524 test-connected aliases. The state retains 5,000 accepted
public identities and 494 frozen-test identities, with zero cross-split conflicts.

Original provenance:

- Archive source commit: `8ad033030205f064946f7ac42572fe640edf23fc`
- Prepared manifest SHA256: `4346cb9643900138fe12fa6bd64cacb17b01bd383a178ef3e90b29bac3dbe997`
- Split-state SHA256: `243a5c58e591dfcda2fa75bc008526f1ceccf4e8d214b4b1638b0a962034237b`
- Original registry file SHA256: `11018097f16541154db11908163c2bd7e79ee94f5217932fbbb43ec7eab0b4f4`
- Frozen test seal: SHA256 `8c684b1c0bba7be4e97d8e5ce5151a835404b2d7d18cc84f71d7af1a2590de72`,
  10,704,382 bytes, 494 rows

The test seal was recovered and cross-checked through manifest/stage/state
metadata. The test payload itself was not rehashed or inspected.

## Conservative observed-source extensions

Seven metadata journals retain 10,131 unique attempt indices: 5,311 accepted,
4,813 failed, and seven interrupted. Their 2,628 source battles include 95
journal-only groups absent from the prepared state. The state's 5,311 cumulative
attempt count is the preparation-input denominator, not the complete generation
attempt denominator. Every journal public digest exists in the original state;
its derived run/combat/family aliases match its original component. No old
generator was executed.

The [attempt-extended registry](../artifacts/recovery-protection-metadata/first5000-with-attempts-protection-v5.json)
keeps those 95 exclusions separate from the unchanged original base. The
[identity/status journal](../artifacts/recovery-protection-metadata/first5000-attempt-identity-status.jsonl)
preserves all attempt statuses without target or outcome values.

Eleven already-inspected map v4/v5/v6 response files contribute 20 distinct
public/source projections and 20 attempt-draw identities, covering source draws
24001–24008 and 24101–24112. Their target/outcome values were skipped lexically.
The resulting [incomplete current registry](../artifacts/recovery-protection-metadata/INCOMPLETE-current-protection-v5.json)
has 2,648 components: 2,533 original + 95 journal-only + 20 map diagnostic groups.

The strict registry schema supports train/validation/test owners only. For new
journal-only and diagnostic components, `train` is an opaque observed-source
exclusion tag; it does not infer historical training or a missing split. Current
v5 preparation, dispatch, and policy review exclude **all** protected owner roles.
All 2,648 actual component probes and all three role tags were checked. The
legacy generic importer rejects the v5 wrapper. Never unwrap it for the legacy
relabeling path, which can deliberately reuse genuine historical train sources.

The extended file SHA256 is
`fff4b77ea36e1ac36dfabfc9edf9d9ad70bf150bd7f820c7d9028a211acfd559`;
the v5 canonical-object binding is
`fc54217445b59bcea5427b16b13f3ef9fa9f1b5ca6dc1298244fc0b77ae9af10`.
These are different serialization contracts. Historical completeness is recorded
in the receipt and local sidecar, not in the strict registry schema. Structural
validation cannot establish that omitted history was supplied.

## Missing history and surviving-archive inventory

The following still require actual source/public identity metadata and original
split ownership where assigned:

- Later balanced 160-attempt / 40-battle block: 81 decisions, 73 effective roots;
  known manifest `adf545cd6ddd13d00a1323ae42318cea4b24797c2d59928b199fb9a3b7ed7db1`
- Sixteen-source constructed policy evaluation
- Fixed natural 200-root cohort and failed-attempt identities
- Documented 2,579-component registry plus later fresh-native regression sources
- Other earlier diagnostic histories omitted from the available map responses

A separate audit inspected central-directory names/sizes of 14 surviving NOSL
ZIPs, with archive payload opening disabled. No named identity files for the
missing cohorts above were found. The original authorized v3 archive does retain
`bound-attempts.json` and `raw-attempts.json` under
`evidence/reports/map-public-v3/candidate-boundary/fresh-source24006-eval601-602-v2/`.
These are potential additional diagnostic metadata, not yet inspected/restored
by this audit. Draw 24006 is already excluded; completeness of extra aliases is
unverified. Existing v4/v5 backups mainly retain the map diagnostics already
covered here.

The audit did not decompress compressed TAR archives or nested archive payloads;
separately materialized `source.zip` files were inventoried. It does not prove
absence from every possible surviving payload. No denied Library reference was
retried or accessed through another route. A larger component count than the old
2,579 is not evidence of more complete historical coverage.

## Validation and execution boundary

Existing registry/state/v5 validators and five focused synthetic protection and
admission regressions passed at `3ae74c7`. The six exclusion-source hashes remain
unchanged at documentation base `561f9ab`; no tests or builds were rerun during
timed v7 probes. The receipt records those hashes and distinguishes historical
checks from the new lightweight JSON/diff checks.

See the local [verification report](../artifacts/recovery-protection-metadata/verification-report.json),
[exclusion guard evidence](../artifacts/recovery-protection-metadata/exclusion-guard-verification.json),
[focused test log](../artifacts/recovery-protection-metadata/protection-focused-tests.log),
and [recovery instructions](../artifacts/recovery-protection-metadata/README.md).
No generation, fit, additional backward call, optimizer step, or model/corpus
mutation occurred. This documentation change does not alter readiness gates.
