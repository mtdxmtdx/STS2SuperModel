using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;

namespace Nosl.Tests;

public sealed class TeacherTests
{
    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task CompleteTeacherIsInvariantToTrueHiddenReplacement(string mode)
    {
        await using var source = await CombatSession.CreateAsync(new(Enemy: "Nibbit", EnemyHp: 35));
        await using var replaced = source.ForkExact();
        var pile = replaced.State.Players[0].PlayerCombatState!.DrawPile;
        var reverse = pile.Cards.Reverse().ToArray();
        foreach (var card in pile.Cards.ToArray()) pile.RemoveInternal(card);
        foreach (var card in reverse) pile.AddInternal(card);
        replaced.ReseedFuture(999991);
        string unchanged = PublicJson.Serialize(source.Observe());
        var options = new TeacherOptions { Mode = mode, ExplorationSeeds = mode == "T1" ? [2, 7, 9] : [], EvaluationSeeds = [30, 31], TreeDepth = 3 };
        var a = await CombatTeacher.EvaluateAsync(source, options);
        var b = await CombatTeacher.EvaluateAsync(replaced, options);
        Assert.Equal(PublicJson.Serialize(a.Candidates), PublicJson.Serialize(b.Candidates));
        Assert.Equal(a.ContinuationVersion, b.ContinuationVersion);
        Assert.Equal(unchanged, PublicJson.Serialize(source.Observe()));
        Assert.Equal(source.Observe().Actions.Length, a.Candidates.Length);
        Assert.All(a.Candidates, c =>
        {
            Assert.Equal(2, c.Outcomes.Length);
            Assert.All(c.Outcomes, o => Assert.True(o.IsTrueTerminal, o.Detail));
            Assert.Equal(0, c.Evaluation.EngineErrors);
            Assert.NotNull(c.Evaluation.ExpectedCost);
        });
        if (mode == "T1") Assert.True(a.FrozenTreeNodes > 0);
    }

    [Fact]
    public void FrozenTreeCannotDifferentiateWorldsBeforePublicInformationChanges()
    {
        var card = new PublicCard("StrikeSilent", 0, 1, 0, "Attack", []);
        var observation = new PublicObservation("nosl.public.v1", 60, 10, 1, 60, 70, 0, 3, 0,
            [card], [], [], [new(card, 1)], [], 1, [], [], [], [], [], null);
        var packet = new DecisionPacket("player_decision", observation, [new(0, "play", 0, 0), new(0, "end_turn")]);
        var policy = new FrozenPublicTreePolicy("test", new Dictionary<string, PublicAction>
        { [FrozenPublicTreePolicy.InformationKey(packet)] = packet.Actions[1] });
        Assert.Equal(packet.Actions[1], policy.Choose(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet))));
        var afterDraw = packet with { Observation = observation with { Turn = 2, History = [new("draw", PublicJson.Serialize(card))] } };
        Assert.NotEqual(FrozenPublicTreePolicy.InformationKey(packet), FrozenPublicTreePolicy.InformationKey(afterDraw));
        Assert.Equal(packet.Actions[0], policy.Choose(afterDraw));
    }

    [Fact]
    public async Task BudgetExhaustionRetainsEveryWorldAndMasksEveryIncompleteTarget()
    {
        await using var source = await CombatSession.CreateAsync(new(EnemyHp: 1000));
        var options = new TeacherOptions { EvaluationSeeds = [1, 2], MaxDecisions = 1 };
        var result = await CombatTeacher.EvaluateAsync(source, options);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(2, c.Evaluation.ComputeTruncated); Assert.Null(c.Evaluation.ExpectedCost);
            Assert.All(c.Outcomes, o => Assert.Equal(TerminalKind.ComputeTruncated, o.TerminalKind));
        });
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "r", "c", "b", [1, 2], [])));
        Assert.Equal(BeliefSampler.ExchangeableProfile, result.Scope);
        Assert.Equal(result.Scope, record.RootElement.GetProperty("audit_only").GetProperty("posterior_profile").GetString());
        Assert.Equal(BeliefSampler.ImplementationVersion, record.RootElement.GetProperty("audit_only").GetProperty("versions").GetProperty("posterior_implementation").GetString());
        Assert.False(record.RootElement.GetProperty("public_input").TryGetProperty("posterior_profile", out _));
        foreach (var action in record.RootElement.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, action.GetProperty("value").ValueKind);
            Assert.False(action.GetProperty("masks").GetProperty("win_probability").GetBoolean());
            Assert.Equal(2, action.GetProperty("truncated_worlds").GetInt32());
        }
        Assert.Empty(record.RootElement.GetProperty("targets").GetProperty("pairwise").EnumerateArray());
    }

    [Fact]
    public async Task ExplorationEvaluationAndProductionGatesFailClosed()
    {
        await using var source = await CombatSession.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => CombatTeacher.EvaluateAsync(source,
            new() { Mode = "T1", ExplorationSeeds = [1], EvaluationSeeds = [1] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatTeacher.EvaluateAsync(source,
            new() { FormalLabels = true }));
        var survivor = source.Observe().Actions.FirstOrDefault(a => a.Kind == "play" && source.Observe().Observation!.Hand[a.Slot].Id == "Survivor");
        if (survivor is not null)
        {
            await source.StepAsync(survivor);
            if (source.HasPendingChoice) await Assert.ThrowsAsync<NotSupportedException>(() => CombatTeacher.EvaluateAsync(source));
        }
    }

    [Fact]
    public async Task TeacherCapturesAutomaticHealingAndUnresolvedPermanentValue()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent"], EnemyHp: 5, Hp: 30, MaxHp: 100,
            Relics: ["MeatOnTheBone", "ChosenCheese"]));
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [11] });
        var strike = result.Candidates.Single(c => c.Action.Kind == "play");
        var outcome = Assert.Single(strike.Outcomes);
        Assert.Equal(43, outcome.HpAfterSettlement); Assert.Equal(101, outcome.MaxHpAfterSettlement);
        Assert.Contains(outcome.PermanentChanges, p => p.Kind == "max_hp" && p.Amount == 1);
        Assert.Null(strike.Evaluation.ExpectedCost);
        Assert.Equal(1, strike.Evaluation.ValueUnresolved);
    }

    [Fact]
    public async Task CombatBoundaryPrecedesAutomaticSetupEffectsWithoutDroppingThem()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent", "DefendSilent"], Relics: ["BeltBuckle"], EnemyHp: 10));
        var observation = source.Observe().Observation!;
        Assert.Equal(new PublicEvent("combat_started", "Silent:A10"), observation.History[0]);
        Assert.Single(observation.History, e => e.Kind == "combat_started");
        Assert.Contains(observation.History, e => e.Kind == "power_changed");
        await using var branch = await source.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
        Assert.Single(branch.Observe().Observation!.History, e => e.Kind == "combat_started");
    }

    [Fact]
    public async Task AcquisitionPrecedesFixedCombatAnchorAndIsNotCombatHistory()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent", "DefendSilent"], Relics: ["BiiigHug"], EnemyHp: 10));
        var observation = source.Observe().Observation!;
        Assert.Empty(source.InitialAssets.Deck); // Obtaining BiiigHug removes the two declared setup cards.
        Assert.Equal(new PublicEvent("combat_started", "Silent:A10"), observation.History[0]);
        Assert.Single(observation.History, e => e.Kind == "combat_started");
        Assert.DoesNotContain(observation.History, e => e.Kind == "automatic_selection");
        await using var branch = await source.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
    }

    [Fact]
    public async Task EarnedExtraRewardOpportunityIsRecordedAndNeverPricedAsZero()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt", "StrikeSilent"], EnemyHp: 5));
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [11], MaxPosteriorAttempts = 256 });
        var hunt = result.Candidates.Single(c => c.Action.Kind == "play" && result.PublicRoot.Observation!.Hand[c.Action.Slot].Id == "TheHunt");
        var outcome = Assert.Single(hunt.Outcomes);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.Contains(outcome.PermanentChanges, c => c.Kind == "earned_extra_reward_opportunity:CardReward" && c.Amount == 1);
        Assert.Null(hunt.Evaluation.ExpectedCost);
        Assert.Equal(1, hunt.Evaluation.ValueUnresolved);
        Assert.NotNull(outcome.PersistentAssetsAtStartJson);
        Assert.NotNull(outcome.PersistentAssetsAfterSettlementJson);
    }

    [Fact]
    public async Task PublicChoiceRootUsesIndependentNativeReplayAndKeepsAllCandidates()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Survivor", "StrikeSilent", "DefendSilent"], EnemyHp: 15));
        var initial = source.Observe();
        await source.StepAsync(initial.Actions.Single(a => a.Kind == "play" && initial.Observation!.Hand[a.Slot].Id == "Survivor"));
        Assert.Equal("card_choice", source.Observe().Status);
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [17], MaxPosteriorAttempts = 256 });
        Assert.Equal(source.Observe().Actions.Length, result.Candidates.Length);
        Assert.All(result.Candidates, c => Assert.Single(c.Outcomes));
        Assert.All(result.Candidates, c => Assert.True(c.Outcomes[0].IsTrueTerminal, c.Outcomes[0].Detail));
        Assert.True(source.HasPendingChoice);
    }

    [Fact]
    public void RankingUsesFixedPairedMeanBoundsAndNeverInventsEquivalence()
    {
        RolloutOutcome Outcome(bool win) => new() { TerminalKind = win ? TerminalKind.Win : TerminalKind.Loss,
            PlayerAlive = win, HpAtCombatStart = 60, HpAfterSettlement = win ? 60 : 0,
            MaxHpStart = 70, MaxHpAfterSettlement = 70, SettlementComplete = true,
            SettlementProfileId = "verified-test-boundary", ContinuationPolicyId = "fixed-test", InventorySnapshotsComplete = true, PermanentChangesComplete = true };
        var win = Enumerable.Range(0, 128).Select(_ => Outcome(true)).ToArray();
        var loss = Enumerable.Range(0, 128).Select(_ => Outcome(false)).ToArray();
        var profile = ObjectiveProfile.Candidate;
        TeacherCandidate Candidate(int slot, RolloutOutcome[] outcomes) => new(new(0, "play", slot, 0), outcomes,
            ObjectiveEvaluator.EvaluateBatch(outcomes, outcomes.Length, profile));
        var ranked = TeacherRanking.Evaluate([Candidate(0, win), Candidate(1, loss)], profile, -10, 1100);
        var pair = Assert.Single(ranked.Pairs); Assert.Equal(0, pair.Preferred); Assert.Equal(1, pair.Other);
        Assert.True(Assert.Single(ranked.Intervals).Upper < 0);
        Assert.Empty(TeacherRanking.Evaluate([Candidate(0, win), Candidate(1, win)], profile, -10, 1100).Pairs);
        Assert.Empty(TeacherRanking.Evaluate([Candidate(0, win), Candidate(1, loss)], profile).Pairs);
        Assert.Throws<InvalidOperationException>(() => TeacherRanking.Evaluate([Candidate(0, win), Candidate(1, loss)], profile, 0, 100));
    }

    [Theory]
    [InlineData("CloakAndDagger")]
    [InlineData("DaggerThrow")]
    [InlineData("Dash")]
    [InlineData("LegSweep")]
    [InlineData("Blur")]
    [InlineData("DodgeAndRoll")]
    [InlineData("BladeDance")]
    [InlineData("PoisonedStab")]
    [InlineData("Slice")]
    [InlineData("NoxiousFumes")]
    [InlineData("DaggerSpray")]
    [InlineData("Footwork")]
    public async Task ExpandedPublicMechanismsCompleteAndPreserveBeliefIsolation(string id)
    {
        foreach (string card in new[] { id, id + "+" })
        {
            await using var source = await CombatSession.CreateAsync(new(Deck: [card, "StrikeSilent", "DefendSilent", "Neutralize"], EnemyHp: 25));
            await using var clone = BeliefSampler.SampleWorld(source, 33);
            Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(clone.Observe()));
            var action = source.Observe().Actions.First(a => a.Kind == "play" && source.Observe().Observation!.Hand[a.Slot].Id == id);
            var packet = await clone.StepAsync(action);
            var policy = new PublicRulePolicy(); int steps = 1;
            while (packet.Status is "player_decision" or "card_choice" && steps++ < 100) packet = await clone.StepAsync(policy.Choose(packet));
            Assert.Equal("terminal_settled", packet.Status);
            Assert.Contains((await clone.SettleAsync()).Result, new[] { "win", "loss" });
            Assert.Equal(70, source.Observe().Observation!.Hp);
        }
    }
}
