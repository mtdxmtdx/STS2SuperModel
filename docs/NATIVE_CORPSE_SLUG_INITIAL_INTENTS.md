# Exact public CorpseSlug initial intents

`NativePublicCorpseSlugIntentCondition` is an optional detached acceleration
certificate for the declared independent primitive-tape prior. It reads the first
startup `IntentPublished` facts for each owner, using the existing public-combat-v2
entry/self-Ravenous closure. It accepts complete, ordered, unslotted two- or
three-slug rosters whose displayed intents form the native cycle:

- Attack, damage 3, repeats 2
- Attack, damage 9, repeats 1
- Debuff, no attack fields

No private move ID, source seed, source graph, or current/live move enters the
condition. Current enemy intents can differ after a turn or Ravenous stun without
replacing the retained first publication. Malformed shapes, unsupported startup
hooks, missing history and parented event owners disable this proposal only. No
catalog entry or source root is dropped. Existing full-history and full-packet
replay equality remains mandatory.

## Native source closure and timing

The pinned `UnderdocksEncounters.CreateCorpseSlugsWeak` and
`CreateCorpseSlugsNormal` factories create two/three mutable slugs in an array,
call `CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves` once, and return the
same unslotted array order. That method draws one `NextInt(3)` before assigning
cyclic indices. `GenerateMoveStateMachine` creates the corresponding ordinary
move states, whose follow-ups retain their original native order. Neither the
initializer nor the proposal executes a move callback. `AfterAddedToRoom` applies
only self-Ravenous; its ordered publications fix lifetime public slot order.

For ordinary map rooms, `RunDriver.ConfigureCombatObserver` invokes
`NativeLabelTape.CombatEntering` before `PushRoom` and `CombatRoom.Prepare`.
The initializer therefore belongs to the current combat index, not the next
index. Event-layout preparation can run earlier from `EventRoom.EnterInternal`;
that owner/lifecycle is outside this certificate. There is no guessed offset.

`LabelCorpseSlugScope` conveys the actual hypothetical `CombatRoom.Prepare`
run/room context and `EncounterDefinition.CreateMonsters` encounter/RNG context
to a scope around the unchanged initializer. These are label-only observations.
Ordinary execution does not snapshot or enumerate the input. Active label
callbacks snapshot only an `IReadOnlyList` roster and share
`LabelRandomScope`'s callback-interception guard, keeping auxiliary sampling off
the native tape. Runtime guards require the attached run, entered room, original
reviewed encounter definition, original fresh factory RNG, mutable unbound unique
slug references, correct roster size, A10 sequential mode, and one initializer
per observed owner. Completion additionally requires exactly one factory RNG
draw, so later words allowed by the general prefix force scope cannot hide a
changed initializer.

## Exact finite law

For a primitive word `w`, native `MegaRandom.NextDouble` uses the upper 53 bits
`h`, then native `NextInt(3)` computes `int((h * 2^-53) * 3)` using IEEE doubles.
Its bucket masses, in cycle order, are:

`3002399751580331, 3002399751580330, 3002399751580331`

They sum to `2^53`; they are not all equal and integer ceiling boundaries alone
would miss the middle rounding boundary. `ConditionalShuffleProposal.Factor`
computes the native floating-point preimages. The plan samples a high-bit value
uniformly in the observed bucket and the discarded low 11 bits independently
and uniformly. Thus each compatible full word has proposal probability
`1 / (bucketSize * 2^11)` and native/proposal ratio `bucketSize / 2^53`.
The public ordered cycle uniquely fixes the starter bucket, making that ratio
constant across all compatible worlds. The envelope equals this exact ratio,
so correction is identically true without a further random draw. HP/shuffle and
all other proposal corrections still apply. This claim is about the declared
ideal independent-word tape, not the game's finite common-seed PRNG prior.

## Integration API

The helper intentionally does not edit the shared `NativeLabelTape` or combat
prefix/shuffle classes. Integration should:

1. Build `NativePublicCorpseSlugIntentCondition.Create(publicRoot)` once from the
   public evidence; pass it unchanged into owned replay copies
2. Construct `NativePublicCorpseSlugIntentProposal` with an independent proposal
   RNG and the tape's `ForcePrefixWords` callback
3. Attach the hypothetical run and forward each existing `CombatEntering` index,
   entry assets and shuffle RNG, including ineligible combat entries
4. Enter `LabelCorpseSlugScope.Enter(proposal.BeginInitialIntents)` alongside the
   existing tape scope, with normal nested disposal
5. Invoke `ValidateCompletion` before correction, compose its ratio/envelope and
   `AcceptCorrection`, and keep the final full-history/public-packet comparison

The one-word force scope uses the existing exact hypothetical RNG-state,
previously visited cell, replay override and consumption guards. An unresolved
state alias remains an error; it is never overwritten or treated as a native
rejection.

## Verification

Focused tests enumerate all reduced four-bit native three-bucket outcomes and
every compatible high-bit preimage, test both low-bit extremes and all native
53-bit bucket boundaries, preserve ordinary lazy-enumeration/draw order, and
verify auxiliary callback sampling is not intercepted. Both native factories
publish the requested shapes alongside existing HP/shuffle conditioning and
retain their native follow-up moves. Full hypothetical replay tests compare all
public packets through owned settlement for two- and three-slug fights. Runtime
negative tests cover owner/index/room/stream/roster/encounter drift, repeated or
missing initializers, actual production tape alias and wrong-stream failures,
and incomplete word consumption. These are bounded correctness fixtures, not
new production sampling, fitting, training, or an empirical acceptance claim.
