# Native carry-in public-entry posterior v2

This page preserves the frozen stable-boundary v2 evidence. The later
[owned-origin native choice extension](NATIVE_CHOICE_REPLAY.md) admits five more
roots from the same cohort without changing this stable certificate or its artifacts.

The original fixed 200-root native cohort now admits **145 roots**, up from 16 in
the v1 prototype. All **1,806 / 1,806** development T0 action-world continuations
reached automatic settlement. The 55 unsupported roots remain present and
unlabelled. No formal labels, training, weight changes, or corpus promotion occurred.
See [the machine-readable result](../NATIVE_BELIEF_EXTENSION_V2_SUMMARY.json).

This remains an explicitly declared **conditional-permutation / independent-future-RNG
prior**, not inference over the actual finite run seed. Actual native run boundaries
are imported and cloned. Neither fresh Scenario resets nor whole-run seed rejection
are used to impersonate naturally reached carry-in states.

## Public entry and version boundary

`NaturalSourceCollector` publishes `native_entry_assets` immediately after the
combat-start marker and before setup. Its JSON detail is:

```
schemaVersion: nosl.native-entry-assets.v1
hp, maxHp, gold
deck: PublicCard[] sorted by full public signature
relics: PublicRelic[] including reviewed public counters, in acquisition order
potions: (string | null)[] in slot order
maxEnergy, potionSlots, orbSlots, cardRemovalsUsed
```

Permanent deck order, card object identities, source seed, future draws and raw RNG
state are absent. The certificate binds this public event to the entry snapshot;
audit-only entry assets can no longer supply otherwise missing policy evidence.
This matters for BoneTea's temporary combat upgrades and FishingRod's permanent
postcombat upgrade.

New raw exports use `nosl.natural-source.v2`, `nosl.student.public.v2`, source prior
`native_sequential_run_silent_a10_public_entry_v2`, and script
`nosl-natural-public-script-v2`. Development labels/reports use
`nosl.native-belief-prototype.v2` / `nosl.native-belief-prototype-report.v2` and
posterior `native-public-entry-reviewed-memory-exchangeable-v2`; the current audit-only
dispatcher identity is `nosl-belief-dispatch-v4`. Existing frozen
v1 records and inference hashes are unchanged. The v1 student must reject the new
event; an explicit v2 validator and encoder must consume it before these records
can be considered student inputs. All emitted development records remain
`trainable=false` and `formal_labels=false` regardless of schema support.

## Reviewed state and continuation

The implementation checks actual native sequential single-player Silent A10,
complete room-entry history, normal player Play boundaries, public entry assets,
ordinary capacities, known draw multiset, reviewed cards/powers/relics/potions,
and the exact native encounter/room identity. Suspended native choices remain
unsupported for import and posterior sampling. Choices that arise inside a sampled
T0 continuation execute normally in their owned coroutine.

`NativeMonsterMemory` reconstructs the entire move log from each turn's published
intent shape and the pinned upstream transition graph. It does not copy a private
choice merely because its current intent matches. A shape must select exactly one
reachable move. Identical-looking deterministic phases, such as FuzzyWurmCrawler's
two attacks, Cubex's consecutive repeater moves, and SkulkingColony's Zoom/Zoom2,
are distinguished by the preceding public path. SludgeSpinner and the random
slimes have distinct public move shapes; their full verified state logs determine
the native repeat restrictions. Transition probabilities still come exclusively
from the upstream state machine and independent future streams.

Reviewed encounters are ToadpolesWeak, SeapunkWeak, SludgeSpinnerWeak,
ShrinkerBeetle, FuzzyWurmCrawler, HauntedShipNormal, SlimesWeak,
ShrinkerBeetleAndFuzzyWurmCrawler, LoneNibbit, CubexConstruct, VineShambler,
and SkulkingColonyElite. Only the last uses an Elite room. Eleven are represented
in the accepted fixed cohort; its Shrinker+Crawler roots carry AttackPotion and
remain excluded. Factory roles, public lifetime slots, enemy deaths and native
room type are preserved. No supported monster summons, revives, or uses a
transient stun state.

The following source review closes the additional non-move state obligations:

- `Models/Powers/ShrinkPower.cs` removes permanent Shrink when its particular
  applier dies. `NativePowerMemory` checks the cloned applier against the original
  public application source slot. Clone tests verify the local creature binding
  and reject a changed binding
- `Models/Powers/TangledPower.cs` applies/clears `Entangled` on attack cards and
  uses only public amount, owner, card type and affliction. Its applier is not read
  by these effects. Card costs, keywords and afflictions remain in full public
  signatures; generated Shivs use the same native entry hook
- `Models/Powers/HardenedShellPower.cs` has a private damage-so-far counter.
  Its outcome-relevant remaining cap is checked against actual public HP damage
  since the current player-turn marker. It resets before every side. In this
  precise allowed closure there is no player-start enemy HP damage before that
  marker: NoxiousFumes only applies Poison, ordinary/Clarity draws are hand draws,
  and Speedster triggers only on non-hand draws. Saturated counters have the same
  future effect until reset. A hidden-counter mutation with unchanged public DTOs
  is rejected, and a real FirePotion/three-Defend path verifies the next reset
- `Models/Cards/Speedster.cs` adds Innate on upgrade. The certificate requires at
  most seven entry Innates, all consumed by the ordinary Ring opening hand.
  Otherwise the residual draw pile would retain an unrepresented Innate stratum.
  The Elite BoomingConch opening can draw more; the seven-card guard remains
  conservative. BoneTea upgrading already drawn cards adds no hidden pile order
- New primitive cards SuckerPunch, Snakebite, Expose, LeadingStrike, Skewer and
  Dazed have static or upgrade-determined values. Untouchable, Reflex and Tactician
  use native Sly/discard execution. Their amounts are upgrade-determined, and
  ordered discard choices remain ordered. Speedster's power has no private
  counter. New Flex/Vulnerable/PowderedDemise/Clarity potions use public scalar
  power/draw effects; their complete power closure is checked
- `Models/Relics/FishingRod.cs` increments a public counter on Monster victories
  and upgrades a uniformly selected upgradable permanent card every third one.
  The hook uses the cloned deck and `IRunState.Rng.Niche`, with no concrete-run
  guard. Sampled permanent decks are canonically reordered by public signature
  before reseeding. Every existing card and graph binding is retained, including
  native DeckVersion/CloneOf links. None of this reviewed card/relic family uses
  those links to mutate a linked permanent original. Canonical ordering preserves
  the uniform upgrade law while eliminating private-list-order dependence from
  the same-seed coupling
- Ring/BoomingConch have no private counters. BoneTea, FishingRod, WingedBoots and
  LavaRock counters are already explicit public relic facts. Pomander,
  LeadPaperweight and WarPaint affect acquisition; RegalPillow affects later rest.
  `LavaRock.ModifyRewards` returns before its concrete RunState check outside Boss
  rooms, so its old unconditional projection rejection is narrowed to Boss.
  Boss rejection is retained and tested

Future run/player/monster generator states are all replaced. Current published
intents, public-determined memory, start HP/gold/permanent assets and the existing
conditional draw-position constraints are preserved. Unobserved reward pity can
remain in the detached snapshot because this closure neither claims reward
offers nor reads their identities before the endpoint; tests perturb it too.
Any broader reward-reading hook would need a new sufficiency review.

## Measured fixed cohort and remaining work

| Accepted encounter | Roots |
|---|---:|
| ShrinkerBeetle | 24 |
| FuzzyWurmCrawler | 23 |
| ToadpolesWeak | 16 |
| SlimesWeak | 16 |
| SeapunkWeak | 15 |
| SludgeSpinnerWeak | 15 |
| CubexConstruct | 8 |
| LoneNibbit | 8 |
| VineShambler | 7 |
| HauntedShipNormal | 7 |
| SkulkingColonyElite | 6 |

All 200 source trajectories match the previous fixed capture. After removing only
the new public entry event and changing the exporter schema back for comparison,
all 200 public inputs match exactly. The intermediate 139-root result is preserved
separately; the final Elite increment adds six roots and 114 settled worlds.

The final 55 rejections comprise 24 generation-potion carry-ins, seven suspended
choices, and 24 encounter roots: CorpseSlugsWeak 15, TwoTailedRatsNormal 7,
RubyRaiders 2. CorpseSlug requires public conditioning of ravenous transient stun
continuations; TwoTailedRat requires summon/counter/roster reconstruction; the
mixed RubyRaider archetypes require their own state review. AttackPotion and
PowerPotion generate full eligible Silent-pool candidate sets. Their generated
card/power closure and native choice origins are not certified by this profile.
TheHunt is explicitly ineligible for combat generation, so its projection issue
is not asserted as a potion blocker.

There are 903 action targets: 453 have empirical objective values and 450 retain
unresolved objective-value masks. All outcome continuations settled, but all
145 roots retain `MASKED_NO_CERTIFIED_UTILITY_SUPPORT` and zero strong pair labels.
Thus broader mechanics support does not resolve user resource preferences or
release the independent data-quality/training gates.

## Verification and reproduction

`NativeCarryInExtensionTests` replays the complete naturally collected fixed cohort
and tests representative encounter/relic families through settlement with hidden
draw order, permanent deck order, all future streams and reward pity replaced.
Equal public histories and sampler seeds yield identical actions, intermediate
public packets and terminal assets. The source graph remains unchanged. It also
compares an exact imported third-FishingRod-victory against the source run at its
first reward question, including the exact permanently upgraded deck, counter and HP.
Corrupted move history, Shrink binding and HardenedShell cap fail closed. Independent
SludgeSpinner samples produce both legal non-repeating next moves.

The bounded JSONL request uses the unchanged prefix `nosl-m5-natural-proof-20261001`,
100 maximum runs, 12 floors, 200 roots, eight roots per combat, evaluation seeds
101/102, T0, and 200 rollout decisions. This is the existing cohort, not a favorable
seed search. Final records stay in ignored `artifacts/native-extension-proof-200-final-v4`.
The summary records hashes and options; the original 16-root, intermediate 139-root and
Elite 145-root development artifacts remain intact with the dispatcher version that
actually produced them. The last v4 verification reruns the same 200 roots solely to
freeze the final audit version; it does not enlarge or replace a training corpus.

Build/test in an isolated checkout with .NET 9 and
`--artifacts-path artifacts/native-extension-build -m:1 -nr:false
-p:UseSharedCompilation=false -p:NuGetAudit=false`. The targeted native suite has
12 passing cases. The final complete NOSL suite passed 947/947, and the separate
worker/public-policy protocol smoke passed. The source/generated records and
verification logs are hashed in the summary. All six source runs contribute
accepted roots, compared with one run in the historical v1 result.
