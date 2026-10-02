# Current upstream + minimal NOSL connection

## Authority and scope

Pinned rule source: [iRyougi/sts2-sim 5a9576b](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0), 2026-10-01. Previous fork pin was a3a6627. The update changes77 upstream files, with1540 insertions and317 deletions; MIT and declared game version0.111.0 /41cef1ea /222455745 are unchanged.

The user accepts the upstream rules. This project directly invokes its Core library; it does not reimplement cards, damage, enemy moves, generation or upgrades. The public export contains Core and Core.Tests, not a reusable public JSONL/RL CLI. The small worker layer provides the NOSL-specific public information boundary, revisioned action tokens, asynchronous choice exposure, public history, hypothetical-world sampling and independently settled branches.

Registry admission replaces the old11-card allowlist. The inventory includes all86 single-player entries of the91-card Silent pool, plus the conservative registered cross-pool/colorless/status/curse/generated superset,64 potions,293 relics,114 monster models and80 natural encounters. Multiplayer-only cards, player pets and demo monsters remain visible in metadata rather than being mislabeled as natural solo enemies. Registry admission is not an assertion that all combinations have been tested.

## Reused APIs

- CombatRoom/CombatEngine execute setup, native hooks, phases, damage, wins/losses and automatic settlement
- CardModel.CanPlay and PotionCmd.CanUseManually own legality; PotionCmd.Discard owns inventory removal
- ICardSelectionDecisionSource exposes ordinary and startup choices; the worker never rewrites card effects
- Native ActDefinition/EncounterDefinition/RoomFactory factories preserve encounter roles, slots and correlated setup
- CombatState.Clone is an exact execution primitive; it is not a NOSL sampler
- Projection-sensitive rewards/concrete run hooks use independent native replay rather than changing upstream effect guards

## Public v2 contract

The DTO exposes hand/public pile order; an unordered known draw multiset; known positions; unidentified newly generated draw-card count; card-local cost modifiers, temporary keywords, upgrades and attachments; power duration/selected-card payloads; public counters, gold, relic state, orb queue and pets. Private RNG/seed, monster graph memory, physical identities and diagnostic fingerprints are not exported.

Unknown generated draw identities remain withheld until a real reveal. This conservatively withholds even some deterministic generated identities instead of leaking a hidden outcome. Worker-private card bindings track publicly known top cards through upgrades, temporary-cost changes and clone remapping. Lifetime public creature slots survive deaths and summons.

Selections from draw/deck are canonical unordered reveals. Automatic hidden-pile results never expose private order or unidentified identities. Explicit ordered-selection enumeration avoids32-bit mask overflow; spaces larger than100000 candidates fail explicitly rather than truncating legal actions.

## Belief and branch provenance

The optimized exchangeable posterior is retained only for its bounded demonstrated mechanisms and initial prior. General registered content uses independently generated complete native setups and rejection conditioned on every public decision packet/action prefix. The actual source seed is replaced before proposals. Accepted RNG/deck/monster memory remain untouched. The proposal prior is versioned `nosl-independent-setup-prior-v1`.

A proposal budget failure is `posterior_budget_exhausted`: computationally inconclusive. It is not a game loss, proof of impossible content, or a sample that may be dropped from a denominator. Ordered observations can make acceptance very sparse. Data-production throughput must measure actual accepted independent worlds.

`ForkForContinuationAsync` replays the accepted world's exact setup/transcript in an independent concrete RunState. `ForkExact` retains the earlier optimized clone behavior and rejects known projection-sensitive contexts. Switching an in-place sampled branch to the native whole-setup prior is rejected. Sampling choices on a normal native transcript is supported; resampling an in-place sampled pending coroutine remains unsupported.

## Explicit remaining boundaries

- Scenario describes a declared constructed starting inventory. Fresh relic acquisition is not a substitute for arbitrary pre-existing counters/permanent card state/floor context
- Startup combat choices are exposed; unresolved choices during out-of-combat acquisition require a resolved public setup
- Five forced-event owners need their native event return/reward contexts: BattlewornDummy, DenseVegetation, PunchOff, FakeMerchant and TheLanternKey. Ordinary standalone rooms cannot silently replace them
- Full natural-run import/public history, exhaustive interaction coverage and same-process parallel simulation are not certified
- Current game-client compatibility is not independently checked; upstream rule trust is explicit

M3–M6 integration consumes this contract after the bridge verification checkpoint. Data-generation/experimental training authority is managed by the parent task; this bridge does not launch training or declare full training readiness.
