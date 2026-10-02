# Optional public run-evidence contract

`nosl.public-run-evidence.v1` is an independent, opt-in **recorded public observation channel**. It is not a claim to include every fact a human could see. The native producer is enabled only by `PublicEvidenceProfile = "nosl.public-run-evidence.v1"` together with `PublicContextProfile = "nosl.public-run-context.v1"`. The omitted option preserves legacy packets, source records and prior identities. This checkpoint adds native capture and exact replay integration; it does not run production collection or fitting, admit v4 to production, or rewrite old corpus/checkpoint bytes.

## Envelope and ordering

Use `PublicRunEvidenceJson.Serialize/Read`, not a generic permissive decoder. The versioned envelope has `schemaVersion`, `completeFromRunStart` and immutable `events`. Each event has a contiguous zero-based `eventOrdinal`, an optional observer-owned `ownerOrdinal`, and a closed typed `payload` with a `kind` discriminator. Owners are contiguous zero-based ordinals created by `BeginOwner`, never native IDs. Optional parent owner ordinals describe an event that temporarily enters combat and later resumes; children must close before their parent. Nothing can append to a closed owner.

A recorder starts with an explicitly observed `PublicRunStarted(character, ascension, assets)` or an explicit `run_start_not_observed` gap. A null start, missed owner start, unknown/malformed observation, interruption or ambiguous visibility prevents a complete run-prefix claim. A midrun observer must not invent startup. `completeFromRunStart` means uninterrupted recording of this declared channel, including an explicit startup observation; it is not a producer-publicity certificate. Still-open owners are valid prefixes.

## Producer-facing API

```csharp
var recorder = new PublicRunEvidenceRecorder(observedRunStart: null);
long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, actIndex: 0,
    floor: 4, completeFromOwnerStart: true);
long decision = recorder.ObserveCombatDecision(combat, detachedPublicPacket);
recorder.Record(combat, new PublicCombatActionTaken(decision, selectedPublicAction));
// Before closing combat, project the final cumulative public event history too.
recorder.ObserveCombatHistory(combat, detachedFinalPublicHistory);
recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory,
    detachedVisibleAssets));
PublicRunEvidence prefix = recorder.Capture();
```

`Record(owner, payload)` accepts only the sealed typed vocabulary and returns its event ordinal. It rejects invalid references atomically. `RecordGap(ownerOrNull, reason)` records a known omission; a global gap invalidates all currently open combat histories. `Capture()` remains unchanged after further recording or mutation of any original input. New DTO properties are read-only; immutable collections copy inputs. Legacy public card, relic, choice, observation and action DTOs are deep-copied both on entry and when accessed, including nested arrays/dictionaries.

## Combat history and stable decisions

Every typed combat fact has an event ordinal and a combat owner. Keeping all events for a closed combat preserves its public history in chronological order, including stable decisions, chosen actions and completion. `PublicCombatDecision` contains a validated v2/v3 public observation with an empty legacy `history` array, offered actions, `historyThroughEventOrdinal` and `historyCompleteFromCombatStart`. Its history link must point to the immediately preceding event for that owner; completeness must agree with the owner's recorded start and all relevant gaps. Choices/actions reference earlier offers from the same owner and cannot consume stale/already-used offers. Combat actions must exactly match an offered token.

Do not clear `PublicObservation.History` in a producer and call it complete. `ObserveCombatDecision` first sends every new history entry through the strict projection, then creates the linked snapshot. `ObserveCombatHistory` requires the same cumulative history prefix on later calls; truncation or rewriting records an interruption gap and leaves previous observations intact. A manually recorded action receives one reconciliation allowance for its exact token at the immediately next cumulative-history position. That single echo does not duplicate it; duplicate or reordered occurrences record gaps. An action without a previously recorded stable decision is a gap.

The allowlisted projection covers the existing public combat start marker, native entry assets, turn starts/ends, intents, draws, card starts/plays/generation, hidden-card generation count marker, potion uses, damage, power changes, shuffle markers, card choices with bundles, automatic selections with unidentified counts, and pre-settlement hand/exhaust facts. Action entries have a separate typed action payload. Public target labels and slots are retained. Hidden card generation has no identity. Existing `canonical_unordered_reveal` candidate order and unidentified draw counts remain explicit.

Unknown or malformed `PublicEvent` kinds or payloads become `unsupported_observation` gaps, with no raw detail copied into the envelope. Forced-event context/merchant payloads are currently outside this projection and therefore become gaps. These gaps must be implemented or explicitly retained before a producer can claim a complete v1 prefix for those cases. The recorder has no `SourceTrace`, native object, `FloorDetail`, raw log or raw snapshot overload.

## Outside combat observations

- Rewards and shops: `PublicOffersObserved` records **all displayed** groups/items before choosing, including unselected cards, relics, potions and gold. Groups distinguish primary, extra, alternative and rerolled offers; alternative groups reference their displayed group index and a reroll references the previous offer event. Selection mode is independent items or choose-one. Visible skip/continue/reroll/service choices and prices have typed fields. The producer must display-record a refreshed offer after each independent take; a choice consumes that observation.
- Outside card choices: `PublicCardsObserved` preserves the source key, min/max, cancelability, candidates, declared candidate order and candidate-aligned bundles. `PublicCardsChosen` distinguishes cancellation from an empty permitted selection and preserves selected-index order.
- Events/rests: `PublicOptionsObserved` records only visible keys, including locked state and shown prices. A locked/missing key cannot be selected. Do not synthesize a rest option because an engine API could execute it.
- Map decisions: `PublicMapObserved` is the **current observed choice slice**: current node, visible offered nodes, observed ordinary edges and each option's ordinary-connection flag. Coordinates are public map coordinates, not native identities. Edge endpoints must exist in the slice; flags must agree with the observed current-node edges. Additional offered moves can have `isOrdinaryConnection=false`. Keep Unknown icons unknown. Do not populate this DTO by exporting a native graph or future encounter list.

Explicit missing/ambiguous observations must be gaps; none of these shapes authorizes inferring visibility from private state. Strings represent public model/option keys, never private source/run/combat IDs, seeds, RNG state/counters, hidden rarity rolls/pity, unrevealed card identities or private outcome-ledger facts. Structural validation cannot prove the provenance of a string. The native adapter uses the field projection below; its read-only review is limited to the declared native callback channel, not a live-client display or all-content certification.

## Native capture and field-by-field visibility

`NativePublicRunEvidence` is the only native projection; `PublicRunEvidenceRecorder` still accepts typed public DTOs only. `SourceBridge` owns one producer across the complete native run. Both ordinary collection and `NativeRunWorld` (sequential and hypothetical label-tape recipes) use that same bridge. The producer does not read `NaturalSourceTrace`, configured future acts, encounter names, native identities, source seeds, private random state, native `FloorDetail` snapshots or the private outcome ledger.

| Public fields | Actual source and declared visibility |
| --- | --- |
| `character`, `ascension` | Fixed requested solo Silent A10 startup; a run-start observation requires the real startup call at act/floor zero and the explicit native-beginning flag |
| Asset `hp`, `maxHp`, `gold` | Visible player totals at startup, room/reward end, or automatic combat settlement |
| Asset `deck` | Persistent public deck via existing `PublicViews.Card`, canonically sorted; no hidden combat draw order |
| Asset `relics` | Existing public relic model/details/cards/selected-model projection, detached from native objects |
| Asset `potions`, `potionSlots` | Visible potion slots, preserving empty slots |
| Asset `maxEnergy`, `orbSlots`, `cardRemovalsUsed` | Existing public persistent asset projection; no random stream counters |
| Owner ordinal/parent | Observer-local sequence and actual callback nesting; native objects serve only as in-memory scope keys |
| Owner act/floor | Current run progress; no future act catalog |
| Event option key/lock | Exactly `EventOption.Key` and `IsLocked` supplied at the choice callback; no callback target/private event payload inspection |
| Duplicate event keys | Explicit `ambiguous_visibility` gap plus unique `option:{displayIndex}:{key}` tokens, preserving visible order and the exact selected index; omitted event effect labels are not guessed |
| Rest keys/locks | Actual supplied `OptionId` groups and `IsEnabled`; native flattened Smith targets become a canonical card choice; no absent rest option is synthesized |
| Outside card candidates | Actual `CardSelectionRequest` candidates, min/max, cancelability, source model key and aligned bundles; persistent/deck lists are canonically sorted with bundles kept aligned |
| Outside card result | Exact ordered returned indices and explicit cancellation flag; selection owner ends without assets because native effects execute after the callback returns |
| Reward card groups | Every unresolved native `CardReward.Options` in the displayed `RewardsSet`, including unselected cards, extra groups, skip and public alternatives; no regeneration or future roll |
| Reward gold/potion/relic | Already generated visible amount/model, availability and group; native zero-gold placeholders omitted |
| Alternatives/rerolls | Actual alternative key/availability; a reroll replacement references the preceding displayed snapshot and only its affected card group is tagged rerolled |
| Optional potion choice | Take and skip share one choose-one group; full-slot lock is retained |
| Reward selection | Exact selected offer token; resolved snapshots are refreshed at each callback, preserving all earlier offers |
| Shop | Actual inventory model/price/purchase state, visible affordability/potion capacity and card-removal service; current source script chooses leave |
| Map nodes/edges | Current node plus offered destinations and ordinary edges among that choice slice; Unknown remains Unknown, extra travel is marked nonordinary |
| Combat facts/actions | Closed projection of already-public cumulative `PublicKnowledge.Events`, exact offered/chosen actions and full past-combat history; no native observer snapshot or private ledger |
| Combat decision | Detached existing v3 observation with only its duplicate legacy history cleared inside the typed event; root observation retains its entire existing legacy history |

The reward visibility statement describes the native `ChooseRewardActionAsync(RewardsSet)` seam: all unresolved supplied groups are exposed by that declared channel. It does not assert live-client screen-opening timing. The selected-combat terminal boundary still occurs before its first postcombat reward decision; future rewards are not inserted into evidence merely because native generation has happened.

A forced fight is a child of its still-open event. On combat settlement the combat owner ends, and reward callbacks that still execute inside the native combat room retain the event parent. Reward owners close on their actual Done callback, including nested rewards. Room owners close on `ExitFloor`. Native startup missing, injected midrun event entry, unknown custom-event screens and UI-bypassed automatic outside selections retain explicit gaps. A bypassed selection does not authorize publishing its whole candidate list. Existing current combat history can remain complete despite an earlier outside gap.

For the new profile only, card selections after `Engine.IsInProgress` becomes false use the outside-choice path. The old profile keeps its historical behavior and bytes; it may misclassify a postcombat card-removal callback as an active combat choice. The profile in the prior identity makes this behavior change explicit. The producer preserves actual native cancelability: `FromDeckForRemoval` currently passes the default false despite a conflicting `CardRemovalReward` comment. This patch changes no simulator rules to resolve that discrepancy.

## Public boundary, replay, and compatibility

`DecisionPacket.PublicEvidence` is an optional omitted-null property. Its property-scoped converter always uses the strict evidence codec (camelCase keys, snake_case enum strings, closed `kind` discriminators, recursively rejected duplicate properties and unknown/case-aliased fields), without changing legacy `PublicJson` encoding.

Enabled raw and teacher exports use `nosl.student.public.v4` with all existing v3 public fields and required `public_evidence`. The root observation remains `nosl.public.v3`. `PublicEvidenceInput` checks that the final evidence event is the current open combat decision, with equal status, history-free observation, actions and act/floor. `history_complete` is that typed decision's actual history-completeness flag. The raw envelope is `nosl.natural-source.v4`; old raw/student schemas remain unchanged when the option is absent. The native replay development envelopes retain their existing top-level schema/kind while their v4 input and audit identity explicitly identify the new channel. These records stay `trainable=false`.

Both native priors serialize the option, producing new prior identities. Native-run and native-tape sampler/profile identifiers gain `-public-evidence-v1` only when enabled; the absent-option tape implementation remains v7. Whole-packet posterior equality and continuation replay equality include the complete evidence prefix. Hypothetical runs reconstruct it from their own selected recipe; no actual source graph, raw trace or hidden source state is supplied. Existing conditional proposals may use a subset of the root observations, but final acceptance still checks the entire packet. Unrecorded facts remain marginalized under the declared prior.

Current validation covers native early event/options/outside card choice, public map slice, first and later combat roots, completed-combat history/actions, ordinary rewards, independent replay/forks, forced-event scopes, actual post-settlement card-removal callbacks, automatic-selection gaps, actual duplicate event keys, all native reward groups and per-card rerolls. Contract regressions cover recursive duplicate properties, null malformed history, single-use action echo/order, missing combat start, strict round trips and deep immutability. Frozen legacy packet/prior bytes and disabled-profile source roots are compared directly. A finite two-seed native test verifies acceptance by the complete enabled packet after source disposal. No broad data collection or training is part of these tests. The native producer's focused C# run passed 59/59 tests in 9 seconds; this is not a claim of a fresh full-suite run. The real 129-event native v4 sample also passed the separately implemented Python strict boundary and current-history binding checks.

The recorder retains an immutable validated prefix. `PublicRunEvidence.Append` applies the same lifecycle transition as full external construction to one new event, using immutable owner state; rejection cannot consume an ordinal/offer or mutate a saved branch. `Capture()` returns the current immutable prefix without revalidating past actions. The public constructor and strict decoder still validate every supplied event. No unchecked construction API is exposed, and legacy DTOs retain their deep-copy boundaries. Appending still copies the event-reference array; history-prefix checks, snapshot construction and serialization retain their existing costs. No concurrency guarantees are made.

The exact two-combat fixture (`NativeEarlyOptionsRewardsAndCompletedCombatSurviveLaterRootAndIndependentReplay`, `owned-native-opening:0`, two first-decision roots) was measured before (`e1220f2`) and after this optimization on the same host. Three fresh-process default-runtime runs had median enabled source collection of **0.817 → 0.765 seconds** (6% lower), with disabled-profile medians 0.0616 → 0.0598 seconds. To separate startup/JIT cost, a local harness invoked that same fixture four times per build with `DOTNET_TieredCompilation=0`, discarded the first warm-up, and measured the next three: enabled medians **0.257 → 0.191 seconds** (26% lower), disabled medians 0.0317 → 0.0288 seconds. Timings cover source collection only, excluding replay/forks. The original author's single 0.454/0.0564-second run is not a matched speedup baseline. This small correctness fixture is not a throughput benchmark or quality gate; material enabled-profile overhead remains.

All measured runs preserve 26/129 first/later prefix events and 47,087/207,775 UTF-8 evidence bytes. The complete later source-record sample is byte-identical before/after (266,969 bytes; SHA-256 `275e861328566cd030e11a1621cac317f374b82dd525e05ce133c3b8f6114fcb`). The post-optimization focused run passed 62/62 tests in 10 seconds. New regressions compare every real native prefix through incremental construction, full construction and strict JSON round-trip, and verify rejected append atomicity, saved captures, independent immutable branches and global-gap/consumed-offer isolation. Run the existing named fixture with `NOSL_EVIDENCE_SAMPLE_PATH=/absolute/output.json` to save its source record and `.metrics.json`; use the isolated build arguments `-c Release --artifacts-path artifacts/evidence-performance-build -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false`.
