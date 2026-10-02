using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicOpeningEncounterIntegrationTests
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
    [InlineData(11001UL)]
    [InlineData(11008UL)]
    public async Task ProductionTapeWiresOpeningConditionAndReplaysWholePublicContinuation(ulong seed)
    {
        var prior = Hybrid.Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket observed;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        { Assert.NotNull(original); observed = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        var source = new NativeTapeReplaySource(observed, prior);
        Assert.True(source.UsesConditionalPublicOpeningEncounter);
        Assert.False(source.UsesConditionalFirstEncounter); // Never overlap slot-zero cells.
        Assert.Contains("conditional-v4", source.PosteriorProfile);
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(observed, prior, out var opening, out var reason), reason);
        // This fixture reuses a chosen hypothetical recipe only to isolate the
        // production hook and exact replay law. It is not source-posterior evidence.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 12345UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            publicOpeningEncounterCondition: opening, expectedPublicEvidence: observed.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.FirstEncounterConditionApplied);
        Assert.Equal(opening!.TargetEncounterIndex, tape.ProposedFirstEncounterIndex);
        Assert.Equal(1, tape.ConditionedCells);
        Assert.True(tape.AcceptCorrection(() => throw new InvalidOperationException("The fixed quarter-bucket cancels")));
        Assert.Equal(PublicJson.Serialize(observed), PublicJson.Serialize(world.Observe()));
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        int steps = 0;
        while (world.Observe().Status != "terminal_settled" && steps++ < 200)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status); Assert.Equal("terminal_settled", fork.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
    }
    [Fact]
    public async Task TypedMapNeowOpeningAndCombatKernelsComposeInOneOwnedNativeReplay()
    {
        var prior = Hybrid.Freeze();
        var originalRecipe = prior.Draw(new Rng(11001, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, originalRecipe,
            NativeLabelTape.ForDeclaredPrior(prior, originalRecipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        Assert.True(NativeInitialPrefixCondition.TryCreate(root, prior, out var initial, out var why), why);
        Assert.NotNull(initial!.PublicMap);
        Assert.True(NativeNeowCondition.TryCreate(root, prior, out var neow, out why), why);
        Assert.NotNull(neow!.ObservedCurseId);
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out why), why);
        var combats = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(1, combats.EligibleShuffleCount); Assert.Equal(1, combats.EligibleHpCount);
        // A previously recorded independent proposal is a regression coordinate,
        // never the observed source recipe or private graph. The source has a
        // different RunSeed, TapeSeed and ProposalSeed.
        var auxiliary = new NativeTapeRecipe(16657447854696103605UL, 2016841812778250893UL,
            8312175039547833285UL, 0, 0);
        var prefix = initial.Prepare(auxiliary, 64);
        Assert.Equal(10773775229783546949UL, prefix.SelectedRecipe.RunSeed);
        var tape = NativeLabelTape.ForDeclaredPrior(prior, prefix.SelectedRecipe,
            expectedEntryJson: root.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail,
            neowCondition: neow, initialPrefixPlan: prefix, publicCombatCondition: combats,
            expectedPublicEvidence: root.PublicEvidence, publicOpeningEncounterCondition: opening);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, prefix.SelectedRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.NeowConditionApplied); Assert.True(tape.FirstEncounterConditionApplied);
        Assert.Equal(1, tape.ConditionedPublicCombatShuffles); Assert.Equal(1, tape.ConditionedPublicCombatHp);
        Assert.Equal(root.PublicEvidence!.Events.Length, tape.PublicPrefixEventsChecked);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        // Matching public observations do not imply density correction accepts.
        // This is a replay fixture; production only admits after a true result.
        _ = tape.AcceptCorrection(new Rng(6161).NextUnsignedLong);
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
    }

}
