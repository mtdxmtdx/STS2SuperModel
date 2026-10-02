using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeFirstRewardReplayTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };

    [Theory]
    [InlineData(9001UL)]
    [InlineData(9004UL)]
    public async Task ConditionalRewardPrefixReplaysNativeCarryInAndAllFutureDraws(ulong sourceDraw)
    {
        var prior = Prior;
        var recipe = prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket packet;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); packet = source.Observe(); }
        Assert.True(NativeFirstRewardCondition.TryCreate(packet, prior, out var condition, out var reason), reason);
        // A deliberately controlled same-recipe integration fixture. Independent
        // inference never receives this recipe or the disposed source graph.
        var proposal = recipe with { ProposalSeed = 33445566 };
        var tape = new NativeLabelTape(proposal, firstRewardCondition: condition,
            expectedEntryJson: packet.Observation!.History.Single(item => item.Kind == NativeEntryAssets.EventKind).Detail);
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposal, tape);
        Assert.NotNull(replay);
        tape.ValidateProposalCompletion();
        Assert.True(tape.FirstRewardConditionApplied);
        Assert.Equal(5, tape.ConditionedCells);
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(replay.Observe()));
        int correctionDraws = 0;
        Assert.True(tape.AcceptCorrection(() => { correctionDraws++; return 0; }));
        Assert.Equal(0, correctionDraws); // exact root-constant reward envelope
        await using var fork = await replay.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && replay.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(replay.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(replay.Observe());
            await replay.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", replay.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await replay.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
    }
}
