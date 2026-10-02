using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class ObjectiveTests
{
    private static RolloutOutcome Win(int hp = 60, int start = 60) => new()
    {
        TerminalKind = TerminalKind.Win, PlayerAlive = true, HpAtCombatStart = start, HpAfterSettlement = hp,
        MaxHpStart = 100, MaxHpAfterSettlement = 100, SettlementComplete = true,
        SettlementProfileId = "test-settled-v1", ContinuationPolicyId = "test-policy-v1",
        InventorySnapshotsComplete = true, PermanentChangesComplete = true
    };
    private static ObjectiveProfile Priced() => ObjectiveProfile.Candidate with
    {
        ValueTableVersion = "explicit-test-only-v1",
        InventoryValues = new Dictionary<string, ResourceValue> { ["FirePotion"] = new(9, "synthetic test-only price, not user calibration") },
        PermanentFutureValues = new Dictionary<string, ResourceValue> { ["max_hp"] = new(2, "synthetic future-only unit value") }
    };
    private static double Cost(RolloutOutcome x, ObjectiveProfile? p = null) => ObjectiveEvaluator.Evaluate(x, p).Cost!.Value;

    [Fact]
    public void ConfirmedRiskExamplesAndFixedCombatStartAnchor()
    {
        double mixture = .9 * Cost(Win()) + .1 * Cost(Win(30));
        Assert.Equal(3.3, mixture, 10);
        Assert.True(mixture < Cost(Win(52)));
        Assert.True(Cost(Win(57)) < mixture);
        Assert.Equal(3.03, Cost(Win(57)), 10);
        Assert.Equal(Cost(Win(57)), Cost(Win(57) with { PlayerTurnsElapsed = 90, AtomicActionsExecuted = 1_000_000 }));
    }
    [Fact]
    public void DamageAndHealingDiagnosticsDoNotDoubleCountTerminalHp()
    {
        var x = Win(65) with { CumulativeHpDamage = 25, HealingReceived = 30, HpEventDiagnosticsComplete = true };
        Assert.Equal(-5, Cost(x));
        Assert.Equal(Cost(x), Cost(x with { CumulativeHpDamage = 0, HealingReceived = 5 }));
    }
    [Fact]
    public void TrueLossOnlyGetsFailurePenaltyAndLosesFutureResources()
    {
        var x = Win(1) with { TerminalKind = TerminalKind.Loss, PlayerAlive = false, HpAfterSettlement = 0,
            InventoryStart = [new("FirePotion", 1)], InventoryEnd = [new("FirePotion", 1)],
            PermanentChanges = [new("max_hp", 2, "test")], MaxHpAfterSettlement = 102 };
        Assert.Equal(1081, Cost(x, Priced()), 10); // 1000 + 60 + 12 + 9; permanent future value is zero on loss.
    }
    [Theory]
    [InlineData(TerminalKind.ComputeTruncated)]
    [InlineData(TerminalKind.EngineError)]
    [InlineData(TerminalKind.PolicyNonterminating)]
    public void IncompleteAndErrorsAreNeverLosses(TerminalKind kind)
    {
        var row = Win() with { TerminalKind = kind, SettlementComplete = false };
        Assert.Equal(EvaluationStatus.Incomplete, ObjectiveEvaluator.Evaluate(row).Status);
        var batch = ObjectiveEvaluator.EvaluateBatch([Win(), row], 2);
        Assert.Null(batch.ExpectedCost); Assert.Null(batch.ExpectedNetHpLoss); Assert.Equal(0, batch.Losses);
        Assert.Equal(.5, batch.CompletionRate); Assert.Equal(new EstimateInterval(0, .5), batch.LossProbabilityBounds);
        Assert.False(batch.FormalLabelsAllowed);
    }
    [Fact]
    public void AssignedWorldConservationAndCombatAnchorEnforced()
    {
        Assert.Throws<ArgumentException>(() => ObjectiveEvaluator.EvaluateBatch([Win()], 2));
        Assert.Throws<ArgumentException>(() => ObjectiveEvaluator.EvaluateBatch([Win(), Win(30, 50)], 2));
    }
    [Fact]
    public void UnchangedUnknownInventoryCancelsButConsumptionIsUnresolved()
    {
        var row = Win() with { InventoryStart = [new("UnknownPotion", 1)], InventoryEnd = [new("UnknownPotion", 1)] };
        Assert.Equal(0, Cost(row));
        var unknown = ObjectiveEvaluator.Evaluate(row with { InventoryEnd = [] });
        Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, unknown.Status); Assert.Null(unknown.Cost);
        Assert.Contains("inventory_value_unresolved:UnknownPotion", unknown.Reasons);
    }
    [Fact]
    public void MissingLedgerDeclarationsFailClosedWithoutMaskingValidHpFacts()
    {
        var x = Win() with { InventorySnapshotsComplete = false, PermanentChangesComplete = false };
        var scored = ObjectiveEvaluator.Evaluate(x);
        Assert.Null(scored.Cost); Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, scored.Status);
        Assert.Contains("inventory_snapshots_incomplete", scored.Reasons);
        Assert.Contains("permanent_change_ledger_incomplete", scored.Reasons);
        var batch = ObjectiveEvaluator.EvaluateBatch([x], 1);
        Assert.Null(batch.ExpectedCost); Assert.Equal(0, batch.ExpectedNetHpLoss);
        Assert.Equal(1, batch.ValueUnresolved); Assert.False(batch.FormalLabelsAllowed);
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win() with { InventoryEnd = null! }).Status);
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win() with { InventoryEnd = [null!] }).Status);
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win() with { ContinuationPolicyId = "" }).Status);
    }
    [Fact]
    public void InventoryIsChargedOnceAndEventsAreOnlyProvenance()
    {
        var row = Win() with { InventoryStart = [new("FirePotion", 1)], InventoryEnd = [],
            ResourceEvents = [new("consumed", "FirePotion", 1, "potion_used")] };
        Assert.Equal(9, Cost(row, Priced()));
        Assert.Equal(0, Cost(row with { InventoryEnd = [new("FirePotion", 1)],
            ResourceEvents = [new("consumed", "FirePotion", 1, "potion_used"), new("generated", "FirePotion", 1, "generated")] }, Priced()));
    }
    [Fact]
    public void MissingPermanentValuesAndMissingMaxHpEventsRemainExplicit()
    {
        var row = Win(61) with { MaxHpAfterSettlement = 101, PermanentChanges = [new("max_hp", 1, "real-max-hp-change")] };
        Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, ObjectiveEvaluator.Evaluate(row).Status);
        Assert.Equal(-3, Cost(row, Priced())); // One immediate HP in terminal HP, plus future-only value 2.
        Assert.Contains("permanent_change_not_recorded:max_hp", ObjectiveEvaluator.Evaluate(row with { PermanentChanges = [] }, Priced()).Reasons);
    }
    [Fact]
    public void CandidateProfileAndUnboundedValuesCannotProduceFormalLabels()
    {
        Assert.Throws<InvalidOperationException>(() => ObjectiveProfile.Candidate.AssertFormalLabelsAllowed());
        Assert.Throws<InvalidOperationException>(() => ObjectiveEvaluator.Evaluate(Win(), formalLabels: true));
        Assert.Throws<InvalidOperationException>(() => (Priced() with { Calibrated = true, CalibrationEvidence = "test" }).AssertFormalLabelsAllowed());
        Assert.Throws<ArgumentException>(() => (Priced() with { MaximumAbsoluteUnitValue = 1 }).Validate());
        var x = Win() with { InventoryStart = [new("FirePotion", 100)] };
        Assert.Contains("inventory_adjustment_exceeds_profile_bound", ObjectiveEvaluator.Evaluate(x, Priced()).Reasons);
    }
    [Fact]
    public void InvalidSettlementCannotBecomeCompleteLabel()
    {
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win() with { SettlementComplete = false }).Status);
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win(0)).Status);
        Assert.Equal(EvaluationStatus.InvalidOutcome, ObjectiveEvaluator.Evaluate(Win() with { PlayerAlive = null }).Status);
        var batch = ObjectiveEvaluator.EvaluateBatch([Win() with { SettlementComplete = false }], 1);
        Assert.Equal(0, batch.Wins); Assert.Equal(1, batch.InvalidOutcomes); Assert.Null(batch.ExpectedCost);
    }
    [Fact]
    public void PotionNineIsInclusiveConsiderationAndRescueCanBeBelowNine()
    {
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.Potion(EstimateInterval.Exact(9)));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.Potion(EstimateInterval.Exact(8.99)));
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.Potion(new(8.9, 9.1)));
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.Potion(EstimateInterval.Exact(1), verifiedRescueNeed: true));
    }
    [Fact]
    public void FiveAndEightyBoundariesAreInclusiveWithUncertaintySeparate()
    {
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.SpecifiedFinish(new(5, 5), new(.8, .8), true));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.SpecifiedFinish(new(5.001, 5.001), new(1, 1), true));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.SpecifiedFinish(new(0, 0), new(.799, .799), true));
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.SpecifiedFinish(new(4.9, 5.1), new(.8, .9), true));
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.SpecifiedFinish(new(4, 4), new(.79, .81), true));
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.SpecifiedFinish(new(4, 4), new(.9, .9), null));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.SpecifiedFinish(new(4, 4), new(.9, .9), false));
    }
    [Fact]
    public void AnchoredPlansNeverRenewBudgetOrMoveDeadline()
    {
        var plan = AnchoredBonusPlan.Begin("public anchor", "specified kill", "finite-template-v1", "robust-baseline-v1", 3);
        var evidence = new WholePlanEvidence("public anchor", "robust-baseline-v1", "finite-template-v1", true, new(5, 5), new(.8, .8), true);
        Assert.Equal(Eligibility.EligibleNotMandatory, plan.Evaluate(evidence));
        var next = plan.Advance(4, false, "public next turn");
        Assert.Equal(3, next.Context.StartPlayerTurn); Assert.Equal(4, next.Context.DeadlinePlayerTurn);
        Assert.Equal("public anchor", next.Context.AnchorPublicSummary);
        Assert.Throws<ArgumentException>(() => next.Evaluate(evidence with { CoversWholePlanFromAnchor = false }));
        Assert.Throws<ArgumentException>(() => next.Evaluate(evidence with { AnchorPublicSummary = "new turn" }));
        Assert.Throws<ArgumentException>(() => next.Evaluate(evidence with { FrozenBaselinePolicyId = "weaker-baseline" }));
        var expired = next.Advance(4, true, "deadline ended");
        Assert.Equal(BonusPlanState.Aborted, expired.Context.State); Assert.Equal(Eligibility.Ineligible, expired.Evaluate(evidence));
        Assert.Throws<InvalidOperationException>(() => expired.Advance(5, false, "restart"));
    }
    [Fact]
    public void SafeFiniteStallHasSeparateDeadlineAndNeedsItsOwnEvidence()
    {
        var plan = AnchoredBonusPlan.Begin("anchor", "heal", "finite-heal-v1", "base", 1, BonusPlanKind.SafeFiniteStall, 5);
        Assert.Equal(5, plan.Context.DeadlinePlayerTurn);
        Assert.Equal(Eligibility.Unresolved, plan.Evaluate(new("anchor", "base", "finite-heal-v1", true, new(0, 0), new(1, 1), true)));
        Assert.Throws<ArgumentException>(() => AnchoredBonusPlan.Begin("anchor", "kill", "template", "base", 1, finiteStallDeadline: 5));
    }
    [Fact]
    public void FixedSampleIntervalsDoNotTreatPointEightAsAnExactProbability()
    {
        var good = Win() with { SpecifiedFinishSuccess = true, EarnedBonus = true, DeadlineMet = true };
        var bad = good with { TerminalKind = TerminalKind.Loss, PlayerAlive = false, HpAfterSettlement = 0 };
        var rows = Enumerable.Repeat(good, 80).Concat(Enumerable.Repeat(bad, 20)).ToArray();
        var interval = FixedSampleEstimates.SpecifiedSuccess(rows, 100);
        Assert.True(interval.Lower < .8); Assert.True(interval.Upper > .8);
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.SpecifiedFinish(new(5, 5), interval, true));
        Assert.Throws<ArgumentException>(() => FixedSampleEstimates.SpecifiedSuccess(rows, 101));
        var unknown = FixedSampleEstimates.SpecifiedSuccess([good with { TerminalKind = TerminalKind.ComputeTruncated, SettlementComplete = false }], 1);
        Assert.Equal(new EstimateInterval(0, 1), unknown);
        Assert.Throws<ArgumentException>(() => FixedSampleEstimates.Mean([6.0], 1, -5, 5));
        Assert.Equal(new EstimateInterval(0, 1), FixedSampleEstimates.SpecifiedSuccess([good with { HpAfterSettlement = 0 }], 1));
    }

    private static RolloutOutcome FromFacts(TerminalFacts f, string?[] startPotions) => new()
    {
        TerminalKind = f.Result == "win" ? TerminalKind.Win : f.Result == "loss" ? TerminalKind.Loss : throw new ArgumentException("Not terminal"),
        PlayerAlive = f.FinalHp > 0, HpAtCombatStart = f.StartHp, HpAfterSettlement = f.FinalHp,
        InventorySnapshotsComplete = true, PermanentChangesComplete = true,
        MaxHpStart = f.StartMaxHp, MaxHpAfterSettlement = f.FinalMaxHp,
        InventoryStart = startPotions.Where(x => x is not null).GroupBy(x => x!).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray(),
        InventoryEnd = f.Potions.Where(x => x is not null).GroupBy(x => x!).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray(),
        PermanentChanges = f.FinalMaxHp == f.StartMaxHp ? [] : [new("max_hp", f.FinalMaxHp - f.StartMaxHp, "real_settlement")],
        SettlementComplete = true, SettlementProfileId = f.Boundary, ContinuationPolicyId = "test-real-fixed-continuation"
    };
    private static PublicAction Potion(CombatSession s) => s.Observe().Actions.Single(x => x.Kind == "potion");
    private static PublicAction End(CombatSession s) => s.Observe().Actions.Single(x => x.Kind == "end_turn");

    [Fact]
    public async Task RealSimulator_FiveHpWholePlanDifferenceAndSamePotionTimingCancel()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["DefendSilent"], Potions: ["FirePotion"], Hp: 60, EnemyHp: 10));
        string?[] start = source.Observe().Observation!.Potions;
        await using var baseline = source.ForkExact(); await using var delayed = source.ForkExact();
        await baseline.StepAsync(Potion(baseline));
        await delayed.StepAsync(End(delayed)); await delayed.StepAsync(Potion(delayed));
        var a = FromFacts(await baseline.SettleAsync(), start); var b = FromFacts(await delayed.SettleAsync(), start);
        Assert.Equal(TerminalKind.Win, a.TerminalKind); Assert.Equal(TerminalKind.Win, b.TerminalKind);
        Assert.Equal(5, a.HpAfterSettlement!.Value - b.HpAfterSettlement!.Value);
        Assert.Equal(9, ObjectiveEvaluator.Evaluate(a, Priced()).InventoryAdjustment);
        Assert.Equal(9, ObjectiveEvaluator.Evaluate(b, Priced()).InventoryAdjustment);
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.SpecifiedFinish(new(5, 5), new(1, 1), true));
        Assert.Equal(60, source.Observe().Observation!.Hp);
    }
    [Fact]
    public async Task RealSimulator_AutomaticHealingAndMaxHpRemainSeparateFromFutureValue()
    {
        await using var s = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent"], Potions: ["FirePotion"],
            Relics: ["MeatOnTheBone", "ChosenCheese"], Hp: 38, MaxHp: 100, EnemyHp: 10));
        string?[] start = s.Observe().Observation!.Potions;
        await s.StepAsync(Potion(s)); var outcome = FromFacts(await s.SettleAsync(), start);
        Assert.Equal(51, outcome.HpAfterSettlement); Assert.Equal(101, outcome.MaxHpAfterSettlement);
        Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, ObjectiveEvaluator.Evaluate(outcome).Status);
        Assert.Equal(-6, Cost(outcome, Priced())); // -13 net HP + 9 inventory - 2 future-only max HP.
        Assert.Equal(Cost(outcome, Priced()), Cost(FromFacts(await s.SettleAsync(), start), Priced()));
    }
    [Fact]
    public async Task RealSimulator_RescueBelowNineIsConsiderationAndActualLossIsDistinct()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["DefendSilent"], Potions: ["FirePotion"], Hp: 1, EnemyHp: 10));
        string?[] start = source.Observe().Observation!.Potions;
        await using var save = source.ForkExact(); await using var die = source.ForkExact();
        await save.StepAsync(Potion(save)); await die.StepAsync(End(die));
        var win = FromFacts(await save.SettleAsync(), start); var loss = FromFacts(await die.SettleAsync(), start);
        Assert.Equal(TerminalKind.Win, win.TerminalKind); Assert.Equal(TerminalKind.Loss, loss.TerminalKind);
        Assert.Equal(1, win.HpAfterSettlement!.Value - loss.HpAfterSettlement!.Value);
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.Potion(new(1, 1), verifiedRescueNeed: true));
        Assert.True(Cost(win, Priced()) < Cost(loss, Priced()));
    }
}
