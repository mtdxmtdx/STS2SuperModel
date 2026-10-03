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
| SunkenStatue | GRAB_SWORD, DIVE_INTO_WATER |
| WoodCarvings | BIRD, SNAKE, TORUS or BIRD, TORUS |
| RoomFullOfCheese | GORGE, SEARCH |

All six event signatures have no public prices. Lock flags may conservatively admit more
states; complete replay checks them. `NO_OPTIONS` alone does not identify
SelfHelpBook. Unrecognized or ambiguous signatures stop this extractor, rather
than choosing a hidden identity. The finite scan kernel itself accepts candidate
sets, and its tests exercise ambiguous two-event signatures.

The three additional identities are source-certified over the entire pinned
native pools, not inferred from a retained source seed. `SunkenStatue` and
`RoomFullOfCheese.GenerateInitialOptions` publish their fixed pairs.
`WoodCarvings.GenerateInitialOptions` always publishes BIRD first and TORUS last;
it inserts SNAKE exactly when some deck card can receive Slither. Both native
pages are recognized without inspecting that hypothetical deck. A review of every
pooled `GenerateInitialOptions`, including their called option helpers and dynamic
keys, excludes these first tokens from every competing event. In particular:

- `RelicTrader` uses PROCEED or TOP/MIDDLE/BOTTOM, not a relic identity as its key
- `EndlessConveyor` uses one of its finite dish IDs or LOCKED, followed by OBSERVE_CHEF
- `SlipperyBridge` starts with OVERCOME; its variable HOLD_ON suffix cannot match
- Ranwid, StoneOfAllTime, Symbiote, SelfHelpBook, TeaMaster and WelcomeToWongos
  only choose their source-literal option keys or their `_LOCKED` variants
- The remaining pooled first pages use source-literal keys; their conditional
  insertions/removals introduce none of GRAB_SWORD, BIRD or GORGE

The wider vendored event catalog also has no competing first page containing any
of those three identifying first tokens. LostWisp does use SEARCH, but its page
is CLAIM/SEARCH; matching SEARCH alone would be unsound and is explicitly rejected.
None of the pinned pool classes overrides `GenerateInitialOptionsWrapper`.
Only the first owner page is considered, so later pages, forced fights and reward
choices cannot be confused with a new event pull. The whole ordered page and no
price requirement remain mandatory, rather than matching just the unique token.

## Map-law compatibility and scope

The retained Map-law roots 24002, 24007 and 24008 already passed the existing
initial-map boundary, complete-map reconstruction and opening-roster certificates
before this change. They failed `certified_public_event_prefix_required` because
their first event pages were outside the original three signatures. Their added
targets are respectively SunkenStatue (owner 6, page 146), WoodCarvings (owner 5,
page 121), and RoomFullOfCheese (owner 6, page 134). Each is one whole-permutation
target. For 24002, event ordinal 289 is a later combat owner, not another event pull.

The initial-map certificate's prior guard is `UsesRewardsProvenance`, which already
includes MapVersion. The event proposal uses it only to certify the native fresh
opening and Neow cursor advance; it never calls its map probability matcher or
prepares an old-law map trace. This fix therefore leaves the boundary untouched.
A Missing full-map capture still permits the event certificate if the published
first map slice and event history suffice; map reconstruction stays disabled and
native map generation remains the fallback. Unknown future option pages still
stop extraction. No full graph or public-history field is removed from replay.

The other direct initial-map consumers were audited: `NativePublicMapHistoryCondition`
feeds the old joint initial-prefix proposal, whose owning source explicitly excludes
MapVersion; `NativePublicMapReconstructionCondition` uses its detached slice-only
projection solely as a boundary certificate. Opening-roster and weak-encounter
successors already accept MapVersion. There is no shared routing refactor here.

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

The additional page signatures only change the fixed public predicate E. Their
pool, native shuffle law, cap K, existential eligibility masks, owned trace and
correction mechanism are unchanged. WoodCarvings eligibility remains unknown;
SunkenStatue and RoomFullOfCheese retain their existing act-zero eligibility masks.
Thus the same root-constant normalizer proof applies under the independent Map
law, alongside its separate map marginalization. It does not justify substituting
a source recipe for an independent posterior draw or ignoring a final mismatch.

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

`NativeMapEventPermutationTests` adds detached retained-root target admission,
whole ordered signature variants and counterexamples, unchanged Missing-map and
unsupported-page fallback, and exact complete-packet/settlement-fork replay with
the native event trace composed with complete-map reconstruction. Separately
prepared plans use one fixed unrelated recipe and are checked against the full
native permutation condition. Source-loop tests use independent recipe draws to
verify fixed-K clean event exhaustion consumes an outer attempt, keeps complete
word/cell counts and no old-prefix statistics, and never relabels runtime native
failure, cancellation or a runtime event-named budget as clean preparation failure.
These tests establish routing and accounting, not an updated acceptance benchmark.
