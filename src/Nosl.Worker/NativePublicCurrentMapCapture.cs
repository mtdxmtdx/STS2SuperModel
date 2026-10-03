using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Map;

namespace Nosl.Worker;

/// <summary>
/// Native public-view declaration for standard act zero. This is a field-by-field
/// projection of displayed icons and ordinary paths, not a live-client visibility
/// adapter. Unsupported views retain an explicit missing capture.
/// </summary>
internal static class NativePublicCurrentMapCapture
{
    internal static PublicCurrentMapCapture Observe(ActMap map, int actIndex)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (actIndex != 0 || map is not StandardActMap || map.GetColumnCount() != 7 || map.GetRowCount() != 16
            || map.SecondBossMapPoint is not null
            || map.StartingMapPoint.coord is not { col: 3, row: 0 }
            || map.BossMapPoint.coord is not { col: 3, row: 16 }
            || map.StartingMapPoint.PointType != MapPointType.Ancient || map.BossMapPoint.PointType != MapPointType.Boss)
            return PublicCurrentMapCapture.Missing();

        // GetAllMapPoints enumerates only the grid, excluding both special nodes.
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint)
            .Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).ToArray();
        var included = points.ToHashSet();
        if (points.Any(p => p.Children.Any(c => !included.Contains(c))
            || p.PointType is not (MapPointType.Ancient or MapPointType.Boss or MapPointType.Elite
                or MapPointType.Monster or MapPointType.RestSite or MapPointType.Shop
                or MapPointType.Treasure or MapPointType.Unknown)))
            return PublicCurrentMapCapture.Missing();

        PublicMapCoordinate Coord(MapPoint p) => new(p.coord.col, p.coord.row);
        try
        {
            var edges = points.SelectMany(p => p.Children.Select(c => new PublicMapEdge(Coord(p), Coord(c))))
                .OrderBy(e => e.From.Row).ThenBy(e => e.From.Col).ThenBy(e => e.To.Row).ThenBy(e => e.To.Col).ToImmutableArray();
            return new(PublicMapCaptureStatus.Complete,
                points.Select(p => new PublicMapNode(Coord(p), NativePublicMapSlice.DisplayedType(map, p))).ToImmutableArray(),
                edges, Coord(map.StartingMapPoint), [Coord(map.BossMapPoint)]);
        }
        catch (ArgumentException)
        {
            // A broken or partial semantic graph cannot attest completeness.
            return PublicCurrentMapCapture.Missing();
        }
    }
}
