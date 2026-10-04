# Native untouched generation-potion entries

The fixed 200-root cohort now admits **174 roots**, including the unchanged 150
v5 roots and **24 stable AttackPotion/PowerPotion carry-ins**. All **2,222/2,222**
action-world continuations reached automatic settlement. The increment contains
189 candidates and 378 worlds: eight roots each in SludgeSpinnerWeak,
HauntedShipNormal, and ShrinkerBeetleAndFuzzyWurmCrawler. The remaining 26 roots
are still blocked by unreviewed encounters. All 200 public inputs and source
traces, and all 150 previously supported target payloads, exactly match the frozen
v5 evidence. See [the machine-readable summary](../NATIVE_GENERATION_POTIONS_SUMMARY.json).

No fitting, formal training, corpus promotion, new source seed search, or vendor
changes occurred. The previous v5 evidence, datasets, checkpoints, and default
binaries remain intact. Every new record is development-only with
`trainable=false` and `formal_labels=false`.

## Admission and prior

The added profile is `native-public-entry-generation-potions-exchangeable-v1`.
It uses the same declared conditional-permutation / independent-future-RNG prior
at a stable native boundary, rather than a posterior over the native finite run
seed. Dispatcher `nosl-belief-dispatch-v6` reports that profile only for the new
entry family. Unchanged stable and conditional-choice roots retain their prior
profile identities. Development record/report schemas advance to
`nosl.native-belief-prototype.v4` and `nosl.native-belief-prototype-report.v4`.
The public student schema stays v2 and contains no sampler or provenance metadata.

The root card and power allowlists are unchanged. Only untouched AttackPotion and
PowerPotion entry/current identities are admitted; current generation potions
require the corresponding entry family. Any public `potion_used` event for either
potion rejects a new certificate, including a completed cancellation which added
no card. An old certificate cannot acquire generation-potion capability merely
by changing the current inventory. Actual player character must be Silent and
the actual run ascension must equal 10. The fixed A10 value in the public DTO is
not evidence for this check.

This increment is stable-only for the new family. A pending generation-potion
choice cannot be imported or resampled. Card-action pending choices whose origin
carries a new generation-potion prior are also outside this increment; they do not
inherit the older conditional profile. The five previously certified native card
choices remain supported.

After sampling the stable root, T0 and T1 fork their root candidates and continue
each owned coroutine through future actions and choices to settlement. A generated
state need not itself pass a fresh native-root certificate. The implementation does
not reconstruct a generated state, clone a suspended generation coroutine, restart
a Scenario, or reseed after observing generated candidates.

## Reviewed generated closure

`NativeGenerationPotionMemory` pins the ordered 23 attack and 16 power IDs from
`vendor/sts2-sim/tests/Sts2Sim.Core.Tests/Models/Potions/CardChoicePotionFidelityTests.cs`.
At admission it compares those IDs with the upstream `CardPoolProjection`, so a
changed eligible pool fails closed. Filtering, pool order, complete-pool shuffle,
candidate creation, optional selection, free-this-turn effect, and card entry all
remain in the upstream `CardChoicePotionEffect` / `CardFactory` / `CardPileCmd`
execution. NOSL copies no generation rules.

The eligible pools exclude TheHunt, Ancient/basic/event cards and multiplayer-only
Sneaky. The only generated-card descendant is Shiv. The closure uses public hand,
draw/discard counts, powers, card attributes, and published historical counters.
Examples include Finisher's completed attacks, MementoMori's discards, Murder's
combat draws, and PhantomBlades' played Shivs. Future random targeting and generation
use replaced native streams. These eligible effects do not read concrete RunState,
scene state, reward offers/pity, or permanent assets and do not heal HP. Existing
entry relic settlement remains covered by the native v2 review.

ToolsOfTheTrade adds a delayed turn-start discard; MasterPlanner makes played
skills Sly. SerpentForm has transient per-play bookkeeping which is created and
consumed inside the ongoing operation. Those future effects execute in their
owned coroutine. They are not added to the stable-root power allowlist or asserted
to have a new exchangeable posterior at every later decision.

## Evidence and its limits

`NativeGenerationPotionTests` separates three kinds of evidence:

- The actual fixed native cohort verifies all 24 added roots, all 189 candidates,
  and both evaluation worlds through automatic settlement. For each new encounter
  family, it replaces private draw order, permanent-deck order, run/player/monster
  future streams, and reward pity; the same public root and sampler seed produce
  identical public packets and terminal assets. It then takes a real potion action,
  deliberately makes a nonempty generated-card selection, and completes both worlds.
  The source graph, original orders, streams, and complete source trajectories stay
  unchanged
- Actual-run versus detached-projection mechanism fixtures take both potion types
  through public choices, nonempty selection or cancellation, and automatic
  settlement. All 39 pinned generated cards also execute and settle in matching
  actual/projection fixtures. These constructed mechanism fixtures are not counted
  as natural roots or new posterior certificates
- A dedicated ToolsOfTheTrade/MasterPlanner fixture reaches the delayed discard,
  chooses a Sly Survivor, resolves its nested discard in the same owned operation,
  and settles with source/projection parity. Negative cases cover actual character,
  actual A9/A11 despite the public A10 DTO, already-used potions including cancellation,
  pending generation import, and card-choice origins with the new entry prior

The frozen `nosl-public-rules-v1` teacher continuation picks the first legal choice.
At these optional generation choices that action selects zero cards, so the 378
new T0 evaluation worlds do **not** establish teacher benefit from playing a
generated card. Nonempty generation behavior is covered by the separate mechanism
and native-family tests. The policy was not changed to improve measured results.

All 174 rankings remain `MASKED_NO_CERTIFIED_UTILITY_SUPPORT`, with no strong pair
labels. Of 1,111 action targets, 494 have empirical objective values and 617 retain
unresolved objective-value masks. More mechanics support does not resolve resource
preferences or release independent data-quality/training gates.

## Reproduction

Use .NET 9 and the isolated build location:

```
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path artifacts/native-potion-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false
```

The bounded request is copied unchanged from v5: prefix
`nosl-m5-natural-proof-20261001`, up to 100 runs, 12 floors, 200 roots, eight roots
per combat, T0, seeds 101/102, and a 200-decision cap. Request, response, individual
records, report, and verification logs are in ignored
`artifacts/native-potion-proof-200-v6`. The isolated worker is
`artifacts/native-potion-build/bin/Nosl.Worker/release/Nosl.Worker.dll`.
The summary records hashes and exact final test results.
