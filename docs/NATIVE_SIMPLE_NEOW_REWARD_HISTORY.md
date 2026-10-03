# GoldenPearl and NutritiousOyster reward-history certificates

This is a narrow extension to the optional public reward-history certificate. It
does not change the native prior, simulator rules, reward rolls, utility values,
or the universal fallback. The central sampler-version change is coordinated
separately from this component.

## Source closure

The reviewed path is `Neow` → `AncientEventModel.RelicOption` →
`RelicCmd.Obtain` → the selected relic's `AfterObtained` → `EventModel.Finish`.
The fixed source files are under `vendor/sts2-sim/src/Sts2Sim.Core/`:

- `Models/Events/Ancients/Neow.cs` exposes GoldenPearl as a positive and
  NutritiousOyster as an extra positive
- `Models/AncientEventModel.cs` resets Neow current HP to zero, applies the A10
  WearyTraveler heal of 80% of missing HP, and obtains exactly the selected relic
- `Commands/RelicCmd.cs` clones and adds that relic, removes its fixed ID from the
  player/shared bags, and calls `AfterObtained`; `Runs/RelicGrabBag.cs::Remove`
  and `Factories/RelicFactory.cs::RemoveFromSharedBag` do not draw randomness
- `Models/Relics/GoldenPearl.cs` calls only `PlayerCmd.GainGold(150m, Owner)`
- `Commands/PlayerCmd.cs::GainGold` passes through `ModifyGoldGained`, adds the
  integer amount, and dispatches `AfterGoldGained`; `RunState.IterateHookListeners`
  supplies the owned relics, potions, and deck. None of the exact admitted starter
  cards, RingOfTheSnake, or GoldenPearl overrides these hooks
- `Models/Relics/NutritiousOyster.cs` calls only
  `CreatureCmd.GainMaxHp(Owner.Creature, 11m)`. `GainMaxHp` raises max HP and heals
  the actual increase. At this out-of-combat event, `Heal` has no combat HP hook
  dispatch; it changes current HP by 11
- `Models/EventModel.cs::Finish` and `Rooms/EventRoom.cs::Exit` close the event
  and restore potion-use availability without reward generation or a random draw

Neither relic overrides the reward-generation hooks checked by
`NativePublicRewardHistoryCertificate.NeutralInventory`. Pickup leaves rarity
pity at -0.05 and potion pity at 0.4. Subsequent proved ordinary weak rewards use
the existing per-card native float transitions, potion presence transitions, and
exact weak-encounter gold range. This is a distribution certificate, not a price
or exchange rate for HP, max HP, gold, potions, or relics.

## Admitted public boundary

The prefix must already satisfy the enclosing complete Silent A10 run-start,
act-zero/floor-one direct event, unlocked selected Neow option, and complete
ordinary weak-combat sequence checks. These two additions additionally require:

1. Exact native starter assets: 70/70 HP, 99 gold, three max energy, two empty
   potion slots, zero orb slots/removals, one pristine RingOfTheSnake, and the
   exact full public projections of the twelve Silent starters plus AscendersBane
2. The next public event after the option selection is the completed Neow owner
   end at ordinal four, with an asset snapshot. No child, intervening operation,
   missing snapshot, or incomplete acquisition can establish this proof
3. GoldenPearl ends at 56/70 HP and 249 gold. NutritiousOyster ends at 67/81 HP
   and 99 gold. The current-HP result includes Neow's earlier 56-HP entry heal;
   it is not calculated as an unchanged 70 HP plus the pickup delta
4. Every other asset is unchanged, including the complete card projections,
   relic projections/order, potion inventory and capacities, energy, orb slots,
   and removal count. The only new relic must have its exact canonical public
   state: unused, non-wax, non-melted, stack count one, no stored cards or
   selected model
5. Every subsequent visible GoldenPearl/NutritiousOyster state remains exact.
   The existing checks for gaps, reward modifiers, unsupported owners,
   replacements/rerolls/extra reward groups, non-weak fights, and wrong acts
   remain active

Unsupported complete histories retain the existing universal card/resource
envelopes. Evidence that is not complete from run start is rejected by the
enclosing proposal before an optional certificate is considered.

## Verification scope

`NativePublicSimpleNeowHistoryTests` uses only the already inspected development
roots 24203 (GoldenPearl, two completed rewards) and 24204 (NutritiousOyster, one).
It checks the native acquisition assets, native potion pity and its update,
native rarity thresholds, gold ranges and observed amounts, and full public
root equality. Two separate proposal seeds per fixed recipe must complete all
owned card, potion-presence, potion-identity and gold seams and return exact
card/resource envelope ratios. Detached evidence must produce the same bounds.

The pickup tests start an actual A10 Neow event and then trap every random draw
during acquisition, compare complete run/player RNG state, compare pity states,
and verify the full asset delta and relevant hook declarations. Guard tests
mutate each acquisition asset category, exact starter state, relic state,
missing/unfinished boundaries, later modifiers, extra rewards, and non-weak
contexts; no mutated history may acquire this certificate.

These fixed native regressions are not fresh evaluation, posterior throughput,
teacher completion, broader natural coverage, production admission, or training.
The v7 fresh-cohort failure accounting remains unchanged until the separately
coordinated, predeclared validation runs establish new outcomes.

Component validation on 2026-10-03 used .NET 9.0.303, isolated build artifacts,
one MSBuild node, disabled node reuse/shared compilation, and `NuGetAudit=false`:

- New tests: 6/6 passed, including 39 complete-history mutations and two rejected
  incomplete-prefix variants for each relic
- Neighboring reward history, NewLeaf, contextual Neow, reward resources,
  identity, owner, and boundary-abort regressions: 55/55 passed
- `git diff --check` passed

The combined 61 focused checks are not the final aggregate Worker/Core gate;
that remains part of the coordinated integration validation.
