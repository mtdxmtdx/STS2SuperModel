# Opt-in finite TheHunt plan evaluator

## Scope and purpose

`AnchoredHuntEvaluator.EvaluateAsync` is a new, opt-in library API for a single deliberately restricted plan. It does not modify the ordinary teacher-record/student schema, start training, select a deployed action, or admit a formal objective profile.

The reviewed goal is a **TheHunt fatal that actually produces an offered extra CardReward**, at or before the end of the next player turn measured from one fixed public anchor. The pinned simulator's TheHunt awards a CardReward, not GoldReward. The evaluator never inspects reward choices, chooses/claims a reward, or invents its future HP value.

## Public policy and baseline

`FiniteHuntPolicy` lives in the simulator-free `Nosl.Contracts` assembly and receives only `DecisionPacket` DTOs. Its frozen template is:

1. Use the ordinary `PublicRulePolicy` for required public card choices
2. If the public HP safety guard is reached, TheHunt has already been spent without finishing, or the fixed deadline expires, permanently exit the pursuit and continue the ordinary baseline
3. Prefer a currently legal TheHunt play; no damage, fatal or reward rule is reimplemented in the policy
4. Otherwise try a currently legal reviewed draw skill; before the deadline turn, defend against the published intent preview if possible and wait for the next turn
5. On the deadline turn, if no reviewed draw or TheHunt play is legal, exit to the baseline immediately

The default public HP floor is 10. This is an explicit, versioned template heuristic, not a calibrated user preference or a guarantee of survival. The parameter and frozen policy IDs are recorded in the immutable anchor. The baseline is unchanged `nosl-public-rules-v1`: it is not artificially weakened, and may naturally earn the same benefit.

The anchor keeps the complete public summary, original start player turn, fixed next-turn deadline and template/baseline versions. Each trajectory records the chosen public action, turn, original deadline, pursuit state and exit reason. There is no budget renewal, new anchor inside a rollout, or online conditional-budget allocation. This API evaluates a whole plan from its initial anchor; repeated new calls at later states must not be represented as continuation of the same budget.

## Independent paired execution

The caller provides the complete final evaluation-seed set before execution. Seeds must be distinct; budgets and configuration are frozen at entry. For each independent belief world, the evaluator makes two separate continuation copies and runs the baseline and plan to true settled outcomes or the declared computation limit. The live source's hidden future/seed is not used as a sampled future.

TheHunt requires concrete native replay for actual reward hooks; the evaluator uses the existing `ForkForContinuationAsync` path rather than projection reward shortcuts. The source public anchor is checked throughout evaluation and the source stays untouched. The public policy never receives either hypothetical world.

The shared `RolloutRecorder` records ordinary settled facts for this evaluator and `CombatTeacher`. The extraction preserves existing teacher scoring/fields/masks and disposes each damage-event JsonDocument. Outcome recording is offline audit work, separate from policy inputs.

## Goal facts and result masks

A positive `TheHuntPower` public event identifies the actual fatal trigger and its player turn. Settlement must independently confirm a true win and an actually offered extra CardReward. The no-other-reward-source support restriction makes this attribution specific. `SpecifiedFinishSuccess`, `EarnedBonus` and `DeadlineMet` remain separate raw facts. Incomplete/error outcomes leave all three unknown rather than writing false game outcomes.

Controller status describes pursuit lifecycle, not incidental success. An already-aborted plan keeps its exit reason even if the normal baseline then earns the benefit in time; baseline trajectories keep the `baseline` tag. Only an active pursuing controller can finish. True settled loss or no timely designated benefit closes a still-active pursuit as aborted. A computation/sampling failure instead leaves an active pursuit unresolved, never a fabricated game loss; a prior genuine policy abort is preserved.

The result contains every allocated world, both complete raw trajectories, per-outcome objective status, separate baseline/plan summaries, sample means, fixed-N intervals and explicit masks. A true failure remains a failure even when the other policy wins. Sampling failures, engine errors and decision-budget truncations retain their allocated mass.

A successful extra CardReward has an unresolved future value in the candidate objective. Its utility stays null; the utility-comparison mask remains false. HP and designated-success sample statistics can still be recorded independently. No positive utility label is fabricated from the unpriced benefit.

## Restricted support and uncertainty

The first support certificate admits one TheHunt and only the explicitly listed no-healing cards, a single TwigSlimeS/LeafSlimeS/Nibbit, no potions, RingOfTheSnake only, no enchantments/afflictions, no native carry-in and no unknown generated identities. Broader legal game content is rejected by this **optional evaluator**, not removed from the ordinary simulator's legal actions or overall project scope.

Within that reviewed family, neither policy can heal or increase maximum HP. Both terminal HP values lie between zero and public anchor HP. Therefore paired extra net HP loss lies in `[-anchor HP, +anchor HP]`; this is a bound on the difference, not a change to the utility's original combat-start reference. Observed terminal facts contradicting the support cause explicit rejection.

Three fixed-N Hoeffding intervals cover whole-plan extra HP loss, unconditional designated success, and the paired increase in death probability. The familywise alpha is split across the three intervals. Missing worlds fill an interval of possible values instead of being dropped or scored as defeats. A template-specific conservative safety admission requires the excess-death interval to establish no increase; otherwise safety remains unresolved, or is rejected if the interval establishes an increase. This safeguard does not redefine the project's overall risk objective as lexicographic.

The 5-HP/.8 gate uses these intervals and safety status. A four-world observed success rate of 1 or observed mean loss of exactly 5 is not an exact expectation/probability. Small-sample eligibility normally remains unresolved. No exact-support theorem, general search optimum, calibrated confidence, arbitrary bonus-plan support or formal training readiness is claimed.

## API example

```csharp
var result = await AnchoredHuntEvaluator.EvaluateAsync(session, new HuntEvaluationOptions
{
    EvaluationSeeds = [101, 102, 103, 104],
    MaxDecisionsPerPolicy = 200,
    MaxPosteriorAttempts = 256,
    HpSafetyFloor = 10,
    FamilywiseAlpha = 0.05,
});
// result.Worlds retains baseline + plan raw facts and traces.
// result.Eligibility means consideration only and may remain Unresolved.
// result.Masks distinguishes usable sample statistics from unpriced utility.
// result.Audit.FormalLabelsAllowed is always false.
```

## Verification

The isolated checkout is based on `f07dc859`; authoritative running-worker binaries were not rebuilt. Focused verification covers real immediate reward, exactly five observed additional HP loss for a next-turn finish, missed pursuit and later-turn fallback, missed next-turn draw/deadline exit, low-HP guard and true death, truncation, original combat-start HP after prior damage, unchanged source, and identical public anchors with different private source seeds. Existing objective and teacher tests are rerun to cover the shared recorder extraction.

Executed on 2026-10-01, all with exit code 0:

- Full `Nosl.Tests`: **866 passed, 0 failed, 0 skipped**, 6m07s
- Focused `AnchoredHuntTests | TeacherTests | ObjectiveTests` after explicit audit-metadata additions: **54 passed**, 1m44s
- Final `AnchoredHuntTests` after hardening late-benefit controller status: **10 passed**, 1m50s. A benefit obtained naturally after the deadline remains an aborted pursuit, even though the raw benefit fact is preserved

The full suite preceded the final localized metadata/controller-status hardening; the affected final code was then rechecked with the focused runs above. The shared ordinary-teacher recorder is unchanged after its successful full/teacher runs.

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~AnchoredHuntTests|FullyQualifiedName~TeacherTests|FullyQualifiedName~ObjectiveTests'
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~AnchoredHuntTests
```

An initial test compile attempted to access an engine-internal reseed method directly and was corrected before the passing runs. The replacement invariance test uses two actual source seeds, verifies different private RNG snapshots but identical public anchors, and verifies identical evaluations for the same independent sampler configuration. No simulator visibility was broadened to enable the test.


## Controller lifecycle audit follow-up

Independent review exposed two controller-only issues after the initial commit: incidental success could overwrite an irreversible abort or baseline role, and a real loss could leave pursuit active. These transitions are corrected without changing raw outcomes, objective calculations, masks or uncertainty. Baseline/incomplete role tags are also preserved consistently.

The exact real regression uses a single TheHunt, enemy HP 5 and player HP 4 with the default safety floor 10. The plan aborts, its baseline fallback immediately earns the reward, all three raw success/benefit/deadline facts remain true, and its final controller remains aborted with the public-HP guard reason. Baseline remains baseline. Additional assertions cover actual terminal loss, no-benefit victory after abort, deadline expiry, truncation and posterior-sampling failure.

Final follow-up verification: **35 passed, 0 failed, 0 skipped**, 2m10s, exit code 0 (12 Hunt integration cases plus 23 existing teacher cases):

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~AnchoredHuntTests|FullyQualifiedName~TeacherTests'
```

The earlier full-suite result remains historical evidence; the follow-up changed only this opt-in evaluator's controller bookkeeping, its regression tests and this document. Raw outcomes and shared ordinary-teacher recording/scoring were not changed.
