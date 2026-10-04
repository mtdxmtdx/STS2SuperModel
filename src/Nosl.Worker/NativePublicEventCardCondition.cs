using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicEventCardTarget(long EventOwnerOrdinal, long ChoiceOwnerOrdinal,
    long OfferEventOrdinal, int ActIndex, int Floor, string Source, PublicEvidenceAssets BeforeAssets,
    NativeEventCardPoolCertificate Certificate);

/// <summary>
/// Detached complete public Gorge and BrainLeech offers, including every unpicked card. The
/// immediately preceding map settlement certifies the inventory before entry.
/// Unsupported inventories or boundaries decline this accelerator, not the source.
/// </summary>
internal sealed class NativePublicEventCardCondition
{
    internal string Character { get; }
    internal IReadOnlyList<NativePublicEventCardTarget> Targets { get; }
    internal ShuffleRational Envelope { get; }

    private NativePublicEventCardCondition(string character, List<NativePublicEventCardTarget> targets)
    {
        Character = character; Targets = targets.AsReadOnly();
        Envelope = targets.Aggregate(new ShuffleRational(1, 1), (p, t) =>
            p.Multiply(t.Certificate.Envelope.Numerator, t.Certificate.Envelope.Denominator));
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicEventCardCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        string sourceReason = "gorge";
        try
        {
            Require(prior.UsesRewardsProvenance, "event_cards_require_rewards_hybrid_prior");
            Require(root.PublicEvidence is { CompleteFromRunStart: true }, "event_cards_require_complete_public_evidence");
            var events = root.PublicEvidence!.Events;
            Require(events.FirstOrDefault()?.Payload is PublicRunStarted, "event_cards_require_public_run_start");
            var start = (PublicRunStarted)events[0].Payload;
            NaturalSourceCollector.InitializeNativeModels();
            var character = ModelDb.AllCharacters.SingleOrDefault(c => c.GetType().Name == start.Character);
            Require(character is not null, "event_cards_require_native_character_catalog");
            var targets = new List<NativePublicEventCardTarget>();
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i].Payload is not PublicCardsObserved { Choice.Source: nameof(RoomFullOfCheese) or nameof(BrainLeech) } observed) continue;
                var choice = observed.Choice;
                bool brainLeech = choice.Source == nameof(BrainLeech);
                sourceReason = brainLeech ? "brain_leech" : "gorge";
                string[] optionKeys = brainLeech ? ["SHARE_KNOWLEDGE", "RIP"] : ["GORGE", "SEARCH"];
                Require(choice is { Cancelable: false, CandidateOrder: "public", Bundles: null }
                    && choice.Min == (brainLeech ? 1 : 2) && choice.Max == (brainLeech ? 1 : 2)
                    && choice.Candidates.Length == (brainLeech ? 5 : 8),
                    brainLeech ? "brain_leech_requires_all_five_displayed_cards" : "gorge_requires_all_eight_displayed_cards");
                Require(i >= 9 && !events.Take(i + 1).Any(e => e.Payload is PublicEvidenceGap),
                    $"{sourceReason}_requires_uninterrupted_public_boundary");
                Require(events[i - 4] is { OwnerOrdinal: { }, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.Event, ParentOwnerOrdinal: null,
                        CompleteFromOwnerStart: true, ActIndex: < 2, Floor: >= 2 } }, $"{sourceReason}_requires_top_level_event_owner");
                var eventEntry = events[i - 4];
                var owner = (PublicOwnerStarted)eventEntry.Payload;
                Require(events[i - 3] is { Payload: PublicOptionsObserved options }
                    && events[i - 3].OwnerOrdinal == eventEntry.OwnerOrdinal
                    && options.Options.Select(o => o.Key).SequenceEqual(optionKeys)
                    && options.Options.All(o => !o.IsLocked && o.Price is null)
                    && events[i - 2] is { Payload: PublicOptionChosen chosen }
                    && events[i - 2].OwnerOrdinal == eventEntry.OwnerOrdinal
                    && chosen.Key == optionKeys[0] && chosen.OfferEventOrdinal == events[i - 3].EventOrdinal,
                    $"{sourceReason}_requires_public_source_option_boundary");
                Require(events[i - 1] is { OwnerOrdinal: { }, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.OutsideChoice, CompleteFromOwnerStart: true } child }
                    && child.ParentOwnerOrdinal == eventEntry.OwnerOrdinal && child.ActIndex == owner.ActIndex
                    && child.Floor == owner.Floor && events[i].OwnerOrdinal == events[i - 1].OwnerOrdinal,
                    $"{sourceReason}_requires_owned_public_card_choice");
                Require(events[i - 8] is { OwnerOrdinal: { }, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.Map, ParentOwnerOrdinal: null,
                        CompleteFromOwnerStart: true } mapOwner }
                    && mapOwner.ActIndex == owner.ActIndex && mapOwner.Floor == owner.Floor - 1
                    && events[i - 7] is { Payload: PublicMapObserved map }
                    && events[i - 7].OwnerOrdinal == events[i - 8].OwnerOrdinal
                    && events[i - 6] is { Payload: PublicMapChosen mapChoice }
                    && events[i - 6].OwnerOrdinal == events[i - 8].OwnerOrdinal
                    && mapChoice.OfferEventOrdinal == events[i - 7].EventOrdinal
                    && map.Nodes.Any(n => n.Coordinate == mapChoice.Coordinate && n.NodeType == PublicMapNodeType.Unknown)
                    && events[i - 5] is { Payload: PublicOwnerEnded
                        { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } }
                    && events[i - 5].OwnerOrdinal == events[i - 8].OwnerOrdinal,
                    $"{sourceReason}_requires_immediate_public_before_assets");
                var assets = ((PublicOwnerEnded)events[i - 5].Payload).Assets!;
                Require(NativeEventCardPoolCertificate.UnmodifiedInventory(assets),
                    $"{sourceReason}_public_inventory_hook_closure_not_certified");
                var pool = character!.CardPool.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false);
                var ids = choice.Candidates.Select(c => c.Id).ToArray();
                var certificate = brainLeech
                    ? NativeEventCardPoolCertificate.FromDefaultOdds(pool, ids, start.Ascension)
                    : NativeEventCardPoolCertificate.FromPool(pool.Where(c => c.Rarity == CardRarity.Common), ids);
                Require(!targets.Any(t => t.ActIndex == owner.ActIndex && t.Floor == owner.Floor),
                    $"{sourceReason}_public_source_boundary_repeated");
                targets.Add(new(eventEntry.OwnerOrdinal!.Value, events[i - 1].OwnerOrdinal!.Value,
                    events[i].EventOrdinal, owner.ActIndex, owner.Floor, choice.Source, assets, certificate));
            }
            Require(targets.Count > 0, "certified_public_event_card_offer_required");
            condition = new(start.Character, targets); return true;
        }
        catch (NotCertifiedException error) { reason = error.Message; return false; }
        catch (NativePublicConstraintMismatchException) { reason = $"{sourceReason}_public_cards_have_no_native_pool_support"; return false; }
    }

    private static void Require(bool value, string reason) { if (!value) throw new NotCertifiedException(reason); }
    private sealed class NotCertifiedException(string reason) : Exception(reason);
}

/// <summary>
/// Root-fixed pools after a public hook-closure certificate. Native Distinct
/// precedes selection; later slots exclude ALL entries with the selected
/// ModelId, rather than removing a reference or only a chosen displayed card.
/// </summary>
internal sealed class NativeEventCardPoolCertificate
{
    internal IReadOnlyList<string> BaseCardIds { get; }
    internal IReadOnlyList<IReadOnlyList<CardModel>> SlotPools { get; }
    internal IReadOnlyList<ShuffleRational> SlotMasses { get; }
    internal ShuffleRational Envelope { get; }
    internal LabelCardRarityThresholds? RarityThresholds { get; }
    internal IReadOnlyList<IReadOnlyList<LabelRewardCardBranch>> SlotBranches { get; }

    private NativeEventCardPoolCertificate(string[] ids, List<IReadOnlyList<CardModel>> pools,
        List<ShuffleRational> masses, LabelCardRarityThresholds? thresholds = null)
    {
        RarityThresholds = thresholds;
        SlotBranches = pools.Select(pool => thresholds is null
            ? (IReadOnlyList<LabelRewardCardBranch>)Array.AsReadOnly(new[] { new LabelRewardCardBranch(null, pool) })
            : NativeNeowCardCondition.CreationBranches(pool)).ToList().AsReadOnly();
        BaseCardIds = Array.AsReadOnly(ids); SlotPools = pools.AsReadOnly(); SlotMasses = masses.AsReadOnly();
        Envelope = masses.Aggregate(new ShuffleRational(1, 1), (p, q) => p.Multiply(q.Numerator, q.Denominator));
    }

    internal static NativeEventCardPoolCertificate FromPool(IEnumerable<CardModel> pool,
        IReadOnlyList<string> targets, int bits = 53)
    {
        CardModel[] remaining = pool.Distinct().ToArray();
        var pools = new List<IReadOnlyList<CardModel>>(); var masses = new List<ShuffleRational>();
        foreach (string target in targets)
        {
            var matches = remaining.Where(c => c.GetType().Name == target).ToArray();
            if (matches.Length == 0) throw new NativePublicConstraintMismatchException("Public event card has zero native conditional mass");
            if (remaining.Any(c => c.Rarity != CardRarity.Common) || matches.Select(c => c.Id).Distinct().Count() != 1)
                throw new InvalidOperationException("Native Gorge common ModelId catalog changed");
            pools.Add(Array.AsReadOnly(remaining));
            masses.Add(NativeNeowCardCondition.UniformMass(remaining, target, bits));
            ModelId selected = matches[0].Id;
            remaining = remaining.Where(c => c.Id != selected).ToArray();
        }
        return new(targets.ToArray(), pools, masses);
    }

    internal static NativeEventCardPoolCertificate FromDefaultOdds(IEnumerable<CardModel> pool,
        IReadOnlyList<string> targets, int ascension, int bits = 53)
    {
        // Base odds depend only on public ascension, never the hidden pity value.
        var thresholds = new CardRarityOdds(new Rng(0), new AscensionManager(ascension))
            .GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, changesFutureOdds: false);
        CardModel[] remaining = pool.Distinct().ToArray();
        var pools = new List<IReadOnlyList<CardModel>>(); var masses = new List<ShuffleRational>();
        foreach (string target in targets)
        {
            var matches = remaining.Where(c => c.GetType().Name == target).ToArray();
            if (matches.Length == 0)
                throw new NativePublicConstraintMismatchException("Public event card has zero native conditional mass");
            if (matches.Select(c => c.Id).Distinct().Count() != 1)
                throw new InvalidOperationException("Native BrainLeech ModelId catalog changed");
            var mass = NativeNeowCardCondition.CreationRarityMass(remaining, target, thresholds, bits);
            if (mass.Numerator.IsZero)
                throw new NativePublicConstraintMismatchException("Public event card has zero native conditional mass");
            pools.Add(Array.AsReadOnly(remaining)); masses.Add(mass);
            ModelId selected = matches[0].Id;
            remaining = remaining.Where(c => c.Id != selected).ToArray();
        }
        return new(targets.ToArray(), pools, masses, thresholds);
    }

    internal static bool UnmodifiedInventory(PublicEvidenceAssets assets)
    {
        // RunState.IterateHookListeners(null) enumerates relics, potions, and deck
        // cards only. Include melted relics too, avoiding hidden-status inference.
        var listeners = new List<Type>();
        foreach (string id in assets.Deck.Select(c => c.Id))
        {
            var model = ModelDb.All<CardModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Relics.Select(r => r.Id))
        {
            var model = ModelDb.All<RelicModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Potions.OfType<string>())
        {
            var model = ModelDb.All<PotionModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        string[] hooks = [nameof(AbstractModel.BeforeRoomEntered), nameof(AbstractModel.AfterRoomEntered),
            nameof(AbstractModel.ModifyNextEvent), nameof(AbstractModel.ModifyUnknownMapPointRoomTypes),
            nameof(AbstractModel.ModifyOddsIncreaseForUnrolledRoomType),
            nameof(AbstractModel.ModifyCardRewardCreationOptions), nameof(AbstractModel.ModifyCardRewardCreationOptionsLate),
            nameof(AbstractModel.TryModifyCardRewardOptions), nameof(AbstractModel.TryModifyCardRewardOptionsLate),
            nameof(AbstractModel.TryModifyCardRewardOptionLate)];
        // GetMethods deliberately checks both late-option overloads.
        return listeners.All(type => type.GetMethods().Where(m => hooks.Contains(m.Name))
            .All(method => method.DeclaringType == typeof(AbstractModel)));
    }
}

/// <summary>Owned hypothetical public event-card replay; every factor is fixed by the detached root.</summary>
internal sealed class NativePublicEventCardProposal(NativePublicEventCardCondition condition,
    Func<LabelRandomAddressV1, bool> wasVisited, Action<LabelRandomAddressV1, ulong> forceFresh,
    Func<ulong> nextProposalWord, Action<Exception>? captureFailure = null)
{
    private RunState? _run;
    private readonly HashSet<long> _completed = [];
    private NativePublicEventCardTarget? _active;
    private int _slot;
    private bool _activeSelection, _failed;
    internal int ConditionedCardCount { get; private set; }
    internal int ConditionedOfferCount => _completed.Count;
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;

    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("Event card proposal already owns a native run");
        _run = run;
    }

    internal IDisposable? BeginSelection(LabelRewardCardSelectionContext context)
    {
        if (_run?.CurrentRoom is not EventRoom { Event: RoomFullOfCheese or BrainLeech } room) return null;
        var target = condition.Targets.SingleOrDefault(t => t.ActIndex == _run.CurrentActIndex && t.Floor == _run.TotalFloor);
        if (target is null || room.Event.GetType().Name != target.Source) return null;
        bool brainLeech = target.Source == nameof(BrainLeech);
        int wordCount = brainLeech ? 2 : 1;
        try
        {
            if (_failed || _activeSelection || _completed.Contains(target.OfferEventOrdinal)
                || !ReferenceEquals(context.Player.RunState, _run) || _run.Players.Count != 1
                || _run.CurrentRoomCount != 1 || _run.Rng.UsesSemanticKeys
                || context.Player.Character.GetType().Name != condition.Character
                || !ReferenceEquals(context.Rng, context.Player.PlayerRng.Rewards))
                throw new InvalidOperationException("Public event cards escaped their owned native boundary");
            if (_active is null)
            {
                if (PublicJson.Serialize(NativePublicRunEvidence.Assets(context.Player)) != PublicJson.Serialize(target.BeforeAssets))
                    throw new NativePublicConstraintMismatchException("Native event-card inventory differs from the public before-assets");
                _active = target; _slot = 0;
            }
            if (!ReferenceEquals(_active, target) || context.Kind != LabelRewardCardSelectionKind.CreationOptions
                || _slot >= target.Certificate.BaseCardIds.Count || context.SelectionIndex != _slot
                || context.OptionCount != target.Certificate.BaseCardIds.Count || context.Source != CardCreationSource.Other
                || context.Flags != (brainLeech ? CardCreationFlags.NoUpgradeRoll
                    : CardCreationFlags.NoUpgradeRoll | CardCreationFlags.NoRarityModification)
                || context.OddsType != (brainLeech ? CardRarityOddsType.RegularEncounter : CardRarityOddsType.Uniform)
                || context.Thresholds != target.Certificate.RarityThresholds || context.ChangesFutureOdds
                || !context.RemainingCards.SequenceEqual(target.Certificate.SlotPools[_slot])
                || context.Branches.Count != target.Certificate.SlotBranches[_slot].Count
                || context.Branches.Where((branch, index) =>
                    branch.RolledRarity != target.Certificate.SlotBranches[_slot][index].RolledRarity
                    || !branch.Candidates.SequenceEqual(target.Certificate.SlotBranches[_slot][index].Candidates)).Any())
                throw new InvalidOperationException("Event cards departed from their certified native pool or draw contract");
            var address = Address(context.Rng);
            var addresses = Enumerable.Range(0, wordCount)
                .Select(i => address with { RawCursor = checked(address.RawCursor + (ulong)i) }).ToArray();
            // Check every rarity/index cell before forcing any of them. Aliasing
            // even the second cell invalidates the fixed-root product envelope.
            if (addresses.Any(wasVisited))
                throw new InvalidOperationException("Event card proposal requires fresh Rewards cells; alias invalidates its envelope");
            var plan = NativeRewardIdentityMath.Create(context, target.Certificate.BaseCardIds[_slot], nextProposalWord);
            if (plan.RawWords.Count != wordCount || plan.NativeToProposalRatio != target.Certificate.SlotMasses[_slot])
                throw new InvalidOperationException("Event card mass differs from its fixed-root envelope");
            for (int i = 0; i < wordCount; i++) forceFresh(addresses[i], plan.RawWords[i]);
            _activeSelection = true;
            NativeToProposalRatio = NativeToProposalRatio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
            return new Completion(() =>
            {
                try
                {
                    // Both reviewed helpers suppress upgrades. BrainLeech retains
                    // its native rarity draw; Gorge has only the index draw.
                    if (Address(context.Rng) != address with { RawCursor = checked(address.RawCursor + (ulong)wordCount) }
                        || addresses.Any(cell => !wasVisited(cell)))
                        throw new InvalidOperationException("Native event cards skipped, restored, or added Rewards draws");
                    _activeSelection = false; _slot++; ConditionedCardCount++;
                    if (_slot == target.Certificate.BaseCardIds.Count) { _completed.Add(target.OfferEventOrdinal); _active = null; }
                }
                catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
            });
        }
        catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
    }

    internal void ValidateCompletion()
    {
        if (_failed || _activeSelection || _active is not null || _completed.Count != condition.Targets.Count
            || NativeToProposalRatio != Envelope)
            throw new InvalidOperationException("Public event card proposal did not complete every displayed offer");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ValidateCompletion();
        return NativeNeowProposal.ExactRationalBernoulli(new(
            NativeToProposalRatio.Numerator * Envelope.Denominator,
            NativeToProposalRatio.Denominator * Envelope.Numerator), nextWord);
    }

    private static LabelRandomAddressV1 Address(Rng rng)
    {
        var p = rng.ToSerializable().LabelProvenance;
        if (p is not { Law: LabelRandomProvenance.LawId or LabelRandomProvenance.MapLawId,
            Partition: LabelRandomProvenance.RewardsPartition, OriginFamily: LabelRandomProvenance.RewardsOrigin,
            InitialSeed: { } seed, RawCursor: { } cursor })
            throw new InvalidOperationException("Event card conditioning requires tagged native Rewards provenance");
        return new(LabelRandomProvenance.RewardsOrigin, seed, cursor);
    }

    private sealed class Completion(Action action) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; action(); }
    }
}
