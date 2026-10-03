# Fresh constructed native-tape raw API

`native_constructed_tape_candidates` is a separate bounded raw producer for a
declared constructed setup. It executes a fresh native encounter, captures the
real public recorder output, releases the source world, and then evaluates every
legal root action through independent native-tape posterior worlds. It accepts
neither saved roots nor existing label rows. This operation does not perform
natural-source admission, calibration, training, or production promotion.

The schemas are `nosl.native-constructed-tape.raw-candidate.v1` and
`nosl.native-constructed-tape.raw-report.v1`; the source kind is
`constructed_under_explicit_label_tape_prior`. The purpose must be
`bounded-constructed-raw`. Both records and reports keep `trainable=false` and
`formal_labels=false`; source provenance explicitly keeps
`native_default_start=false`. The existing natural complete-map operation and its
admission rules are unchanged.

## Request and binding

First send `{"op":"native_constructed_tape_runtime_identity"}` to the exact worker
binary that will execute the request. Build the same exact-byte receipt used by
the native raw API: `upstream_commit`, `wrapper_source_sha256`,
`vendor_source_sha256`, `worker_assembly_sha256`, `core_assembly_sha256`, and
`runtime_version`, all as strings. The source hashes are build assertions, not
proof that a binary was compiled from particular sources. The worker verifies the
loaded worker/core assembly hashes, upstream pin, and runtime version; the receipt
SHA256 includes its original JSON whitespace.

The following shows the authored request shape. Replace the receipt placeholder
with the JSON receipt encoded as one string.

```json
{
  "op": "native_constructed_tape_candidates",
  "purpose": "bounded-constructed-raw",
  "build_receipt_json": "<exact receipt JSON string>",
  "options": {
    "collectionId": "my-constructed-fixture",
    "sourceDrawSeeds": [18001],
    "wallBudgetSeconds": 30,
    "enableConditioning": true,
    "prior": {
      "schemaVersion": "nosl.constructed-native-map-rewards-state-tape-prior.v1",
      "setup": {
        "encounter": "SludgeSpinnerWeak",
        "deck": ["StrikeSilent", "DefendSilent"],
        "potions": [],
        "relics": []
      },
      "sourcePolicyId": "nosl-public-rules-v2",
      "sourceDecisionHorizon": 8,
      "rootSelection": "opening",
      "decisionIndex": 0
    }
  },
  "teacherOptions": {
    "mode": "T0",
    "continuationPolicyId": "nosl-public-rules-v2",
    "explorationSeeds": [],
    "evaluationSeeds": [702, 701],
    "maxPosteriorAttempts": 4,
    "maxDecisions": 1,
    "formalLabels": false
  }
}
```

Request keys are case-sensitive, duplicate keys are rejected recursively, and
unknown members fail before any source executes. Prior input accepts only
`schemaVersion`, `setup`, `sourcePolicyId`, `sourceDecisionHorizon`, `rootSelection`,
and `decisionIndex`. The separate event-owner schema below additionally requires
`eventOwner`; that member is rejected for ordinary v1 requests, even when null.
Setup accepts only `encounter`, `deck`, `potions`, `relics`,
`hp`, `maxHp`, and `gold`. Computed prior law metadata appears in output and is not
an accepted override. There are no fields for seed-conditioned inference,
private state, fixed enemy HP, fabricated maps, replacement roots, or promotion.

The collector freezes the setup arrays, source draws and teacher seed arrays,
validates the runtime receipt, and hashes a complete collection contract before
the first source draw. The contract records the prior, root rule, source policy,
all requested draws, source-draw domain, teacher options, sampler implementation,
and runtime dependencies. Each row carries that contract hash, the receipt hash,
the prior identity, exact teacher options, and per-action evaluation seeds in
their requested order. A collection ID alone is not proof of freshness.

At most 16 unique source draw seeds, 16 unique independent evaluation seeds,
4,096 posterior proposals per sampled world, 300 teacher decisions per branch,
and 900 wall-clock seconds are accepted. Source and evaluation seeds must be
disjoint. The initial raw operation is T0 only, with no exploration or formal
labels. Sampling may use certified conditioning or plain rejection according to
the predeclared `enableConditioning` setting.

## Source law and public evidence

One independently drawn recipe is used for each requested source draw. A fixed
setup names an encounter from the native author factory and optional public
inventory/resources. Formation, enemy HP, AI, startup, coroutine ownership,
automatic settlement, and reward opportunities remain native. Encounter/setup
validation rejects unsupported content explicitly. Under the ordinary v1 prior,
encounters requiring an event owner return `constructed_forced_owner_required`;
an ordinary combat room cannot stand in for that owner. The versioned
TheLanternKey path below owns its actual native event instead.

The native run advances to the encounter's declared act before applying the base
inventory/resources and obtaining declared relics at constructed floor 0. Relics
run their native acquisition hooks and pickup choices at that point, so actual
combat-entry assets can differ from the requested base values. This is fresh
acquisition at the constructed act; it does not assert that those assets existed
during earlier act/map generation. Prior-act counters and history remain canonical
fresh defaults, with missing run-start evidence preserved explicitly.

Fresh acquisition between rooms currently rejects owner-dependent relic pickups:
`NeowsBones`, `LostCoffer`, `SmallCapsule`, `Kaleidoscope`, `CallingBell`,
`Cauldron`, `Orrery`, `GlassEye`, and `ToyBox`. Their pinned native acquisition
hooks require an event, rest, or merchant owner to resolve pending rewards.
`SeaGlass` is separately unsupported because its foreign-character selection is
not part of this setup contract. These are explicit preexecution setup gaps;
queued native rewards are never skipped and no owner or owner history is
fabricated. Independent declared owner setup remains future work. This rejection
closes unsupported-setup classification; it does not add content coverage.

Choose one root rule before execution: `opening`, `decision_index`,
`first_player_turn_2`, `first_player_turn_3`, or `first_pending_choice`. The source
follows its declared public continuation policy until the first matching decision
or the declared horizon. `decisionIndex` is only meaningful for `decision_index`
and must otherwise be zero. A recipe's decision index is not a claim about where
a phase-based rule actually stopped; `selected_public_decision_index` records the
actual captured revision. A root that is not reached is retained as absent with
no replacement draw.

The source's Map/Rewards/native-state primitive tape law is a declared label law,
not the game's finite common-seed law. Inference receives only the detached
recorded public packet and frozen setup prior, after the original source is
disposed. It does not receive the source seed, tape, native graph or private trace.

Public input uses the full `nosl.student.public.v5` channel with
`nosl.public-run-evidence.v2`. The real recorder begins with
`run_start_not_observed`; both evidence and run context retain
`completeFromRunStart=false`, and `combatEntryIndex` remains null. No map
observations or earlier run events are invented. Complete local combat history
does not imply complete history from a natural run start.

## Separately versioned TheLanternKey owner

The raw operation also accepts the distinct prior schema
`nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1`. It requires
this authored declaration alongside the usual setup and fixed root rule:

```json
{
  "schemaVersion": "nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1",
  "setup": {
    "encounter": "MysteriousKnightEventEncounter"
  },
  "eventOwner": {
    "event": "TheLanternKey",
    "act": "Hive",
    "fixtureFloor": 1,
    "choiceRule": "keep-the-key-then-fight-v1"
  },
  "sourcePolicyId": "nosl-public-rules-v2",
  "sourceDecisionHorizon": 48,
  "rootSelection": "opening",
  "decisionIndex": 0
}
```

Only that event, act, encounter and choice rule are supported. Fixture floor is
1 through the pinned Hive room count (14). It declares fresh native location
state using the existing fixture positioner, with no preceding rooms simulated
and no map observations emitted. Base inventory/resources and native relic pickup
hooks run before the event. Omitted HP/maxHP retain native 70/70 before pickup
effects, just as in the ordinary construction.

The world enters the real EventRoom before RunDriver drives its native event and
forced-combat lifecycle. The fixed public option rule selects KEEP_THE_KEY and
then FIGHT through SourceBridge, retaining both actual option offers and selected
keys. Both choices count against the source decision horizon. There is one
locally complete event owner and one complete combat child. Admission checks the
exact declared parent, act/floor and option history. Run-start evidence remains
missing, the natural combat count remains null, and ordinary v1/natural guards
retain their previous requirements.

With `enableConditioning=true`, this owner path uses the separately versioned
`owned-constructed-native-event-tape-shuffle-v2-public-evidence-v2` profile and
`nosl-constructed-native-event-tape-shuffle-v2-public-evidence-v2` sampler. Its
narrow [Lantern startup certificate](NATIVE_CONSTRUCTED_LANTERN_SHUFFLE.md) can
condition only the initial shuffle. Fixed native HP and later draws remain
unconditioned. Uncertified starts retain ordinary rejection under this version.
With `enableConditioning=false`, the historical rejection profile
`owned-constructed-native-event-tape-conditional-v1-public-evidence-v2` and sampler
`nosl-constructed-native-event-tape-rejection-v1-public-evidence-v2` remain the
reference path. These versions are bound before execution, not selected from
successful samples. The full v5 packet is matched exactly after source disposal.

Native victory stops after automatic settlement and before the first reward
decision. The actual extra SpecialCardReward for LanternKey remains an unresolved
opportunity; it is not silently acquired into the deck. A loss exits combat,
resumes the real event, clears its native pending extras and records the completed
event owner before terminal capture. The wrapper subsequently exits and releases
its owned event room on settlement, absence, cancellation or failure. Independent
forks replay their own complete owner graphs.

The ordinary prior's `eventOwner` field is omitted from serialization. Frozen
pre-change literal tests pin its exact prior bytes/identity, source-generation
bytes/identity and family/battle aliases. The new source-generation contract adds
`event_owner`; the conservative primitive family still joins identical run/tape
keys across both setup types. Existing v1 sampler metadata is unchanged. The raw
schema remains nonformal and nontrainable; consumers that implement only the
ordinary prior must reject this new prior/profile until separately extended.

PunchOff, FakeMerchant, the nine owner-dependent relic acquisitions and SeaGlass
remain explicit unsupported setup cases for this increment.

## Source isolation across selected roots

The full prior identity continues to bind the stopping rule and horizon. A
separate `source_generation_json` and its SHA256 `source_generation_identity`
bind the declared setup, setup/primitive laws, source policy/script, native
Silent A10 baseline, upstream pin and actual source RNG domain contracts. They
exclude the root rule, local decision index, horizon, collection, teacher, and
proposal-only seed. These fields are included in the preexecution collection
contract and report, and the detailed generation identity is retained in every
attempt's provenance and every raw row.

Canonical `source_run_group` is deliberately broader: it equals
`source_random_family_alias`, with `source_group_semantics` set to
`conservative_primitive_rng_family`. It binds only the actual primitive-law
namespace, native run-seed string and tape seed. It excludes setup and source
policy as well as all root-selection/teacher/proposal settings. Different
constructed setups or public policies can reuse primitive RNG cells, so this
conservative overgrouping protects correlated variants even if no matching
opening row was retained. Existing source-run grouping paths therefore see this
family directly. A family is not a claim that only one constructed execution
occurred.

The exact family preimage is the public JSON serialization of
`{primitive_namespace, native_run_seed, tape_seed}` in that order.
`primitive_namespace` is itself the serialized object
`{primitive_law, primitive_implementation, random_domains}`. The RNG domain
descriptors are included in `source_generation_json`: the native
`NOSL-NATIVE-TAPE-V1:{RunSeed:X16}` seed template and the unconditioned
little-endian SHA256 state, Rewards, and Map domain constants
`4E4F534C54415031`, `4E4F534C52574431`, and `4E4F534C4D415031` with their exact
address fields. Prefix the preimage's SHA256 with
`constructed-native-tape-random-family-v1:`. Neither `ProposalSeed` nor the
source draw seed enters this preimage; the latter only generates the actual
run/tape keys.

`underlying_battle_alias` retains a more specific configured source identity. Its
preimage is the serialized object
`{source_generation_identity, native_run_seed, tape_seed}` in that order; its
alias is `constructed-native-tape-configured-source-v1:<sha256>/combat:0`.
Opening, turn-2, turn-3 and fixed-index roots of the same declared generation
share this alias despite their different full prior identities. Configured
aliases may differ across setups/policies, while their canonical RNG-family
group remains shared. Distinct actual run/tape pairs have distinct family and
configured aliases.

The public input digest is additionally stored as
`public_root_alias=constructed-native-tape-public-root-v1:<public_state_digest>`.
It is captured in attempt provenance before source disposal, so a subsequent
cleanup error retains the observed-root alias even when no raw training row is
emitted. Another seed producing that same public root cannot evade future
exclusion through seed identity alone. These aliases are split-protection
metadata; they grant no admission or training permission.

## All-attempt and execution accounting

The report has exactly one ordered attempt for each requested source draw. Each
attempt retains its recipe, provenance, stage, source release state, elapsed time,
posterior proposal ledger when one exists, and its raw record index when a row was
produced. Attempt statuses distinguish `recorded_complete`, `absent`,
`posterior_exhausted`, `computation_truncated`, `engine_error`,
`unsupported_capability`, and `not_executed`. An evaluated incomplete or failed
root can still have a raw record; its status does not delete that row. Preflight
validation errors reject the request before drawing or opening any source.
Invalid or unsupported setup contracts are preflight failures, rather than
accepted collections with a selectively missing attempt ledger. Exactly-one-
attempt accounting applies after the request has passed that preflight.

Every legal action retains one outcome slot for every requested evaluation seed,
including posterior exhaustion, cancellation, branch/cleanup errors and decision
truncation. Missing outcomes keep null value/probability targets and false masks;
they are not losses or zero utility. No candidate is selected away to improve a
completion rate.

The existing teacher's `costs.worlds_allocated` and each target's
`allocated_worlds` are preserved as requested outcome-slot counts. The row labels
this meaning explicitly with `teacher_cost_semantics`, and includes
`requested_outcome_slots` / `requested_worlds`. These legacy counters are not
treated as measurements of executed work.

The independent `execution_accounting` ledger reports sampler calls, calls that
actually entered the sampler, sampled worlds returned, fork calls, candidate
worlds returned, step calls and successful step returns, plus per-world cleanup
and settlement-record status. `candidate_branches_started` means that a first
step was called; `steps_returned` means that a step returned successfully. These
are interface observations, not a claim to count every internal runtime
allocation. A teacher that never started has null accounting. A sampler failure
has no invented forked worlds. Cancellation before a later sample call leaves an
explicit unexecuted sample entry.

Settled candidate outcomes count only true terminal outcomes with completed
settlement after the shared teacher's cleanup checks. The exact original teacher
cost metadata remains available, including its elapsed/clone/settlement timings.
Raw accounting and typed outcome facts are evidence for a later review; this
producer provides no admission or training authority.

## Focused verification

`NativeConstructedTapeDatasetTests` checks worker dispatch and runtime binding,
fresh recorded v5 capture, source disposal before sampling, every legal action
and ordered evaluation seed, immutable preflight declarations, strict rejected
inputs, unsupported forced owners, absent/cancelled draws, injected cleanup and
sampling failures, null target masks, and separate requested/executed counters.
The native prior/source tests independently verify native lifecycle ownership and
posterior conditioning. These small fixtures are not a generated training corpus.
