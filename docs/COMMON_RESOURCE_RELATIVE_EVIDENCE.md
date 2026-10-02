# Common inventory relative evidence, development v1

The opt-in `CommonResourceObjective` API identifies a paired cost difference when the
same unknown additive inventory component occurs in both outcomes. It never assigns
that component a price, fills an absolute target, or changes `CombatTeacher`,
`TeacherDataset`, `TeacherRanking`, the strict corpus schema, or a model bundle.
Its separate label profile is `nosl-common-inventory-relative-development-v1`;
`FormalLabelsAllowed` is always false. Candidate K=1000 and eta=0.2 remain uncalibrated.

## Algebra and admission

For resource r, the existing objective uses the loss-aware coefficient

    d_r(Z) = inventory_start_r - 1[WIN] * inventory_end_r

and cost

    C(Z) = K * 1[LOSS] + L + eta * max(L,0)^2 / H_start
           + sum_r d_r(Z) * v_r - permanent_future_value(Z)

The new profile requires the **entire** inventory coefficient vector to be equal,
including priced resources. Its inventory contribution cancels exactly. Matching
consumption events or matching raw end inventories alone are insufficient: a loss
has no retained inventory value. A win and a loss can be compared when the correctly
masked coefficients are equal; the defeat term remains in the difference.

The existing inventory/permanent caps reject an objective evaluation; they do not
clip costs. Cancellation must not bypass these validity guards. The new evaluator
requires the sum of absolute coefficients times their known absolute unit prices,
or `MaximumAbsoluteUnitValue` for missing prices, to fit the inventory cap. Thus the
common component is valid under every unresolved unit value admitted by the profile.
No price, including zero, is substituted into either absolute evaluation.

The deliberately narrow profile also requires:

- Valid actual settled endpoints and identical settlement and frozen continuation IDs
- Explicit paired-world, public-root, combat-start, public-controller and sampler
  audit identities, with a verified shared-world fork declaration
- Identical start HP, max HP, inventory and exact persistent asset snapshots
- Complete inventory snapshots and permanent-change declarations
- No permanent changes, changed persistent assets, earned bonus, or mismatched
  specified-finish/deadline facts, even when aggregate change counts are equal
- A complete reconciled resource ledger, or a separately reviewed empty-inventory
  continuation certificate scoped to the same root and endpoint
- In a batch, exactly the planned ordered unique independent worlds, one fixed
  root action per arm, and one shared root/anchor/controller/continuation across worlds

Exact string equality for persistent asset snapshots intentionally fails closed on
different serialization. Permanent deck/relic changes and future reward opportunity
counts are not precise enough for cancellation here; equal counts do not prove equal
objects or future values. This API applies only to the existing additive objective,
not an arbitrary nonlinear or interacting resource-value model.

Audit identities, predeclared sampling evidence and supplied support proofs are
caller obligations. Equal strings do not independently prove how a simulator was
sampled or executed. The generic API checks their consistency; callers must retain
the actual sampling/fork/plan evidence. These fields belong in audit data, never
student input. The regression below checks that evidence against the real original
teacher and independently replays it.

## Reviewed empty-inventory mechanics closure

The public `EmptyPotionContinuationProof.Certify(CombatSession)` entry point binds
the certificate to an actual declared constructed scenario. It refuses native-run
carry-in, other enemies/encounters, extra relics and unreviewed cards. A packet-only
helper is internal, for the independently replay-verified archived regression.

The reviewed family contains only unupgraded NoxiousFumes, DeadlyPoison, Backflip,
Footwork, DefendSilent and Slimed, RingOfTheSnake, and TwigSlimeM. The public-v2
packet must explicitly show empty potions, no unidentified draw cards, no pets or
orbs, exact known card/relic metadata, player dexterity/Noxious Fumes powers and
enemy poison only. Unknown/missing effects or metadata are rejected.

The pinned native rules in these files support the closure:

- `Models/Cards/{NoxiousFumes,DeadlyPoison,Backflip,Footwork,DefendSilent,Slimed}.cs`
- `Models/Powers/{NoxiousFumesPower,PoisonPower,DexterityPower}.cs`
- `Models/Relics/RingOfTheSnake.cs` and `Models/Monsters/TwigSlimeM.cs`
- Existing generated-card execution, card/power commands and the worker settlement
  boundary, which stops before any reward selection

These mechanics draw, block, apply dexterity/poison or generate combat Slimed cards.
They do not heal, regenerate potion inventory, mutate persistent assets or earn
extra rewards. Empty root inventory therefore stays empty in every continuation
world. The existing recorder's `ResourceProvenanceComplete=false` remains unchanged:
the separate closure supplies the missing relevant proof; it does not claim the
generic diagnostic event ledger became complete. Complete start/end snapshots and
unchanged persistent assets are still checked on each outcome.

This is an implementation-specific constructed-fixture certificate, not a claim
about arbitrary game content, native run state, original-client fidelity, or whole-run
optimality. Extend it only with a separately reviewed and versioned mechanics proof.

## Actual revision-14 native regression

`tests/data/common-resource-relative-native-v1.json` losslessly deduplicates the
original low-hp-poison:1 revision-14 teacher record. All five actions had exactly
one repeated outcome across their sixteen independent worlds. The fixture stores
each outcome once, its sixteen original world indices, and the identical persistent
asset JSON once. Source record/plan/runtime SHA-256 identities are preserved. This
is engineering regression evidence, not a training shard or additional independent
data.

The live test recreates the original scenario, replays its fourteen observed actions,
checks the complete public packet, reruns the original sixteen seeds and compares
every one of the eighty native endpoint records exactly. It also checks the ordinary
teacher still has null absolute expected costs and no ranking pairs.

For all eighty endpoints, combat-start inventory contains SwiftPotion ×1 and final
inventory is empty. Every absolute utility remains unresolved. With the separate
closure, all ten paired empirical means are identifiable. Relative to Backflip:

| Root action | Empirical cost difference | Native final HP |
| --- | ---: | ---: |
| Backflip | 0 | 12 |
| Either Slimed | 65/12 = 5.4166666667 | 7 |
| NoxiousFumes | 65/12 = 5.4166666667 | 7 |
| End turn | 1014.4 | 0, true loss |

These differences do not declare an exact expected difference or a preference from
the observed mean alone. The closure proves no healing and common inventory in all
future worlds, so the full cost-difference support at start HP12 is
[-1014.4, +1014.4], including possible losses. It must not be narrowed by conditioning
on the observed all-win samples.

For the NoxiousFumes-minus-Backflip contrast, fixed N=16 paired Hoeffding with
familywise alpha=.05 over the ten action pairs yields approximately
[-872.454417, 883.287750]. It crosses zero; `PreferredArm` stays null. Omitting an
independently justified support produces no interval and no preference at all.
Incomplete/masked allocated rows keep the empirical batch mean and interval null;
the evaluator does not drop them or renormalize. Overlapping intervals do not prove
equivalence. The API can expose a development preference only when an explicitly
supplied valid support yields a nonzero-sided fixed-N interval; it never promotes
formal labels.

## Verification and isolation

The new ten tests plus the existing objective and teacher tests passed: 54 total.
They cover death masking, component bounds, unknown and incomplete ledgers,
unobserved assets, reward opportunities, mismatched worlds/anchors/continuations,
world-adaptive arm identities, missing probability mass, no-support abstention,
strict closure metadata, exact archived outcomes and real native replay.

An independent read-only harness against the final isolated DLLs also checked all
2,916 two-resource start/end and win/loss combinations: 655 pairs were safely
admitted, and 16,375 comparisons across a 5×5 admissible price grid matched the
original absolute evaluator. Unequal loss-aware vectors and unsafe component
bounds remained masked.

Builds/tests used a separate worktree and `--artifacts-path`; main/default binaries
were not rebuilt. The frozen main worker and simulator hashes remained
`9f8232685cdfb027d0b3b44c1a6934049f8be6e4e426a6a7bc7b92ee7dac9644` and
`520b62ac298ecaee24eab42683203306b0c466497348747bc6832169463ef231`.

From a checkout with the documented .NET environment configured:

```sh
DOTNET_PROCESSOR_COUNT=1 dotnet test tests/Nosl.Tests/Nosl.Tests.csproj \
  -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --artifacts-path /tmp/nosl-common-relative-build \
  --filter 'FullyQualifiedName~CommonResourceObjectiveTests|FullyQualifiedName~ObjectiveTests|FullyQualifiedName~TeacherTests'
```

No optimizer, formal label job, model promotion, remote write or corpus rewrite is
part of this change. Broader closure families and training integration remain
separate work requiring their own evidence and versioned data contract.
