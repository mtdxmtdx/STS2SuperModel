using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicEventUpgradeTarget(long OwnerOrdinal, long ChoiceOrdinal,
    long EndOrdinal, int ActIndex, int Floor, PublicEvidenceAssets BeforeAssets,
    PublicEvidenceAssets AfterAssets, IReadOnlyList<string> PoolKeys, IReadOnlyList<string> SelectedKeys,
    ShuffleRational Envelope);

/// <summary>Detached LIGHT settlement; the public result fixes an unordered pair, never a physical copy or order.</summary>
internal sealed class NativePublicEventUpgradeCondition
{
    internal NativePublicEventUpgradeTarget Target { get; }
    internal ShuffleRational Envelope => Target.Envelope;
    private NativePublicEventUpgradeCondition(NativePublicEventUpgradeTarget target) => Target = target;

    // Complete sealed upgrade/metadata implementations are source reviewed. The
    // four starter cards and Acrobatics only change their own numeric fields;
    // Blur/DeadlyPoison use IsUpgraded and inherit no-op OnUpgrade. AscendersBane
    // has MaxUpgradeLevel=0. These plain cards have no hidden upgrade eligibility.
    // This is a content certificate, not an assumption for other native cards.
    private static readonly HashSet<string> ReviewedCards =
    [nameof(StrikeSilent), nameof(DefendSilent), nameof(Neutralize), nameof(Survivor),
        nameof(Acrobatics), nameof(Blur), nameof(DeadlyPoison), nameof(AscendersBane)];

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicEventUpgradeCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try
        {
            Require(prior.UsesRewardsProvenance, "event_upgrades_require_hybrid_prior");
            Require(root.PublicEvidence is { CompleteFromRunStart: true }, "event_upgrades_require_complete_public_evidence");
            var events = root.PublicEvidence!.Events;
            Require(events.FirstOrDefault()?.Payload is PublicRunStarted, "event_upgrades_require_public_run_start");
            NaturalSourceCollector.InitializeNativeModels();
            var pages = events.Where(e => e.Payload is PublicOptionsObserved options
                && NativeEventSelectionCertificate.Identify(options) == nameof(DoorsOfLightAndDark)).ToArray();
            // Each visit recreates its event RNG from the same run seed. Decline
            // repeated Doors visits rather than multiplying fresh-cell factors.
            Require(pages.Length == 1, "one_public_doors_visit_required");
            var page = pages[0]; int at = checked((int)page.EventOrdinal);
            Require(at >= 5 && at + 2 < events.Length
                && !events.Take(at + 3).Any(e => e.Payload is PublicEvidenceGap), "doors_complete_public_boundary_required");
            Require(events[at - 1] is { OwnerOrdinal: { } owner, Payload: PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Event, ActIndex: 0, Floor: >= 2,
                    ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } }, "doors_top_level_event_required");
            var begin = (PublicOwnerStarted)events[at - 1].Payload;
            long ownerId = events[at - 1].OwnerOrdinal!.Value;
            Require(page.OwnerOrdinal == ownerId
                && events[at + 1] is { Payload: PublicOptionChosen { Key: "LIGHT" } choice }
                && events[at + 1].OwnerOrdinal == ownerId && choice.OfferEventOrdinal == page.EventOrdinal
                && events[at + 2] is { Payload: PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } }
                && events[at + 2].OwnerOrdinal == ownerId, "doors_immediate_light_settlement_required");
            Require(events[at - 5] is { OwnerOrdinal: { } mapOwner, Payload: PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Map, ActIndex: 0, ParentOwnerOrdinal: null,
                    CompleteFromOwnerStart: true } mapStart }
                && mapStart.Floor == begin.Floor - 1
                && events[at - 4] is { Payload: PublicMapObserved map } && events[at - 4].OwnerOrdinal == mapOwner
                && events[at - 3] is { Payload: PublicMapChosen move } && events[at - 3].OwnerOrdinal == mapOwner
                && move.OfferEventOrdinal == events[at - 4].EventOrdinal
                && map.Nodes.Any(n => n.Coordinate == move.Coordinate && n.NodeType == PublicMapNodeType.Unknown)
                && events[at - 2] is { Payload: PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } }
                && events[at - 2].OwnerOrdinal == mapOwner, "doors_immediate_public_before_assets_required");
            var before = ((PublicOwnerEnded)events[at - 2].Payload).Assets!;
            var after = ((PublicOwnerEnded)events[at + 2].Payload).Assets!;
            Require(NativeEventCardPoolCertificate.UnmodifiedInventory(before), "doors_inventory_hook_closure_not_certified");
            Require(PublicJson.Serialize(WithDeck(before, after.Deck)) == PublicJson.Serialize(after),
                "doors_noncard_assets_changed");
            var pool = new List<string>(); var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in before.Deck)
            {
                Require(TryPlainCard(card, out var native), "doors_card_upgrade_not_certified");
                string key = PublicJson.Serialize(card);
                if (!native!.IsUpgradable) continue;
                pool.Add(key); native.Upgrade(); replacements[key] = PublicJson.Serialize(PublicViews.Card(native));
            }
            Require(pool.Count >= 2, "doors_requires_two_upgradeable_cards");
            var original = Counts(before.Deck.Select(PublicJson.Serialize));
            var result = Counts(after.Deck.Select(PublicJson.Serialize));
            var predicted = new Dictionary<string, int>(original, StringComparer.Ordinal);
            var selected = new List<string>();
            foreach (var (key, replacement) in replacements)
            {
                int removed = original[key] - result.GetValueOrDefault(key);
                Require(removed >= 0, "doors_public_upgrade_delta_not_certified");
                for (int i = 0; i < removed; i++) selected.Add(key);
                predicted[key] -= removed;
                predicted[replacement] = predicted.GetValueOrDefault(replacement) + removed;
            }
            Require(selected.Count == 2 && EqualCounts(predicted, result), "doors_exact_two_upgrade_delta_required");
            var envelope = ConditionalUnorderedPairShuffleProposal.Envelope(pool, selected);
            Require(envelope is not null && envelope.Value.Numerator > 0, "doors_upgrade_pair_has_no_support");
            condition = new(new(ownerId, events[at + 1].EventOrdinal, events[at + 2].EventOrdinal,
                begin.ActIndex, begin.Floor, before, after, pool.AsReadOnly(), selected.AsReadOnly(), envelope!.Value));
            return true;
        }
        catch (NotCertifiedException error) { reason = error.Message; return false; }
    }

    private static bool TryPlainCard(PublicCard card, out CardModel? native)
    {
        native = null;
        if (!ReviewedCards.Contains(card.Id) || card.Upgrade is < 0 or > 1
            || card.Affliction is not null || (card.Enchantments?.Length ?? 0) != 0) return false;
        var canonical = ModelDb.All<CardModel>().SingleOrDefault(c => c.GetType().Name == card.Id);
        if (canonical is null || !canonical.GetType().IsSealed || card.Upgrade > canonical.MaxUpgradeLevel) return false;
        native = (CardModel)canonical.MutableClone();
        for (int i = 0; i < card.Upgrade; i++) native.Upgrade();
        return PublicJson.Serialize(PublicViews.Card(native)) == PublicJson.Serialize(card);
    }

    internal static bool SameMultiset(IEnumerable<string> left, IEnumerable<string> right) => EqualCounts(Counts(left), Counts(right));
    private static Dictionary<string, int> Counts(IEnumerable<string> keys) => keys.GroupBy(k => k, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    private static bool EqualCounts(Dictionary<string, int> left, Dictionary<string, int> right) =>
        left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).All(k => left.GetValueOrDefault(k) == right.GetValueOrDefault(k));
    private static PublicEvidenceAssets WithDeck(PublicEvidenceAssets source, PublicCard[] deck) => new(source.Hp,
        source.MaxHp, source.Gold, deck, source.Relics, source.Potions, source.MaxEnergy, source.PotionSlots,
        source.OrbSlots, source.CardRemovalsUsed);
    private static void Require(bool value, string reason) { if (!value) throw new NotCertifiedException(reason); }
    private sealed class NotCertifiedException(string reason) : Exception(reason);
}

/// <summary>One owned Doors LIGHT shuffle plus its exact public native settlement.</summary>
internal sealed class NativePublicEventUpgradeProposal(NativePublicEventUpgradeCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forceWords,
    Action<Exception>? captureFailure = null)
{
    private RunState? _run;
    private ConditionalUnorderedPairShufflePlan? _plan;
    private bool _choiceObserved, _active, _shuffled, _settled, _failed;
    internal int ConditionedUpgradeCount => _settled ? 2 : 0;
    internal int ConditionedShuffleCount => _shuffled ? 1 : 0;
    internal ShuffleRational NativeToProposalRatio => _plan?.NativeToProposalRatio ?? new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;

    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("Event upgrade proposal already owns a native run");
        _run = run;
    }

    internal void ObservePublicEvidence(PublicRunEvidenceEvent entry)
    {
        var target = condition.Target;
        try
        {
            if (entry.EventOrdinal == target.ChoiceOrdinal)
            {
                if (_choiceObserved || entry.OwnerOrdinal != target.OwnerOrdinal
                    || entry.Payload is not PublicOptionChosen { Key: "LIGHT" })
                    throw new InvalidOperationException("Doors upgrade proposal lost its public LIGHT choice boundary");
                _choiceObserved = true;
            }
            if (entry.EventOrdinal != target.EndOrdinal) return;
            if (!_shuffled || _active || _failed || _settled || entry.OwnerOrdinal != target.OwnerOrdinal
                || entry.Payload is not PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: { } after })
                throw new InvalidOperationException("Doors upgrade proposal did not finish before its public settlement");
            if (PublicJson.Serialize(after) != PublicJson.Serialize(target.AfterAssets))
                throw new NativePublicConstraintMismatchException("Native Doors LIGHT settlement differs from the complete public after-assets");
            _settled = true;
        }
        catch (Exception error) { Fail(error); throw; }
    }

    internal IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        var target = condition.Target;
        if (_run?.CurrentRoom is not EventRoom { Event: DoorsOfLightAndDark doors }
            || _run.CurrentActIndex != target.ActIndex || _run.TotalFloor != target.Floor) return null;
        try
        {
            if (_failed || _active || _shuffled || _settled || !_choiceObserved || _plan is not null
                || _run.Players.Count != 1 || _run.CurrentRoomCount != 1 || _run.Rng.UsesSemanticKeys
                || !ReferenceEquals(rng, doors.Rng) || !ReferenceEquals(doors.Owner, _run.Players[0]))
                throw new InvalidOperationException("Doors upgrade shuffle escaped its owned native boundary");
            var player = _run.Players[0];
            if (PublicJson.Serialize(NativePublicRunEvidence.Assets(player)) != PublicJson.Serialize(target.BeforeAssets))
                throw new NativePublicConstraintMismatchException("Native Doors LIGHT before-assets differ from the public boundary");
            if (items.Any(item => item is not CardModel)) throw new InvalidOperationException("Doors upgrade shuffle lost its physical cards");
            var cards = items.Cast<CardModel>().ToArray();
            var eligible = player.Deck.Cards.Where(c => c.IsUpgradable).ToArray();
            string[] keys = cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))).ToArray();
            if (cards.Length != eligible.Length || cards.Distinct(ReferenceEqualityComparer.Instance).Count() != cards.Length
                || cards.Any(c => !ReferenceEquals(c.Owner, player) || c.Pile?.Type != PileType.Deck || !eligible.Contains(c))
                || !NativePublicEventUpgradeCondition.SameMultiset(keys, target.PoolKeys)
                || !keys.SequenceEqual(cards.OrderBy(c => c).Select(c => PublicJson.Serialize(PublicViews.Card(c)))))
                throw new InvalidOperationException("Doors upgrade shuffle changed its complete native sorted physical pool");
            _plan = ConditionalUnorderedPairShuffleProposal.Create(keys, target.SelectedKeys, nextWord)
                ?? throw new NativePublicConstraintMismatchException("Public Doors upgrade pair has no native shuffle support");
            if (_plan.Envelope != condition.Envelope || _plan.RawWords.Count != cards.Length - 1)
                throw new InvalidOperationException("Doors upgrade shuffle escaped its fixed public envelope");
            int before = rng.Counter;
            var inner = forceWords(_plan.RawWords, rng, "public Doors LIGHT upgrades");
            _active = true;
            return new Completion(() =>
            {
                try
                {
                    inner.Dispose();
                    if (_failed) return;
                    if (rng.Counter != before + cards.Length - 1)
                        throw new InvalidOperationException("Doors LIGHT did not consume the complete native shuffle");
                    _active = false; _shuffled = true;
                }
                catch (Exception error) { Fail(error); throw; }
            }, error => { Fail(error); (inner as IAbortableConditionedWordScope)?.Abort(); });
        }
        catch (Exception error) { Fail(error); throw; }
    }

    internal void ValidateCompletion()
    {
        if (_failed || _active || !_shuffled || !_settled || _plan is null)
            throw new InvalidOperationException("Public Doors LIGHT proposal did not complete its native shuffle and settlement");
    }
    internal bool AcceptCorrection(Func<ulong> random) { ValidateCompletion(); return _plan!.AcceptCorrection(random); }
    private void Fail(Exception error) { _failed = true; captureFailure?.Invoke(error); }
    private sealed class Completion(Action finish, Action<Exception> abort) : IAbortableLabelShuffleBoundary
    {
        private bool _disposed, _aborted;
        public void Abort(Exception error) { if (_aborted) return; _aborted = true; abort(error); }
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
