using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicBrainLeechTapeTests
{
    [Fact]
    public async Task Source24111RecoversBothEventIdentitiesAndReplaysEveryBrainLeechCardThroughSettlement()
    {
        // Fixed natural-source lifecycle regression, not an independent posterior
        // draw, reachability search, benchmark, or source-seed proposal input.
        var prior = NativePublicBrainLeechCardTests.Prior;
        var recipe = prior.Draw(new Rng(24111, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        {
            Assert.NotNull(original);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe()));
        }
        string expected = PublicJson.Serialize(root);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(root, prior, out var eventIds, out var reason), reason);
        Assert.Equal(new[] { nameof(WaterloggedScriptorium), nameof(BrainLeech) }, eventIds!.TargetEventIds);
        Assert.True(NativePublicEventCardCondition.TryCreate(root, prior, out var eventCards, out reason), reason);
        var target = Assert.Single(eventCards!.Targets);
        Assert.Equal(162, target.OfferEventOrdinal);
        Assert.Equal(nameof(BrainLeech), target.Source);
        Assert.Equal(new[] { "PoisonedStab", "Backflip", "Backstab", "Finisher", "DaggerThrow" }, target.Certificate.BaseCardIds);
        var dispatcher = new NativeTapeReplaySource(root, prior);
        Assert.True(dispatcher.UsesConditionalPublicEventCards); Assert.Equal(1, dispatcher.PublicEventCardTargets);
        Assert.True(dispatcher.UsesConditionalEventPermutation); Assert.Equal(2, dispatcher.PublicEventTargets);

        var proposalRecipe = recipe with { ProposalSeed = recipe.ProposalSeed ^ 918273UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, proposalRecipe,
            publicEventCardCondition: eventCards, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposalRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(expected, PublicJson.Serialize(world.Observe()));
        Assert.Equal(expected, PublicJson.Serialize(root));
        Assert.Equal(5, tape.ConditionedEventCards); Assert.Equal(1, tape.ConditionedEventCardOffers);
        Assert.Equal(eventCards.Envelope, tape.EventCardRatio); Assert.Equal(eventCards.Envelope, tape.EventCardEnvelope);
        Assert.Equal(10, tape.ConditionedCells);
        Assert.Equal(root.PublicEvidence!.Events.Length, tape.PublicPrefixEventsChecked);

        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(10, tape.ConditionedCells); tape.ValidateProposalCompletion();
        Assert.Equal(5, tape.ConditionedEventCards); Assert.Equal(1, tape.ConditionedEventCardOffers);
    }
}
