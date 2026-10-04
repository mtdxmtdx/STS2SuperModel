# Finite public Regen engineering evidence

This opt-in C03 path executes one finite beneficial stall without changing the simulator, frozen ordinary baseline, Hunt semantics, v1 student files, or the existing first trial. It is a narrow executable engineering closure, not full-content support, statistical admission, a trained policy, or a general planner.

## Reviewed public family

`FiniteRegenPolicy` receives only `DecisionPacket`. The reviewed family has exactly two plain unupgraded Finesse and one plain StrikeSilent, no exhaust or unidentified cards, the ordinary RingOfTheSnake state, no current potions/pets/orbs, and one TwigSlimeS with at most six HP, zero block/powers and a public five-damage single attack. The only permitted player power is one ordinary RegenPower of integer amount 1–5, or none for an immediate no-benefit exit. Card effects, healing, damage, draws, shuffles and settlement execute through the pinned native simulator.

The immutable anchor stores its public packet, initial Regen, baseline/template identity, start turn, and deadline `start + max(1, initial Regen)`. Useful Finesse actions supply defense until the actual public block covers the current intent; only then may the policy end the turn. It does not predict or award healing itself. Full HP, exhausted Regen, the original deadline, missing defense, history regression, or departure from the reviewed safety scope ends pursuit permanently and delegates to the frozen ordinary baseline. A completed benefit can be marked finished at a live decision; this is distinct from Hunt's requirement to observe a specified terminal reward.

## Actual paired evidence and its limits

`AnchoredRegenEvaluator` declares unique independent evaluation seeds and finite sampling/execution budgets before sampling. It independently replays the declared setup, conditions on the complete observed prefix, then runs both policies from the same hypothetical world using native continuation branches. It never copies the actual hidden source future, resets the anchor, or directly changes HP.

The constructed native fixture consumes RegenPotion before the anchor. Starting at 50/70 HP with enemy HP 6, the immediate baseline finishes at 50 HP. The finite controller plays Finesse for adequate public defense and ends five player turns; native healing is 5+4+3+2+1, with zero HP damage, then the ordinary Strike finishes at 65 HP. Both evaluation worlds produce that outcome. Their deadline remains turn 6. A 68/70 fixture stops after one end turn at full HP; full-HP and no-Regen anchors do not stall.

Whole-combat HP and inventory references remain unchanged. The consumed RegenPotion remains in the original inventory ledger. Its unknown price keeps both absolute utilities unresolved, and the existing common-inventory comparison requires matching continuation identities, so this evaluator does not claim its relative-cost mask. Actual settled HP comparison is available separately. No subjective resource price or zero-value imputation is used. Fixed-N bounded intervals use maximum HP, including potential healing above anchor HP; truncated/error worlds remain unknown mass. The two-world evidence leaves statistical safety/eligibility unresolved, even though its actual trajectories are beneficial. `SafeFiniteStall` uses separate positive-benefit/safety evidence, with none of Hunt's next-turn, five-HP or 80% conditions.

## Public v2 boundary and targets

The distinct `nosl.controller.finite-regen.v1` / `safe_finite_regen` context contains public anchor, target power, initial Regen, deadline, status and observed events only. Python validates exact fields, card/power/relic scope, history extension and immutable controller transitions. Healing above anchor HP is admitted here; the existing Hunt rejection remains unchanged. Public departures can be represented and immediately produce a safe exit. The v2 encoder consumes the kind and finite context; standalone inference never selects a learned Regen action or exposes Hunt-head predictions for this context.

`FiniteRegenDataset` exports inspectable non-trainable engineering evidence. The comparison evaluates whole policies, not individual candidates, so every ordinary action target remains null/masked. Both Hunt-only plan targets are also null/masked; attempts to apply those heads are rejected. No new learned heads, fitting, optimizer steps, backward passes, training artifacts or old-test reads are involved.

## Focused verification

- Eight native tests cover five-turn healing, full HP, no benefit, permanent safety/defense fallback, original combat-start HP, truncation mass, replaced hidden source, and distinct finite-benefit gates
- A combined targeted run with existing Hunt, finite-Hunt export and objective regressions passed 56/56; a subsequent targeted Regen run checks the final history-prefix hardening
- Eight new Python tests cover strict public schema, native packets through v2 forward, context encoding, Hunt no-healing regression, no reopening/reset, public danger exit, private-field rejection, masked targets and inference abstention. Status-independent HP/power progress must agree with recorded public facts; finished claims must also agree with the complete public exit guard, and the anchor's own turn must match its history
- Twelve selected existing v2 forward/schema/inference tests passed; backward/fit tests and `test_v2_pipeline_acceptance.py` were not run

Reproduce native checks from repository root using the supplied SDK environment:

```sh
source ../.dotnet-nosl-env.sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --artifacts-path artifacts/finite-stall-build -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~FiniteRegenTests|FullyQualifiedName~ObjectiveTests|FullyQualifiedName~AnchoredHuntTests|FullyQualifiedName~FiniteHuntDatasetTests'
PYTHONPATH=python ../.venv-nosl/bin/python -m unittest discover -s tests/python -p test_finite_regen_v2.py -v
```

`NOSL_REGEN_FIXTURE_PATH` optionally exports the native engineering record and three public decision snapshots plus terminal facts while running the native tests. The retained fixtures come from this path; they are not synthetic HP edits.
