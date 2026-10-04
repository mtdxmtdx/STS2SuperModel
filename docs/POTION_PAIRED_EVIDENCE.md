# M3 native paired potion evidence

The frozen native T0 teacher can compare **use this potion now, then B0** with
**B0 from this root, preserving the potion** without assigning a potion price.
The new runner is [verify_potion_comparisons.py](../tools/verify_potion_comparisons.py).
It recovers B0's actual first action from a native `continue` command's public
action event, before any confirmation outcomes are collected. It never chooses
the best sampled preserving alternative. Both arms thereafter use the unchanged
`nosl-public-rules-v1` continuation, which does not use or discard potions.

This is additional constructed-fixture engineering evidence. It adds **zero
unique production roots**, changes no production data, and does not calibrate
the objective, choose an inventory price, authorize training or open formal
labels. The consumed-potion utility and the full expected-utility comparison
remain masked. Preserving-arm utility can be resolved on wins; this does not
resolve the cross-resource comparison.

## Executed confirmation

Executed on 2026-10-02 with the existing frozen main worker, `DOTNET_PROCESSOR_COUNT=1`,
and the driver plus its only child pinned to one CPU. No build or native-source
change was performed. Combined peak driver/worker RSS was 99.953 MiB. All 128
allocated confirmation worlds returned both selected settled outcomes; no
truncations, engine errors or policy nontermination occurred in these pairs.

| Fixed constructed root | No-use B0 endpoint | Use-now endpoint | Empirical HP saved | Simultaneous HP interval | General nine-HP gate only |
|---|---:|---:|---:|---:|---|
| TwigSlimeS, enemy HP 10; player HP 8; one StrikeSilent | 32/32 wins, HP 3 | 32/32 wins, HP 8 | 5 | [2.415, 7.585] | Below threshold |
| Nibbit, enemy HP 15; player HP 20; StrikeSilent + Neutralize | 32/32 wins, HP 11 | 32/32 wins, HP 20 | 9 | [2.536, 15.464] | Unresolved |
| TwigSlimeS, enemy HP 20; player HP 16; one StrikeSilent | 32/32 wins, HP 1 | 32/32 wins, HP 16 | 15 | [9.829, 16] | Eligible, not mandatory |
| TwigSlimeS, enemy HP 10; player HP 1; one StrikeSilent | 32/32 losses, HP 0 | 32/32 wins, HP 1 | 1 | [0.677, 1] | Below general threshold; rescue unresolved |

All roots have one FirePotion, only the native starter RingOfTheSnake relic,
and no other declared relics. Root-time public guards described below apply.

The last row is **sampled rescue evidence**, not an overall rejection of the
potion. The frozen worker has no primitive that certifies `verified_rescue_need`.
The report therefore records `rescue_exception_eligibility="unresolved"` for every
case and records `verified_rescue_need=null` to distinguish unknown from false.
In the rescue
case, the paired rescue-rate interval is [0.677, 1], and the potion death-rate
interval is [0, 0.323]. Neither 32/32 rescues nor 0/32 deaths is an exact
probability proof. The contract would admit an independently verified rescue
below nine, but this runner does not manufacture that verification.

An exact distribution with expected saving nine is admitted inclusively by the
existing V4 evaluator. That separate mathematical boundary check appears in the
report with a non-native evidence label. The native sample mean of nine remains
unresolved; repeated identical samples do not turn an ordinary finite-N interval
into a point interval. Eligibility also never means that the potion must be used
now or that its value is universally nine HP.

## Predeclared sampling, pairing and missing mass

There was one diagnostic pass through **four setups, two worlds per setup**.
These selected fixtures are explicitly diagnostic, not an unbiased sample from
the game's state distribution. No fifth setup or adaptive confirmation extension
was executed. Diagnostic seeds were `8800000 + 10*i + j`, for `i=0..3`, `j=0..1`.
The diagnostic raw records remain locally under
`artifacts/reports/potion-paired-diagnostic-v1/`.

Before confirmation, the runner wrote and hashed a plan containing all four
exact public roots, native-selected B0 root actions, potion actions, support
arguments, source and runtime hashes, and all evaluation seeds. Confirmation
seeds were the disjoint ranges 9100000–9100031, 9100100–9100131,
9100200–9100231 and 9100300–9100331, in table order. Source scenario seeds retain
the original diagnostic root identities; independent evaluation resamples the
world **conditional on that fixed public root**, not a new source battle.

The existing [CombatTeacher](../src/Nosl.Worker/CombatTeacher.cs) samples once
per evaluation seed and forks that world for every root action. The report pairs
outcomes in the teacher's fixed seed order, checking the full seed sequence,
candidate ledger, unchanged root, policy ID, endpoint and combat-start HP.
The raw teacher also evaluates unselected legal actions: there are 128 root-world
draws, 256 selected pair endpoints, and 544 total action/world outcomes. Action
copies are not independent worlds or new battles.

Twenty intervals are declared: HP saved, use-arm death, hold-arm death, paired
rescue and paired harm for each of four cases. Each uses two-sided fixed-N
Hoeffding at alpha 0.05/20, giving a union-bound family error of at most 0.05
under the independent sampler and stated support assumptions. Their common
unit-support radius is 0.3231827010. These conservative intervals are not a
calibration of event probabilities. No sequential stopping or sample-selected
confidence claim is used.

For a bounded variable in [L,U], the radius is
`(U-L)*sqrt(log(2/alpha)/(2*N))`. An incomplete allocated outcome contributes
[L,U] to the empirical-mean identification interval, which is then expanded by
that radius. Missing outcomes are never dropped, renormalized away, or called
deaths. An omitted ledger row fails verification. A failed request leaves an
incomplete-confirmation failure artifact rather than claiming fixed-N completion.

## Why the restricted HP support is [0, startHp]

This is a small manually reviewed mechanics certificate, **not a general claim
that potion use can never hurt** and not a bound inferred from sample extrema.

1. Each exact declared recipe is at turn one with current HP equal to anchored
   start HP. The player has zero block, no powers, no pets or orbs, no pending
   choice, and all of its one or two unupgraded, unenchanted cards are in hand.
   Draw, known-draw, discard and exhaust piles are empty. The only relic is
   RingOfTheSnake and the only potion is FirePotion. Unreviewed recipes fail
   closed rather than receiving this certificate.
2. The sole enemy is the specified native TwigSlimeS or Nibbit at no more than
   20 HP, with zero block and no powers. Native
   [FirePotion](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Potions/FirePotion.cs)
   deals 20 unpowered damage. These two
   [enemy](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Monsters/TwigSlimeS.cs)
   [models](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Monsters/Nibbit.cs) have no
   retaliation or death hook. There are no additional relic/power callbacks in
   this declared fixture to damage the player after potion use.
3. The use arm thus kills the only enemy and settles immediately, before an
   enemy turn, at `HP_use = startHp`. The support depends on this immediate-win
   fact, not monotonicity of the unchanged continuation under arbitrary potions.
4. [StrikeSilent](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/StrikeSilent.cs),
   [Neutralize](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/Neutralize.cs),
   these enemies and
   [RingOfTheSnake](../vendor/sts2-sim/src/Sts2Sim.Core/Models/Relics/RingOfTheSnake.cs)
   cannot heal the player, increase max HP or generate a healing resource.
   RingOfTheSnake only modifies opening draw. No postcombat reward is selected
   at the teacher's endpoint. Consequently `0 <= HP_hold <= startHp`, including
   true defeat at zero; subtracting gives `0 <= HP_use - HP_hold <= startHp`.

The executed plan pins the direct rule source hashes and all runtime DLL/JSON
hashes. Independent review also inspected the potion/creature commands, base
models, hook dispatch, combat engine, combat room, character, generated-card
implementation, powers and setup lifecycle supporting the immediate-win argument.
Their expanded source manifest is recorded as a **post-run support review**, not
represented as a predeclared fingerprint. The executed DLL hashes already pin the
entire compiled implementation. The current runner freezes the expanded source
list before any future confirmation run.
Every settled outcome is additionally checked against the certificate: the use
arm must win at start HP, the hold arm must keep the potion, max HP and permanent
assets must not change, and required settlement/inventory ledgers must be
complete. A contradiction fails the run; it does not widen the bound after
looking at outcomes. Outside these recipes, generic no-healing alone would
allow the broader paired range [-startHp,+startHp]; the tool declines such
unreviewed inputs. Full-content or original-client fidelity remains a separate
gate from this pinned-simulator argument.

## Artifacts and reproduction

Local evidence directory: `artifacts/reports/potion-paired-confirmation-v1/`.
It contains the frozen `plan.json`, `plan.sha256`, executed `evaluator-source.py`, exact requests, complete native
`*.record.json` responses, and `report.json` with all paired samples and intervals.
Large raw artifacts are intentionally ignored by Git; this document and the
runner are the small reviewable deliverables.

Review clarified the rescue-certification field from an unasserted Boolean to
explicit `null`. `report.initial.json` preserves the executed report; `report.json`
was regenerated from the same saved records with the current reducer. Generator
and reducer hashes are recorded separately. `support-review-sources.json` records
the expanded source coverage and its post-run timing. No samples, frozen plan, support
bounds, estimates or gate results changed, and no additional simulation ran.

Frozen plan SHA-256:
`99ef92cc4c145ca475a30a17a1bf15367b46bb75e77898d10bde7e1e563e3ac2`.
Frozen worker DLL SHA-256:
`9f8232685cdfb027d0b3b44c1a6934049f8be6e4e426a6a7bc7b92ee7dac9644`.
Frozen simulator DLL SHA-256:
`520b62ac298ecaee24eab42683203306b0c466497348747bc6832169463ef231`.
All match the active v4 generation manifest, before and after confirmation.

From the repository, with those existing frozen binaries and manifest present:

```sh
source ../.dotnet-nosl-env.sh
DOTNET_PROCESSOR_COUNT=1 python -B tools/verify_potion_comparisons.py \
  --output artifacts/reports/potion-paired-confirmation-reproduction
python -B -m unittest discover -s tests/calibration -v
```

The first command deliberately requires a new output directory; it cannot
overwrite the executed evidence or resume with a changed fixed-N plan. It
refuses a mismatch with `--runtime-config` (default: the existing v4 shard-1
generation manifest), never rebuilds binaries and never launches an optimizer.
There are 24 passing calibration tests, including eight new ledger/uncertainty
regressions. These Python fixtures test evidence handling, not simulated game
correctness; the four native runs above are the integration evidence.
