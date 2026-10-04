using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class ForcedEventLifecycleTests
{
    [Fact]
    public async Task RandomizedUnaffordablePurchaseRejectsOnlyTheIndependentSetupProposal()
    {
        var setup = new Scenario(Seed: "MERCHANT-AUDIT:0", Deck: Enumerable.Repeat("GrandFinale", 5).ToArray(),
            Hp: 20, MaxHp: 100, Gold: 50, Potions: ["FoulPotion"],
            ForcedEvent: new("FakeMerchant", "Hive", [], FoulPotionSlot: 0, PurchasedRelicSlots: [0]));
        await using var source = await CombatSession.CreateAsync(setup);
        Assert.Equal(1, source.StartGold); // The actual visible purchase costs 49.
        string original = PublicJson.Serialize(source.Observe());
        // The first independent proposal has the same slot item priced at 54.
        await Assert.ThrowsAsync<ConstructedSetupRejectedException>(() => CombatSession.CreateAsync(setup with
            { Seed = "NOSL-REPLAY:1B88176EBE04D966" }));
        var exhausted = await Assert.ThrowsAsync<PosteriorSamplingException>(() => BeliefSampler.SampleWorldAsync(source, 0, 8));
        Assert.Contains("8", exhausted.Message);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        // Static malformed choices are still errors, not rejected random hypotheses.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CombatSession.CreateAsync(setup with
            { ForcedEvent = setup.ForcedEvent! with { PurchasedRelicSlots = [99] } }));
    }

    public static IEnumerable<object[]> Owners => new[]
    {
        new ForcedEventScenario("BattlewornDummy", "Glory", ["SETTING_1"]),
        new ForcedEventScenario("BattlewornDummy", "Glory", ["SETTING_2"]),
        new ForcedEventScenario("BattlewornDummy", "Glory", ["SETTING_3"]),
        new ForcedEventScenario("DenseVegetation", "Overgrowth", ["REST", "FIGHT"]),
        new ForcedEventScenario("PunchOff", "Underdocks", ["I_CAN_TAKE_THEM", "FIGHT"]),
        new ForcedEventScenario("FakeMerchant", "Hive", [], FoulPotionSlot: 0),
        new ForcedEventScenario("TheLanternKey", "Hive", ["KEEP_THE_KEY", "FIGHT"]),
    }.Select(scenario => new object[] { scenario });

    private static Scenario Fixture(ForcedEventScenario owner, string seed = "NOSL-FORCED-OWNER") => new(
        Seed: seed, Deck: Enumerable.Repeat("GrandFinale", 5).ToArray(), Hp: 20, MaxHp: 100,
        Potions: owner.Event == "FakeMerchant" ? ["FoulPotion"] : [], Relics: ["MeatOnTheBone"],
        ForcedEvent: owner);

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task EveryOwnerMatchesNativeRunDriverAtTheFirstRewardDecision(ForcedEventScenario owner)
    {
        var setup = Fixture(owner);
        await using var session = await CombatSession.CreateAsync(setup);
        Assert.Equal("constructed-native-event", session.FixtureOrigin);
        Assert.False(session.HasNativeProvenance);
        Assert.False(BeliefSampler.UsesExchangeablePosterior(session));
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, BeliefSampler.PosteriorProfileFor(session));
        Assert.Throws<NotSupportedException>(() => session.ForkExact());
        var (nativePlayer, decisions, nativeEvent) = await RunNativeToBoundary(setup);
        Assert.Equal(decisions.RewardDrawsAtFirstDecision, session.State.Players[0].PlayerRng.Rewards.Counter);
        await PlayToTerminal(session);
        var terminal = await session.SettleAsync();
        var facts = Assert.IsType<ForcedEventSettlementFacts>(terminal.ForcedEvent);
        Assert.Equal("win", terminal.Result);
        Assert.False(facts.TimedOut);
        Assert.Equal(0, terminal.RewardSelectionsMade);
        Assert.Equal(decisions.HpAtBoundary ?? nativePlayer.Creature.CurrentHp, terminal.FinalHp);
        Assert.Equal(decisions.DeckAtBoundary ?? Deck(nativePlayer), Deck(session.State.Players[0]));
        Assert.Equal(decisions.GoldAtBoundary ?? nativePlayer.Gold, session.State.Players[0].Gold);
        Assert.Equal(decisions.RewardTypesAtBoundary ?? [], facts.RewardOpportunities.SelectMany(fact => Enumerable.Repeat(fact.Kind, fact.Count)).Order());
        Assert.Equal(decisions.RewardsAtBoundary,
            session.Room.GeneratedRewards.Concat(session.EventRewardOffers).Select(RewardSignature).SingleOrDefault());
        Assert.Equal(decisions.RewardDrawsAtBoundary ?? nativePlayer.PlayerRng.Rewards.Counter,
            session.State.Players[0].PlayerRng.Rewards.Counter);
        var recorded = RolloutRecorder.Settled(session, terminal, "event-ledger-integration", 1);
        Assert.True(recorded.HpEventDiagnosticsComplete);
        Assert.True(recorded.ResourceProvenanceComplete);
        Assert.Equal((double)terminal.FinalHp, terminal.StartHp - recorded.CumulativeHpDamage!.Value
            + recorded.HealingReceived!.Value + recorded.OtherHpAdjustment!.Value);
        // FoulPotion is consumed before the fixed anchor; Battleworn's offered
        // potion has not been selected. Neither is a combat inventory movement.
        Assert.Empty(recorded.ResourceEvents);
        Assert.Equal(owner.Event == "BattlewornDummy", facts.ReturnedToEvent);
        Assert.Equal(owner.Event != "BattlewornDummy", facts.ReturnPendingRewardDecisions);
        if (owner.Event == "BattlewornDummy" && owner.OptionKeys[0] == "SETTING_2")
        {
            Assert.Equal(2, session.State.Players[0].Deck.Cards.Count(card => card.CurrentUpgradeLevel == 1));
            Assert.Contains(RolloutRecorder.Settled(session, terminal, "test", 1).PermanentChanges,
                change => change.Kind == "permanent_deck_change");
        }
        if (owner.Event == "BattlewornDummy" && owner.OptionKeys[0] != "SETTING_2")
            Assert.Contains(RolloutRecorder.Settled(session, terminal, "test", 1).PermanentChanges,
                change => change.Kind.StartsWith("earned_event_reward_opportunity:"));
        if (owner.Event != "BattlewornDummy")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReturnToForcedEventAsync());
            Assert.NotNull(session.State.Players[0].PlayerCombatState);
            foreach (var set in session.Room.GeneratedRewards) await SkipExplicitly(set);
            await session.ReturnToForcedEventAsync();
            Assert.Same(session.ForcedEventRoom, session.State.RunState.CurrentRoom);
            Assert.Null(session.State.Players[0].PlayerCombatState);
            Assert.True(session.ForcedEventRoom!.Event.IsFinished);
            Assert.False(session.ForcedEventRoom.Event.IsAwaitingForcedCombat);
        }
        // Repeated settlement/return cannot apply automatic hooks or rewards twice, and a
        // later host continuation cannot rewrite the frozen pre-choice terminal facts.
        string settled = PublicJson.Serialize(terminal);
        string deckAfterReturn = Deck(session.State.Players[0]);
        await session.ReturnToForcedEventAsync();
        Assert.Same(terminal, await session.SettleAsync());
        Assert.Equal(settled, PublicJson.Serialize(await session.SettleAsync()));
        Assert.Equal(deckAfterReturn, Deck(session.State.Players[0]));
        Assert.True(nativeEvent.Event.IsFinished || decisions.RewardTypesAtBoundary is not null);
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task ExactReplayOwnsIndependentEventAndRewardState(ForcedEventScenario owner)
    {
        await using var source = await CombatSession.CreateAsync(Fixture(owner) with { Hp = 10000, MaxHp = 10000 });
        await source.StepAsync(source.Observe().Actions.Single(action => action.Kind == "end_turn"));
        await using var branch = await source.ForkForContinuationAsync();
        Assert.NotSame(source.ForcedEventRoom, branch.ForcedEventRoom);
        Assert.NotSame(source.ForcedEventRoom!.Event, branch.ForcedEventRoom!.Event);
        Assert.NotSame(source.State.RunState, branch.State.RunState);
        string root = PublicJson.Serialize(source.Observe());
        string originalDeck = Deck(source.State.Players[0]);
        await PlayToTerminal(branch);
        var branchFacts = await branch.SettleAsync();
        Assert.Equal(root, PublicJson.Serialize(source.Observe()));
        Assert.Equal(originalDeck, Deck(source.State.Players[0]));
        Assert.Empty(source.Room.GeneratedRewards);
        await PlayToTerminal(source);
        Assert.Equal(PublicJson.Serialize(branchFacts), PublicJson.Serialize(await source.SettleAsync()));
        if (source.Room.GeneratedRewards.Count != 0)
        {
            Assert.NotSame(source.Room.Rewards, branch.Room.Rewards);
            Assert.NotSame(source.Room.Rewards!.Gold.Player, branch.Room.Rewards!.Gold.Player);
        }
    }

    [Theory]
    [InlineData("SETTING_1")]
    [InlineData("SETTING_2")]
    [InlineData("SETTING_3")]
    public async Task BattlewornTimeoutReturnsWithoutTierRewardOrUpgrade(string tier)
    {
        await using var session = await CombatSession.CreateAsync(new(Deck: ["DefendSilent"],
            ForcedEvent: new("BattlewornDummy", "Glory", [tier])));
        string deck = Deck(session.State.Players[0]);
        for (int turn = 0; session.Observe().Status == "player_decision" && turn < 4; turn++)
            await session.StepAsync(session.Observe().Actions.Single(action => action.Kind == "end_turn"));
        var terminal = await session.SettleAsync();
        Assert.Equal("win", terminal.Result); // Native escape is a combat win, but a failed event tier.
        Assert.True(terminal.ForcedEvent!.TimedOut);
        Assert.True(terminal.ForcedEvent.ReturnedToEvent);
        Assert.True(terminal.ForcedEvent.EventFinished);
        Assert.Empty(terminal.ForcedEvent.RewardOpportunities);
        Assert.Equal(deck, Deck(session.State.Players[0]));
        Assert.Empty(session.Room.GeneratedRewards);
    }

    [Theory]
    [InlineData("PunchOff", "Underdocks", "I_CAN_TAKE_THEM")]
    [InlineData("TheLanternKey", "Hive", "KEEP_THE_KEY")]
    public async Task LossReturnsToEventWithoutOffers(string owner, string act, string firstOption)
    {
        await using var session = await CombatSession.CreateAsync(new(Deck: ["DefendSilent"], Hp: 1,
            ForcedEvent: new(owner, act, [firstOption, "FIGHT"])));
        for (int turn = 0; session.Observe().Status == "player_decision" && turn < 20; turn++)
            await session.StepAsync(session.Observe().Actions.Single(action => action.Kind == "end_turn"));
        var terminal = await session.SettleAsync();
        Assert.Equal("loss", terminal.Result);
        Assert.True(terminal.ForcedEvent!.ReturnedToEvent);
        Assert.True(terminal.ForcedEvent.EventFinished);
        Assert.Empty(terminal.ForcedEvent.RewardOpportunities);
        Assert.Null(session.State.Players[0].PlayerCombatState);
    }

    [Fact]
    public async Task EventPreEffectsAreOutsideFixedCombatAnchorAndOffersAreNotAssets()
    {
        await using var dense = await CombatSession.CreateAsync(Fixture(new("DenseVegetation", "Overgrowth", ["REST", "FIGHT"])));
        Assert.Equal(50, dense.StartHp);
        Assert.Equal(new[] { "wriggler1", "wriggler2", "wriggler3", "wriggler4" }, dense.State.Enemies.Select(enemy => enemy.SlotName));
        await using var merchant = await CombatSession.CreateAsync(Fixture(new("FakeMerchant", "Hive", [], FoulPotionSlot: 0)));
        Assert.All(merchant.StartPotions, Assert.Null);
        Assert.All(merchant.Observe().Observation!.Potions, Assert.Null);
        await PlayToTerminal(merchant);
        var facts = await merchant.SettleAsync();
        Assert.Equal(300, facts.ForcedEvent!.RewardOpportunities.Single(opportunity => opportunity.Kind == "GoldReward").GoldAmount);
        Assert.Equal(7, facts.ForcedEvent.RewardOpportunities.Single(opportunity => opportunity.Kind == "RelicReward").Count);
        Assert.Equal(merchant.StartGold, merchant.State.Players[0].Gold);
        Assert.DoesNotContain(merchant.State.Players[0].Relics, relic => relic is FakeMerchantsRug);
        var rollout = RolloutRecorder.Settled(merchant, facts, "test", 1);
        Assert.Contains(rollout.PermanentChanges, change => change.Kind == "earned_extra_reward_opportunity:RelicReward" && change.Amount == 7);
        Assert.DoesNotContain(rollout.ResourceEvents, resource => resource.ResourceId == "FoulPotion");
    }

    [Fact]
    public async Task WholeSetupPosteriorUsesIndependentEventOwnerAndSeedWithoutExposingOffers()
    {
        await using var source = await CombatSession.CreateAsync(Fixture(new("BattlewornDummy", "Glory", ["SETTING_3"])));
        await using var proposal = await BeliefSampler.SampleWorldAsync(source, 17, maxReplayAttempts: 8);
        Assert.NotEqual(source.State.RunState.Rng.Seed, proposal.State.RunState.Rng.Seed);
        Assert.NotSame(source.ForcedEventRoom!.Event, proposal.ForcedEventRoom!.Event);
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(proposal.Observe()));
        Assert.Empty(source.Room.GeneratedRewards);
        Assert.Empty(proposal.EventRewardOffers);
        Assert.DoesNotContain("rewardOpportunities", PublicJson.Serialize(proposal.Observe()));
        await using var continuation = await proposal.ForkForContinuationAsync();
        Assert.Equal(proposal.State.RunState.Rng.Seed, continuation.State.RunState.Rng.Seed);
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task EventTeacherCompletesIndependentBranchesAndMasksUnpricedNativeUpgrades(string mode)
    {
        await using var source = await CombatSession.CreateAsync(Fixture(new("BattlewornDummy", "Glory", ["SETTING_2"])));
        string original = PublicJson.Serialize(source.Observe());
        var result = await CombatTeacher.EvaluateAsync(source, new()
        {
            Mode = mode, EvaluationSeeds = [42], ExplorationSeeds = mode == "T1" ? [41] : [],
            MaxDecisions = 100, MaxPosteriorAttempts = 8, TreeDepth = 1,
        });
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, result.Scope);
        Assert.All(result.Candidates, candidate =>
        {
            var outcome = Assert.Single(candidate.Outcomes);
            Assert.True(outcome.IsTrueTerminal, outcome.Detail);
            Assert.True(outcome.SettlementComplete);
            Assert.Equal(32, outcome.HpAfterSettlement);
            Assert.Contains(outcome.PermanentChanges, change => change.Kind == "permanent_deck_change");
            Assert.Null(candidate.Evaluation.ExpectedCost);
            Assert.Equal(1, candidate.Evaluation.ValueUnresolved);
        });
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Empty(source.Room.GeneratedRewards);
        Assert.All(source.State.Players[0].Deck.Cards, card => Assert.Equal(0, card.CurrentUpgradeLevel));
        using var dataset = System.Text.Json.JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "event-fixture", "battleworn", "declared-event", [42], mode == "T1" ? [41] : [])));
        Assert.Equal("constructed", dataset.RootElement.GetProperty("audit_only").GetProperty("source_kind").GetString());
        Assert.Equal("nosl.student.public.v2", dataset.RootElement.GetProperty("public_input").GetProperty("schema_version").GetString());
        Assert.True(dataset.RootElement.GetProperty("audit_only").GetProperty("constructed_event_fixture").GetBoolean());
        Assert.Equal("nosl.dataset.forced-events.v2", dataset.RootElement.GetProperty("audit_only").GetProperty("dataset_version").GetString());
        Assert.DoesNotContain("rewardOpportunities", dataset.RootElement.GetProperty("public_input").GetRawText());
    }

    [Fact]
    public async Task MerchantPurchasesPreserveRevealedStockAndOnlyOfferUnpurchasedRelics()
    {
        var owner = new ForcedEventScenario("FakeMerchant", "Hive", [], FoulPotionSlot: 0, PurchasedRelicSlots: [0]);
        await using var session = await CombatSession.CreateAsync(Fixture(owner) with { Gold = 1000 });
        var merchant = Assert.IsType<FakeMerchant>(session.ForcedEventRoom!.Event);
        var purchased = merchant.Inventory.Relics[0];
        Assert.True(purchased.Purchased);
        Assert.Equal(1000 - purchased.Price, session.StartGold);
        Assert.Contains(purchased.Relic, session.State.Players[0].Relics);
        var stockHistory = Assert.Single(session.Observe().Observation!.History, entry => entry.Kind == "event_merchant_inventory_revealed");
        foreach (var entry in merchant.Inventory.Relics) Assert.Contains(entry.Relic.GetType().Name, stockHistory.Detail);
        await using var branch = await session.ForkForContinuationAsync();
        Assert.NotSame(merchant.Inventory, Assert.IsType<FakeMerchant>(branch.ForcedEventRoom!.Event).Inventory);
        await PlayToTerminal(branch);
        var facts = await branch.SettleAsync();
        Assert.Equal(6, facts.ForcedEvent!.RewardOpportunities.Single(opportunity => opportunity.Kind == "RelicReward").Count);
        Assert.DoesNotContain(branch.Room.Rewards!.ExtraRewards.OfType<RelicReward>(),
            reward => reward.Relic!.GetType() == purchased.Relic.GetType());
        Assert.Empty(session.Room.GeneratedRewards);
    }

    [Fact]
    public async Task InvalidEventPathsAndOrdinaryRoomSubstitutionsFailClosed()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CombatSession.CreateAsync(new(ForcedEvent: new("PunchOff", "Underdocks", ["I_CAN_TAKE_THEM", "FIGHT"], FixtureFloor: 0))));
        await Assert.ThrowsAsync<ArgumentException>(() => CombatSession.CreateAsync(new(ForcedEvent: new("BattlewornDummy", "Overgrowth", ["SETTING_2"]))));
        await Assert.ThrowsAsync<ArgumentException>(() => CombatSession.CreateAsync(new(ForcedEvent: new("DenseVegetation", "Overgrowth", ["FIGHT"]))));
        await Assert.ThrowsAsync<ArgumentException>(() => CombatSession.CreateAsync(new(ForcedEvent: new("FakeMerchant", "Hive", [], FoulPotionSlot: 0))));
        await Assert.ThrowsAsync<ArgumentException>(() => CombatSession.CreateAsync(new(EnemyHp: 1, ForcedEvent: new("BattlewornDummy", "Glory", ["SETTING_2"]))));
        foreach (var encounter in EncounterCoverage.AllEncounters.Where(entry => entry.RequiresEventContext))
            Assert.Throws<NotSupportedException>(() => EncounterCoverage.CreateRun("blocked", encounter.Name));
    }

    private static async Task PlayToTerminal(CombatSession session)
    {
        for (int step = 0; session.Observe().Status is "player_decision" or "card_choice"; step++)
        {
            Assert.True(step < 100, "Native event fixture did not terminate within the bounded integration run.");
            await session.StepAsync(PublicDiagnosticPolicy.Choose(session.Observe()));
        }
    }

    private static string Deck(Sts2Sim.Core.Entities.Players.Player player) =>
        string.Join(',', player.Deck.Cards.Select(card => card.GetType().Name + ":" + card.CurrentUpgradeLevel).Order());

    // Test-only comparison of full native offers establishes RNG/order fidelity. The adapter
    // and public policy never inspect these identities as combat input or opportunity values.
    private static string RewardSignature(RewardsSet rewards) => PublicJson.Serialize(new
    {
        gold = rewards.Gold.Amount, potion = rewards.Potion?.Potion?.GetType().Name,
        relic = rewards.Relic?.Relic?.GetType().Name,
        cards = rewards.Card.Options.Select(card => card.GetType().Name + ":" + card.CurrentUpgradeLevel).ToArray(),
        extras = rewards.ExtraRewards.Select(reward => reward switch
        {
            RelicReward relic => "relic:" + relic.Relic?.GetType().Name,
            PotionReward potion => "potion:" + potion.Potion?.GetType().Name,
            SpecialCardReward card => "special_card:" + card.Card.GetType().Name,
            _ => reward.GetType().Name,
        }).ToArray(),
    });

    private static async Task SkipExplicitly(RewardsSet rewards)
    {
        await rewards.Gold.Skip();
        if (rewards.Potion is not null) await rewards.Potion.Skip();
        if (rewards.Relic is not null) await rewards.Relic.Skip();
        if (!rewards.Card.IsResolved) await rewards.Card.Skip();
        foreach (Reward reward in rewards.ExtraRewards)
            if (reward is TakeableReward takeable) await takeable.Skip();
            else if (reward is CardReward card) await card.Skip();
            else throw new InvalidOperationException("Unexpected event reward in explicit test skip.");
    }

    private static async Task<(Sts2Sim.Core.Entities.Players.Player, NativeBoundarySource, EventRoom)> RunNativeToBoundary(Scenario setup)
    {
        var owner = setup.ForcedEvent!;
        var run = CombatSession.CreateForcedEventRun(setup.Seed, owner);
        var player = run.Players.Single();
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        for (int index = 0; index < 5; index++)
        {
            var card = (GrandFinale)ModelDb.Card<GrandFinale>().MutableClone();
            card.AssignOwner(player); player.Deck.AddInternal(card);
        }
        player.Creature.SetMaxHpInternal(100); player.Creature.SetCurrentHpInternal(20);
        await RelicCmd.Obtain((RelicModel)ModelDb.Relic<MeatOnTheBone>().MutableClone(), player);
        if (owner.Event == "FakeMerchant")
        {
            var potion = (PotionModel)ModelDb.Potion<FoulPotion>().MutableClone();
            potion.AssignOwner(player); player.AddPotionInternal(potion);
        }
        var model = (EventModel)ModelDb.All<EventModel>().Single(model => model.GetType().Name == owner.Event).MutableClone();
        var room = new EventRoom(() => model);
        run.PushRoom(room); await room.Enter(run);
        var decisions = new NativeBoundarySource(owner);
        try { await new RunDriver(run, decisions).DriveEventAsync(room); }
        catch (RewardBoundaryReached) { }
        return (player, decisions, room);
    }

    private sealed class RewardBoundaryReached : Exception;
    private sealed class NativeBoundarySource(ForcedEventScenario setup) : IRunDecisionSource
    {
        private readonly Queue<string> _options = new(setup.OptionKeys);
        public int? HpAtBoundary { get; private set; }
        public int? GoldAtBoundary { get; private set; }
        public string? DeckAtBoundary { get; private set; }
        public string[]? RewardTypesAtBoundary { get; private set; }
        public string? RewardsAtBoundary { get; private set; }
        public int? RewardDrawsAtFirstDecision { get; private set; }
        public int? RewardDrawsAtBoundary { get; private set; }
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new NotSupportedException();
        public Task<EventOption> ChooseEventOptionAsync(IReadOnlyList<EventOption> options)
        {
            string key = _options.Dequeue();
            return Task.FromResult(options.Single(option => option.Key == key));
        }
        public Task<CustomEventDecision> ChooseCustomEventActionAsync(EventModel model) =>
            Task.FromResult<CustomEventDecision>(new CustomEventDecision.UsePotion(model.Owner.PotionSlots[setup.FoulPotionSlot!.Value]!));
        public Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
        {
            var player = state.Players.Single();
            RewardDrawsAtFirstDecision ??= player.PlayerRng.Rewards.Counter;
            var card = player.PlayerCombatState!.Hand.Cards.FirstOrDefault(card => card.CanPlay(out _));
            return Task.FromResult<CombatDecision>(card is null ? new CombatDecision.EndTurn() : new CombatDecision.PlayCard(card, null));
        }
        public Task<RewardDecision> ChooseRewardActionAsync(RewardsSet rewards)
        {
            HpAtBoundary = rewards.Player.Creature.CurrentHp; GoldAtBoundary = rewards.Player.Gold;
            DeckAtBoundary = Deck(rewards.Player);
            RewardsAtBoundary = RewardSignature(rewards);
            RewardDrawsAtBoundary = rewards.Player.PlayerRng.Rewards.Counter;
            var types = new List<string>();
            if (rewards.Gold.Amount > 0) types.Add(nameof(GoldReward));
            if (rewards.Potion is not null) types.Add(nameof(PotionReward));
            if (rewards.Relic is not null) types.Add(nameof(RelicReward));
            if (!rewards.Card.IsResolved) types.Add(nameof(CardReward));
            types.AddRange(rewards.ExtraRewards.Select(reward => reward.GetType().Name));
            RewardTypesAtBoundary = types.Order().ToArray();
            throw new RewardBoundaryReached();
        }
    }
}
