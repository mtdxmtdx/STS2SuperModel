using System.Reflection;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Rooms;
using Nosl.Worker;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicEventCardTapeTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Fact]
    public async Task Source24008ComposesPublicGorgeNeowAndCombatRewardsThroughExactSettlement()
    {
        // One fixed natural source, followed by same-recipe lifecycle replay.
        // This is not a fresh posterior draw or an acceptance-rate measurement.
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        Assert.Equal(2, recipe.CombatIndex); Assert.Equal(7, recipe.DecisionIndex);
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            // Only detached public values enter the conditions below.
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        string expected = PublicJson.Serialize(root);
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var eventCards, out var reason), reason);
        var target = Assert.Single(eventCards!.Targets);
        Assert.Equal(137, target.OfferEventOrdinal); Assert.Equal(6, target.EventOwnerOrdinal);
        Assert.Equal(7, target.ChoiceOwnerOrdinal); Assert.Equal(0, target.ActIndex); Assert.Equal(3, target.Floor);
        Assert.Equal(new[] { "PiercingWail", "Deflect", "Prepared", "CloakAndDagger", "DaggerThrow", "Slice", "DaggerSpray", "Predator" },
            target.Certificate.BaseCardIds);
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var neow, out reason), reason);
        Assert.Equal(nameof(PreciseScissors), neow!.TargetRelicId);
        var rewards = NativePublicRewardCondition.Create(root.PublicEvidence!);
        Assert.Equal(new[] { 0, 1 }, rewards.Targets.Keys.Order().ToArray());
        var dispatcher = new NativeTapeReplaySource(root, Prior);
        Assert.True(dispatcher.UsesConditionalPublicEventCards);
        Assert.Equal(1, dispatcher.PublicEventCardTargets);
        Assert.True(dispatcher.UsesConditionalNeow); Assert.True(dispatcher.UsesConditionalPublicRewards);

        // A caller cannot swallow the owning wrapper's guard and replay a clean tape.
        var foreignOwnerTape = NativeLabelTape.ForDeclaredPrior(Prior, recipe, publicRewardCondition: rewards);
        var foreignRun = new RunState("foreign-reward-owner", ascensionLevel: 10);
        var foreignPlayer = Player.CreateForNewRun(ModelDb.Character<Silent>(), foreignRun);
        foreignRun.AddPlayer(foreignPlayer);
        var beginReward = typeof(NativeLabelTape).GetMethod("BeginCombatRewardGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<LabelCombatRewardContext, IDisposable?>>(foreignOwnerTape);
        var ownerError = Assert.Throws<InvalidOperationException>(() => beginReward(new(foreignPlayer,
            foreignPlayer.PlayerRng.Rewards, RoomType.Monster, null, null, 1f)));
        Assert.Contains("owned hypothetical run", ownerError.Message);
        Assert.True(foreignOwnerTape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(() => foreignOwnerTape.ReplayCopy());

        var proposalRecipe = recipe with { ProposalSeed = recipe.ProposalSeed ^ 918273UL };
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, proposalRecipe, neowCondition: neow,
            publicRewardCondition: rewards, publicEventCardCondition: eventCards, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, proposalRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(expected, PublicJson.Serialize(world.Observe()));
        Assert.Equal(expected, PublicJson.Serialize(root));
        Assert.True(tape.NeowConditionApplied);
        Assert.Equal(6, tape.ConditionedPublicRewardCards);
        Assert.Equal(0, tape.ConditionedNeowCards); // Scissors' deck choice is ordinary.
        Assert.Equal(8, tape.ConditionedEventCards); Assert.Equal(1, tape.ConditionedEventCardOffers);
        Assert.Equal(eventCards.Envelope, tape.EventCardRatio);
        Assert.Equal(eventCards.Envelope, tape.EventCardEnvelope);
        Assert.Equal(root.PublicEvidence!.Events.Length, tape.PublicPrefixEventsChecked);

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
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(conditioned, tape.ConditionedCells); tape.ValidateProposalCompletion();
        Assert.Equal(8, tape.ConditionedEventCards); Assert.Equal(1, tape.ConditionedEventCardOffers);
        Assert.Equal(6, tape.ConditionedPublicRewardCards);
    }

    [Fact]
    public async Task ActualOwningRewardsLineageAliasCannotBeReconditionedAtGorge()
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root; ulong throughRoot;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
            // Native source cursor is test-only alias setup, never a condition
            // input or an oracle used for an independent posterior proposal.
            throughRoot = source.NativeRun.Players.Single().PlayerRng.Rewards.ToSerializable().LabelProvenance!.RawCursor!.Value;
        }
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe, publicEventCardCondition: condition,
            expectedPublicEvidence: root.PublicEvidence);
        using (tape.EnterScope())
        {
            var earlier = new PlayerRngSet(new RunRngSet(recipe.IndependentRunSeed).Seed).Rewards;
            for (ulong cursor = 0; cursor < throughRoot; cursor++) earlier.NextUnsignedLong();
        }
        Assert.True(tape.RewardsCells >= 8); Assert.Equal(0, tape.ConditionedCells);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, tape));
        Assert.Contains("fresh Rewards cells", error.Message);
        Assert.Equal(0, tape.ConditionedEventCards); Assert.Equal(0, tape.ConditionedEventCardOffers);
        Assert.Equal(0, tape.ConditionedCells);
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.AcceptCorrection(() => 0));
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
    }

    [Fact]
    public async Task DisposalDrawFailureLatchesButGenuinePublicMismatchDoesNot()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await NativePublicEventCardConditionTests.Fixture(new NativeRewardsOracle(71).Word)).Root;
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, new(11, 22, 33, 0, 0), publicEventCardCondition: condition);
        var proposal = Component(tape); var oracle = Oracle(tape);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativePublicEventCardConditionTests.Fixture(
            oracle.Word, proposal, extraSelectionWord: true));
        Assert.True(tape.HasConditionedWordFailure); Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
        Assert.Contains("added Rewards draws", tape.ConditionedWordError!.Message);

        var entries = root.PublicEvidence!.Events;
        int at = entries.IndexOf(entries.Single(e => e.Payload is PublicCardsObserved));
        var assets = ((PublicOwnerEnded)entries[at - 5].Payload).Assets!;
        var changed = new PublicEvidenceAssets(assets.Hp - 1, assets.MaxHp, assets.Gold, assets.Deck, assets.Relics,
            assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
        entries = entries.SetItem(at - 5, new(entries[at - 5].EventOrdinal, entries[at - 5].OwnerOrdinal,
            new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, changed)));
        root = root with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out condition, out _));
        var mismatchTape = NativeLabelTape.ForDeclaredPrior(Prior, new(11, 22, 33, 0, 0), publicEventCardCondition: condition);
        await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => NativePublicEventCardConditionTests.Fixture(
            Oracle(mismatchTape).Word, Component(mismatchTape)));
        Assert.False(mismatchTape.HasConditionedWordFailure);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("mismatch")]
    [InlineData("cancellation")]
    public async Task SourceCannotRetryCaughtEventCallbackFailure(string later)
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        int opened = 0;
        async Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe ignored,
            NativeLabelTape tape, CancellationToken token)
        {
            opened++;
            // Construct a native event callback context with a deliberately foreign
            // RNG. This test-only opener then imitates a caller swallowing it.
            var run = new RunState("caught-gorge-callback-fixture", ascensionLevel: 10);
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
            int floor = condition!.Targets[0].Floor;
            for (int i = 0; i < floor; i++) run.AddVisitedMapCoord(new MapCoord(0, i));
            var room = new EventRoom(() => (RoomFullOfCheese)ModelDb.Event<RoomFullOfCheese>().MutableClone());
            run.PushRoom(room); await room.Enter(run);
            var proposal = Component(tape); proposal.AttachHypotheticalRun(run);
            var callback = typeof(NativeLabelTape).GetMethod("BeginRewardCardSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<LabelRewardCardSelectionContext, IDisposable?>>(tape);
            var context = new LabelRewardCardSelectionContext(player, new Rng(7), LabelRewardCardSelectionKind.CreationOptions,
                0, 8, CardCreationSource.Other, CardCreationFlags.NoUpgradeRoll | CardCreationFlags.NoRarityModification,
                CardRarityOddsType.Uniform, null, false, [], []);
            var failure = Assert.Throws<InvalidOperationException>(() => callback(context));
            Assert.Contains("owned native boundary", failure.Message);
            Assert.True(tape.HasConditionedWordFailure); Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
            if (later == "mismatch") throw new NativePublicConstraintMismatchException("later public mismatch");
            if (later == "cancellation") throw new OperationCanceledException("later cancellation");
            return null;
        }
        var source = new NativeTapeReplaySource(root, Prior, nativeOpenerForTests: Open);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.SampleWorldAsync(501, 4));
        Assert.Contains("owned native boundary", error.Message); Assert.Equal(1, opened);
        var audit = Assert.Single(source.ProposalAudit);
        Assert.Equal("proposal_engine_error", audit.Status); Assert.Equal(0, audit.ConditionedEventCards);
    }

    private static NativePublicEventCardProposal Component(NativeLabelTape tape) =>
        (NativePublicEventCardProposal)typeof(NativeLabelTape).GetField("_publicEventCardProposal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tape)!;
    private static NativeRewardsOracle Oracle(NativeLabelTape tape) =>
        (NativeRewardsOracle)typeof(NativeLabelTape).GetField("_rewardsOracle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tape)!;
}
