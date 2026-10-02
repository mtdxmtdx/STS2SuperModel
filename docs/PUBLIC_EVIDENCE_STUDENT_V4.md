# Public-evidence student v4 engineering boundary

This is an independent, untrained engineering consumer for `nosl.student.public.v4`. Every existing v3 public field remains required, including the v3 observation and run context. The additional `public_evidence` field uses the exact `nosl.public-run-evidence.v1` wire contract: camelCase properties, snake_case enum strings and a closed `kind` discriminator. No v1/v2/v3 schema, implementation, configuration, checkpoint, corpus or weight file is changed or promoted.

## Boundary and evidence

Use `nosl.schema_v4.loads` for JSON transport. It rejects duplicate properties recursively, non-finite constants and duplicate properties inside encoded legacy public-history details. `validate_public` checks complete nested whitelists, configured model vocabularies, typed fact field-presence masks, event/owner ordinals, owner lifecycle and nesting, same-owner references, latest unconsumed offers, displayed/locked selections, map coordinates/edges/ordinary-connection flags, and explicit run/owner gaps. Combat actions must use an offered token and precede subsequent owner effects.

`nosl.evidence_v4.validate_evidence` validates a recorded evidence prefix, including explicitly incomplete prefixes. This channel means the declared observations were recorded; it does not claim to contain every fact a human could see. Keys carry public model/option names rather than native identities. Structural validation cannot establish the publicity or provenance of a string. Native field-by-field review remains necessary.

The student root must end at a stable recorded combat decision. Its history-free observation, legal offered actions and owner location must agree with the current root. The current owner's typed facts/actions must match the complete legacy public history, in order; dropping or rewriting a typed fact cannot silently preserve a completeness claim. Current incomplete combat history remains an `INCOMPLETE_HISTORY` abstention under the frozen mechanical guard. Past explicit run gaps are allowed and remain visible to identity and features; they do not invent current-combat gaps.

Nested evidence decision snapshots contain an intentionally empty untyped history. A private, discarded sentinel is used only to reuse the old syntactic observation/action validator. Actual history completeness is checked from the typed owner lifecycle, and current-root history is reconciled separately. That sentinel is never returned, hashed, encoded or used as evidence of completeness.

Finite controllers retain their entire v4 anchor. Its evidence must remain an exact prefix of current evidence, and subsequent advances cannot rewrite a previous prefix or reset the anchor. Existing Hunt/Regen deadlines, safety and terminal guards continue through a private v3 mechanical projection.

## Identity and model features

`public_identity_v4.public_input_digest` hashes the full root and finite anchor under an independent identity scheme. All typed history is retained: closed combats, unselected offers, offer groups and replacements, prices/locks, card-choice order and bundles, selections/cancellations, visible maps, observation gaps, assets and decision links. Only transport revisions and known unordered permanent/unknown decks are canonicalized consistently with existing identity rules. Evidence-stripped aliases deliberately over-group roots across versions for split isolation only. They are never label identities, model inputs or admission evidence.

`StudentV4` composes the frozen v3 mechanics with a separate evidence branch. Every event has explicit type, owner type, fact type, bounded ordinal/reference-distance features and a bounded signed hash of every typed path/value, including empty containers and numerical magnitudes. A GRU processes every event in order. Both the final state and the mean over all states contribute, giving early prefix events a direct path. Bounded summary features expose completeness, observed startup, event/owner/gap counts, open owners and the last observed public asset snapshot with a knownness bit. Root and finite-anchor channels are encoded separately.

The default event limit is 16,384. Over-limit input fails closed; the sequence is never truncated. The 64 MiB strict JSON transport limit also rejects oversized input. This finite feature representation and GRU are lossy engineering choices, not claims of complete semantic representation or policy strength. Exact identity is separate from feature hashing.

## Targets, source records and inference

`data_v4.validate_record` accepts only the explicit `nosl.public-run-evidence.engineering.v1` / `public_run_evidence_engineering` envelope. It requires `trainable:false` and a digest binding labels to the full v4 conditioning input, including the original finite anchor. Existing action head/mask, rollout-mass, pairwise/equivalence, whole-plan scope and unavailable-as-null guards remain enforced. Regen has no applicable learned target heads. Validation does not establish a target's truth or admit the row for training.

`validate_native_source_record` separately inspects `nosl.natural-source.v4` / `natural_raw_source_candidate` captures. They must have genuinely empty target arrays, zero teacher labels and `trainable:false`. It never synthesizes utility or mask rows. Audit metadata stays outside public identity and model features. The existing native tape/owned replay development envelopes remain unsupported by this consumer's target-record validator; adding a v4 public input does not admit them.

`validate_production_record` always rejects. There is no v4 dataset loader, preparation/training entry point, optimizer run, supervision promotion, calibrated policy or production admission. `InferenceV4` returns `MODEL_UNTRAINED` for an ordinary valid input without trained weights and `MODEL_UNVALIDATED` even if a caller supplies a forged trained/promoted manifest. Every response abstains from learned action selection. Finite controllers can report their existing terminal/guard outcome independently of learned execution. Bundle loading requires its exact v4 configuration, weight checksum and implementation/runtime fingerprint; legacy weights cannot be promoted.

## Verification

Run the focused forward-only suite:

```bash
PYTHONPATH=python OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 \
  python -m unittest discover -s tests/python -p test_public_evidence_v4.py -v
```

An independently generated C# natural-source record can be included with `NOSL_V4_NATIVE_SAMPLE=/absolute/path/to/native-v4-sample.json`. The optional check parses and validates that record, runs a finite forward, checks identity/inference abstention and verifies production quarantine. It never changes source targets, does not backpropagate and does not run an optimizer.

The initial producer integration check used a two-combat native sample containing 129 evidence events and 207,775 serialized evidence bytes. The strict boundary, current-history reconciliation and forward check passed. This one example is an engineering interoperability check, not coverage certification or a formal training run. The focused suite also tests changed earlier unselected offers affecting model outputs, unchanged weights/no gradients, immutable legacy-model behavior, full identity, malformed inputs, finite anchors and terminal settlement. All test-created evidence is explicitly synthetic and does not upgrade existing labels.

The integrated reviewed snapshot passes 26 forward-only tests including the actual native sample. V4 additionally enforces native integer enemy/pet HP, HP bounds and power source-slot rules, converts numeric overflow into schema rejection, and reconciles a complete combat count with complete recorded combat owners. Explicitly incomplete prefixes do not infer missing combat counts. The CLI recovers after a malformed huge-number request; no backward or optimizer execution is included.
