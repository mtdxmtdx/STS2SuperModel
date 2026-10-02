using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Xunit.Abstractions;

namespace Nosl.Tests;

/// <summary>Bounded execution evidence, not a loop detector or a macro implementation.</summary>
public sealed class FiniteLoopTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("FlashOfSteel")]
    [InlineData("FlashOfSteel+")]
    public async Task NativeAttackDrawCycleTerminatesAndExpandedReplayPreservesEveryAtomicAction(string card)
    {
        var setup = new Scenario(Seed: "finite-attack-loop", Deck: [card, card], EnemyHp: 65);
        await using var source = await CombatSession.CreateAsync(setup);
        string original = PublicJson.Serialize(source.Observe());
        string originalRng = PublicJson.Serialize(source.State.RunState.Rng.ToSerializable());
        await using var executed = await source.ForkForContinuationAsync();
        await using var replay = await source.ForkForContinuationAsync();
        var actions = new List<PublicAction>();
        var observations = new List<string> { PublicJson.Serialize(executed.Observe()) };
        var physicalPlays = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var policy = new PublicRulePolicy();
        var packet = executed.Observe();
        int energy = packet.Observation!.Energy;
        for (int step = 0; step < 64 && packet.Status == "player_decision"; step++)
        {
            Assert.Equal(1, packet.Observation!.Turn);
            Assert.Equal(energy, packet.Observation.Energy);
            var action = policy.Choose(packet); // Policy sees only the public packet.
            Assert.Equal("play", action.Kind);
            Assert.Equal("FlashOfSteel", packet.Observation.Hand[action.Slot].Id);
            Assert.Equal(0, packet.Observation.Hand[action.Slot].Cost);
            // Physical references are test-only execution audit, never policy inputs.
            object physical = executed.State.Players[0].PlayerCombatState!.Hand.Cards[action.Slot];
            physicalPlays[physical] = physicalPlays.GetValueOrDefault(physical) + 1;
            actions.Add(action);
            packet = await executed.StepAsync(action);
            observations.Add(PublicJson.Serialize(packet));
        }
        Assert.Equal("terminal_settled", packet.Status);
        Assert.True(actions.Count > setup.Deck!.Length);
        Assert.Equal(2, physicalPlays.Count);
        Assert.All(physicalPlays.Values, count => Assert.True(count > 1));
        var facts = await executed.SettleAsync();
        Assert.Equal("win", facts.Result);
        Assert.Equal(facts.StartHp, facts.FinalHp);
        Assert.Equal(0, facts.RewardSelectionsMade);
        Assert.Equal("AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION", facts.Boundary);
        Assert.Same(facts, await executed.SettleAsync());
        Assert.True(facts.Events.Count(e => e.Kind == "shuffle") > 1);
        Assert.True(facts.Events.Count(e => e.Kind == "draw") > setup.Deck.Length);
        Assert.Equal(actions.Select(PublicJson.Serialize), facts.Events.Where(e => e.Kind == "action").Select(e => e.Detail));
        Assert.Equal(actions.Count, facts.Events.Count(e => e.Kind == "card_played"));
        Assert.DoesNotContain(facts.Events, e => e.Kind == "player_turn_ended");

        // This is an explicit atomic transcript replay, NOT compressed macro expansion.
        Assert.Equal(observations[0], PublicJson.Serialize(replay.Observe()));
        for (int i = 0; i < actions.Count; i++)
            Assert.Equal(observations[i + 1], PublicJson.Serialize(await replay.StepAsync(actions[i])));
        Assert.Equal(PublicJson.Serialize(facts), PublicJson.Serialize(await replay.SettleAsync()));
        Assert.Equal(PublicJson.Serialize(executed.State.RunState.Rng.ToSerializable()), PublicJson.Serialize(replay.State.RunState.Rng.ToSerializable()));
        Assert.Equal(PublicJson.Serialize(executed.State.Players[0].PlayerRng.ToSerializable()), PublicJson.Serialize(replay.State.Players[0].PlayerRng.ToSerializable()));
        Assert.Equal(PublicJson.Serialize(executed.FinalAssets), PublicJson.Serialize(replay.FinalAssets));

        // Two actually executed wins with different action counts have identical utility.
        await using var shortFight = await CombatSession.CreateAsync(setup with { EnemyHp = 1 });
        await shortFight.StepAsync(policy.Choose(shortFight.Observe()));
        var shortFacts = await shortFight.SettleAsync();
        var longOutcome = RolloutRecorder.Settled(executed, facts, policy.Id, 1);
        var shortOutcome = RolloutRecorder.Settled(shortFight, shortFacts, policy.Id, 1);
        Assert.True(longOutcome.AtomicActionsExecuted > shortOutcome.AtomicActionsExecuted);
        var longValue = ObjectiveEvaluator.Evaluate(longOutcome);
        var shortValue = ObjectiveEvaluator.Evaluate(shortOutcome);
        Assert.Equal(EvaluationStatus.Scored, longValue.Status);
        Assert.Equal(EvaluationStatus.Scored, shortValue.Status);
        Assert.Equal(shortValue.Cost, longValue.Cost);
        Assert.Equal(0d, longValue.Cost);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Equal(originalRng, PublicJson.Serialize(source.State.RunState.Rng.ToSerializable()));
        Assert.Empty(source.Room.GeneratedRewards);
        output.WriteLine(PublicJson.Serialize(new { card, actions = actions.Count, shortActions = shortOutcome.AtomicActionsExecuted,
            draws = facts.Events.Count(e => e.Kind == "draw"), shuffles = facts.Events.Count(e => e.Kind == "shuffle"),
            facts.Result, facts.FinalHp, utilityCost = longValue.Cost, exactAtomicReplay = true }));
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task TeacherKeepsAllCandidatesAndIndependentWorldsForFiniteAttackCycle(string mode)
    {
        var setup = new Scenario(Seed: "finite-teacher-a", Deck: ["FlashOfSteel", "FlashOfSteel"], EnemyHp: 65);
        await using var source = await CombatSession.CreateAsync(setup);
        await using var replaced = await CombatSession.CreateAsync(setup with { Seed = "finite-teacher-b" });
        string original = PublicJson.Serialize(source.Observe());
        string originalRng = PublicJson.Serialize(source.State.RunState.Rng.ToSerializable());
        string replacedRng = PublicJson.Serialize(replaced.State.RunState.Rng.ToSerializable());
        Assert.Equal(original, PublicJson.Serialize(replaced.Observe()));
        Assert.NotEqual(originalRng, replacedRng);
        var options = new TeacherOptions { Mode = mode, EvaluationSeeds = [101, 102],
            ExplorationSeeds = mode == "T1" ? [7] : [], TreeDepth = 2, MaxDecisions = 64 };
        var result = await CombatTeacher.EvaluateAsync(source, options);
        var replacementResult = await CombatTeacher.EvaluateAsync(replaced, options);
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, result.Scope);
        Assert.Equal(PublicJson.Serialize(result.Candidates), PublicJson.Serialize(replacementResult.Candidates));
        Assert.Equal(result.ContinuationVersion, replacementResult.ContinuationVersion);
        Assert.Equal(source.Observe().Actions.Select(PublicJson.Serialize), result.Candidates.Select(c => PublicJson.Serialize(c.Action)));
        Assert.Equal(3, result.Candidates.Length); // Both physical root cards, plus end turn.
        Assert.Equal(6, result.Costs.WorldsAllocated);
        Assert.Equal(6, result.Costs.WorldsCompleted);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(2, c.Outcomes.Length);
            Assert.Equal(2, c.Evaluation.AssignedWorlds);
            Assert.Equal(2, c.Evaluation.Wins);
            Assert.Equal(1d, c.Evaluation.CompletionRate);
            Assert.Equal(0, c.Evaluation.Losses + c.Evaluation.EngineErrors + c.Evaluation.ComputeTruncated + c.Evaluation.PolicyNonterminating);
            Assert.NotNull(c.Evaluation.ExpectedCost);
            Assert.False(c.Evaluation.FormalLabelsAllowed);
            Assert.All(c.Outcomes, o =>
            {
                Assert.Equal(TerminalKind.Win, o.TerminalKind);
                Assert.True(o.SettlementComplete);
                Assert.True(o.AtomicActionsExecuted > setup.Deck!.Length);
                Assert.Equal("AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION", o.SettlementProfileId);
            });
        });
        if (mode == "T0")
        {
            // Re-execute every sampled world/action with the public continuation. Compare
            // the shared settled recorder's whole raw outcome, not a hand-coded reward.
            for (int worldIndex = 0; worldIndex < options.EvaluationSeeds.Length; worldIndex++)
            {
                await using var world = await BeliefSampler.SampleWorldAsync(source, options.EvaluationSeeds[worldIndex]);
                foreach (var candidate in result.Candidates)
                {
                    await using var branch = await world.ForkForContinuationAsync();
                    var policy = new PublicRulePolicy();
                    var packet = await branch.StepAsync(candidate.Action);
                    int lastTurn = 1;
                    for (int count = 1; count < options.MaxDecisions && packet.Status == "player_decision"; count++)
                    {
                        lastTurn = packet.Observation!.Turn;
                        packet = await branch.StepAsync(policy.Choose(packet));
                    }
                    Assert.Equal("terminal_settled", packet.Status);
                    var expanded = RolloutRecorder.Settled(branch, await branch.SettleAsync(), policy.Id, lastTurn);
                    Assert.Equal(PublicJson.Serialize(candidate.Outcomes[worldIndex]), PublicJson.Serialize(expanded));
                }
            }
            var insufficient = await CombatTeacher.EvaluateAsync(source, options with { MaxDecisions = 2 });
            AssertAllWorldsUnresolved(insufficient, options.EvaluationSeeds, 2);
        }
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Equal(originalRng, PublicJson.Serialize(source.State.RunState.Rng.ToSerializable()));
        Assert.Equal(original, PublicJson.Serialize(replaced.Observe()));
        Assert.Equal(replacedRng, PublicJson.Serialize(replaced.State.RunState.Rng.ToSerializable()));
        output.WriteLine(PublicJson.Serialize(new { mode, result.Scope, result.Costs.RootCandidates,
            result.Costs.WorldsAllocated, result.Costs.WorldsCompleted, result.Costs.RolloutDecisions,
            actionsPerCandidate = result.Candidates.Select(c => c.Outcomes.Select(o => o.AtomicActionsExecuted).ToArray()).ToArray(),
            costs = result.Candidates.Select(c => c.Evaluation.ExpectedCost).ToArray(), sourceSeedIndependent = true }));
    }

    [Theory]
    [InlineData("Impatience")]
    [InlineData("Finesse")]
    public async Task DrawOrDefensiveCycleRemainsUnresolvedAtComputeBudgetRatherThanBecomingDefeat(string card)
    {
        const int budget = 24;
        await using var source = await CombatSession.CreateAsync(new(Seed: "finite-nonprogress", Deck: [card, card], EnemyHp: 65));
        string original = PublicJson.Serialize(source.Observe());
        string originalRng = PublicJson.Serialize(source.State.RunState.Rng.ToSerializable());
        await using var branch = await source.ForkForContinuationAsync();
        var policy = new PublicRulePolicy();
        var initial = branch.Observe().Observation!;
        var packet = branch.Observe();
        var actions = new List<PublicAction>();
        for (int i = 0; i < budget; i++)
        {
            Assert.Equal("player_decision", packet.Status);
            var action = policy.Choose(packet);
            Assert.Equal("play", action.Kind);
            Assert.Equal(card, packet.Observation!.Hand[action.Slot].Id);
            actions.Add(action);
            packet = await branch.StepAsync(action);
        }
        Assert.Equal("player_decision", packet.Status);
        var final = packet.Observation!;
        Assert.Equal(initial.Turn, final.Turn);
        Assert.Equal(initial.Hp, final.Hp);
        Assert.Equal(initial.Energy, final.Energy);
        Assert.Equal(PublicJson.Serialize(initial.Enemies), PublicJson.Serialize(final.Enemies));
        Assert.Equal(budget, final.History.Count(e => e.Kind == "action"));
        Assert.Equal(actions.Select(PublicJson.Serialize), final.History.Where(e => e.Kind == "action").Select(e => e.Detail));
        Assert.True(final.History.Count(e => e.Kind == "shuffle") > initial.History.Count(e => e.Kind == "shuffle"));
        Assert.True(final.Counters!.CardsPlayedCombat > initial.Counters!.CardsPlayedCombat);
        Assert.NotEqual(PublicJson.Serialize(initial), PublicJson.Serialize(final)); // History/counters are semantic state too.
        Assert.Contains(packet.Actions, a => a.Kind == "end_turn"); // An exit still exists; no nontermination proof.
        await Assert.ThrowsAsync<InvalidOperationException>(() => branch.SettleAsync());
        if (card == "Impatience") Assert.Equal(initial.Block, final.Block);
        else
        {
            Assert.True(final.Block > initial.Block);
            Assert.True(final.Block > final.Enemies.Sum(e => e.Intents.Sum(i => (i.Damage ?? 0) * (i.Repeats ?? 1))));
        }

        ulong[] seeds = [101, 102];
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = seeds, MaxDecisions = budget });
        AssertAllWorldsUnresolved(result, seeds, budget);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Equal(originalRng, PublicJson.Serialize(source.State.RunState.Rng.ToSerializable()));
        Assert.Empty(source.Room.GeneratedRewards);
        output.WriteLine(PublicJson.Serialize(new { card, atomicActions = budget, final.Hp, enemyHp = final.Enemies.Single().Hp,
            final.Block, final.Turn, final.Energy, result.Costs.WorldsAllocated, result.Costs.WorldsCompleted,
            unresolvedWorlds = result.Candidates.Sum(c => c.Evaluation.ComputeTruncated), terminalStatus = packet.Status }));
    }

    private static void AssertAllWorldsUnresolved(TeacherResult result, ulong[] seeds, int budget)
    {
        Assert.Equal(result.PublicRoot.Actions.Length, result.Candidates.Length);
        Assert.Equal(result.Candidates.Length * seeds.Length, result.Costs.WorldsAllocated);
        Assert.Equal(0, result.Costs.WorldsCompleted);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(seeds.Length, candidate.Outcomes.Length);
            Assert.Equal(seeds.Length, candidate.Evaluation.ComputeTruncated);
            Assert.Equal(0, candidate.Evaluation.Wins + candidate.Evaluation.Losses + candidate.Evaluation.EngineErrors + candidate.Evaluation.PolicyNonterminating);
            Assert.Equal(0d, candidate.Evaluation.CompletionRate);
            Assert.Null(candidate.Evaluation.ExpectedCost);
            Assert.Null(candidate.Evaluation.ExpectedNetHpLoss);
            Assert.Equal(new EstimateInterval(0, 1), candidate.Evaluation.WinProbabilityBounds);
            Assert.Equal(new EstimateInterval(0, 1), candidate.Evaluation.LossProbabilityBounds);
            Assert.All(candidate.Outcomes, outcome =>
            {
                Assert.Equal(TerminalKind.ComputeTruncated, outcome.TerminalKind);
                Assert.Equal(budget, outcome.AtomicActionsExecuted);
                Assert.False(outcome.IsTrueTerminal);
                Assert.False(outcome.SettlementComplete);
                Assert.Null(outcome.PlayerAlive);
                Assert.Null(outcome.HpAfterSettlement);
                Assert.Equal(EvaluationStatus.Incomplete, ObjectiveEvaluator.Evaluate(outcome).Status);
            });
        });
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "finite-loop-run", "finite-loop-combat", "finite-loop-branch", seeds, [])));
        foreach (var action in record.RootElement.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.Equal(seeds.Length, action.GetProperty("allocated_worlds").GetInt32());
            Assert.Equal(seeds.Length, action.GetProperty("truncated_worlds").GetInt32());
            Assert.Equal(0, action.GetProperty("completed_worlds").GetInt32());
            Assert.Equal("unresolved", action.GetProperty("quality").GetString());
            Assert.Equal(JsonValueKind.Null, action.GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.Null, action.GetProperty("win_probability").ValueKind);
            Assert.All(action.GetProperty("masks").EnumerateObject(), mask => Assert.False(mask.Value.GetBoolean()));
        }
        Assert.Empty(record.RootElement.GetProperty("targets").GetProperty("pairwise").EnumerateArray());
        Assert.Empty(record.RootElement.GetProperty("targets").GetProperty("equivalent_action_set").EnumerateArray());
    }
}
