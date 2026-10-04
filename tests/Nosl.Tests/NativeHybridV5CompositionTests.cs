using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeHybridV5CompositionTests
{
    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData(11002UL, 16)]
    [InlineData(11003UL, 12)]
    public async Task OwningTapeComposesSlugIntentsAndObservedDrawCycleThroughSettlement(ulong seed, int drawCount)
    {
        var prior = Hybrid.Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var sourceWorld = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        {
            Assert.NotNull(sourceWorld);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(sourceWorld.Observe()));
        }
        string rootJson = PublicJson.Serialize(root);
        var combats = NativePublicCombatPrefixCondition.Create(root);
        var slugIntents = NativePublicCorpseSlugIntentCondition.Create(root);
        Assert.Equal(1, slugIntents.EligibleCombatCount);
        Assert.Equal(drawCount, combats.Combats[0].DrawPrefix!.DrawPrefixCount);
        var source = new NativeTapeReplaySource(root, prior);
        Assert.True(source.UsesConditionalSlugIntents);
        Assert.Equal(drawCount, source.PublicCombatDiagnostics[0].DrawPrefix!.DrawPrefixCount);
        Assert.Equal("owned-native-rewards-state-tape-conditional-v7-public-evidence-v1", source.PosteriorProfile);
        Assert.Equal("nosl-native-rewards-state-tape-conditional-v14-public-evidence-v1", NativeTapeReplayDataset.ImplementationFor(prior));
        NativeNeowCardCondition.TryCreate(root, prior, out var cards, out _);
        // This fixed native recipe isolates owning-scope composition. It is not
        // an independent posterior draw or a throughput/production admission.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 0x1739abcdUL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            publicCombatCondition: combats, slugIntentCondition: slugIntents,
            neowCardCondition: cards, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world);
        tape.ValidateProposalCompletion();
        Assert.Equal(1, tape.ConditionedSlugIntents);
        Assert.Equal(tape.SlugIntentEnvelope, tape.SlugIntentRatio);
        Assert.Equal(combats.EligibleShuffleCount, tape.ConditionedPublicCombatShuffles);
        Assert.Equal(combats.EligibleHpCount, tape.ConditionedPublicCombatHp);
        Assert.Equal(rootJson, PublicJson.Serialize(world.Observe()));
        int conditionedCells = tape.ConditionedCells;
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(conditionedCells, tape.ConditionedCells);
        Assert.Equal(1, tape.ConditionedSlugIntents);
        tape.ValidateProposalCompletion();
        Assert.Equal(rootJson, PublicJson.Serialize(root));
    }
}
