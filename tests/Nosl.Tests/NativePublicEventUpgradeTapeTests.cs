using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicEventUpgradeTapeTests
{
    [Fact]
    public async Task Source24104ConditionsDetachedDoorsPairAndReplaysExactContinuationAndFork()
    {
        // Fixed natural-source lifecycle regression only. The fixture seed locates public
        // evidence; it is never given to the upgrade proposal or used for a posterior claim.
        var prior = NativePublicBrainLeechCardTests.Prior;
        var recipe = prior.Draw(new Rng(24104, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        {
            Assert.NotNull(original);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe()));
        }
        string expected = PublicJson.Serialize(root);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        var target = condition!.Target;
        Assert.Equal(294, target.ChoiceOrdinal - 1);
        Assert.Equal(296, target.EndOrdinal);
        Assert.Equal(nameof(DoorsOfLightAndDark), NativeEventSelectionCertificate.Identify(
            Assert.IsType<PublicOptionsObserved>(root.PublicEvidence!.Events[294].Payload)));
        var dispatcher = new NativeTapeReplaySource(root, prior);
        Assert.True(dispatcher.UsesConditionalPublicEventUpgrades);
        Assert.Equal(1, dispatcher.PublicEventUpgradeTargets);
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(prior, recipe, publicEventUpgradeCondition: condition));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(prior with { SchemaVersion = NativeTapePrior.Version }, recipe,
            publicEventUpgradeCondition: condition, expectedPublicEvidence: root.PublicEvidence));

        var proposalRecipe = recipe with { ProposalSeed = recipe.ProposalSeed ^ 918273UL };
        // The public pair leaves physical copies latent. The next combat's public
        // draws must be conditioned separately before claiming whole-root equality.
        var combats = NativePublicCombatPrefixCondition.Create(root);
        Assert.True(combats.EligibleShuffleCount > 0);
        var tape = NativeLabelTape.ForDeclaredPrior(prior, proposalRecipe,
            publicEventUpgradeCondition: condition, publicCombatCondition: combats, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposalRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(expected, PublicJson.Serialize(world.Observe()));
        Assert.Equal(expected, PublicJson.Serialize(root));
        Assert.Equal(2, tape.ConditionedEventUpgrades);
        Assert.Equal(1, tape.ConditionedEventUpgradeShuffles);
        Assert.Equal(condition.Envelope, tape.EventUpgradeEnvelope);
        Assert.NotNull(tape.EventUpgradeRatio);
        Assert.True(tape.EventUpgradeRatio!.Value.Numerator > 0);
        Assert.True(tape.EventUpgradeRatio.Value.Numerator * condition.Envelope.Denominator
            <= condition.Envelope.Numerator * tape.EventUpgradeRatio.Value.Denominator);
        Assert.Equal(combats.EligibleShuffleCount, tape.ConditionedPublicCombatShuffles);
        Assert.Equal(combats.EligibleHpCount, tape.ConditionedPublicCombatHp);
        Assert.Equal(target.PoolKeys.Count - 1 + combats.EligibleHpCount
            + combats.Combats.Values.Sum(combat => combat.Shuffle is { } shuffle ? shuffle.DeckCount - 1 : 0), tape.ConditionedCells);
        Assert.Equal(root.PublicEvidence.Events.Length, tape.PublicPrefixEventsChecked);
        // Exact correction is a separate acceptance gate; either result is valid
        // for this native lifecycle replay, which makes no posterior-draw claim.
        _ = tape.AcceptCorrection(new Rng(7433).NextUnsignedLong);

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
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(conditionedCells, tape.ConditionedCells);
        Assert.Equal(2, tape.ConditionedEventUpgrades);
        Assert.Equal(1, tape.ConditionedEventUpgradeShuffles);
        tape.ValidateProposalCompletion();
    }
}
