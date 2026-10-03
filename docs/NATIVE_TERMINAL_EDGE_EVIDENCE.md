# Native terminal-edge evidence

Two bounded, constructed Silent A10 fixtures compare `CombatSession` with an independent
`RunDriver.RunOneInjectedCombatAsync` execution using the pinned simulator. The native driver
owns the combat verdict, automatic settlement and settlement-completed callback. No runtime
or game-rule change is included. Runtime baseline: `28cccdd`; pinned upstream: `5a9576b`.

## Simultaneous death

`NativeTerminalEdgeTests.L06_LethalStrikeAndNativeThornsKeepTheNativeDoubleDeathVerdict`
uses `SpinyToadNormal`, a one-card `StrikeSilent` deck, player HP 5/100, enemy HP 6,
and `MeatOnTheBone`. One legal end turn lets the toad's native opening move apply 5 Thorns.
A legal Strike then leaves both creatures at 0 HP in the same action.

The native result is **loss**. Thorns retaliates in `BeforeDamageReceived`; the in-flight
Strike still finishes its damage. `CombatEngine.CheckWinCondition` checks player survival
before enemy survival. The adapter matches this actual verdict, final HP 0, cumulative HP
damage 5, healing 0, and zero reward sets. The test does not derive victory from enemy HP.

## Delayed damage and settlement

`NativeTerminalEdgeTests.L06_DelayedBombWaitsForRegenAndNativeVictorySettlementBeforeSealingLedger`
uses the same native encounter, a one-card `TheBomb` deck, player HP 38/100, enemy HP 40,
`RegenPotion`, and `MeatOnTheBone`. Its five public actions are potion, card, and three end turns.

The Bomb's native countdown remains active through two turns. HP is 43 after the first
end turn and 22 after the second; the enemy remains at 40 HP and no rewards exist. On the
third end turn, Regen heals 22 to 25 before the Bomb kills the enemy. The adapter's actual
pre-settlement snapshot is 25 HP; the victory hook then heals to 37.

Both native and adapter paths produce this exact committed HP callback sequence:

| Mutation | Before | After |
|---|---:|---:|
| Regen heal, turn 1 | 38 | 43 |
| Regen heal, turn 2 | 43 | 47 |
| SpinyToad attack | 47 | 22 |
| Regen heal, turn 3 | 22 | 25 |
| MeatOnTheBone victory heal | 25 | 37 |

The completed ledger records damage 25, healing 24 and exactly one consumed `RegenPotion`;
38 − 25 + 24 = 37. The native settlement callback fires once after the final HP mutation,
potion mutation and reward generation. The adapter exposes complete HP/resource provenance,
cleared powers, one unselected reward set, and zero reward selections. Repeated settlement
preserves the same terminal facts, HP and reward objects without another automatic heal.

## Source and scope

Mechanic references: `SpinyToad.GenerateMoveStateMachine`, `ThornsPower.BeforeDamageReceived`,
`CreatureCmd.DamageSingle`, `CombatEngine.CheckWinCondition`, `TheBomb.OnPlay`,
`TheBombPower.BeforeSideTurnEnd`, `RegenPower.BeforeSideTurnEndEarly`,
`MeatOnTheBone.AfterCombatVictoryEarly`, `CombatRoom.ResolveVictoryOnceAsync`, and
`RunDriver.DriveCombatAsync` in `vendor/sts2-sim/src/Sts2Sim.Core`.

The low starting HP, reduced enemy HP and declared decks are constructed test inputs.
Powers, damage, healing, death and rewards are reached through normal native actions and hooks;
the tests do not force a verdict, inject terminal DTOs or manually invoke power callbacks.
This closes dedicated adapter evidence for these two L06 cases. It does not prove every
delayed-effect interaction or natural-run source distribution, and does not authorize
production data generation, training or policy promotion.

## Verification

Targeted run: **2 passed, 0 failed**, .NET SDK 9.0.303.

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter FullyQualifiedName~NativeTerminalEdgeTests
```

Adjacent regression set: **59 passed, 0 failed** (including the two new cases), 13.57 seconds.

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-build -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~NativeTerminalEdgeTests|FullyQualifiedName~M012Tests|FullyQualifiedName~ForcedEventLifecycleTests'
```

These are focused tests, not a new full-suite result. Existing complete regression evidence
belongs to its recorded runtime revision; this change contains only the two tests and this note.
