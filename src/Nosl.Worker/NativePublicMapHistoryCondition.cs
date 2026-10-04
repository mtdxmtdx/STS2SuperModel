using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Map;

namespace Nosl.Worker;

/// <summary>
/// A fixed necessary event on the original act-zero map, obtained from a linked
/// prefix of public choice slices. Unsupported suffixes remain in final replay.
/// See NATIVE_PUBLIC_MAP_HISTORY.md for the source-pinned mutation exclusion.
/// </summary>
internal sealed class NativePublicMapHistoryCondition
{
    private sealed record Slice(long Ordinal, PublicMapCoordinate Current, PublicMapCoordinate Chosen,
        string Observation, int Hp, int MaxHp, bool FreeTravel);
    private readonly Slice[] _laterSlices;
    private readonly string? _profile;
    internal NativePublicInitialMapCondition Initial { get; }
    internal IReadOnlyList<long> MapEventOrdinals { get; }
    internal int CertifiedSliceCount => MapEventOrdinals.Count;
    internal string? SuffixFallbackReason { get; }

    private NativePublicMapHistoryCondition(NativePublicInitialMapCondition initial, List<Slice> laterSlices,
        string? profile, string? fallback)
    {
        Initial = initial; _laterSlices = laterSlices.ToArray(); _profile = profile;
        MapEventOrdinals = Array.AsReadOnly(new[] { initial.MapEventOrdinal }.Concat(laterSlices.Select(s => s.Ordinal)).ToArray());
        SuffixFallbackReason = fallback;
    }

    internal static bool TryCreate(PublicRunEvidence? evidence, NativeTapePrior prior,
        out NativePublicMapHistoryCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        if (evidence is null) { reason = "observed_initial_map_required"; return false; }
        var events = evidence.Events;
        int start = -1;
        for (int i = 0; i < events.Length; i++)
            if (events[i].Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Map }) { start = i; break; }
        if (start < 0) { reason = "first_native_map_boundary_required"; return false; }
        // Certify the first slice independently of an unavailable/malformed later
        // suffix (including its option-order profile). This is a detached view,
        // never a replacement for the original evidence used in native replay.
        var prefix = events.Take(Math.Min(start + 4, events.Length)).ToImmutableArray();
        var firstEvidence = new PublicRunEvidence(evidence.SchemaVersion,
            prefix[0].Payload is PublicRunStarted && !prefix.Any(e => e.Payload is PublicEvidenceGap
                || e.Payload is PublicOwnerStarted { CompleteFromOwnerStart: false }), prefix);
        if (!NativePublicInitialMapCondition.TryCreate(firstEvidence, prior, out var initial, out reason)) return false;
        var later = new List<Slice>();
        string? fallback = null;
        PublicMapCoordinate? previousChoice = null;
        for (int i = start; i < events.Length; i++)
        {
            var entry = events[i];
            if (entry.Payload is PublicEvidenceGap)
            { fallback = "map_history_gap"; break; }
            if (HasCompass(entry.Payload))
            { fallback = "map_history_regeneration_context"; break; }
            if (entry.Payload is not PublicOwnerStarted owner) continue;
            if (owner.ActIndex != 0 || !owner.CompleteFromOwnerStart)
            { fallback = "map_history_unsupported_owner_context"; break; }
            if (owner.OwnerKind != PublicEvidenceOwnerKind.Map) continue;
            if (owner.ParentOwnerOrdinal is not null || i + 3 >= events.Length
                || events[i + 1].OwnerOrdinal != entry.OwnerOrdinal || events[i + 2].OwnerOrdinal != entry.OwnerOrdinal
                || events[i + 3].OwnerOrdinal != entry.OwnerOrdinal
                || events[i + 1].Payload is not PublicMapObserved observed
                || events[i + 2].Payload is not PublicMapChosen chosen
                || events[i + 3].Payload is not PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: { } assets }
                || chosen.OfferEventOrdinal != events[i + 1].EventOrdinal
                || observed.Current is not { } current || owner.Floor != current.Row + 1
                || (previousChoice is not null && previousChoice != current)
                || observed.Options.Length == 0 || observed.Options.Any(o => o.Coordinate.Row != current.Row + 1))
            { fallback = "complete_linked_map_owner_required"; break; }
            if (PublicMapObservationProfiles.UsesCoordinateOrder(prior.Execution.PublicMapObservationProfile)
                && !PublicMapObservationProfiles.IsCoordinateOrdered(observed))
            { fallback = "coordinate_order_map_options_required"; break; }
            if (assets.Relics.Any(r => r.Id == "GoldenCompass"))
            { fallback = "map_history_regeneration_context"; break; }
            if (!TryFreeTravel(assets, out bool freeTravel) || assets.Hp <= 0 || assets.MaxHp <= 0)
            { fallback = "map_choice_time_assets_required"; break; }
            if (i != start)
                later.Add(new(events[i + 1].EventOrdinal, current, chosen.Coordinate,
                    PublicJson.Serialize(observed), assets.Hp, assets.MaxHp, freeTravel));
            previousChoice = chosen.Coordinate;
            i += 3;
        }
        condition = new(initial!, later, prior.Execution.PublicMapObservationProfile, fallback);
        return true;
    }

    // GoldenCompass cannot be acquired even transiently in this source's act zero:
    // it is Ancient, absent from all direct act-zero grants, and Tezcatara is a
    // later-act Ancient. An observed contradiction disables only this suffix.
    private static bool HasCompass(PublicEvidencePayload payload) => payload switch
    {
        PublicOwnerEnded { Assets: { } assets } => assets.Relics.Any(r => r.Id == "GoldenCompass"),
        PublicCombatFact { Assets: { } assets } => assets.Relics.Any(r => r.Id == "GoldenCompass"),
        PublicOffersObserved offers => offers.Groups.Any(g => g.Offers.Any(o => o.Relic?.Id == "GoldenCompass")),
        PublicOptionsObserved options => options.Options.Any(o => o.Key == "GoldenCompass"),
        PublicOptionChosen chosen => chosen.Key == "GoldenCompass",
        _ => false,
    };

    private static bool TryFreeTravel(PublicEvidenceAssets assets, out bool freeTravel)
    {
        freeTravel = false;
        var boots = assets.Relics.Where(r => r.Id == "WingedBoots").ToArray();
        if (boots.Length == 0) return true;
        if (boots.Length != 1 || !boots[0].Details.TryGetValue("timesUsed", out int uses) || uses < 0
            || !boots[0].Details.TryGetValue("isMelted", out int melted) || melted is not (0 or 1)) return false;
        // RunState.IterateHookListeners omits melted relics; WingedBoots alone
        // overrides ShouldAllowFreeTravel and tests TimesUsed < 3.
        freeTravel = melted == 0 && uses < 3;
        return true;
    }

    /// <summary>Pure projection of MapTravel.GetTravelablePointsFrom, including its boss-row fallback.</summary>
    internal static MapPoint[] TravelablePoints(ActMap map, MapPoint current, bool freeTravel)
    {
        if (freeTravel)
        {
            var next = map.GetPointsInRow(current.coord.row + 1).ToArray();
            if (next.Length > 0) return next;
        }
        return current.Children.ToArray();
    }

    internal bool MatchesMap(ActMap map) => MatchingSlicePrefix(map) == CertifiedSliceCount;

    /// <summary>Number of successive certified predicates passed; diagnostic, with no RNG reads.</summary>
    internal int MatchingSlicePrefix(ActMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!Initial.MatchesMap(map)) return 0;
        int matched = 1;
        foreach (var slice in _laterSlices)
        {
            var current = map.GetPoint(slice.Current.Col, slice.Current.Row);
            if (current is null) break;
            var choices = TravelablePoints(map, current, slice.FreeTravel);
            if (choices.Length == 0) break;
            var chosen = NativeSourceMapChoice.Choose(choices, slice.Hp, slice.MaxHp);
            if (slice.Chosen != new PublicMapCoordinate(chosen.coord.col, chosen.coord.row)
                || slice.Observation != PublicJson.Serialize(NativePublicMapSlice.Observe(map, current, choices, _profile))) break;
            matched++;
        }
        return matched;
    }
}
