# STS2BestChoice.Core

This directory contains the pure C# planning and shadow-simulation layer used by
the training tools. It has no Godot, Mod loader, or game-assembly dependency.

## Components

- `Simulation/DeterministicSimulator.cs`: deterministic card, potion, status,
  enemy-turn, RNG, and turn-boundary transitions.
- `Simulation/MutableCombatState.cs`: cloneable mutable state and canonical keys.
- `Search/CombatSearchSession.cs`: current-turn search used by the teacher bridge.
- `Search/ExpectimaxEngine.cs`: reusable Max/Chance search engine.
- `Model/`: snapshots, action candidates, Power/Relic state, RNG, and training
  contracts.
- `Scoring/`: balanced, damage, and loss objectives.
- `Semantics/`: versioned card-effect semantic compilation.
- `Data/`: version-aware card and relic catalog readers.

The v0.111 relic catalog is embedded from
`data/relics/v0.111/relic-catalog.json` by the project file. Game binaries and
the live Mod adapter remain outside this repository.
