using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicRunEvidenceTests
{
    private static NativeRunExecutionOptions Execution => new(MaxFloors: 8,
        OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
        PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version);

    [Fact]
    public async Task NativeEarlyOptionsRewardsAndCompletedCombatSurviveLaterRootAndIndependentReplay()
    {
        var options = new NaturalSourceOptions(MaxFloors: 8, MaxRoots: 2, MaxRootsPerCombat: 1,
            SeedPrefix: "owned-native-opening", ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: Execution.OutsideCombatScript, PublicContextProfile: Execution.PublicContextProfile,
            PublicEvidenceProfile: Execution.PublicEvidenceProfile);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var observed = await NaturalSourceCollector.CollectAsync(options);
        double evidenceSeconds = timer.Elapsed.TotalSeconds;
        Assert.Null(Assert.Single(observed.Runs).Error);
        Assert.Equal(2, observed.Roots.Length);
        timer.Restart();
        var legacy = await NaturalSourceCollector.CollectAsync(options with { PublicEvidenceProfile = null });
        double legacySeconds = timer.Elapsed.TotalSeconds;
        Assert.Null(Assert.Single(legacy.Runs).Error);
        for (int i = 0; i < observed.Roots.Length; i++)
        {
            var root = observed.Roots[i]; var packet = root.PublicRoot;
            var evidence = Assert.IsType<PublicRunEvidence>(packet.PublicEvidence);
            Assert.True(evidence.CompleteFromRunStart, PublicRunEvidenceJson.Serialize(evidence));
            var start = Assert.IsType<PublicRunStarted>(evidence.Events[0].Payload);
            Assert.Equal("Silent", start.Character); Assert.Equal(10, start.Ascension);
            Assert.NotEmpty(start.Assets.Deck); Assert.NotEmpty(start.Assets.Relics);
            Assert.Contains(evidence.Events, e => e.Payload is PublicOptionsObserved);
            Assert.Contains(evidence.Events, e => e.Payload is PublicOptionChosen);
            var map = Assert.IsType<PublicMapObserved>(evidence.Events.First(e => e.Payload is PublicMapObserved).Payload);
            Assert.NotEmpty(map.Options); Assert.NotEmpty(map.Edges);
            Assert.All(map.Options, o => Assert.Equal(map.Edges.Any(e => e.From == map.Current && e.To == o.Coordinate), o.IsOrdinaryConnection));
            Assert.IsType<PublicCombatDecision>(evidence.Events[^1].Payload);
            var wrongCount = System.Text.Json.Nodes.JsonNode.Parse(PublicJson.Serialize(packet))!;
            wrongCount["observation"]!["runContext"]!["combatEntryIndex"] = 999;
            wrongCount["publicEvidence"]!["events"]!.AsArray()[^1]!["payload"]!["observation"]!["runContext"]!["combatEntryIndex"] = 999;
            Assert.Throws<ArgumentException>(() => PublicEvidenceInput.Validate(PublicJson.Read<DecisionPacket>(wrongCount.ToJsonString())));
            // A deliberately missing run prefix cannot reconstruct a combat count.
            wrongCount["publicEvidence"]!["completeFromRunStart"] = false;
            wrongCount["publicEvidence"]!["events"]![0]!["payload"] = new System.Text.Json.Nodes.JsonObject
                { ["kind"] = "gap", ["reason"] = "run_start_not_observed" };
            PublicEvidenceInput.Validate(PublicJson.Read<DecisionPacket>(wrongCount.ToJsonString()));
            Assert.Equal(PublicJson.Serialize(legacy.Roots[i].PublicRoot), PublicJson.Serialize(packet with { PublicEvidence = null }));
            int slot = root.SourceTrace.Count(t => t.Kind == "combat_action");
            await using var world = await NativeRunWorld.OpenAsync(Execution, options.SeedPrefix + ":0", slot);
            Assert.NotNull(world);
            Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(world.Observe()));
            await using var fork = await world.ForkForContinuationAsync();
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(packet);
            await world.StepAsync(action); await fork.StepAsync(action);
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            Assert.DoesNotContain(options.SeedPrefix, PublicRunEvidenceJson.Serialize(evidence));
            Assert.DoesNotContain("encounterName", PublicRunEvidenceJson.Serialize(evidence));
            Assert.DoesNotContain("actualSeed", PublicRunEvidenceJson.Serialize(evidence));
        }
        var later = observed.Roots[1].PublicRoot.PublicEvidence!;
        long earlierCombat = later.Events.First(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }).OwnerOrdinal!.Value;
        Assert.Contains(later.Events, e => e.OwnerOrdinal == earlierCombat && e.Payload is PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Victory });
        Assert.Contains(later.Events, e => e.OwnerOrdinal == earlierCombat && e.Payload is PublicCombatActionTaken);
        Assert.Contains(later.Events, e => e.OwnerOrdinal == earlierCombat && e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Damage });
        var offers = later.Events.Select(e => e.Payload).OfType<PublicOffersObserved>().SelectMany(o => o.Groups).SelectMany(g => g.Offers).ToArray();
        Assert.Contains(offers, o => o.OfferKind == PublicOfferKind.Gold);
        Assert.True(offers.Count(o => o.OfferKind == PublicOfferKind.Card) >= 3);
        using var input = JsonDocument.Parse(PublicJson.Serialize(observed.Roots[1].ToSourceRecord()));
        Assert.Equal(PublicRunEvidence.StudentSchema, input.RootElement.GetProperty("public_input").GetProperty("schema_version").GetString());
        Assert.Equal("run_started", input.RootElement.GetProperty("public_input").GetProperty("public_evidence").GetProperty("events")[0].GetProperty("payload").GetProperty("kind").GetString());
        var stats = new { evidenceSeconds, legacySeconds, roots = observed.Roots.Length,
            eventCounts = observed.Roots.Select(r => r.PublicRoot.PublicEvidence!.Events.Length).ToArray(),
            prefixBytes = observed.Roots.Select(r => System.Text.Encoding.UTF8.GetByteCount(PublicRunEvidenceJson.Serialize(r.PublicRoot.PublicEvidence!))).ToArray() };
        Console.WriteLine("Evidence fixture: " + PublicJson.Serialize(stats));
        if (Environment.GetEnvironmentVariable("NOSL_EVIDENCE_SAMPLE_PATH") is { Length: > 0 } sample)
        {
            File.WriteAllText(sample, PublicJson.Serialize(observed.Roots[1].ToSourceRecord()));
            File.WriteAllText(sample + ".metrics.json", PublicJson.Serialize(stats));
        }
    }

    [Fact]
    public void MidrunAndUnavailableOwnerBoundariesStayIncompleteWithoutHiddenReconstruction()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("evidence-midrun", ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        var options = new NaturalSourceOptions(PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version);
        var bridge = new NaturalSourceCollector.SourceBridge(run, options,
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "audit", "secret", null, null, default,
            startsAtNativeRunBeginning: true);
        bridge.BeginRun(run);
        var evidence = Assert.IsType<PublicRunEvidence>(bridge.CaptureEvidence());
        Assert.False(evidence.CompleteFromRunStart);
        Assert.IsType<PublicEvidenceGap>(evidence.Events[0].Payload);
        Assert.DoesNotContain("secret", PublicRunEvidenceJson.Serialize(evidence));
    }

    [Fact]
    public async Task ForcedEventRetainsParentScopeAndFullCombatActionsThroughSettlement()
    {
        async Task Fixture(RunState run, RunDriver driver)
        {
            var player = run.Players.Single();
            foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
            for (int i = 0; i < 5; i++)
            {
                var card = (GrandFinale)ModelDb.Card<GrandFinale>().MutableClone();
                card.AssignOwner(player); player.Deck.AddInternal(card);
            }
            var model = (BattlewornDummy)ModelDb.Event<BattlewornDummy>().MutableClone();
            var room = new EventRoom(() => model); run.PushRoom(room); await room.Enter(run);
            await driver.DriveEventAsync(room);
        }
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(Execution, "evidence-forced-owner", 0, Fixture);
        Assert.NotNull(world);
        var before = world.Observe().PublicEvidence!;
        Assert.False(before.CompleteFromRunStart);
        var combat = before.Events.Single(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat });
        var parent = Assert.IsType<PublicOwnerStarted>(combat.Payload).ParentOwnerOrdinal;
        Assert.NotNull(parent);
        Assert.Contains(before.Events, e => e.OwnerOrdinal == parent && e.Payload is PublicOptionsObserved);
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 100 && world.Observe().Observation is not null; i++)
        {
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        }
        Assert.Null(world.Observe().Observation);
        var terminal = world.Observe().PublicEvidence!;
        Assert.Contains(terminal.Events, e => e.OwnerOrdinal == combat.OwnerOrdinal && e.Payload is PublicOwnerEnded);
        Assert.Contains(terminal.Events, e => e.OwnerOrdinal == combat.OwnerOrdinal && e.Payload is PublicCombatActionTaken);
    }

    [Fact]
    public void ProfileAndLegacyPacketBytesAreExplicitAndPriorIdentityIncludesTheOptIn()
    {
        Assert.Equal("{\"status\":\"terminal_settled\",\"observation\":null,\"actions\":[]}", PublicJson.Serialize(new DecisionPacket("terminal_settled", null, [])));
        Assert.DoesNotContain("publicEvidenceProfile", PublicJson.Serialize(new NativeRunExecutionOptions()));
        Assert.Throws<ArgumentException>(() => (new NativeRunPrior { Execution = new(PublicEvidenceProfile: "unknown") }).Validate());
        Assert.Throws<ArgumentException>(() => (new NativeRunPrior { Execution = new(PublicEvidenceProfile: PublicRunEvidence.Version) }).Validate());
        var legacy = new NativeTapePrior { Execution = Execution with { PublicEvidenceProfile = null } };
        var evidence = legacy with { Execution = Execution };
        Assert.NotEqual(legacy.Identity, evidence.Identity);
        Assert.Equal(NativeTapeReplayDataset.ImplementationVersion, NativeTapeReplayDataset.ImplementationFor(legacy.Execution));
        Assert.NotEqual(NativeTapeReplayDataset.ImplementationFor(legacy.Execution), NativeTapeReplayDataset.ImplementationFor(evidence.Execution));
    }
    private static (RunState Run, Player Player) ConstructedRun(string seed)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState(seed, ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        return (run, player);
    }

    [Fact]
    public async Task NativeRewardCallbacksAfterSettlementUseOutsideChoicesAndPreserveAllShownOffers()
    {
        var (run, player) = ConstructedRun("evidence-reward-callback");
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        for (int i = 0; i < 5; i++)
        {
            var card = (GrandFinale)ModelDb.Card<GrandFinale>().MutableClone();
            card.AssignOwner(player); player.Deck.AddInternal(card);
        }
        var bridge = new NaturalSourceCollector.SourceBridge(run, new(MaxRoots: 100,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "fixture", "private", null, null, default);
        var driver = new RunDriver(run, bridge, recorder: bridge, useAvailablePotions: false)
            { CombatObserverDecorator = bridge.Decorate, AutomaticCombatSettlementCompleted = bridge.CompleteOutcome };
        try
        {
            var room = await driver.RunOneInjectedCombatAsync(RoomType.Monster, run.Act.MonsterEncounterCandidates.First().IdEntry);
            Assert.False(room.Engine.IsInProgress);
            int count = player.Deck.Cards.Count;
            var removal = new CardRemovalReward(player);
            var reward = RewardsSet.CreateCustom(player, extraRewards: [removal]);
            Assert.IsType<RewardDecision.TakeGold>(await bridge.ChooseRewardActionAsync(reward)); await reward.Gold.Take();
            Assert.IsType<RewardDecision.ResolveExtra>(await bridge.ChooseRewardActionAsync(reward)); await removal.Take();
            Assert.Equal(count - 1, player.Deck.Cards.Count);
            Assert.IsType<RewardDecision.Done>(await bridge.ChooseRewardActionAsync(reward));
            var evidence = bridge.CaptureEvidence()!;
            var outside = evidence.Events.Last(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.OutsideChoice });
            var parent = ((PublicOwnerStarted)outside.Payload).ParentOwnerOrdinal;
            Assert.NotNull(parent);
            Assert.Contains(evidence.Events, e => e.OwnerOrdinal == parent && e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Reward });
            var cards = Assert.IsType<PublicCardsObserved>(evidence.Events.Single(e => e.OwnerOrdinal == outside.OwnerOrdinal && e.Payload is PublicCardsObserved).Payload);
            Assert.Equal(count, cards.Choice.Candidates.Length);
            // Preserve the actual callback flag; FromDeckForRemoval currently
            // omits its cancelable argument despite its native class comment.
            Assert.False(cards.Choice.Cancelable);
            Assert.Null(((PublicOwnerEnded)evidence.Events.Single(e => e.OwnerOrdinal == outside.OwnerOrdinal && e.Payload is PublicOwnerEnded).Payload).Assets);
            // Actual native automatic callback bypasses the selection UI. Its
            // candidate identities must not be fabricated as a displayed choice.
            await CardSelectCmd.SelectCardsAsync(run, player, player.Deck.Cards.Take(1), 1, 1, null);
            Assert.Contains(bridge.CaptureEvidence()!.Events, e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.AmbiguousVisibility });
        }
        finally { bridge.DetachOutcome(); await bridge.ReleaseChoiceOriginAsync(); }
    }

    [Fact]
    public async Task NativeRewardRerollOnlyReplacesItsOwnGroupAndKeepsUnselectedPotionGoldAndExtraCards()
    {
        var (run, player) = ConstructedRun("evidence-reward-groups");
        await RelicCmd.Obtain((Driftwood)ModelDb.Relic<Driftwood>().MutableClone(), player);
        var primary = new CardReward(player, CardRarityOddsType.RegularEncounter);
        var extra = new CardReward(player, CardRarityOddsType.RegularEncounter);
        primary.Populate(run); extra.Populate(run);
        var potion = new PotionReward((DexterityPotion)ModelDb.Potion<DexterityPotion>().MutableClone(), player);
        var set = RewardsSet.CreateCustom(player, gold: new GoldReward(19, player), card: primary,
            potion: potion, extraRewards: [extra]);
        var producer = new NativePublicRunEvidence(run); producer.BeginRun(false);
        var alternative = primary.Alternatives.Single(a => a.OptionId == "REROLL");
        producer.Rewards(set, new RewardDecision.SelectCardAlternative(primary, alternative));
        await primary.SelectAlternative(alternative);
        producer.Rewards(set, new RewardDecision.TakeGold());
        var snapshots = producer.Capture().Events.Select(e => e.Payload).OfType<PublicOffersObserved>().ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Contains(snapshots[0].Groups.SelectMany(g => g.Offers), o => o.Gold == 19);
        Assert.Contains(snapshots[0].Groups.SelectMany(g => g.Offers), o => o.Potion == nameof(DexterityPotion));
        Assert.Equal(6, snapshots[0].Groups.SelectMany(g => g.Offers).Count(o => o.OfferKind == PublicOfferKind.Card));
        Assert.Single(snapshots[1].Groups.Where(g => g.GroupKind == PublicOfferGroupKind.Reroll));
        Assert.Contains(snapshots[1].Groups, g => g.GroupKind == PublicOfferGroupKind.Extra && g.Offers.Any(o => o.Card is not null));
        Assert.NotNull(snapshots[1].ReplacesOfferEventOrdinal);
    }

    [Fact]
    public async Task ActualDuplicateEventKeysPreserveChoiceIndexAndExplicitAmbiguity()
    {
        var (run, player) = ConstructedRun("evidence-event-duplicates");
        await PotionCmd.TryToProcure((DexterityPotion)ModelDb.Potion<DexterityPotion>().MutableClone(), player);
        await PotionCmd.TryToProcure((StrengthPotion)ModelDb.Potion<StrengthPotion>().MutableClone(), player);
        var model = (TheFutureOfPotions)ModelDb.Event<TheFutureOfPotions>().MutableClone();
        var room = new EventRoom(() => model); run.PushRoom(room); await room.Enter(run);
        Assert.True(model.CurrentOptions.Count > 1);
        var producer = new NativePublicRunEvidence(run); producer.BeginRun(false); producer.EnterFloor();
        producer.EventOptions(model.CurrentOptions, model.CurrentOptions[1]);
        var evidence = producer.Capture();
        Assert.Contains(evidence.Events, e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.AmbiguousVisibility });
        var options = evidence.Events.Select(e => e.Payload).OfType<PublicOptionsObserved>().Single();
        Assert.Equal(model.CurrentOptions.Count, options.Options.Length);
        Assert.Equal("option:1:POTION", evidence.Events.Select(e => e.Payload).OfType<PublicOptionChosen>().Single().Key);
        var locked = new[] { new EventOption("PAY", null), new EventOption("LEAVE", () => Task.CompletedTask) };
        producer.EventOptions(locked, locked[1]);
        Assert.True(producer.Capture().Events.Select(e => e.Payload).OfType<PublicOptionsObserved>().Last().Options[0].IsLocked);
    }

    [Fact]
    public async Task ForcedRewardOwnerStaysNestedAfterCombatEndsAndBeforeEventReturns()
    {
        var (run, player) = ConstructedRun("evidence-forced-rewards");
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        for (int i = 0; i < 5; i++)
        {
            var card = (GrandFinale)ModelDb.Card<GrandFinale>().MutableClone();
            card.AssignOwner(player); player.Deck.AddInternal(card);
        }
        var bridge = new NaturalSourceCollector.SourceBridge(run, new(MaxRoots: 100,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "fixture", "secret", null, null, default);
        var driver = new RunDriver(run, bridge, recorder: bridge, useAvailablePotions: false)
            { CombatObserverDecorator = bridge.Decorate, AutomaticCombatSettlementCompleted = bridge.CompleteOutcome };
        var model = (DenseVegetation)ModelDb.Event<DenseVegetation>().MutableClone();
        var room = new EventRoom(() => model); run.PushRoom(room); await room.Enter(run);
        // Explicit constructed entry: these choices did not pass the bridge, so
        // the event owner and run prefix must remain incomplete.
        await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == "REST"));
        await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == "FIGHT"));
        try { await driver.DriveEventAsync(room); }
        finally { bridge.DetachOutcome(); await bridge.ReleaseChoiceOriginAsync(); }
        var evidence = bridge.CaptureEvidence()!;
        Assert.False(evidence.CompleteFromRunStart);
        var owner = evidence.Events.Single(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Event });
        var combat = evidence.Events.Single(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat });
        var reward = evidence.Events.First(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Reward });
        Assert.Equal(owner.OwnerOrdinal, ((PublicOwnerStarted)combat.Payload).ParentOwnerOrdinal);
        Assert.Equal(owner.OwnerOrdinal, ((PublicOwnerStarted)reward.Payload).ParentOwnerOrdinal);
        Assert.True(evidence.Events.Single(e => e.OwnerOrdinal == combat.OwnerOrdinal && e.Payload is PublicOwnerEnded).EventOrdinal < reward.EventOrdinal);
        Assert.Contains(evidence.Events, e => e.OwnerOrdinal == reward.OwnerOrdinal && e.Payload is PublicOwnerEnded);
    }

}
