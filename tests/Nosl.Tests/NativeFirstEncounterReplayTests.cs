using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeFirstEncounterReplayTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };

    [Theory]
    [InlineData(9001UL, 9004UL)]
    [InlineData(9004UL, 9001UL)]
    public async Task JointEncounterPrefixKeepsNativePastAndOwnedFuture(ulong sourceDraw, ulong oppositeActDraw)
    {
        var prior = Prior;
        var recipe = prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket packet;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); packet = source.Observe(); }
        Assert.True(NativeFirstEncounterCondition.TryCreate(packet, prior, out var condition, out var reason), reason);
        // This controlled native integration fixture conditions encounters only.
        // Earlier HP may change. It is not a complete posterior acceptance test;
        // the independent sampler still requires the entire original public packet.
        var proposal = recipe with { ProposalSeed = 33445566 };
        var tape = new NativeLabelTape(proposal, firstEncounterCondition: condition);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposal, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.FirstEncounterConditionApplied); Assert.Equal(2, tape.ConditionedCells);
        Assert.Equal(packet.Observation!.Enemies.Single().Id, world.Observe().Observation!.Enemies.Single().Id);
        Assert.Equal(packet.Observation.RunContext, world.Observe().Observation!.RunContext);
        int correctionDraws = 0;
        Assert.True(tape.AcceptCorrection(() => { correctionDraws++; return 0; })); Assert.Equal(0, correctionDraws);
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
        var incompatible = prior.Draw(new Rng(oppositeActDraw, "nosl-native-tape-source-draw-v1"));
        var wrongActTape = new NativeLabelTape(incompatible, firstEncounterCondition: condition);
        await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(prior.Execution, incompatible, wrongActTape));
        Assert.False(wrongActTape.FirstEncounterConditionApplied); Assert.Equal(0, wrongActTape.ConditionedCells);
    }
}
