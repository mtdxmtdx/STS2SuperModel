using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeHybridV6CompositionTests
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
    [InlineData(11002UL)]
    [InlineData(11004UL)]
    [InlineData(11006UL)]
    [InlineData(11007UL)]
    public async Task OwningTapeRoutesNewPublicConditionsAndReplaysThroughSettlement(ulong seed)
    {
        var prior = Hybrid.Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        {
            Assert.NotNull(original);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe()));
        }
        var reshuffles = NativePublicReshuffleCondition.Create(root);
        var branches = NativePublicMonsterBranchIntentCondition.Create(root);
        NativePublicWeakSlimeFormationCondition.TryCreate(root, prior, out var formation, out _);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(root, prior, out var encounters, out var encounterReason), encounterReason);
        var source = new NativeTapeReplaySource(root, prior);
        Assert.True(source.UsesConditionalWeakEncounterSequence);
        Assert.False(source.UsesConditionalPublicOpeningEncounter);
        Assert.False(source.UsesConditionalFirstEncounter);
        Assert.Equal(encounters!.Targets.Count, source.WeakEncounterTargets);
        Assert.Equal(encounters.PrefixLength, source.WeakEncounterPrefixLength);
        if (seed == 11002)
        {
            Assert.True(NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out _));
            Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(prior, recipe,
                weakEncounterCondition: encounters, publicOpeningEncounterCondition: opening));
            Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(
                prior with { SchemaVersion = NativeTapePrior.Version }, recipe, weakEncounterCondition: encounters));
            var reference = new NativeTapeReplaySource(root, prior, enableConditioning: false);
            Assert.False(reference.UsesConditionalWeakEncounterSequence);
            Assert.False(reference.UsesPrimitiveConditioning);
        }
        Assert.Equal("owned-native-rewards-state-tape-conditional-v7-public-evidence-v1", source.PosteriorProfile);
        Assert.Equal(reshuffles.EligibleShuffleCount, source.PublicReshuffleTargets);
        Assert.Equal(branches.EligibleBranchCount, source.PublicMonsterBranchTargets);
        Assert.Equal(formation is not null, source.UsesConditionalWeakFormation);
        Assert.True(source.UsesConditionalReshuffles || source.UsesConditionalMonsterBranches || source.UsesConditionalWeakFormation);
        // The fixed source recipe is reused only to isolate native routing and
        // owned replay. This is not an independent posterior or throughput test.
        // Other covered startup paths are jointly enabled for the all-observed
        // first-cycle root; remaining roots isolate the new boundary conditions.
        var combats = seed == 11002 ? NativePublicCombatPrefixCondition.Create(root) : null;
        var slugs = seed == 11002 ? NativePublicCorpseSlugIntentCondition.Create(root) : null;
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 0x1739abcdUL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            publicCombatCondition: combats, slugIntentCondition: slugs,
            weakFormationCondition: formation,
            publicReshuffleCondition: reshuffles.EligibleShuffleCount > 0 ? reshuffles : null,
            monsterBranchCondition: branches.EligibleBranchCount > 0 ? branches : null,
            expectedPublicEvidence: root.PublicEvidence, weakEncounterCondition: encounters);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world);
        tape.ValidateProposalCompletion();
        Assert.Equal(encounters.Targets.Count, tape.ConditionedWeakEncounters);
        Assert.Equal(encounters.Envelope, tape.WeakEncounterEnvelope);
        Assert.Equal(reshuffles.EligibleShuffleCount, tape.ConditionedReshuffles);
        Assert.Equal(branches.EligibleBranchCount, tape.ConditionedMonsterBranches);
        Assert.Equal(formation is not null ? 1 : 0, tape.ConditionedWeakFormations);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        int conditioned = tape.ConditionedCells;
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
        Assert.Equal(conditioned, tape.ConditionedCells);
        tape.ValidateProposalCompletion();
    }
}
