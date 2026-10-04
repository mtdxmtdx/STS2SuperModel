using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Nosl.Contracts;

public enum PublicMapCaptureStatus { Missing, Complete }

/// <summary>
/// A declared capture of the current act's entire visible semantic graph. The
/// enclosing map owner supplies the act index. Completeness is a producer claim,
/// not evidence that a live client displayed every node or path.
/// </summary>
public sealed class PublicCurrentMapCapture
{
    public PublicMapCaptureStatus Status { get; }
    public ImmutableArray<PublicMapNode> Nodes { get; }
    public ImmutableArray<PublicMapEdge> Edges { get; }
    public PublicMapCoordinate? StartingNode { get; }
    public ImmutableArray<PublicMapCoordinate> BossNodes { get; }

    [JsonConstructor]
    public PublicCurrentMapCapture(PublicMapCaptureStatus status, ImmutableArray<PublicMapNode> nodes,
        ImmutableArray<PublicMapEdge> edges, PublicMapCoordinate? startingNode,
        ImmutableArray<PublicMapCoordinate> bossNodes)
    {
        Status = EvidenceGuard.Enum(status);
        Nodes = EvidenceGuard.Array(nodes, nameof(nodes));
        Edges = EvidenceGuard.Array(edges, nameof(edges));
        StartingNode = startingNode;
        BossNodes = EvidenceGuard.Array(bossNodes, nameof(bossNodes));
        if (status == PublicMapCaptureStatus.Missing)
        {
            if (Nodes.Length != 0 || Edges.Length != 0 || StartingNode is not null || BossNodes.Length != 0)
                throw new ArgumentException("A missing current map capture cannot contain a partial graph");
            return;
        }

        var byCoordinate = Nodes.ToDictionary(n => n.Coordinate);
        if (StartingNode is null || !byCoordinate.TryGetValue(StartingNode, out var start)
            || start.NodeType is not (PublicMapNodeType.Start or PublicMapNodeType.Ancient)
            || BossNodes.Length == 0 || BossNodes.Contains(StartingNode)
            || BossNodes.Any(b => !byCoordinate.TryGetValue(b, out var node) || node.NodeType != PublicMapNodeType.Boss)
            || !Nodes.Where(n => n.NodeType == PublicMapNodeType.Boss).Select(n => n.Coordinate).ToHashSet().SetEquals(BossNodes)
            || Edges.Any(e => !byCoordinate.ContainsKey(e.From) || !byCoordinate.ContainsKey(e.To) || e.To.Row <= e.From.Row)
            || !StrictlyOrdered(Nodes.Select(n => n.Coordinate)) || !StrictlyOrdered(BossNodes)
            || Edges.Zip(Edges.Skip(1)).Any(pair => Compare(pair.First, pair.Second) >= 0))
            throw new ArgumentException("Invalid or noncanonical complete current map graph");

        var reachable = new HashSet<PublicMapCoordinate> { StartingNode };
        foreach (var node in Nodes)
            if (reachable.Contains(node.Coordinate))
                reachable.UnionWith(Edges.Where(e => e.From == node.Coordinate).Select(e => e.To));
        var reachesBoss = BossNodes.ToHashSet();
        foreach (var node in Nodes.Reverse())
            if (Edges.Any(e => e.From == node.Coordinate && reachesBoss.Contains(e.To)))
                reachesBoss.Add(node.Coordinate);
        if (reachable.Count != Nodes.Length || reachesBoss.Count != Nodes.Length)
            throw new ArgumentException("A complete current map must retain every start-to-boss path");
    }

    public static PublicCurrentMapCapture Missing() => new(PublicMapCaptureStatus.Missing, [], [], null, []);

    internal static int Compare(PublicMapCoordinate a, PublicMapCoordinate b)
    {
        int row = a.Row.CompareTo(b.Row);
        return row != 0 ? row : a.Col.CompareTo(b.Col);
    }
    private static int Compare(PublicMapEdge a, PublicMapEdge b)
    {
        int from = Compare(a.From, b.From);
        return from != 0 ? from : Compare(a.To, b.To);
    }
    private static bool StrictlyOrdered(IEnumerable<PublicMapCoordinate> coordinates) =>
        !coordinates.Zip(coordinates.Skip(1)).Any(pair => Compare(pair.First, pair.Second) >= 0);

    internal void ValidateSlice(PublicMapObserved slice)
    {
        if (Status != PublicMapCaptureStatus.Complete) return;
        var byCoordinate = Nodes.ToDictionary(n => n.Coordinate);
        var sliceCoordinates = slice.Nodes.Select(n => n.Coordinate).ToHashSet();
        if (slice.Nodes.Any(n => !byCoordinate.TryGetValue(n.Coordinate, out var full) || full.NodeType != n.NodeType)
            || !Edges.Where(e => sliceCoordinates.Contains(e.From) && sliceCoordinates.Contains(e.To))
                .Select(e => (e.From, e.To)).ToHashSet().SetEquals(slice.Edges.Select(e => (e.From, e.To))))
            throw new ArgumentException("The visible map choice slice differs from the complete current map");
    }
}
