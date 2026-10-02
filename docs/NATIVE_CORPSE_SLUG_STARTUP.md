# CorpseSlug public-combat startup certificate

The hybrid public-combat helper explicitly opts into
`NativeInitialShuffleCondition.TryCreatePublicCombatV2`. The legacy `TryCreate`
and `NativeInitialHpCondition` paths keep their original eligibility rules and
the declared prior identity is unchanged. This extends acceleration only.

The typed startup projection preserves pre-draw power facts. The new profile
accepts exactly one self-applied `RavenousPower` amount 5 for each published
`CorpseSlug` startup slot, in slot order, before the uninterrupted initial draw.
Unknown, missing, duplicated, reordered, or interrupted facts disable that
certificate. All original typed facts remain in early replay checks and final
full-packet equality.

The source-pinned closure follows `CorpseSlug.AfterAddedToRoom`,
`GenerateMoveStateMachine`, and `RavenousPower.AfterDeath`: startup only applies
the self-power and selects an ordinary move state. The power's only hook acts
on monster AI and Strength after a death. These paths do not reorder cards.
Existing entry card/relic/potion hook and metadata checks still apply.

The separate HP helper covers complete two- or three-slug rosters with distinct
A10 MaxHp values in 27–29. Both `UnderdocksEncounters` factories create unslotted
slugs in array order. `CombatRoom` registers that order, empty encounter slots
prevent sorting, and startup self-power publication assigns lifetime public
slots in that order. `CorpseSlug` and `RavenousPower` do not change MaxHp later.
Runtime guards verify the owned run/stream, empty slot configuration, null slot
names, preserved creation roster, earlier target HP, and the native distinct
used-HP set. Public contradictions reject; invalid runtime assumptions throw.

Each slot uses `NativeHpProposal`'s exact native floating-point bucket law and a
public-root-constant envelope, excluding that slot when collecting the other
target HP values. The proposal retains all HP and shuffle density corrections.
Four-bit exhaustive enumeration covers every distinct two- and three-slot HP
target; native tests cover both factories and a same-recipe hybrid replay through
owned settlement. Such a replay is an integration check, not an independent
posterior acceptance or production-admission claim.
