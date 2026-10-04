using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicCombatPrefixReplayTests
{
    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Fact]
    public async Task ActualSecondCombatRootSuppliesEarlierTypedStartupWithoutPrivateInputs()
    {
        // Fixed same-recipe lifecycle evidence, not an independent posterior sample.
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 1, DecisionIndex = 0 };
        await using var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe));
        Assert.NotNull(original);
        var root = original.Observe();
        string before = PublicJson.Serialize(root);
        var condition = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(2, condition.Combats.Count);
        Assert.NotNull(condition.Combats[0].Shuffle);
        Assert.NotNull(condition.Combats[0].Hp);
        Assert.Equal(0, condition.Combats[0].CombatIndex);
        var first = root.PublicEvidence!.Events.First(entry => entry.OwnerOrdinal == condition.Combats[0].OwnerOrdinal
            && entry.Payload is PublicCombatDecision);
        Assert.Equal(first.EventOrdinal, condition.Combats[0].DecisionEventOrdinal);
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    [Fact]
    public async Task EarlierNeowHpShuffleAndRewardKernelsReplayTheirOwnWholePublicHypothesis()
    {
        // Controlled same-recipe execution checks kernel composition/replay. It is
        // not an independent-posterior sample or a source-public-equality claim.
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 1, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(original); root = original.Observe(); }
        string before = PublicJson.Serialize(root);
        var combats = NativePublicCombatPrefixCondition.Create(root);
        Assert.NotNull(combats.Combats[0].Shuffle);
        Assert.NotNull(combats.Combats[0].Hp);
        Assert.True(NativeNeowCondition.TryCreate(root, Hybrid, out var neow, out var reason), reason);
        var rewards = NativePublicRewardCondition.Create(root.PublicEvidence!);
        Assert.True(rewards.Targets.ContainsKey(0));
        var proposal = recipe with { ProposalSeed = 88899 };
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Hybrid, proposal,
            condition: combats.Combats[0].Shuffle, publicCombatCondition: combats));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Hybrid, proposal,
            hpCondition: combats.Combats[0].Hp, publicCombatCondition: combats));
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, proposal, neowCondition: neow,
            publicRewardCondition: rewards, publicCombatCondition: combats);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, proposal, tape);
        Assert.NotNull(world);
        tape.ValidateProposalCompletion();
        Assert.True(tape.NeowConditionApplied);
        Assert.Equal(combats.EligibleShuffleCount, tape.ConditionedPublicCombatShuffles);
        Assert.Equal(combats.EligibleHpCount, tape.ConditionedPublicCombatHp);
        Assert.Equal(3 * rewards.Targets.Count, tape.ConditionedPublicRewardCards);
        Assert.Equal(0, tape.ConditionedHpCount); // Old current-only HP is not applied twice.
        Assert.False(tape.ConditionApplied);
        Assert.True(tape.RewardsCells > 0);
        var correction = new Rng(334455);
        _ = tape.AcceptCorrection(correction.NextUnsignedLong);
        Assert.Equal(before, PublicJson.Serialize(root));
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && world.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));

        // Exercise the actual shared force callback, not just the unit harness:
        // an earlier same-state observation cannot be silently reconditioned.
        var aliased = NativeLabelTape.ForDeclaredPrior(Hybrid, proposal, neowCondition: neow,
            publicRewardCondition: rewards, publicCombatCondition: combats);
        using (aliased.EnterScope()) new RunRngSet(proposal.IndependentRunSeed).Shuffle.NextInt(5);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, proposal, aliased));
        Assert.Contains("earlier tape cell", error.Message);
        Assert.Contains("alias correction is unresolved", error.Message);
    }
}
