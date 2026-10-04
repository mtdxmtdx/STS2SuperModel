# Narrow native carry-in belief prototype

This document preserves the historical v1 profile and 16/200 result. The current
public-entry v2 implementation and 145/200 bounded result are documented in
[Native carry-in extension v2](NATIVE_BELIEF_EXTENSION_V2.md).

This is development-only evidence, not broad native posterior coverage, finite-seed inference,
formal labels, or permission to train. The raw natural-source collector shares the corrected explicit combat boundary.
The prototype imports an actual paused RunDriver root; it never restarts a Scenario using
mid-run HP, inventory or deck.

## Exact admitted family

- Actual native sequential single-player Silent A10 run, complete observation history from
  combat entry, stable normal player Play phase; no suspended selection or extra turn
- Normal monster room `ToadpolesWeak` or `SeapunkWeak`, with native encounter identity preserved
- Entry and current relics exactly RingOfTheSnake + BoomingConch; no wax/melt/used/stack variants
- Entry deck and current piles within the existing 24-card exchangeable family; no enchantments,
  afflictions, unknown generated identities, pets, or orbs
- Entry and current potion inventory within Fire/Block/Energy/Swift/FruitJuice; checking entry
  inventory prevents an unsupported consumed potion from becoming spuriously eligible
- Ordinary Silent permanent capacities: max energy 3, two potion slots, no base orb slots
- Reviewed power closure: Weak, Poison, Strength, Thorns, Dexterity, Blur, BlockNextTurn,
  NoxiousFumes. All numerical amounts and duration bookkeeping are public observations

The exact lists and fail-closed checks live in `NativeBeliefCertificate.cs`. Current published
intents and all other observed facts are copied, never redrawn. Unknown draw cards are canonicalized
by full public signature before the existing conditional-permutation sampler; known positions
retain their physical bindings. Future run/player/monster streams are replaced independently of
source streams by the existing `nosl-hidden-order` / `NOSL-FUTURE` sampler profile.

## Why these monsters are measurable from public history

Reviewed source is the pinned local simulator, not registry metadata:

- `Models/Monsters/Seapunk.cs`: no instance model-memory fields. Starts at SEA_KICK, then
  SPINNING_KICK, BUBBLE_BURP, repeat. Turn number determines the phase. Attack repeats and
  buff/defend intent types distinguish all phases even under damage modifiers
- `Models/Monsters/Toadpole.cs`: the only model field is IsFront. The native factory in
  `Content/Acts/UnderdocksEncounters.BatchB.cs` places front first, rear second. At initial
  publication front has Buff and rear has one Attack. The front cycle is SPIKEN, SPIKE_SPIT,
  WHIRL; rear starts at WHIRL, then follows the same cycle. History plus lifetime public slot
  and turn number determines role and phase. Enemy deaths remove live entries without
  renumbering lifetime slots. No moves sample hidden behavior or create new enemies/cards
- Both graphs are deterministic. Inherited move history is entirely determined by this prefix;
  runtime assertions compare the cloned model phase/role to its public-derived value
- `Models/Relics/RingOfTheSnake.cs`: first-turn draw is already observed at import, no private
  counter. `Models/Relics/BoomingConch.cs`: only first-turn elite draw/energy modifiers, no
  instance model memory. The admitted normal monster rooms cannot trigger it
- Allowed cards/potions add no hidden persistent model identity, no turn-skipping mechanics,
  no unsupported generation. Their supported statuses, counters, pile movements and decisions
  are in the public history/DTO. Carry-in permanent assets are retained from the native boundary
- The detached room uses the existing native outcome/settlement lifecycle with independent tasks,
  reward containers and hooks. These relics/cards add no concrete-RunState-dependent settlement
  effects; pre-entry assets and native room identity/type remain intact

This is a conditional-permutation + independent-future-randomness model, the same explicit ideal
profile as the existing fast sampler. It does not claim the native PRNG's finite source-seed
posterior: past observations can correlate with that finite seed, and no such inference is done.
This is an outcome-relevant belief model rather than a posterior over every field of a full
native run graph. In particular, unobserved reward pity/rarity counters can survive in the detached
snapshot. Reviewed allowed content does not read them in combat or use generated offer identities
before this endpoint. They only influence unclaimed reward offers, which are excluded from policy
input and utility. A test perturbs these counters as well as hidden order/RNG and checks identical
public actions and scored terminal facts. Widening content to a reward-reading or reward-mutating
hook would invalidate this argument and requires a new certificate.

The source seed is not supplied to proposals or public policy. The retained immutable player seed
metadata is not consumed by sequential future streams; all actual generator states are replaced.

## Provenance and exclusions

Native sessions have a certificate and no Scenario. Generic replay, including choice replay,
throws `native_prior_mismatch` before any fresh setup. Continuation forks use detached native
projections. An out-of-family descendant cannot silently fall back to fresh-Scenario conditioning.
Choice coroutines already executing inside a sampled rollout continue normally, but cannot be
imported or re-created by pretending the original run started at this state.

SludgeSpinner and HauntedShip remain blocked. The former uses stochastic no-repeat behavior;
the latter introduces Dazed outside this reviewed closure. Other relics, potions, encounters,
start conditions and unsupported choices also stay raw and explicitly unlabelled. Strong paired
rankings remain masked: this prototype does not extend the utility-support certificate.

## Interface and evidence procedure

`native_belief_prototype` accepts NaturalSourceOptions under `options` and TeacherOptions under
`teacherOptions`. Hard limits: 200 roots, 100 runs, 20 floors; T0 only, at most 16 independent
worlds and 300 decisions, FormalLabels forbidden. Returns the whole unchanged source sequence,
source distribution, blocked-reason counts, separate labeled/unlabeled totals, and masked teacher
records. Development records are explicitly `trainable=false` and `formal_labels=false`.

Use the exact existing proof prefix `nosl-m5-natural-proof-20261001`, runs=100, maxFloors=12,
maxRoots=200, maxRootsPerCombat=8. This is the original bounded source sequence, not a favorable
seed search. Report the eligible subset against the complete 200-root denominator; do not
represent it as broad natural coverage. Source-policy decisions never receive sampled worlds.

Tests cover actual native imports, exact source-run invariance, carry-in preservation, hidden
order/future stream replacement, same-public-history strategy equality through terminal settlement,
multiple sampled draw orders, stable-boundary/history/entry-potion/role rejection, and blocked
fresh-Scenario replay. The native root never becomes a synthetic scenario.

### Observed bounded proof (2026-10-01)

The exact 200-root proof sequence produced 16 eligible roots: eight ToadpolesWeak and eight
SeapunkWeak, all from the same existing native run. These yielded 87 legal action targets and
174/174 settled branches with two independent evaluation worlds per action. All 87 empirical
value/outcome masks were valid; there were zero strong pair labels. The remaining 184 roots
were retained with explicit rejection reasons. After the explicit combat-boundary fix, all 200 full source trajectories remain
unchanged. Of the public inputs, 170 remain identical and 30 differ only in placing
the combat-start marker before automatic room-setup power effects. Acquisition-time
choices are outside the combat anchor. Old artifacts remain preserved; their raw
public-input hashes must not be reused for the corrected corpus. This is narrow feasibility,
not representative coverage: only one of the six original source runs furnished eligible roots.

`NATIVE_BELIEF_PROOF_SUMMARY.json` records the full denominator, rejection distribution,
source options, evaluation seeds, and artifact hashes. Large records remain ignored under
`artifacts/native-belief-proof-200-v2/`; no training, formal labels, or remote publication occurred.

Reproduction in an isolated checkout: source the provided SDK environment, build/test with
`--artifacts-path ./artifacts/build -m:1 -nr:false -p:UseSharedCompilation=false
-p:NuGetAudit=false`, then send the saved JSONL request to
`artifacts/build/bin/Nosl.Worker/release/Nosl.Worker.dll`. The pre-existing standalone-policy
test assumes `src/Nosl.PublicPolicy/bin/Release/net9.0`; for isolated artifact builds, a local
ignored symlink to `artifacts/build/bin/Nosl.PublicPolicy/release` satisfies that assumption.
Do not point it at or rebuild another checkout's binaries.

Final guarded verification: full NOSL suite 854/854 passed, four targeted native-belief tests
passed, and the separate-process protocol smoke passed. The subsequent corrected-boundary 200-root rerun preserved all source trajectories
and settled all 174 allocated branches; public-history differences are recorded above. The first isolated
full run exposed only the existing hardcoded policy-DLL path; after the local ignored symlink,
the full suite was rerun successfully. The prototype was then integrated into the coverage checkout, with separate final
boundary and provenance regression evidence in M3_M6_STATUS.md.
