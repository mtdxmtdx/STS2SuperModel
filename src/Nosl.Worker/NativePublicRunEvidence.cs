using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// The sole native-to-public evidence projection. Native references identify open
/// observer scopes only; every emitted value is an explicitly selected public DTO.
/// No source trace, encounter catalog, random state, or outcome ledger is an input.
/// </summary>
internal sealed class NativePublicRunEvidence(RunState run, string? mapObservationProfile = null,
    Action<PublicRunEvidenceEvent>? onAppended = null)
{
    private PublicRunEvidenceRecorder? _recorder;
    private string EvidenceVersion => PublicMapObservationProfiles.UsesCompleteGraph(mapObservationProfile)
        ? PublicRunEvidence.CompleteMapVersion : PublicRunEvidence.Version;
    private PublicRunEvidenceRecorder Recorder => _recorder ??= new(null, onAppended, EvidenceVersion);
    private readonly Dictionary<AbstractRoom, long> _rooms = new(ReferenceEqualityComparer.Instance);
    private EventRoom? _declaredEventRoom;
    private readonly List<(RewardsSet Set, long Owner)> _rewards = [];
    private readonly Dictionary<RewardsSet, long> _lastRewardOffers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RewardsSet, CardReward> _rerolled = new(ReferenceEqualityComparer.Instance);
    private long? _combat, _combatParent;
    private CombatRoom? _combatRoom;
    private bool _floorObserved;

    internal static PublicEvidenceAssets Assets(Player player)
    {
        var entry = NativeEntryAssets.Capture(CombatAssetSnapshot.Capture(player), player.Creature.CurrentHp,
            player.PotionSlots.Select(p => p?.GetType().Name).ToArray());
        return new(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
    }
    private static PublicRelic Relic(RelicModel relic) => new(relic.GetType().Name,
        PublicRelicDetails.Details(relic), PublicRelicDetails.Cards(relic), PublicRelicDetails.SelectedModel(relic));
    internal void BeginRun(bool observedNativeStart)
    {
        if (_recorder is not null) { Recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted); return; }
        _recorder = new(observedNativeStart ? new("Silent", 10, Assets(run.Players.Single())) : null, onAppended, EvidenceVersion);
    }
    internal PublicRunEvidence Capture() => Recorder.Capture();
    internal void EnterFloor() => _floorObserved = true;
    // Explicitly observed room entry for a declared fixture does not assert a
    // map traversal or a native run start. Call before the actual Enter hook.
    internal void DeclaredEventEntering(EventRoom room)
    {
        if (!ReferenceEquals(run.CurrentRoom, room) || _rooms.ContainsKey(room) || _declaredEventRoom is not null)
            throw new InvalidOperationException("Declared event entry must observe one newly owned native room");
        _rooms.Add(room, Begin(PublicEvidenceOwnerKind.Event));
        _declaredEventRoom = room;
    }
    internal void DeclaredEventReturned()
    {
        // Loss/no-reward settlement is notified after the native event resumes.
        // A reward-enabled victory is intentionally still suspended before its
        // first reward decision, so it must not receive a fabricated owner end.
        if (_combat is not null || _declaredEventRoom is not { } room
            || !ReferenceEquals(run.CurrentRoom, room) || !room.Event.IsFinished) return;
        if (_rooms.Remove(room, out long owner)) End(owner);
        _declaredEventRoom = null;
    }
    private long Begin(PublicEvidenceOwnerKind kind, long? parent = null, bool complete = true) =>
        Recorder.BeginOwner(kind, run.CurrentActIndex, run.TotalFloor, parent, complete);
    private long? RoomOwner()
    {
        if (run.CurrentRoom is not (EventRoom or RestSiteRoom or MerchantRoom)) return null;
        var room = run.CurrentRoom;
        if (_rooms.TryGetValue(room, out long existing)) return existing;
        var kind = room switch { EventRoom => PublicEvidenceOwnerKind.Event,
            RestSiteRoom => PublicEvidenceOwnerKind.Rest, _ => PublicEvidenceOwnerKind.Shop };
        long owner = Begin(kind, complete: _floorObserved);
        _rooms.Add(room, owner);
        return owner;
    }
    private long? ParentOwner() => _rewards.Count > 0 ? _rewards[^1].Owner
        : RoomOwner() ?? (run.CurrentRoom is CombatRoom ? _combatParent : null);
    private void End(long owner, PublicEvidenceOwnerOutcome outcome = PublicEvidenceOwnerOutcome.Completed) =>
        Recorder.Record(owner, new PublicOwnerEnded(outcome, Assets(run.Players.Single())));
    internal void ExitFloor()
    {
        // Reward owners close on the actual Done callback, including nested offers.
        // A remaining scope means native execution skipped a declared lifecycle hook.
        while (_rewards.Count > 0)
        {
            var reward = _rewards[^1]; _rewards.RemoveAt(_rewards.Count - 1);
            Recorder.RecordGap(reward.Owner, PublicEvidenceGapReason.Interrupted);
            End(reward.Owner, PublicEvidenceOwnerOutcome.Interrupted);
        }
        foreach (long owner in _rooms.Values.Reverse()) End(owner);
        _rooms.Clear(); _floorObserved = false; _combatParent = null;
    }
    internal void CombatEntering()
    {
        if (_combat is not null) throw new InvalidOperationException("Public combat scope did not settle");
        _combatParent = ParentOwner();
        _combat = Begin(PublicEvidenceOwnerKind.Combat, _combatParent);
    }
    internal void CombatStarted() => _combatRoom = run.CurrentRoom as CombatRoom;
    internal DecisionPacket Decision(DecisionPacket packet)
    {
        if (_combat is not long owner) throw new InvalidOperationException("Missing public combat owner");
        Recorder.ObserveCombatDecision(owner, packet);
        return packet with { PublicEvidence = Capture() };
    }
    internal void Action(PublicAction action)
    {
        if (_combat is not long owner) throw new InvalidOperationException("Missing public combat owner");
        var decision = Recorder.Capture().Events.Last(e => e.OwnerOrdinal == owner && e.Payload is PublicCombatDecision);
        Recorder.Record(owner, new PublicCombatActionTaken(decision.EventOrdinal, action));
    }
    internal void CombatSettled(IReadOnlyList<PublicEvent> history)
    {
        if (_combat is not long owner) return;
        Recorder.ObserveCombatHistory(owner, history);
        var outcome = run.Players.Single().Creature.CurrentHp <= 0 ? PublicEvidenceOwnerOutcome.Defeat
            : _combatRoom?.Engine.Won == true ? PublicEvidenceOwnerOutcome.Victory : PublicEvidenceOwnerOutcome.Escaped;
        End(owner, outcome);
        _combat = null; _combatRoom = null;
    }
    internal void EventOptions(IReadOnlyList<EventOption> options, EventOption chosen)
    {
        long owner = RoomOwner() ?? Begin(PublicEvidenceOwnerKind.Event, complete: false);
        bool ambiguous = options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != options.Count;
        if (ambiguous) Recorder.RecordGap(owner, PublicEvidenceGapReason.AmbiguousVisibility);
        string Key(int i) => ambiguous ? "option:" + i + ":" + options[i].Key : options[i].Key;
        long observed = Recorder.Record(owner, new PublicOptionsObserved(options.Select((o, i) =>
            new PublicVisibleOption(Key(i), o.IsLocked)).ToImmutableArray()));
        int selected = Enumerable.Range(0, options.Count).Single(i => ReferenceEquals(options[i], chosen));
        Recorder.Record(owner, new PublicOptionChosen(observed, Key(selected)));
    }
    internal void CustomEvent()
    {
        // Custom minigames expose heterogeneous screens; no common public option
        // snapshot exists at this seam. Do not serialize the event or action union.
        long owner = RoomOwner() ?? Begin(PublicEvidenceOwnerKind.Event, complete: false);
        Recorder.RecordGap(owner, PublicEvidenceGapReason.UnsupportedObservation);
    }
    internal void OutsideAutomaticSelection()
    {
        // A bypassed card-selection UI does not prove that its whole candidate
        // list was displayed. Preserve the missing observation, never expose it.
        long owner = Begin(PublicEvidenceOwnerKind.OutsideChoice, ParentOwner());
        Recorder.RecordGap(owner, PublicEvidenceGapReason.AmbiguousVisibility);
        Recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
    }
    internal void OutsideCards(PublicChoice choice, int[] selected)
    {
        long owner = Begin(PublicEvidenceOwnerKind.OutsideChoice, ParentOwner());
        long observed = Recorder.Record(owner, new PublicCardsObserved(choice));
        Recorder.Record(owner, new PublicCardsChosen(observed, selected.ToImmutableArray(),
            selected.Length == 0 && choice.Min > 0 && choice.Cancelable));
        Recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
    }
    internal void Rest(IReadOnlyList<RestSiteDecision> choices, RestSiteDecision chosen)
    {
        long owner = RoomOwner() ?? Begin(PublicEvidenceOwnerKind.Rest, complete: false);
        var grouped = choices.GroupBy(c => c.OptionId, StringComparer.Ordinal).ToArray();
        long observed = Recorder.Record(owner, new PublicOptionsObserved(grouped.Select(g =>
            new PublicVisibleOption(g.Key, g.All(c => !c.IsEnabled))).ToImmutableArray()));
        Recorder.Record(owner, new PublicOptionChosen(observed, chosen.OptionId));
        // The native bridge flattens Smith into visible card targets. Rebuild only
        // that displayed target group, never other deck cards or omitted options.
        if (chosen is RestSiteDecision.Smith smith)
        {
            var cards = choices.OfType<RestSiteDecision.Smith>().Where(c => c.IsEnabled).Select(c => c.Card)
                .OrderBy(c => PublicJson.Serialize(PublicViews.Card(c)), StringComparer.Ordinal).ToArray();
            long choice = Recorder.Record(owner, new PublicCardsObserved(new("smith", 1, 1, false,
                cards.Select(PublicViews.Card).ToArray(), "canonical_unordered_reveal")));
            Recorder.Record(owner, new PublicCardsChosen(choice, [Array.FindIndex(cards, c => ReferenceEquals(c, smith.Card))], false));
        }
    }
    internal void Map(IReadOnlyList<MapPoint> choices, MapPoint selected)
    {
        long owner = Begin(PublicEvidenceOwnerKind.Map);
        MapPoint? current = run.CurrentMapPoint;
        if (current is null)
        {
            Recorder.RecordGap(owner, PublicEvidenceGapReason.ObservationMissing);
            End(owner); return;
        }
        // Legacy channels remain choice slices. Only the separately declared
        // complete-graph profile adds the current act's native public-view capture.
        var currentMap = PublicMapObservationProfiles.UsesCompleteGraph(mapObservationProfile)
            ? NativePublicCurrentMapCapture.Observe(run.Map, run.CurrentActIndex) : null;
        long observed = Recorder.Record(owner, NativePublicMapSlice.Observe(run.Map, current, choices,
            mapObservationProfile, currentMap));
        Recorder.Record(owner, new PublicMapChosen(observed, new(selected.coord.col, selected.coord.row))); End(owner);
    }
    internal void Rewards(RewardsSet rewards, RewardDecision selected)
    {
        int existing = _rewards.FindIndex(r => ReferenceEquals(r.Set, rewards));
        long owner;
        if (existing < 0)
        {
            owner = Begin(PublicEvidenceOwnerKind.Reward, ParentOwner());
            _rewards.Add((rewards, owner));
        }
        else
        {
            if (existing != _rewards.Count - 1) throw new InvalidOperationException("Nested public reward scope did not end");
            owner = _rewards[existing].Owner;
        }
        var groups = new List<PublicOfferGroup>();
        var keys = new Dictionary<Reward, string>(ReferenceEqualityComparer.Instance);
        string? selectedKey = null;
        bool rerolled = _rerolled.Remove(rewards, out var rerolledCard);
        void Add(Reward reward, string key, PublicOfferGroupKind groupKind)
        {
            if (reward.IsResolved) return;
            keys[reward] = key;
            switch (reward)
            {
                case GoldReward gold when gold.Amount > 0:
                    groups.Add(new(groupKind, PublicOfferSelectionMode.Independent,
                        [new(key, PublicOfferKind.Gold, !gold.CanTake, gold: gold.Amount)])); break;
                case GoldReward: break; // Native zero-gold placeholders are not shown.
                case PotionReward potion when potion.Potion is not null:
                    var potionOffers = new List<PublicOffer> { new(key, PublicOfferKind.Potion, !potion.CanTake, potion: potion.Potion.GetType().Name) };
                    if (potion.IsOptionalMerchantChoice) potionOffers.Add(new(key + ":skip", PublicOfferKind.Skip));
                    groups.Add(new(groupKind, potion.IsOptionalMerchantChoice ? PublicOfferSelectionMode.ChooseOne
                        : PublicOfferSelectionMode.Independent, potionOffers.ToImmutableArray())); break;
                case RelicReward relic when relic.Relic is not null:
                    groups.Add(new(groupKind, PublicOfferSelectionMode.Independent,
                        [new(key, PublicOfferKind.Relic, !relic.CanTake, relic: Relic(relic.Relic))])); break;
                case CardReward card:
                    int cardGroup = groups.Count;
                    groups.Add(new(ReferenceEquals(card, rerolledCard) ? PublicOfferGroupKind.Reroll : groupKind, PublicOfferSelectionMode.ChooseOne,
                        card.Options.Select((c, i) => new PublicOffer(key + ":card:" + i, PublicOfferKind.Card,
                            card: PublicViews.Card(c))).Append(new(key + ":skip", PublicOfferKind.Skip)).ToImmutableArray()));
                    if (card.Alternatives.Count > 0)
                        groups.Add(new(PublicOfferGroupKind.Alternative, PublicOfferSelectionMode.ChooseOne,
                            card.Alternatives.Select((a, i) => a.OptionId == "REROLL"
                                ? new PublicOffer(key + ":alternative:" + i, PublicOfferKind.Reroll, !a.IsAvailable)
                                : new PublicOffer(key + ":alternative:" + i, PublicOfferKind.Service, !a.IsAvailable, serviceKey: a.OptionId))
                                .ToImmutableArray(), cardGroup));
                    break;
                case CardRemovalReward removal:
                    groups.Add(new(groupKind, PublicOfferSelectionMode.ChooseOne,
                        [new(key, PublicOfferKind.Service, !removal.CanTake, serviceKey: "remove_card"), new(key + ":skip", PublicOfferKind.Skip)])); break;
                default: Recorder.RecordGap(owner, PublicEvidenceGapReason.UnsupportedObservation); break;
            }
        }
        Add(rewards.Gold, "gold", PublicOfferGroupKind.Primary);
        if (rewards.Potion is not null) Add(rewards.Potion, "potion", PublicOfferGroupKind.Primary);
        if (rewards.Relic is not null) Add(rewards.Relic, "relic", PublicOfferGroupKind.Primary);
        Add(rewards.Card, "card", PublicOfferGroupKind.Primary);
        for (int i = 0; i < rewards.ExtraRewards.Count; i++) Add(rewards.ExtraRewards[i], "extra:" + i, PublicOfferGroupKind.Extra);
        bool done = selected is RewardDecision.Done;
        groups.Add(new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.Independent,
            [new("continue", PublicOfferKind.Continue, !done)]));
        long observed = Recorder.Record(owner, new PublicOffersObserved(groups.ToImmutableArray(),
            rerolled ? _lastRewardOffers[rewards] : null));
        _lastRewardOffers[rewards] = observed;
        string CardKey(CardReward reward, CardModel? card) => keys[reward] + (card is null ? ":skip"
            : ":card:" + reward.Options.Select((c, i) => (c, i)).Single(p => ReferenceEquals(p.c, card)).i);
        selectedKey = selected switch
        {
            RewardDecision.TakeGold when rewards.Gold.Amount > 0 => "gold",
            RewardDecision.TakePotion => "potion", RewardDecision.TakeRelic => "relic",
            RewardDecision.TakeCard card => CardKey(rewards.Card, card.Card), RewardDecision.SkipCard => "card:skip",
            RewardDecision.SelectCardAlternative alt => keys[alt.Reward] + ":alternative:"
                + alt.Reward.Alternatives.Select((a, i) => (a, i)).Single(p => ReferenceEquals(p.a, alt.Alternative)).i,
            RewardDecision.ResolveExtra extra when extra.Reward is CardReward card => CardKey(card, extra.SelectedCard),
            RewardDecision.ResolveExtra extra when extra.Reward is not GoldReward { Amount: 0 } => keys[extra.Reward] + (extra.Skip ? ":skip" : ""),
            RewardDecision.Done => "continue", _ => null,
        };
        if (selectedKey is not null)
        {
            if (groups.SelectMany(g => g.Offers).Any(o => o.Key == selectedKey && !o.IsLocked))
                Recorder.Record(owner, new PublicOptionChosen(observed, selectedKey));
            else Recorder.RecordGap(owner, PublicEvidenceGapReason.AmbiguousVisibility);
        }
        if (selected is RewardDecision.SelectCardAlternative { Alternative.OptionId: "REROLL" } reroll) _rerolled[rewards] = reroll.Reward;
        if (done) { End(owner); _rewards.RemoveAt(_rewards.Count - 1); _lastRewardOffers.Remove(rewards); }
    }
    internal void Shop(MerchantInventory inventory, Player player)
    {
        long owner = RoomOwner() ?? Begin(PublicEvidenceOwnerKind.Shop, complete: false);
        var offers = new List<PublicOffer>();
        bool Locked(MerchantEntry e) => e.Purchased || e.Price > player.Gold;
        offers.AddRange(inventory.Cards.Select((e, i) => new PublicOffer("card:" + i, PublicOfferKind.Card, Locked(e), e.Price, card: PublicViews.Card(e.Card))));
        offers.AddRange(inventory.Relics.Select((e, i) => new PublicOffer("relic:" + i, PublicOfferKind.Relic, Locked(e), e.Price, relic: Relic(e.Relic))));
        offers.AddRange(inventory.Potions.Select((e, i) => new PublicOffer("potion:" + i, PublicOfferKind.Potion,
            Locked(e) || !player.PotionSlots.Contains(null), e.Price, potion: e.Potion.GetType().Name)));
        offers.Add(new("remove", PublicOfferKind.Service, Locked(inventory.CardRemoval), inventory.CardRemoval.Price, serviceKey: "remove_card"));
        offers.Add(new("leave", PublicOfferKind.Continue));
        long observed = Recorder.Record(owner, new PublicOffersObserved([new(PublicOfferGroupKind.Primary,
            PublicOfferSelectionMode.Independent, offers.ToImmutableArray())]));
        Recorder.Record(owner, new PublicOptionChosen(observed, "leave"));
    }
}
