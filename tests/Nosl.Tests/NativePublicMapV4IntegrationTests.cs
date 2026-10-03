using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicMapV4IntegrationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version,
            PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(24002UL)]
    [InlineData(24007UL)]
    [InlineData(24008UL)]
    public async Task OwningPublicComponentsReplayHistoryAndPreserveContinuation(ulong sourceSeed)
    {
        // Already inspected development recipes isolate native lifecycle wiring.
        // This identity replay is not a posterior draw or a throughput estimate.
        var recipe = Prior.Draw(new Rng(sourceSeed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(root, Prior, out var unknown, out var reason), reason);
        bool hasShop = NativePublicShopCondition.TryCreate(root, Prior, out var shop, out _);
        Assert.Equal(sourceSeed == 24008, hasShop);
        NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var formation, out _);
        if (sourceSeed == 24007)
        {
            Assert.NotNull(formation);
            Assert.Equal(2, formation.CombatIndex);
        }
        var cards = NativePublicRewardCondition.Create(root.PublicEvidence!);
        var resources = NativePublicRewardResourceCondition.Create(root.PublicEvidence!, cards);
        var selected = new NativeTapeReplaySource(root, Prior);
        Assert.True(selected.UsesConditionalPublicUnknownRooms);
        Assert.Equal(hasShop, selected.UsesConditionalPublicShop);

        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Prior, recipe,
            publicUnknownRoomCondition: unknown));
        if (shop is not null)
            Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Prior, recipe,
                publicShopCondition: shop));

        var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe,
            expectedPublicEvidence: root.PublicEvidence, publicRewardCondition: cards,
            publicResourceCondition: resources, weakFormationCondition: formation,
            publicUnknownRoomCondition: unknown, publicShopCondition: shop);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, tape);
        Assert.NotNull(world);
        tape.ValidateProposalCompletion();
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(unknown!.Targets.Count, tape.ConditionedUnknownRooms);
        Assert.Equal(unknown.Envelope, tape.UnknownRoomRatio);
        Assert.Equal(hasShop ? 1 : 0, tape.ConditionedShopOffers);
        if (hasShop) Assert.True(tape.ConditionedShopBags > 0);
        if (tape.ShopRatio is { } ratio && tape.ShopEnvelope is { } bound)
            Assert.True(ratio.Numerator * bound.Denominator <= bound.Numerator * ratio.Denominator);
        // An identity world may legitimately fail correction. The independent
        // finite-domain tests establish its law; this case checks native wiring.
        var correction = new Rng(81024, "nosl-map-v4-test-correction");
        _ = tape.AcceptCorrection(correction.NextUnsignedLong);
        int conditioned = tape.ConditionedCells;
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action);
            await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(conditioned, tape.ConditionedCells);
        tape.ValidateProposalCompletion();

        if (sourceSeed == 24008) await CheckLegacyRewardsFallback(root);
    }

    private static async Task CheckLegacyRewardsFallback(DecisionPacket mapRoot)
    {
        // Coarsen the separately versioned graph channel to the existing v1
        // public packet. No source seed or hidden source state enters this check.
        var entries = mapRoot.PublicEvidence!.Events.Select(e => e.Payload is PublicMapObserved map
            ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal,
                new PublicMapObserved(map.Current, map.Nodes, map.Edges, map.Options)) : e).ToImmutableArray();
        var root = mapRoot with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        var prior = Prior with
        {
            SchemaVersion = NativeTapePrior.RewardsVersion,
            Execution = Prior.Execution with
            {
                PublicEvidenceProfile = PublicRunEvidence.Version,
                PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1,
            },
        };
        Assert.True(NativePublicShopCondition.TryCreate(root, prior, out _, out var reason), reason);
        var source = new NativeTapeReplaySource(root, prior, initialPrefixMaxTrials: 1, eventPermutationMaxTrials: 1);
        Assert.False(source.UsesConditionalPublicUnknownRooms);
        Assert.False(source.UsesConditionalPublicShop);
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var sample = await source.SampleWorldAsync(9123, 1);
        });
        Assert.True(error is null or PosteriorSamplingException, error?.ToString());
        var attempt = Assert.Single(source.ProposalAudit);
        Assert.Null(attempt.ConditionedUnknownRooms);
        Assert.Null(attempt.ConditionedShopOffers);
    }
}
