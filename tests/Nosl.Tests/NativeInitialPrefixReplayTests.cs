using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeInitialPrefixReplayTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };

    private static async Task<DecisionPacket> PublicRoot(ulong sourceDraw)
    {
        var recipe = Prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, new(recipe));
        Assert.NotNull(source); return source.Observe();
    }

    [Theory]
    [InlineData(9001UL)]
    [InlineData(9004UL)]
    public async Task JointPrefixUsesSelectedRecipeAndReplaysOwnedNativeFuture(ulong sourceDraw)
    {
        var root = await PublicRoot(sourceDraw);
        Assert.True(NativeInitialPrefixCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var neow, out _));
        var auxiliary = new NativeTapeRecipe(101, 202, 303, 1, 0);
        var plan = condition!.Prepare(auxiliary, 64);
        Assert.Equal(auxiliary, plan.AuxiliaryRecipe);
        Assert.NotEqual(auxiliary.RunSeed, plan.SelectedRecipe.RunSeed);
        var tape = new NativeLabelTape(plan.SelectedRecipe, neowCondition: neow, initialPrefixPlan: plan);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, plan.SelectedRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.ConditionedCells >= plan.Trace.Select(item => item.State).Distinct().Count());
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && world.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));

        Assert.Throws<ArgumentException>(() => new NativeLabelTape(auxiliary, initialPrefixPlan: plan));
        var altered = plan.Trace.ToArray(); altered[0] = altered[0] with { State = new(0, 0, 0, 0) };
        var corrupt = new NativeInitialPrefixProposal(plan.AuxiliaryRecipe, plan.SelectedRecipe, plan.TargetActType,
            plan.ConditionsMapTravel, altered, plan.Stats, plan.MaxTrials);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenLabelTapeAsync(Prior.Execution,
            plan.SelectedRecipe, new(plan.SelectedRecipe, initialPrefixPlan: corrupt)));
        // A failed constructor must not poison the next independently owned run.
        await using var afterFailure = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution,
            plan.SelectedRecipe, new(plan.SelectedRecipe, neowCondition: neow, initialPrefixPlan: plan));
        Assert.NotNull(afterFailure);
    }

    [Fact]
    public async Task ComponentExhaustionRemainsComputationallyInconclusiveWithSeparateWorkCounts()
    {
        var root = await PublicRoot(9001);
        var source = new NativeTapeReplaySource(root, Prior, initialPrefixMaxTrials: 1);
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(2, 2));
        Assert.Contains(source.ProposalAudit, item => item.Status == "component_budget_exhausted");
        Assert.DoesNotContain(source.ProposalAudit, item => item.Status == "proposal_engine_error");
        Assert.All(source.ProposalAudit, item =>
        {
            Assert.NotNull(item.AuxiliaryRecipe); Assert.NotNull(item.InitialPrefixStats);
            Assert.Equal(1, item.InitialPrefixMaxTrials);
            Assert.True(item.InitialPrefixStats!.CompletedTrials >= 1);
        });
    }
}
