using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowPotionIntegrationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(24001UL)]
    [InlineData(24003UL)]
    public async Task OwningTapeRoutesBothPublicGrantsReplaysAndRejectsPriorStateAlias(ulong seed)
    {
        var prior = Prior.Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        Assert.True(NativeNeowPotionCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        var source = new NativeTapeReplaySource(root, prior);
        Assert.True(source.UsesConditionalNeowPotions);
        // This fixed hypothetical recipe isolates native routing and exact replay. It is not
        // an independent posterior acceptance claim; the condition receives only detached root evidence.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 8127311UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            neowPotionCondition: condition, expectedPublicEvidence: root.PublicEvidence);
        await using (var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape))
        {
            Assert.NotNull(world); tape.ValidateProposalCompletion();
            Assert.Equal(2, tape.ConditionedNeowPotions); Assert.Equal(condition!.Envelope, tape.NeowPotionRatio);
            Assert.Equal(tape.NeowPotionRatio, tape.NeowPotionEnvelope);
            Assert.True(tape.AcceptCorrection(() => throw new InvalidOperationException("Root-fixed grant mass cancels")));
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            await using var fork = await world.ForkForContinuationAsync();
            var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
            for (int i = 0; i < 200 && world.Observe().Status != "terminal_settled"; i++)
            {
                Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                var action = policy.Choose(world.Observe()); await world.StepAsync(action); await fork.StepAsync(action);
            }
            Assert.Equal("terminal_settled", world.Observe().Status);
            Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
                PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        }
        var aliased = NativeLabelTape.ForDeclaredPrior(prior, hypothetical, neowPotionCondition: condition);
        using (aliased.EnterScope()) new RunRngSet(hypothetical.IndependentRunSeed).CombatPotionGeneration.NextFloat();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, aliased));
        Assert.Contains("PhialHolster potions", error.Message); Assert.Contains("earlier tape cell", error.Message);
        Assert.True(aliased.HasConditionedWordFailure); Assert.Equal(0, aliased.ConditionedNeowPotions);
        Assert.Throws<InvalidOperationException>(aliased.RequireSuccessfulConditionedWords);
        Assert.Throws<InvalidOperationException>(aliased.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => aliased.AcceptCorrection(() => 0));
    }
}
