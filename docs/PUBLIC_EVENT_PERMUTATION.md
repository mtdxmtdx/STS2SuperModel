# Public whole-event-permutation proposal

`NativePublicEventPermutationCondition` is an optional accelerator for the declared
independent native-word tape prior. It samples one complete act-zero event shuffle;
the original `RunState.PullNextEvent` still selects events. It neither forces
successive event IDs independently nor defines a posterior for the finite seed law.
Every candidate must still satisfy the complete public replay predicate.

## Fixed public condition

The existing initial-map and opening-roster certificates establish Silent/A10,
the original Neow visit, and one public act identity, Overgrowth or Underdocks.
The whole original `EffectiveEventPool` is pinned in order, including shared and
currently ineligible entries. All types are sealed. The sole nonnatural entry is
`WarHistorianRepy`; it stays in the permutation and consumes shuffle support.

For each top-level event, a consecutive ordinary map owner must have published an
Unknown destination and its pre-selection assets. The scan stops before any gap,
uncertified event, foreign act, parented event, missing page, or unreviewed selection
modifier. It never drops an unknown earlier pull and conditions a later pull as if
it were first. The public event owners are observations, not source-run references.

The first-page option key sequences of `EventModel.GenerateInitialOptions` identify
these singleton candidate sets in the exhaustive pinned pools:

| Native event | Ordered public option keys |
| --- | --- |
| TabletOfTruth | DECIPHER, SMASH |
| WaterloggedScriptorium | BLOODY_INK, TENTACLE_QUILL, PRICKLY_SPONGE |
| SelfHelpBook | READ_THE_BACK[_LOCKED], READ_PASSAGE[_LOCKED], READ_ENTIRE_BOOK[_LOCKED] |

All three pages have no public prices. Lock flags may conservatively admit more
states; complete replay checks them. `NO_OPTIONS` alone does not identify
SelfHelpBook. Unrecognized or ambiguous signatures stop this extractor, rather
than choosing a hidden identity. The finite scan kernel itself accepts candidate
sets, and its tests exercise ambiguous two-event signatures.

## Eligibility and containment

Eligibility is source-pinned to the pooled events' `IsAllowed` implementations.
For one player in act zero, exact public predicates cover HP, gold, floor, potion
count, deck size, and Eternal-based `CardModel.IsRemovable/IsTransformable` gates.
Act-one-or-later gates are false. The single-player branches of DenseVegetation
and JungleMazeAdventure are true. Public deck/relic/potion listener types must
inherit the no-op `AbstractModel.ModifyNextEvent`,
`ModifyUnknownMapPointRoomTypes`, and `ModifyOddsIncreaseForUnrolledRoomType`.
This certifies the identity selection modifier between the observed assets and
the native event pull.

Each observation has a must-be-allowed and may-be-allowed mask. ByrdonisNest's
pet gate and WoodCarvings/SpiralingWhirlpool enchantment gates remain unknown.
LuminousChoir requires gold >=149 and a nonempty hidden RelicGrabBag: below 149
it is false; otherwise it remains unknown. No hypothetical bag is read when
building this condition. An unknown gate is existentially branched independently
at each observation, potentially adding impossible paths. That overapproximation
is deliberate: it cannot remove a fully compatible native permutation.

The kernel carries the cursor and visited-ID set jointly. Initial cursor 1 records
the already visited Neow. Every rejected candidate advances the cursor; successful
selection advances once and marks the selected identity visited. Scanning wraps.
After exactly one failed traversal, native fallback restarts at that same cursor
and chooses the first naturally appearing entry, ignoring eligibility and visited
IDs. Thus even repeated IDs can occur through fallback. A must-allowed unvisited
entry terminates the speculative scan; an unknown entry can either be selected
or skipped. Induction over observations embeds each concrete native path in this
state set. The finite exhaustive test independently enumerates eligibility paths,
permutations, initial visits and cursor positions and verifies that equivalence.

## Native word law and bounded rejection

Before any native callback, `Prepare` draws independent scratch words, executes
the full original `UnstableShuffle`, and accepts its complete permutation only
when the fixed necessary condition holds. It retains all n-1 original Fisher-Yates
words, exact `NextInt` floor buckets, discarded low bits and skipped/unobserved
pool entries. It does not replace native buckets with a uniform permutation law.
The fixed trial cap K applies to complete trials; failures, cancellation and
incomplete evaluations propagate with component statistics rather than becoming
false contradictions of public evidence.

Let p(w) be the native complete-word density, E the root-fixed superset event, and
Z=P(E). Successful bounded rejection has subdensity

q_success(w) = p(w) 1[E(w)] sum_{j=0}^{K-1}(1-Z)^j.

Its native/proposal ratio is Z/(1-(1-Z)^K), constant for this public root, pool and
K. The same constant is its envelope, so the correction acceptance is one without
drawing another word. This does **not** claim a numerical p/q of one, estimate Z,
or justify holding a latent act/pool/eligibility realization fixed during retries.
The detached predicate and trial law cannot depend on those hidden values. Final
full-public replay intersects E with the exact observed history and restores all
constraints deliberately relaxed by the superset. A finite unequal-bucket test
checks the bounded successful subdensity, including the failure mass.

## Owned native boundary

The optional `LabelEventGenerationScope` wraps only the original act event shuffle
in `RunState.GenerateActRooms`. With no scope, all native draws/swaps and selection
rules are unchanged. The proposal checks its attached run, act index/object/type,
original ordered pool, sequential UpFront stream, fresh floor/room/map state,
single Silent player, ascension and remaining native acts. An incompatible act
is a public mismatch; ownership or pool drift is an engine error.

The context is a sealed class with get-only identity fields and a copied pool, so
record cloning cannot copy completion onto a different run. Only Core sets its
completed permutation after the original shuffle returns successfully. The
proposal additionally checks the primitive draw count and completed permutation.
The existing `ForcePrefixWords` enforces full-state order, prior-cell freshness,
alias consistency and full prefix consumption. Missing/repeated/foreign scopes
remain unresolved. A missing completion marker does not replace a native exception
during unwind; later `ValidateCompletion` still rejects acceptance. The test
injects a native post-shuffle failure and checks exact exception identity.

## Verification scope

`NativePublicEventPermutationTests` covers finite existential containment, ambiguous
candidate sets, wrap/fallback/revisits, exact native shuffle bucket weights and
bounded correction, public-only extraction and hidden choir eligibility, cancellation,
ownership/pool/stream/reuse/alias/partial/native-completion guards, and native roots
11004 (SelfHelpBook then WaterloggedScriptorium) and 11007 (TabletOfTruth).
Identity replay of those existing source fixtures checks every public observation;
separate independent plans are checked against the whole-permutation condition.
Fixture source words are used only in the tests, never production proposal inputs.
These are engineering checks, not production generation, acceptance-rate evidence,
formal training, or claims that the broader data-stage gate has passed.
