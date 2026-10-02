# Optional public run context

The native source/replay observer can opt into `nosl.public.v3` with
`PublicContextProfile: "nosl.public-run-context.v1"`. Its `runContext` contains
exactly `schemaVersion`, `actIndex`, `floor`, `combatEntryIndex` and
`completeFromRunStart`. The student public envelope is `nosl.student.public.v3`.
Ordinary teacher records use `nosl.dataset.public-run-context.v1`; raw sequential
source records use `nosl.natural-source.v3`. Other explicit dataset producers
retain responsibility for their own versioned envelopes and admission.

`actIndex` is zero-based current act progress. `floor` is the currently visible
total visited-room count across acts, not the map row. `combatEntryIndex` is the
zero-based count of every publicly observed combat entry since run startup,
including the current entry. Forced fights and combats with no player decision
still count. Several fights can share a floor; advancing an act does not reset
the count. A local decision coordinate is already available from complete public
combat action history and matching action revisions, including card choices; it
is not duplicated in the context.

The native collector marks history complete only after an ordinary new-run
construction and its observed `BeginRun`, before any floor or combat. A recorder
attached midrun and the explicitly injected lifecycle fixture API publish
`completeFromRunStart: false` and `combatEntryIndex: null`. Neither infers missing
events from native private history, floor, source identifiers or a map. The
current act and floor remain available independently of this history.

`PublicCombatHistoryMode: "unavailable"` declares an observation channel that
always publishes the incomplete/null combination. It requires the run-context
profile. This mode is part of execution/prior identity and applies equally to
source generation, hypothetical proposals, forks and continuation packets. It
prevents a fresh hypothetical replay from disclosing a count its observer did
not know at the root. Omitting the mode enables complete recording only where
the actual startup recorder establishes it; it is not a claim that injected
fixtures have a remembered past. No other mode is accepted.

Both option properties are individually omitted when null. The appended
`PublicObservation.RunContext` property is individually omitted when null as
well. Legacy `nosl.public.v2` packets, default option JSON and default run/tape
prior hashes therefore keep their original bytes. Profile selection changes
the observation channel and prior identity. Old corpora are not retrofitted
from their audit metadata, and v3 evidence cannot be silently removed to admit
a record or a checkpoint through an old student boundary. Teacher and source
serialization reject inconsistent schema/context combinations.

The native observer uses current progress plus its own entry counter. It does
not export a source seed, private run graph, generated room identity, unseen
past content or future map outcomes. Act/floor provide public constraints; they
do not authorize setting native progress or forcing a path. Conditioning on a
known combat coordinate is a separate sampler proof against the declared prior.

This is an implementation in the pinned native simulator, not a live-tested
client observer. A future live producer must map act/floor to displayed current
progress, observe every combat entry from new-run startup, include forced and
no-decision fights, and preserve recorder continuity across save/resume. If that
continuity cannot be established, it must publish the unavailable mode. Original
client observation and differential verification remain M8 work.

`PublicRunContextTests` covers frozen legacy bytes/prior identities, invalid
profile and memory combinations, student-envelope selection, ordinary source
versus owned replay through a second combat, replay/fork consistency, pending
card choices without invented history, and midrun recorder rejection. This
profile alone does not establish broad posterior throughput, production corpus
admission, training readiness or learned policy strength.

On 2026-10-02, the six new cases passed with .NET SDK 9.0.303. The affected
regression selection (`PublicRunContextTests`, `NativeRunWorldTests`,
`NaturalSourceTests`, `NativeRunPriorTests`, `NativeTapeReplayTests` and
`TeacherDatasetProvenanceTests`) passed all 33 cases. No upstream game rules,
existing checkpoints or frozen corpora were changed by this implementation.

## Conditional tape coordinate use

The v4 tape sampler validates the observation channel declared in the prior. A
complete run-start recorder fixes the public zero-based combat-entry index C.
For latent tape/run state Z and independent uniform coordinate prior 1/(Cmax Dmax),
conditioning the public indices replaces those draws with their observed values.
The original and proposed unnormalized posterior masses differ by the same
constant Cmax Dmax for every matching Z. Absent coordinates remain part of the
original source law; no source is retried to fill a quota. When combat memory is
unavailable, only the local decision index is fixed and the sampled combat index
is retained, so its possible values and absent mass are not discarded.

Every component of the original recipe is still drawn before known coordinates
are replaced. Current act and floor are checked in complete public-packet equality;
they are never written into a hypothetical run or used to derive its seed. The
`enableConditioning: false` reference disables primitive proposals only: public
coordinate conditioning remains active, as local decision conditioning did in the
previous version. New v3 records remain quarantined engineering evidence.

The v3 Python student keeps the full context in semantic identity and explicit
root/finite-anchor features. Its mechanical submodule uses a private validated v2
projection, while context reaches distinct parameters. Existing weights are not
upgraded: the untrained v3 inference boundary abstains. A single authorized M6
gradient-connectivity backward passed with zero optimizer steps; it does not
establish learned policy quality.
