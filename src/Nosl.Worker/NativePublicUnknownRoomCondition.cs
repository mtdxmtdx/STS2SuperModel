using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;

namespace Nosl.Worker;

internal sealed record NativeUnknownRoomOdds(float Monster, float Elite, float Treasure, float Shop)
{
    internal static NativeUnknownRoomOdds Read(UnknownMapPointOdds odds) =>
        new(odds.MonsterOdds, odds.EliteOdds, odds.TreasureOdds, odds.ShopOdds);
}
internal sealed record NativeUnknownRoomBucket(ulong Start, ulong Size);
internal sealed record NativePublicUnknownRoomTarget(int Index, int Floor, PublicMapCoordinate Coordinate,
    long MapOwnerOrdinal, long MapEndEventOrdinal, long RoomOwnerOrdinal, RoomType RoomType,
    bool ExcludeShop, NativeUnknownRoomOdds Before, NativeUnknownRoomOdds After, NativeUnknownRoomBucket Bucket);

/// <summary>
/// A detached, root-fixed prefix of publicly resolved unknown rooms. The native
/// odds start at defaults and are advanced using only these public outcomes.
/// Incomplete history or an unreviewed listener stops acceleration, never the prior.
/// </summary>
internal sealed class NativePublicUnknownRoomCondition
{
    internal IReadOnlyList<NativePublicUnknownRoomTarget> Targets { get; }
    internal ShuffleRational Envelope { get; }
    internal string? SuffixFallbackReason { get; }
    internal PublicRunEvidence BoundaryEvidence { get; }

    private NativePublicUnknownRoomCondition(List<NativePublicUnknownRoomTarget> targets,
        PublicRunEvidence evidence, string? fallback)
    {
        Targets = targets.AsReadOnly(); SuffixFallbackReason = fallback;
        Envelope = targets.Aggregate(new ShuffleRational(1, 1),
            (mass, target) => mass.Multiply(target.Bucket.Size, 1UL << 53));
        // Include the last resolved owner as well as its preceding map boundary.
        var prefix = evidence.Events.Take(checked((int)targets[^1].MapEndEventOrdinal + 2)).ToImmutableArray();
        BoundaryEvidence = new(evidence.SchemaVersion, true, prefix);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicUnknownRoomCondition? condition, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(prior);
        condition = null;
        // This independently certifies the native constructor and first Neow/map
        // boundary even when the later evidence has an unsupported suffix.
        if (!NativePublicMapHistoryCondition.TryCreate(root.PublicEvidence, prior, out _, out reason)) return false;
        return TryCreate(root.PublicEvidence!, out condition, out reason);
    }

    internal static bool TryCreate(PublicRunEvidence evidence,
        out NativePublicUnknownRoomCondition? condition, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        condition = null; reason = "complete_native_unknown_room_prefix_required";
        var events = evidence.Events;
        if (events.FirstOrDefault()?.Payload is not PublicRunStarted { Character: "Silent", Ascension: 10 }) return false;
        var targets = new List<NativePublicUnknownRoomTarget>();
        var scratchRng = new Rng(0, "nosl-public-unknown-odds-only");
        var odds = new UnknownMapPointOdds(scratchRng, NullOddsHooks.Instance);
        int expectedMapFloor = 1;
        PublicMapCoordinate? previousChoice = null;
        bool previousPointHasShop = false;
        string? fallback = null;
        for (int i = 1; i < events.Length; i++)
        {
            var entry = events[i];
            if (entry.Payload is PublicEvidenceGap)
            { fallback = "unknown_room_history_gap"; break; }
            if (entry.Payload is not PublicOwnerStarted owner) continue;
            if (owner.ActIndex != 0 || !owner.CompleteFromOwnerStart)
            { fallback = "unknown_room_unsupported_owner_context"; break; }
            if (owner.OwnerKind == PublicEvidenceOwnerKind.Shop) previousPointHasShop = true;
            if (owner.OwnerKind != PublicEvidenceOwnerKind.Map) continue;
            if (owner.ParentOwnerOrdinal is not null || owner.Floor != expectedMapFloor || i + 3 >= events.Length
                || events[i + 1].OwnerOrdinal != entry.OwnerOrdinal || events[i + 2].OwnerOrdinal != entry.OwnerOrdinal
                || events[i + 3].OwnerOrdinal != entry.OwnerOrdinal
                || events[i + 1].Payload is not PublicMapObserved map || map.Current is not { } current
                || events[i + 2].Payload is not PublicMapChosen chosen
                || events[i + 3].Payload is not PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: { } assets }
                || chosen.OfferEventOrdinal != events[i + 1].EventOrdinal
                || current.Row + 1 != owner.Floor || chosen.Coordinate.Row != current.Row + 1
                || previousChoice is not null && current != previousChoice
                || previousChoice is null && (current.Row != 0 || map.Nodes.Single(n => n.Coordinate == current).NodeType != PublicMapNodeType.Ancient))
            { fallback = "complete_linked_unknown_map_owner_required"; break; }
            var type = map.Nodes.Single(node => node.Coordinate == chosen.Coordinate).NodeType;
            if (type == PublicMapNodeType.Unknown)
            {
                if (!UnmodifiedOdds(assets))
                { fallback = "unknown_room_public_modifier_closure_required"; break; }
                if (!TryBlacklist(map, chosen.Coordinate, previousPointHasShop, out bool excludeShop))
                { fallback = "unknown_room_complete_shop_blacklist_required"; break; }
                if (i + 4 >= events.Length || events[i + 4] is not { OwnerOrdinal: { } roomOwner,
                    Payload: PublicOwnerStarted { ActIndex: 0, ParentOwnerOrdinal: null,
                        CompleteFromOwnerStart: true } room } || room.Floor != owner.Floor + 1)
                { fallback = "unknown_room_immediate_public_result_required"; break; }
                RoomType? result = room.OwnerKind switch
                {
                    PublicEvidenceOwnerKind.Event => RoomType.Event,
                    // With native defaults and identity hooks, Elite odds remain
                    // strictly negative throughout the entire certified prefix.
                    PublicEvidenceOwnerKind.Combat => RoomType.Monster,
                    PublicEvidenceOwnerKind.Shop => RoomType.Shop,
                    _ => null,
                };
                if (result is null)
                { fallback = "unknown_room_unambiguous_public_result_required"; break; }
                var before = NativeUnknownRoomOdds.Read(odds);
                var bucket = NativeUnknownRoomMath.Bucket(before, excludeShop, result.Value);
                if (bucket.Size == 0)
                    throw new NativePublicConstraintMismatchException("Public unknown-room result has zero certified native mass");
                // Invoke the exact native update on an independent scratch object.
                // No source seed, odds object, or hypothetical latent odds is read.
                int draws = 0;
                using (LabelRandomScope.Enter(_ => { draws++; return bucket.Start << 11; }))
                    if (odds.Roll(excludeShop ? [RoomType.Shop] : [], scratchRng) != result)
                        throw new InvalidOperationException("Native unknown-room bucket no longer matches its reviewed roll");
                if (draws != 1) throw new InvalidOperationException("Native unknown-room roll draw count changed");
                targets.Add(new(targets.Count, owner.Floor + 1, chosen.Coordinate, entry.OwnerOrdinal!.Value,
                    events[i + 3].EventOrdinal, roomOwner, result.Value, excludeShop, before,
                    NativeUnknownRoomOdds.Read(odds), bucket));
            }
            previousChoice = chosen.Coordinate; expectedMapFloor++; previousPointHasShop = false; i += 3;
        }
        if (targets.Count == 0) { reason = fallback ?? "no_certified_public_unknown_room"; return false; }
        condition = new(targets, evidence, fallback); reason = null; return true;
    }

    private static bool TryBlacklist(PublicMapObserved map, PublicMapCoordinate chosen,
        bool previousPointHasShop, out bool excludeShop)
    {
        excludeShop = previousPointHasShop;
        if (excludeShop) return true;
        if (map.CurrentMap is not { Status: PublicMapCaptureStatus.Complete } graph) return false;
        var children = graph.Edges.Where(edge => edge.From == chosen).Select(edge => edge.To).ToArray();
        excludeShop = children.Length > 0 && children.All(child =>
            graph.Nodes.Single(node => node.Coordinate == child).NodeType == PublicMapNodeType.Shop);
        return true;
    }

    internal static bool UnmodifiedOdds(PublicEvidenceAssets assets)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var listeners = new List<Type>();
        foreach (string id in assets.Deck.Select(card => card.Id))
        {
            var model = ModelDb.All<CardModel>().SingleOrDefault(candidate => candidate.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Relics.Select(relic => relic.Id))
        {
            var model = ModelDb.All<RelicModel>().SingleOrDefault(candidate => candidate.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Potions.OfType<string>())
        {
            var model = ModelDb.All<PotionModel>().SingleOrDefault(candidate => candidate.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        // RunState.IterateHookListeners(null) consists exactly of these public
        // relics, potions and deck cards. Conservatively retain melted listeners.
        string[] hooks = [nameof(AbstractModel.ModifyUnknownMapPointRoomTypes),
            nameof(AbstractModel.ModifyOddsIncreaseForUnrolledRoomType)];
        return listeners.All(type => type.IsSealed && hooks.All(hook =>
            type.GetMethod(hook)?.DeclaringType == typeof(AbstractModel)));
    }
}

/// <summary>Source-pinned UnknownMapPointOdds.Roll order and IEEE float cumulative comparisons.</summary>
internal static class NativeUnknownRoomMath
{
    internal static NativeUnknownRoomBucket Bucket(NativeUnknownRoomOdds odds, bool excludeShop,
        RoomType result, int precisionBits = 53)
    {
        if (precisionBits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        ulong consumed = 0, domain = 1UL << precisionBits;
        float cumulative = 0f;
        (RoomType Type, float Odds)[] ordered = [(RoomType.Monster, odds.Monster), (RoomType.Elite, odds.Elite),
            (RoomType.Treasure, odds.Treasure), (RoomType.Shop, odds.Shop)];
        foreach (var candidate in ordered)
        {
            if (candidate.Type == RoomType.Shop && excludeShop || candidate.Odds < 0f) continue;
            if (!float.IsFinite(candidate.Odds)) throw new InvalidOperationException("Unreviewed unknown-room odds");
            cumulative += candidate.Odds;
            ulong end = NativeRewardResourceMath.FloatUpperBound(cumulative, precisionBits);
            if (candidate.Type == result) return new(consumed, end - consumed);
            consumed = end;
        }
        return result == RoomType.Event ? new(consumed, domain - consumed) : new(0, 0);
    }

    internal static NativeResourcePlan Create(NativePublicUnknownRoomTarget target, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        return new([NativeRewardResourceMath.SampleWord(target.Bucket.Start, target.Bucket.Size, 53, nextWord)],
            new(target.Bucket.Size, 1UL << 53));
    }
}
