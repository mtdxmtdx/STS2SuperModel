# Ordered Toadpole HP and turn closure

This targeted public-combat accelerator covers only the complete two-Toadpole startup produced by the registered `UnderdocksEncounters.BatchB` `ToadpolesWeak` factory. It reuses the existing ideal-tape HP primitive and correction machinery. Legacy unique-ID HP APIs, native combat rules, other duplicate rosters, public packet schemas, and prior/corpus semantics are unchanged.

## Public slot and HP proof

The pinned factory creates an unslotted front Toadpole, then an unslotted rear Toadpole. Its registered encounter has no slot ordering. `CombatRoom.Prepare` registers players first and calls `CombatState.AddMonster` in factory order. Each `CreateCreature` rolls unique HP before `AddCreature` appends it to `Enemies` and `SpawnedEnemies`.

Unlike CorpseSlug, Toadpole has no self-power setup publication or `AfterAddedToRoom` override. Its setup state-machine branch only reads `IsFront` and selects a move; it does not run that move. `PublicKnowledge.CombatStarted` therefore assigns the lifetime enemy slots in the unchanged creation order, before the initial draw and turn-one intent publication. Front publishes Buff; rear publishes an 8-damage, single-hit Attack at A10.

The detached helper requires exactly those two ordered startup slots and intents, a complete certified public startup, and distinct published MaxHp values in 22–26. It uses MaxHp, never damaged CurrentHp. Toadpole and every admitted Thorns/turn callback preserve MaxHp; the composed mapper deliberately takes the first stable public snapshot even when the selected root is later in combat.

At the native HP callback the helper verifies the exact registered encounter object, Monster room before its engine starts, sequential owned Niche RNG, A10 range, non-projection state, empty encounter slots, null creature slot names, front/rear role, and the expected pre-add lengths/order of both enemy lists. Earlier conditioned creatures must still have their original target MaxHp and CurrentHp. The native sorted used-HP set must equal the earlier public target values. A same-name custom room cannot satisfy the factory-identity guard.

## Exact conditional law

The first native HP draw has five available values. The second has four, excluding the first target. Each slot calls `NativeHpProposal.Create` with the actual native sorted exclusion set and its published target. Full 64-bit words are sampled inside the exact native floating-point bucket, including low bits.

For each slot the envelope comes from `NativeHpProposal.RootEnvelopeBucket(22, 26, earlierPublicTargets)`. The earlier targets and their order are public-fixed; no runtime pool selects an envelope. The first envelope bounds the five-way bucket, and the second bounds the four-way bucket. Exact likelihood and envelope factors compose with all retained HP/shuffle proposals. The familiar ordered-pair probability is approximately 1/20; native floating-point bucket differences remain in the exact correction.

Unknown/incomplete public rosters, non-distinct or out-of-range HP, and other startup role patterns leave this optional helper ineligible. Public contradictions during native replay reject the entire attempt. Wrong owner/stream/factory/order, changed earlier creature state, aliases, and missing/extra forced words remain unresolved errors. Full public replay equality and the owning tape's completion/failure guards remain mandatory.

## Toadpole and Thorns transition closure

Draw-cycle certificate v3 adds the complete sealed Toadpole turn implementation and ThornsPower. Toadpole uses a fixed cycle: Whirl attacks, Spiken adds two Thorns, and SpikeSpit subtracts two Thorns then attacks three times. Front and rear enter that same cycle at different fixed moves. Neither branch/transition nor move generates a card, touches a card pile, summons, changes MaxHp, requests an extra turn, or randomly rerolls a move.

ThornsPower's sole custom combat hook retaliates before a powered attack (or its explicitly named Omnislice case) through direct unpowered damage with a null card source. That retaliation cannot satisfy its own recursion condition. Application and removal inherit no-ops. The existing admitted damage/death listeners stay inside the same closure; possible deaths stop normal native progression without manufacturing further draws. All other enemy-turn families and generated cards retain their existing fallback behavior.

## Focused evidence

Tests enumerate all 64 finite native word pairs at three-bit precision for each of the 20 distinct public target pairs, checking exact mass and correction outcomes. Native fixtures check original public slot assignment, complete public-opening equality after composing HP and shuffle proposals, two consumed HP words, damaged CurrentHp with unchanged MaxHp, and rejection of wrong roles, ranges, named slots, RNGs, used-HP sets, same-name custom factories, and changed earlier creatures.

A native combat proceeds through Thorns gain, attack retaliation, Thorns removal, subsequent draws, and a reshuffle while retaining the public pool certificate. Retained source11004 combat 2 now has two ordered HP targets (26 and 25), extends the first draw prefix to 12, and reaches the complete observed prefix. Across that retained run the composed HP target count increases from three to five. These are bounded mechanism/integration checks, not new throughput measurements, formal labels, training, or production admission.
