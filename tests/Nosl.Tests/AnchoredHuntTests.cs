using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class AnchoredHuntTests
{
    [Fact]
    public async Task ActualHuntFatalAndOfferedCardRewardAreRecordedWithoutInventedUtility()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], EnemyHp: 5));
        string before = PublicJson.Serialize(source.Observe());
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [11, 12] });
        Assert.Equal(before, PublicJson.Serialize(source.Observe())); Assert.Empty(source.Room.GeneratedRewards);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal("baseline", pair.Baseline.Controller.Status);
            Assert.Equal("finished", pair.Plan.Controller.Status);
            foreach (var trajectory in new[] { pair.Baseline, pair.Plan })
            {
                Assert.Equal(TerminalKind.Win, trajectory.Outcome.TerminalKind);
                Assert.True(trajectory.Outcome.SpecifiedFinishSuccess); Assert.True(trajectory.Outcome.EarnedBonus);
                Assert.True(trajectory.Outcome.DeadlineMet); Assert.Equal(1, trajectory.FatalPlayerTurn);
                Assert.Equal(1, trajectory.ActualExtraCardRewardsOffered);
                Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, trajectory.Objective.Status);
                Assert.Contains(trajectory.Outcome.PermanentChanges, x => x.Kind == "earned_extra_reward_opportunity:CardReward");
            }
        });
        Assert.Equal(0, result.MeanExtraNetHpLoss); Assert.False(result.UtilityComparisonMask);
        Assert.Equal(1, result.MeanSpecifiedSuccess); Assert.True(result.Masks.MeanSpecifiedSuccess); Assert.False(result.Masks.UtilityComparison);
        Assert.False(result.Audit.FormalLabelsAllowed); Assert.Equal(Eligibility.Unresolved, result.Eligibility);
        Assert.Equal(.05, result.Audit.FamilywiseAlpha); Assert.Equal(200, result.Audit.MaxDecisionsPerPolicy);
        Assert.True(result.UnconditionalSuccessInterval.Lower < .8);
    }

    [Fact]
    public async Task IncidentalSuccessAfterSafetyAbortPreservesAbortAndBaselineIdentity()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], EnemyHp: 5, Hp: 4));
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [91] });
        var pair = Assert.Single(result.Worlds);
        Assert.Equal("baseline", pair.Baseline.Controller.Status); Assert.Null(pair.Baseline.Controller.ExitReason);
        Assert.Equal("aborted", pair.Plan.Controller.Status); Assert.Equal("public_hp_safety_guard", pair.Plan.Controller.ExitReason);
        Assert.Equal("aborted", Assert.Single(pair.Plan.PublicTrace).Status);
        foreach (var trajectory in new[] { pair.Baseline, pair.Plan })
        {
            Assert.Equal(TerminalKind.Win, trajectory.Outcome.TerminalKind);
            Assert.True(trajectory.Outcome.SpecifiedFinishSuccess); Assert.True(trajectory.Outcome.EarnedBonus);
            Assert.True(trajectory.Outcome.DeadlineMet); Assert.Equal(1, trajectory.FatalPlayerTurn);
            Assert.Equal(1, trajectory.ActualExtraCardRewardsOffered); Assert.Equal(4, trajectory.Outcome.HpAfterSettlement);
            Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, trajectory.Objective.Status);
        }
        Assert.Equal(1, result.MeanSpecifiedSuccess); Assert.Equal(0, result.MeanExtraNetHpLoss);
        Assert.False(result.UtilityComparisonMask);
    }

    private static async Task<CombatSession> HuntInDraw(int hp)
    {
        for (int i = 0; i < 64; i++)
        {
            var source = await CombatSession.CreateAsync(new(Seed: "finite-hunt-hidden-" + i,
                Deck: ["TheHunt", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent"],
                Hp: hp, EnemyHp: 5));
            if (source.Observe().Observation!.UnknownDraw.Any(c => c.Card.Id == "TheHunt")) return source;
            await source.DisposeAsync();
        }
        throw new InvalidOperationException("Fixture could not publicly reach Hunt-in-draw anchor");
    }

    [Fact]
    public async Task ActualNextTurnFinishCostsFiveVersusUnmodifiedNormalBaseline()
    {
        await using var source = await HuntInDraw(60);
        string before = PublicJson.Serialize(source.Observe());
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [21, 22, 23], MaxPosteriorAttempts = 512 });
        Assert.Equal(before, PublicJson.Serialize(source.Observe()));
        Assert.Equal(5, result.MeanExtraNetHpLoss);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal(60, pair.Baseline.Outcome.HpAfterSettlement); Assert.Equal(55, pair.Plan.Outcome.HpAfterSettlement);
            Assert.False(pair.Baseline.Outcome.EarnedBonus); Assert.True(pair.Plan.Outcome.EarnedBonus);
            Assert.Equal(2, pair.Plan.FatalPlayerTurn); Assert.True(pair.Plan.Outcome.DeadlineMet);
            Assert.Equal("end_turn", pair.Plan.PublicTrace[0].Action.Kind);
            Assert.All(pair.Plan.PublicTrace, x => Assert.Equal(2, x.FixedDeadlinePlayerTurn));
        });
        Assert.Equal(Eligibility.Unresolved, result.Eligibility); // An observed mean of exactly five is not an exact expectation.
    }

    [Fact]
    public async Task MissExitsToBaselineAndNeverRenewsDeadlineAcrossLaterTurns()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt", "StrikeSilent", "DefendSilent"], EnemyHp: 40));
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [31], MaxDecisionsPerPolicy = 100 });
        var plan = Assert.Single(result.Worlds).Plan;
        Assert.Equal(TerminalKind.Win, plan.Outcome.TerminalKind);
        Assert.False(plan.Outcome.SpecifiedFinishSuccess); Assert.False(plan.Outcome.EarnedBonus); Assert.False(plan.Outcome.DeadlineMet);
        Assert.Equal(0, plan.ActualExtraCardRewardsOffered);
        Assert.Equal("aborted", plan.Controller.Status);
        Assert.Equal("designated_card_spent_without_finish", plan.Controller.ExitReason);
        Assert.True(plan.PublicTrace.Max(x => x.PlayerTurn) > result.Anchor.DeadlinePlayerTurn);
        Assert.All(plan.PublicTrace, x => Assert.Equal(result.Anchor.StartPlayerTurn + 1, x.FixedDeadlinePlayerTurn));
        Assert.Equal(result.Anchor, plan.Controller.Anchor);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, true)]
    public async Task RealDeathAndPublicSafetyExitRemainDistinct(int hpFloor, bool shouldExit)
    {
        await using var source = await HuntInDraw(4);
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [41], HpSafetyFloor = hpFloor, MaxPosteriorAttempts = 512 });
        var pair = Assert.Single(result.Worlds);
        Assert.Equal(TerminalKind.Win, pair.Baseline.Outcome.TerminalKind);
        if (shouldExit)
        {
            Assert.Equal(TerminalKind.Win, pair.Plan.Outcome.TerminalKind);
            Assert.Equal("public_hp_safety_guard", pair.Plan.Controller.ExitReason);
            Assert.Equal(PublicJson.Serialize(pair.Baseline.PublicTrace[0].Action), PublicJson.Serialize(pair.Plan.PublicTrace[0].Action));
        }
        else
        {
            Assert.Equal(TerminalKind.Loss, pair.Plan.Outcome.TerminalKind); Assert.False(pair.Plan.Outcome.PlayerAlive);
            Assert.False(pair.Plan.Outcome.SpecifiedFinishSuccess); Assert.False(pair.Plan.Outcome.EarnedBonus);
            Assert.Equal(1, result.PlanSummary.Losses); Assert.Equal(0, result.PlanSummary.EngineErrors);
            Assert.Equal("aborted", pair.Plan.Controller.Status); Assert.Equal("terminal_loss", pair.Plan.Controller.ExitReason);
        }
    }

    [Fact]
    public async Task ComputationTruncationKeepsUnknownMassAndBonusLabelsNull()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt", "DefendSilent"], EnemyHp: 50));
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [51, 52], MaxDecisionsPerPolicy = 1 });
        Assert.Null(result.MeanExtraNetHpLoss); Assert.False(result.UtilityComparisonMask);
        Assert.Null(result.MeanSpecifiedSuccess); Assert.False(result.Masks.MeanSpecifiedSuccess); Assert.False(result.Masks.MeanExtraNetHpLoss);
        Assert.Equal(0, result.PlanSummary.Losses); Assert.Equal(2, result.PlanSummary.ComputeTruncated);
        Assert.Equal(new EstimateInterval(0, 1), result.UnconditionalSuccessInterval);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal("baseline", pair.Baseline.Controller.Status);
            Assert.Equal("unresolved", pair.Plan.Controller.Status); Assert.Equal("evaluation_incomplete", pair.Plan.Controller.ExitReason);
            Assert.Equal(TerminalKind.ComputeTruncated, pair.Plan.Outcome.TerminalKind);
            Assert.Null(pair.Plan.Outcome.SpecifiedFinishSuccess); Assert.Null(pair.Plan.Outcome.EarnedBonus); Assert.Null(pair.Plan.Outcome.DeadlineMet);
        });
    }

    [Fact]
    public async Task SamplingFailureRetainsSeparateBaselineAndUnresolvedPlanControllers()
    {
        await using var source = await HuntInDraw(60);
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new()
            { EvaluationSeeds = Enumerable.Range(101, 16).Select(x => (ulong)x).ToArray(), MaxPosteriorAttempts = 1 });
        Assert.Equal(16, result.Worlds.Length);
        Assert.All(result.Worlds, pair => Assert.Equal("baseline", pair.Baseline.Controller.Status));
        var failed = result.Worlds.Where(x => x.Plan.Outcome.TerminalKind == TerminalKind.ComputeTruncated).ToArray();
        Assert.NotEmpty(failed);
        Assert.All(failed, pair =>
        {
            Assert.Equal("unresolved", pair.Plan.Controller.Status);
            Assert.Equal(TerminalKind.ComputeTruncated, pair.Baseline.Outcome.TerminalKind);
            Assert.Null(pair.Plan.Outcome.SpecifiedFinishSuccess); Assert.Null(pair.Baseline.Outcome.SpecifiedFinishSuccess);
            Assert.Equal(result.Anchor.TemplateId, pair.Plan.Outcome.ContinuationPolicyId);
            Assert.Equal(result.Anchor.BaselinePolicyId, pair.Baseline.Outcome.ContinuationPolicyId);
        });
        Assert.Null(result.MeanExtraNetHpLoss); Assert.False(result.Masks.MeanSpecifiedSuccess);
    }

    [Fact]
    public async Task ActualMissedDrawExitsOnTheOriginalDeadlineTurn()
    {
        CombatSession? source = null;
        try
        {
            for (int i = 0; i < 64; i++)
            {
                var candidate = await CombatSession.CreateAsync(new(Seed: "hunt-deadline-fixture-" + i,
                    Deck: new[] { "TheHunt", "StrikeSilent" }.Concat(Enumerable.Repeat("DefendSilent", 13)).ToArray(), EnemyHp: 5));
                var o = candidate.Observe().Observation!;
                if (o.UnknownDraw.Any(x => x.Card.Id == "TheHunt") && o.Hand.Any(x => x.Id == "StrikeSilent"))
                { source = candidate; break; }
                await candidate.DisposeAsync();
            }
            Assert.NotNull(source);
            var result = await AnchoredHuntEvaluator.EvaluateAsync(source!, new()
                { EvaluationSeeds = [71, 72, 73, 74, 75, 76, 77, 78], MaxPosteriorAttempts = 1024 });
            Assert.All(result.Worlds, x => Assert.Equal(TerminalKind.Win, x.Plan.Outcome.TerminalKind));
            var missed = result.Worlds.Where(x => x.Plan.Controller.ExitReason == "deadline_turn_no_legal_hunt_or_draw").ToArray();
            Assert.NotEmpty(missed);
            Assert.All(missed, pair =>
            {
                var exit = pair.Plan.PublicTrace.First(x => x.Status == "aborted");
                Assert.Equal(2, exit.PlayerTurn); Assert.Equal(2, exit.FixedDeadlinePlayerTurn);
                Assert.False(pair.Plan.Outcome.DeadlineMet); Assert.Equal("aborted", pair.Plan.Controller.Status);
                Assert.All(pair.Plan.PublicTrace, x => Assert.Equal(2, x.FixedDeadlinePlayerTurn));
            });
            Assert.Equal(Eligibility.Unresolved, result.Eligibility);
        }
        finally { if (source is not null) await source.DisposeAsync(); }
    }

    [Fact]
    public async Task MidCombatAnchorKeepsOriginalCombatHpReference()
    {
        await using var source = await HuntInDraw(60);
        await source.StepAsync(source.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.Equal(55, source.Observe().Observation!.Hp); Assert.Equal(60, source.StartHp);
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [81], MaxPosteriorAttempts = 512 });
        Assert.Equal(2, result.Anchor.StartPlayerTurn); Assert.Equal(3, result.Anchor.DeadlinePlayerTurn);
        var plan = Assert.Single(result.Worlds).Plan;
        Assert.Equal(60, plan.Outcome.HpAtCombatStart); Assert.Equal(55, plan.Outcome.HpAfterSettlement);
        Assert.Equal(5, plan.Objective.NetHpLoss);
        Assert.True(plan.Outcome.DeadlineMet);
    }

    [Fact]
    public async Task SamePublicAnchorDifferentPrivateSourceSeedsGivesSameEvaluation()
    {
        await using var a = await CombatSession.CreateAsync(new(Seed: "actual-hidden-A", Deck: ["TheHunt"], EnemyHp: 5));
        await using var b = await CombatSession.CreateAsync(new(Seed: "actual-hidden-B", Deck: ["TheHunt"], EnemyHp: 5));
        Assert.NotEqual(PublicJson.Serialize(a.State.RunState.Rng.ToSerializable()), PublicJson.Serialize(b.State.RunState.Rng.ToSerializable()));
        Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
        var options = new HuntEvaluationOptions { EvaluationSeeds = [61, 62] };
        var first = await AnchoredHuntEvaluator.EvaluateAsync(a, options);
        var second = await AnchoredHuntEvaluator.EvaluateAsync(b, options);
        Assert.Equal(PublicJson.Serialize(first), PublicJson.Serialize(second));
    }

    [Fact]
    public async Task UnsupportedHealingScopeAndReusedSeedsAreRejectedWithoutChangingSource()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], Potions: ["FruitJuice"], EnemyHp: 5));
        string before = PublicJson.Serialize(source.Observe());
        await Assert.ThrowsAsync<NotSupportedException>(() => AnchoredHuntEvaluator.EvaluateAsync(source));
        await Assert.ThrowsAsync<ArgumentException>(() => AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [1, 1] }));
        Assert.Equal(before, PublicJson.Serialize(source.Observe()));
    }
}
