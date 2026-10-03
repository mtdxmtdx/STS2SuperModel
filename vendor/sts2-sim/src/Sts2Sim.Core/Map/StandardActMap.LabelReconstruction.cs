using Sts2Sim.Core.Random;

namespace Sts2Sim.Core.Map;

/// <summary>A semantic public node, without generation state or encounter identity.</summary>
public readonly record struct LabelMapNode(MapCoord Coordinate, MapPointType PointType);

/// <summary>A directed public path, without native collection insertion order.</summary>
public readonly record struct LabelMapEdge(MapCoord From, MapCoord To);

public sealed partial class StandardActMap
{
    /// <summary>
    /// Explicit label-only import of a completely observed, already postprocessed
    /// standard map. The caller must certify the public producer and probability law;
    /// structural validation alone does not establish native positive support.
    /// No generation, pruning, postprocessing, or RNG draw occurs here.
    /// </summary>
    public static StandardActMap ReconstructForLabels(Rng mapRng, int numberOfRooms,
        IReadOnlyList<LabelMapNode> nodes, IReadOnlyList<LabelMapEdge> edges,
        bool hasSecondBoss = false) => new(mapRng, numberOfRooms, nodes, edges, hasSecondBoss);

    private StandardActMap(Rng mapRng, int numberOfRooms,
        IReadOnlyList<LabelMapNode> nodes, IReadOnlyList<LabelMapEdge> edges, bool hasSecondBoss)
    {
        ArgumentNullException.ThrowIfNull(mapRng);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        if (numberOfRooms < 1) throw new ArgumentOutOfRangeException(nameof(numberOfRooms));
        _mapLength = checked(numberOfRooms + 1);
        Grid = new MapPoint[MapWidth, _mapLength];
        _rng = mapRng;
        // Only the generator reads these counts. Imported maps never re-enter it.
        _pointTypeCounts = new MapPointTypeCounts(0, 0);
        var points = new Dictionary<MapCoord, MapPoint>();
        var start = new MapCoord(MapWidth / 2, 0);
        var boss = new MapCoord(MapWidth / 2, _mapLength);
        var secondBoss = new MapCoord(MapWidth / 2, _mapLength + 1);
        foreach (var node in nodes)
        {
            var coord = node.Coordinate;
            bool special = coord == start || coord == boss || (hasSecondBoss && coord == secondBoss);
            if (coord.col < 0 || coord.col >= MapWidth || (!special && (coord.row < 1 || coord.row >= _mapLength))
                || !Enum.IsDefined(node.PointType) || node.PointType == MapPointType.Unassigned
                || (coord == start ? node.PointType != MapPointType.Ancient
                    : special ? node.PointType != MapPointType.Boss
                    : node.PointType is MapPointType.Ancient or MapPointType.Boss)
                || !points.TryAdd(coord, new MapPoint(coord.col, coord.row) { PointType = node.PointType }))
                throw new ArgumentException("Invalid or duplicate semantic standard-map node", nameof(nodes));
            if (!special) Grid[coord.col, coord.row] = points[coord];
        }
        if (!points.TryGetValue(start, out var startPoint) || !points.TryGetValue(boss, out var bossPoint)
            || (hasSecondBoss && !points.ContainsKey(secondBoss)))
            throw new ArgumentException("Missing standard-map special node", nameof(nodes));
        StartingMapPoint = startPoint;
        BossMapPoint = bossPoint;
        SecondBossMapPoint = hasSecondBoss ? points[secondBoss] : null;
        var uniqueEdges = new HashSet<LabelMapEdge>();
        foreach (var edge in edges)
        {
            if (!points.TryGetValue(edge.From, out var from) || !points.TryGetValue(edge.To, out var to)
                || edge.To.row != edge.From.row + 1 || !uniqueEdges.Add(edge))
                throw new ArgumentException("Invalid or duplicate semantic standard-map path", nameof(edges));
            from.AddChildPoint(to);
        }
        var terminal = SecondBossMapPoint ?? BossMapPoint;
        if (points.Values.Any(p => !ReferenceEquals(p, StartingMapPoint) && p.parents.Count == 0
                || !ReferenceEquals(p, terminal) && p.Children.Count == 0)
            || StartingMapPoint.parents.Count != 0 || terminal.Children.Count != 0)
            throw new ArgumentException("Incomplete standard-map paths", nameof(edges));
        foreach (var point in GetPointsInRow(1)) startMapPoints.Add(point);
    }
}
