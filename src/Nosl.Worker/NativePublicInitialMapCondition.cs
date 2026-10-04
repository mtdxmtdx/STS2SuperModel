using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Map;

namespace Nosl.Worker;

/// <summary>
/// A necessary initial-map event extracted only from the detached public recorder.
/// Ineligibility disables this accelerator, never any native content or root.
/// </summary>
internal sealed class NativePublicInitialMapCondition
{
    // Source-pinned Neow positives. None changes topology or map point types.
    // SmallCapsule only grants a Common/Uncommon/Rare relic (or Circlet), never
    // GoldenCompass, the native map-replacement relic. LavaRock triggers at the
    // act-zero boss. WingedBoots changes travel options, handled separately below.
    private static readonly HashSet<string> ReviewedNeowPositives = new(StringComparer.Ordinal)
    {
        "ArcaneScroll", "BoomingConch", "FishingRod", "GoldenPearl", "Kaleidoscope",
        "LeadPaperweight", "LostCoffer", "NeowsTorment", "NewLeaf", "PhialHolster",
        "PreciseScissors", "ScrollBoxes", "WingedBoots", "LavaRock", "SmallCapsule",
        "NutritiousOyster", "StoneHumidifier", "NeowsTalisman", "Pomander",
    };

    private readonly string _slice;
    private readonly string? _mapObservationProfile;
    private readonly PublicMapCoordinate _chosen;
    internal bool HasFreeTravel { get; }
    internal long MapEventOrdinal { get; }

    private NativePublicInitialMapCondition(PublicMapObserved slice, PublicMapChosen chosen,
        bool freeTravel, long mapEventOrdinal, string? mapObservationProfile)
    {
        _slice = PublicJson.Serialize(slice); _chosen = chosen.Coordinate;
        HasFreeTravel = freeTravel; MapEventOrdinal = mapEventOrdinal;
        _mapObservationProfile = mapObservationProfile;
    }

    internal static bool TryCreate(PublicRunEvidence? evidence, NativeTapePrior prior,
        out NativePublicInitialMapCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        if (!prior.UsesRewardsProvenance || !prior.Execution.EmitsPublicEvidence
            || prior.Execution.SourcePolicyId != PublicContinuationPolicies.ReviewedId
            || prior.Execution.ResolvedOutsideCombatScript is not
                (NaturalSourceCollector.ScriptVersion or NaturalSourceCollector.BoundedEventScriptVersion))
        { reason = "typed_map_native_prior_required"; return false; }
        if (evidence is null || evidence.Events.Length < 8)
        { reason = "observed_initial_map_required"; return false; }
        if (PublicMapObservationProfiles.UsesCoordinateOrder(prior.Execution.PublicMapObservationProfile)
            && evidence.Events.Select(e => e.Payload).OfType<PublicMapObserved>()
                .Any(map => !PublicMapObservationProfiles.IsCoordinateOrdered(map)))
        { reason = "coordinate_order_map_options_required"; return false; }
        var events = evidence.Events;
        if (events[0].Payload is not PublicRunStarted { Character: "Silent", Ascension: 10 } runStart
            || runStart.Assets.Relics is not [ { Id: "RingOfTheSnake" } ]
            || events[1] is not { OwnerOrdinal: 0, Payload: PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Event, ActIndex: 0, Floor: 1,
                    ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } }
            || events[2] is not { OwnerOrdinal: 0, Payload: PublicOptionsObserved options }
            || options.Options.Length != 3 || options.Options.Any(o => o.IsLocked || o.Price is not null)
            || events[3] is not { OwnerOrdinal: 0, Payload: PublicOptionChosen neow }
            || neow.OfferEventOrdinal != events[2].EventOrdinal || neow.Key != options.Options[0].Key
            || !ReviewedNeowPositives.Contains(neow.Key))
        { reason = "unmodified_native_neow_map_not_certified"; return false; }

        var firstMap = events.FirstOrDefault(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Map });
        if (firstMap is not { OwnerOrdinal: long owner, Payload: PublicOwnerStarted
                { ActIndex: 0, Floor: 1, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } })
        { reason = "first_native_map_boundary_required"; return false; }
        // The map choice is observed after Neow finishes, whereas initial map
        // generation occurs in RunState's constructor before any players exist.
        // Do not move the rejection loop into that later callback with a held seed.
        var before = events.Take((int)firstMap.EventOrdinal).ToArray();
        if (before.Any(e => e.Payload is PublicEvidenceGap)
            || before.Any(e => e.Payload is PublicOwnerStarted s && e.OwnerOrdinal != 0
                && (s.OwnerKind is not (PublicEvidenceOwnerKind.Reward or PublicEvidenceOwnerKind.OutsideChoice)
                    || s.ActIndex != 0 || s.Floor != 1 || s.ParentOwnerOrdinal is null))
            || before.LastOrDefault(e => e.OwnerOrdinal == 0)?.Payload is not PublicOwnerEnded
                { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: { } afterNeow }
            || !afterNeow.Relics.Any(r => r.Id == neow.Key)
            || afterNeow.Relics.Any(r => r.Id == "GoldenCompass"))
        { reason = "uninterrupted_neow_to_map_boundary_required"; return false; }
        var mapEvents = events.Where(e => e.OwnerOrdinal == owner).ToArray();
        if (mapEvents.Length < 3 || mapEvents[1].Payload is not PublicMapObserved slice
            || mapEvents[2].Payload is not PublicMapChosen chosen
            || chosen.OfferEventOrdinal != mapEvents[1].EventOrdinal
            || slice.Current is not { Row: 0 } current
            || slice.Nodes.Single(n => n.Coordinate == current).NodeType != PublicMapNodeType.Ancient
            || slice.Options.Length == 0
            || slice.Options.Any(o => !o.IsOrdinaryConnection || o.Coordinate.Row != 1)
            || slice.Nodes.Any(n => n.Coordinate != current && (n.Coordinate.Row != 1
                || n.NodeType != PublicMapNodeType.Monster)))
        { reason = "complete_native_first_map_slice_required"; return false; }
        condition = new(slice, chosen, neow.Key == "WingedBoots", mapEvents[1].EventOrdinal,
            prior.Execution.PublicMapObservationProfile);
        return true;
    }

    internal bool MatchesMap(ActMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var current = map.StartingMapPoint;
        var row = map.GetPointsInRow(1).ToArray();
        if (map is not StandardActMap || current.PointType != MapPointType.Ancient
            || current.coord.row != 0 || row.Length == 0
            || row.Any(point => point.PointType != MapPointType.Monster || !current.Children.Contains(point)))
            throw new InvalidOperationException("Reviewed native initial map shape changed");
        // Boots offers the entire next row in grid order; ordinary travel uses
        // Children order. Every surviving first-row node has its Ancient parent,
        // so the first chosen node is the leftmost Monster regardless of HP.
        var choices = HasFreeTravel ? row : current.Children.ToArray();
        var selected = NativeSourceMapChoice.Choose(choices, 1, 1);
        return _chosen == new PublicMapCoordinate(selected.coord.col, selected.coord.row)
            && _slice == PublicJson.Serialize(NativePublicMapSlice.Observe(map, current, choices, _mapObservationProfile));
    }
}

/// <summary>The recorder's exact visible slice; Unknown is an icon, never its hidden room roll.</summary>
internal static class NativePublicMapSlice
{
    internal static PublicMapObserved Observe(ActMap map, MapPoint current, IReadOnlyList<MapPoint> choices,
        string? mapObservationProfile = null, PublicCurrentMapCapture? currentMap = null)
    {
        PublicMapCoordinate Coord(MapPoint p) => new(p.coord.col, p.coord.row);
        var points = choices.Prepend(current).Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).ToArray();
        var included = points.ToHashSet();
        var edges = points.SelectMany(p => p.Children.Where(included.Contains).Select(c => new PublicMapEdge(Coord(p), Coord(c))))
            .OrderBy(e => e.From.Row).ThenBy(e => e.From.Col).ThenBy(e => e.To.Row).ThenBy(e => e.To.Col).ToImmutableArray();
        // Sort a projection, never the native collection or the source's actions.
        IEnumerable<MapPoint> observedChoices = PublicMapObservationProfiles.UsesCoordinateOrder(mapObservationProfile)
            ? choices.OrderBy(p => p.coord.row).ThenBy(p => p.coord.col) : choices;
        bool completeGraph = PublicMapObservationProfiles.UsesCompleteGraph(mapObservationProfile);
        if (!completeGraph && currentMap is not null)
            throw new ArgumentException("A current map capture requires its complete-graph observation profile");
        return new(Coord(current), points.Select(p => new PublicMapNode(Coord(p), DisplayedType(map, p))).ToImmutableArray(), edges,
            observedChoices.Select(p => new PublicMapOption(Coord(p), current.Children.Contains(p))).ToImmutableArray(),
            completeGraph ? currentMap ?? PublicCurrentMapCapture.Missing() : null);
    }

    internal static PublicMapNodeType DisplayedType(ActMap map, MapPoint point) => point.PointType switch
    {
        MapPointType.Ancient => PublicMapNodeType.Ancient, MapPointType.Boss => PublicMapNodeType.Boss,
        MapPointType.Elite => PublicMapNodeType.Elite, MapPointType.Monster => PublicMapNodeType.Monster,
        MapPointType.RestSite => PublicMapNodeType.Rest, MapPointType.Shop => PublicMapNodeType.Shop,
        MapPointType.Treasure => PublicMapNodeType.Treasure, MapPointType.Unknown => PublicMapNodeType.Unknown,
        _ when ReferenceEquals(point, map.StartingMapPoint) => PublicMapNodeType.Start,
        _ => PublicMapNodeType.Unknown,
    };
}
