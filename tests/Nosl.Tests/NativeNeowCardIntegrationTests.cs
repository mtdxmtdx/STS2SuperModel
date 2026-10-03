using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowCardIntegrationTests
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
    [InlineData(11002UL, 6, 0)]
    [InlineData(11005UL, 1, 0)]
    [InlineData(11006UL, 6, 2)]
    [InlineData(11002UL, 6, 0, true)]
    [InlineData(11005UL, 1, 0, true)]
    [InlineData(11006UL, 6, 2, true)]
    [InlineData(24004UL, 6, 0, true)] // Fresh Map-law probe: ScrollBoxes provenance failure.
    [InlineData(24005UL, 6, 0, true)]
    [InlineData(24002UL, 3, 0, true)] // Complete LostCoffer offer including its potion.
    [InlineData(24104UL, 1, 0, true)] // NewLeaf public single replacement.
    [InlineData(24105UL, 1, 0, true)]
    [InlineData(24110UL, 2, 0, true)] // Both LeadPaperweight candidates despite no pick.
    public async Task OwningTapeRoutesPublicNeowCardsAndPreservesExactContinuation(ulong seed, int cards, int shuffles,
        bool mapLaw = false)
    {
        var prior = (mapLaw ? Hybrid with
        {
            SchemaVersion = NativeTapePrior.MapVersion,
            Execution = Hybrid.Execution with { PublicEvidenceProfile = PublicRunEvidence.CompleteMapVersion,
                PublicMapObservationProfile = PublicMapObservationProfiles.CompleteGraphV1 },
        } : Hybrid).Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        var source = new NativeTapeReplaySource(root, prior);
        Assert.True(source.UsesConditionalNeowCards);
        Assert.True(NativeNeowCardCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        // Reusing a fixed hypothetical recipe isolates the native routing and
        // replay contract. This is not an independent posterior acceptance claim.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 7654321UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            neowCardCondition: condition, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(mapLaw ? LabelRandomProvenance.MapLawId : LabelRandomProvenance.LawId,
            world.NativeRun.Players.Single().PlayerRng.Rewards.ToSerializable().LabelProvenance!.Law);
        Assert.Equal(cards, tape.ConditionedNeowCards); Assert.Equal(shuffles, tape.ConditionedNeowPoolShuffles);
        Assert.Equal(tape.NeowCardEnvelope, tape.NeowCardRatio);
        Assert.True(tape.AcceptCorrection(() => throw new InvalidOperationException("Root-fixed card mass cancels")));
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

        if (shuffles == 0 && condition!.RelicId != "NewLeaf") return;
        // The real owning ForceWords callback must reject a previously visited
        // Niche state; a helper-only mock would not establish this protection.
        var aliased = NativeLabelTape.ForDeclaredPrior(prior, hypothetical, neowCardCondition: condition);
        using (aliased.EnterScope()) new RunRngSet(hypothetical.IndependentRunSeed).Niche.NextInt(4);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, aliased));
        Assert.Contains(condition!.RelicId == "NewLeaf" ? "NewLeaf transform" : "Kaleidoscope pools", error.Message);
        Assert.Contains("earlier tape cell", error.Message);
    }
}
