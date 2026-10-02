using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

public sealed partial class CombatSession
{
    private EventRoom? _forcedEventRoom;
    private HashSet<Creature> _forcedEnemies = [];
    private BattlewornDummyTimeLimitPower[] _forcedTimeoutPowers = [];
    private ForcedCombatOutcome? _forcedOutcome;
    private Task? _forcedReturn;
    private readonly List<RewardsSet> _eventRewardOffers = [];

    // Audit/settlement surfaces only. No reward option identities are projected into Observe().
    public ForcedEventSettlementFacts? ForcedEventSettlement { get; private set; }
    public string FixtureOrigin => HasNativeProvenance ? "native-run-import" :
        _forcedEventRoom is null ? "constructed-combat" : "constructed-native-event";
    internal EventRoom? ForcedEventRoom => _forcedEventRoom;
    internal IReadOnlyList<RewardsSet> EventRewardOffers => _eventRewardOffers;

    internal static RunState CreateForcedEventRun(string seed, ForcedEventScenario scenario)
    {
        if (!EncounterCoverage.AllForcedEvents.Any(entry => entry.Event == scenario.Event))
            throw new NotSupportedException($"Unsupported native forced-event owner: {scenario.Event}");
        ActDefinition act = scenario.Act switch
        {
            "Overgrowth" => new Overgrowth(), "Underdocks" => new Underdocks(),
            "Hive" => new Hive(), "Glory" => new Glory(),
            _ => throw new ArgumentException($"Unknown event fixture act: {scenario.Act}"),
        };
        Type eventType = ContentRegistry.AllTypes.Single(type => type.Name == scenario.Event && typeof(EventModel).IsAssignableFrom(type));
        if (!act.EventPool.Contains(eventType) && !SharedEventPool.All.Contains(eventType))
            throw new ArgumentException($"{scenario.Event} does not belong to the native {scenario.Act} event pool.");
        var run = new RunState(seed, new ActDefinition[]
            { scenario.Act == "Underdocks" ? new Underdocks() : new Overgrowth(), new Hive(), new Glory() }, 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        while (run.CurrentActIndex < act.Index) run.AdvanceToNextAct();
        if (scenario.FixtureFloor < 0 || scenario.FixtureFloor > run.Act.BaseNumberOfRooms)
            throw new ArgumentOutOfRangeException(nameof(scenario.FixtureFloor));
        // Valid map coordinates keep location-sensitive native hooks usable. No rooms have
        // been played here; this synthetic positioning is part of the declared fixture prior.
        var point = run.Map.StartingMapPoint;
        for (int floor = 0; floor < scenario.FixtureFloor; floor++)
        {
            run.AddVisitedMapCoord(point.coord);
            if (floor + 1 < scenario.FixtureFloor)
                point = point.Children.OrderBy(child => child.coord).First();
        }
        return run;
    }

    private async Task<CombatSession> EnterForcedEventAsync(RunState run, ForcedEventScenario scenario)
    {
        var model = Model<EventModel>(scenario.Event, EncounterCoverage.AllForcedEvents.Select(entry => entry.Event).ToArray());
        if (!model.IsAllowed(run)) throw new ArgumentException($"{scenario.Event} is not legal for this declared run state.");
        var eventRoom = new EventRoom(() => model);
        _forcedEventRoom = eventRoom;
        run.PushRoom(eventRoom);
        await eventRoom.Enter(run);
        string? revealedInventory = null;
        foreach (string key in scenario.OptionKeys)
        {
            var option = model.CurrentOptions.SingleOrDefault(option => option.Key == key)
                ?? throw new ArgumentException($"Event option {key} is not currently offered by {scenario.Event}.");
            await model.ChooseOption(option);
            if (model.HasPendingRewardOffers)
                throw new NotSupportedException("Event setup reached an unresolved pre-combat reward decision.");
        }
        if (scenario.InspectMerchantInventory || scenario.PurchasedRelicSlots is { Length: > 0 })
        {
            if (model is not FakeMerchant merchant) throw new ArgumentException("Only FakeMerchant has a merchant inventory.");
            merchant.IsInventoryOpen = true;
            // The visible shop inventory is public history when opened. It is not read
            // from future combat rewards, and unopened stock remains private.
            revealedInventory = PublicJson.Serialize(merchant.Inventory.Relics.Select(entry => new
                { relic = entry.Relic.GetType().Name, entry.Price }).ToArray());
            foreach (int slot in scenario.PurchasedRelicSlots ?? [])
            {
                if (slot < 0 || slot >= merchant.Inventory.Relics.Count) throw new ArgumentOutOfRangeException(nameof(scenario.PurchasedRelicSlots));
                var entry = merchant.Inventory.Relics[slot];
                // Prices depend on the independent proposal seed. A purchase that
                // was observed in the source must be possible in the proposed world.
                // Keep malformed/repeated slots and other native failures as errors.
                if (!entry.Purchased && model.Owner.Gold < entry.Price)
                    throw new ConstructedSetupRejectedException("Declared merchant purchase is unaffordable under this setup proposal.");
                await CustomEventDecisionPolicy.ExecuteAsync(model, new CustomEventDecision.BuyRelic(entry));
            }
            merchant.IsInventoryOpen = false;
        }
        if (scenario.FoulPotionSlot is int potionSlot)
        {
            if (model is not FakeMerchant || potionSlot < 0 || potionSlot >= model.Owner.PotionSlots.Count
                || model.Owner.PotionSlots[potionSlot] is not FoulPotion potion)
                throw new ArgumentException("FakeMerchant requires an owned FoulPotion in the declared slot.");
            await CustomEventDecisionPolicy.ExecuteAsync(model, new CustomEventDecision.UsePotion(potion));
        }
        if (!model.HasPendingForcedCombat)
            throw new ArgumentException("The declared legal event path did not request a forced combat.");

        var player = model.Owner;
        // The fixed combat anchor is after REST healing, purchases, potion use and all other
        // native event effects; none of them are charged again as combat resource changes.
        StartHp = player.Creature.CurrentHp; StartMaxHp = player.Creature.MaxHp;
        StartGold = player.Gold; StartPotions = player.PotionSlots.Select(potion => potion?.GetType().Name).ToArray();
        InitialAssets = CombatAssetSnapshot.Capture(player);
        _knowledge.OutcomeLedger.Begin(player);
        _knowledge.BeginCombat();
        if (revealedInventory is not null) _knowledge.Events.Add(new("event_merchant_inventory_revealed", revealedInventory));
        _knowledge.Events.Add(new("forced_event_context", PublicJson.Serialize(new
        {
            owner = scenario.Event, act = run.Act.GetType().Name, floor = run.TotalFloor, options = scenario.OptionKeys,
            usedFoulPotionSlot = scenario.FoulPotionSlot, purchasedRelicSlots = scenario.PurchasedRelicSlots ?? [],
        })));
        Room = eventRoom.CreatePendingForcedCombatRoom();
        Room.ConfigureCardSelectionSource(this); Room.ConfigureObserver(_knowledge);
        run.PushRoom(Room);
        _operation = EnterOwnedCombatAsync();
        State = Room.Engine.State;
        await AwaitBoundaryAsync();
        if (_request is null)
        {
            Room.Engine.CheckWinCondition();
            if (!Room.Engine.IsInProgress) await SettleAsync();
        }
        AssertScope(); _publicTrace.Add(PublicJson.Serialize(Observe()));
        return this;

        async Task EnterOwnedCombatAsync()
        {
            await Room.Enter(run);
            _forcedEnemies = Room.Engine.State.Enemies.ToHashSet();
            _forcedTimeoutPowers = _forcedEnemies.Select(enemy => enemy.GetPower<BattlewornDummyTimeLimitPower>())
                .OfType<BattlewornDummyTimeLimitPower>().ToArray();
            // Exactly the native RunDriver order: register unpopulated extras after entry;
            // CombatRoom populates them only after ordinary rewards at victory settlement.
            foreach (Reward reward in model.ForcedCombatExtraRewards) Room.QueueExtraReward(player, reward);
        }
    }

    private async Task ResolveForcedEventOutcomeAsync()
    {
        var owner = _forcedEventRoom!.Event;
        await Room.ResolveOutcomeAsync(owner.GenerateForcedCombatRewards);
        _forcedOutcome = new ForcedCombatOutcome(Room.Won,
            State.EscapedCreatures.Any(_forcedEnemies.Contains) || _forcedTimeoutPowers.Any(power => power.HasExpired));
        // RunDriver returns to the owner only after its combat reward decisions. Do not
        // silently claim or skip them just to run the owner's later automatic callback.
        if (!HasUnresolvedCombatRewards) await ReturnToForcedEventAsync();
        RefreshForcedEventFacts();
    }

    /// <summary>
    /// Native host continuation after reward decisions made outside combat evaluation.
    /// Refuses to cross any unchosen combat reward. It never selects a reward itself and
    /// does not change the already frozen terminal scoring facts or the combat anchor.
    /// </summary>
    internal Task ReturnToForcedEventAsync()
    {
        if (_forcedReturn is not null) return _forcedReturn;
        if (_forcedEventRoom is null || _forcedOutcome is null) throw new InvalidOperationException("No settled forced event.");
        if (HasUnresolvedCombatRewards)
            throw new InvalidOperationException("Forced-event return must wait for explicit combat reward decisions.");
        return _forcedReturn = ReturnOnceAsync();
    }

    private async Task ReturnOnceAsync()
    {
        var run = (RunState)State.RunState;
        await Room.Exit(run);
        if (!ReferenceEquals(run.CurrentRoom, Room)) throw new InvalidOperationException("Forced combat is not the active room.");
        run.PopCurrentRoom();
        if (!ReferenceEquals(run.CurrentRoom, _forcedEventRoom)) throw new InvalidOperationException("Forced event owner was lost.");
        if (_forcedEventRoom!.Event.IsAwaitingForcedCombat)
            _forcedEventRoom.Event.ResumeAfterForcedCombat(_forcedOutcome!.Value);
        while (_forcedEventRoom.Event.TryDequeuePendingRewardOffer(out var rewards)) _eventRewardOffers.Add(rewards);
        RefreshForcedEventFacts(returned: true);
    }

    private void RefreshForcedEventFacts(bool? returned = null)
    {
        bool didReturn = returned ?? ForcedEventSettlement?.ReturnedToEvent ?? false;
        ForcedEventSettlement = new(_forcedEventRoom!.Event.GetType().Name, _forcedOutcome!.Value.TimedOut,
            didReturn, _forcedEventRoom.Event.IsFinished, !didReturn && HasUnresolvedCombatRewards,
            OpportunityFacts(Room.GeneratedRewards, "forced_combat_offer")
                .Concat(OpportunityFacts(_eventRewardOffers, "event_return_offer")).ToArray());
    }

    private static IEnumerable<Reward> RewardsIn(RewardsSet set)
    {
        yield return set.Gold;
        if (set.Potion is not null) yield return set.Potion;
        if (set.Relic is not null) yield return set.Relic;
        yield return set.Card;
        foreach (Reward reward in set.ExtraRewards) yield return reward;
    }

    private bool HasUnresolvedCombatRewards => Room.GeneratedRewards.Any(set => RewardsIn(set).Any(reward => !reward.IsResolved));

    private static IEnumerable<RewardOpportunity> OpportunityFacts(IEnumerable<RewardsSet> sets, string source)
    {
        // Presence/type/count and offered gold are opportunity facts. Never read Options,
        // Relic, Potion or SpecialCardReward.Card to extract future selection identities.
        var rewards = sets.SelectMany(RewardsIn).Where(reward => reward switch
        {
            GoldReward gold => gold.Amount > 0,
            CardReward card => !card.IsResolved,
            _ => true,
        }).ToArray();
        foreach (var group in rewards.GroupBy(reward => reward.GetType().Name, StringComparer.Ordinal))
            yield return new(group.Key, group.Count(), source,
                group.Key == nameof(GoldReward) ? group.Cast<GoldReward>().Sum(gold => gold.Amount) : null);
    }

    internal IEnumerable<RewardOpportunity> EventReturnOpportunities =>
        OpportunityFacts(_eventRewardOffers, "event_return_offer");
}
