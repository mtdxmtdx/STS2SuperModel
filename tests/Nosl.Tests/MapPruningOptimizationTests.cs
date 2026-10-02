using Sts2Sim.Core.Content;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class MapPruningOptimizationTests
{
    [Theory]
    [InlineData(0, "Overgrowth", 0, "b2ad00fbc489baf9cabbdfeefb4fc89ff226efe93dae14bb2ca522c48ca5eadf")]
    [InlineData(0, "Overgrowth", 4, "8a440fd9dd81956effa37afccde6edcb4fb0fa3f80e64c76d6161c8cac4dea68")]
    [InlineData(0, "Underdocks", 0, "b2ad00fbc489baf9cabbdfeefb4fc89ff226efe93dae14bb2ca522c48ca5eadf")]
    [InlineData(0, "Underdocks", 4, "8a440fd9dd81956effa37afccde6edcb4fb0fa3f80e64c76d6161c8cac4dea68")]
    [InlineData(0, "Hive", 0, "ab1f094953cc916e061fafbbef01ea3955b0efead93a1b52e7fcff2708868a26")]
    [InlineData(0, "Hive", 4, "84eb9ba0dc6406b1ac58292afa1ace49f0fa0e37bafa53639dcfd1c5b5618e78")]
    [InlineData(0, "Glory", 0, "1a2d0d817df45d2938443280dd11fa891e04eb3c76e628420bcdf80f837cfe82")]
    [InlineData(0, "Glory", 4, "5da22ef740ed66b4f2a455307565aeeb9b09e0ce8aba04cd482b6bc5295991e0")]
    [InlineData(10, "Overgrowth", 0, "1f15171b9f95aea754720c26d05ccd5b343859827e29bcdb314b2856ac62f86d")]
    [InlineData(10, "Overgrowth", 4, "f10972abd102bfa710d6cec4ecd43ff48f53e73410a117f5c72e01df27782868")]
    [InlineData(10, "Underdocks", 0, "1f15171b9f95aea754720c26d05ccd5b343859827e29bcdb314b2856ac62f86d")]
    [InlineData(10, "Underdocks", 4, "f10972abd102bfa710d6cec4ecd43ff48f53e73410a117f5c72e01df27782868")]
    [InlineData(10, "Hive", 0, "feb79f52a0ce72fad23744ad3a97bbc8e0632567fd4793b9ae27c904e1214d86")]
    [InlineData(10, "Hive", 4, "173f85de8fc111c1a073db6bdcb91dcbb7c0a68133df1fe4e459c02f90b841b0")]
    [InlineData(10, "Glory", 0, "fa4c63adeaccfdcbe4c9374a0e559a5d30f92ae377190e6910ae3193de258e4e")]
    [InlineData(10, "Glory", 4, "c29354888e312e92c06c8ac97552a9c1f322b9b45a40ec9d504b0c8e5c44e05b")]
    public void CompleteMapTopologyOrderAndRandomStateMatchPreOptimizationGolden(int level, string actName, int index, string expected)
    {
        ActDefinition act = actName switch { "Overgrowth" => new Overgrowth(), "Underdocks" => new Underdocks(), "Hive" => new Hive(), "Glory" => new Glory(), _ => throw new ArgumentException() };
        var rng = new Rng(new RunRngSet("nosl-map-profile:" + index).Seed, $"act_{act.Index + 1}_map");
        var counts = act.GetMapPointTypes(rng, new AscensionManager(level));
        var map = new StandardActMap(rng, act.BaseNumberOfRooms, counts, hasSecondBoss: level == 10 && act.Index == 2);
        string Point(MapPoint p) => $"{p.coord.col},{p.coord.row}";
        var state = new { starts = map.startMapPoints.Select(Point).ToArray(),
            points = map.GetAllMapPoints().Concat(new[] { map.StartingMapPoint, map.BossMapPoint }).Concat(map.SecondBossMapPoint is {} boss ? new[] { boss } : Array.Empty<MapPoint>()).Select(p => new
            { point = Point(p), p.PointType, p.CanBeModified, parents = p.parents.Select(Point).ToArray(), children = p.Children.Select(Point).ToArray() }).ToArray(),
            rng = rng.ToSerializable() };
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { IncludeFields = true });
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
    }

    [Fact]
    public void DuplicateWorkElisionPreservesReferenceAlgorithmOrderAndDoesNotCacheAcrossMutations()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new System.Random(seed);
            var root = new MapPoint(1, 0) { PointType = MapPointType.Ancient };
            var layers = Enumerable.Range(1, 6).Select(row => Enumerable.Range(0, 3).Select(col =>
                new MapPoint(col, row) { PointType = random.Next(3) == 0 ? MapPointType.Unknown : MapPointType.Monster }).ToArray()).ToArray();
            var boss = new MapPoint(1, 7) { PointType = MapPointType.Boss };
            foreach (var node in layers[0]) root.AddChildPoint(node);
            for (int row = 0; row < layers.Length - 1; row++) foreach (var node in layers[row])
            {
                node.AddChildPoint(layers[row + 1][random.Next(3)]);
                node.AddChildPoint(layers[row + 1][random.Next(3)]);
            }
            foreach (var node in layers[^1]) node.AddChildPoint(boss);
            Check(root);
            layers[2][1].PointType = MapPointType.Elite;
            layers[3][0].AddChildPoint(layers[4][2]);
            Check(root);
        }
        static void Check(MapPoint root)
        {
            var expected = ReferenceMatchingSegments(root);
            var actual = MapPathPruning.FindMatchingSegments(root);
            Assert.Equal(expected.Count, actual.Count);
            for (int group = 0; group < expected.Count; group++)
            {
                Assert.Equal(expected[group].Count, actual[group].Count);
                for (int segment = 0; segment < expected[group].Count; segment++)
                    Assert.Equal(expected[group][segment], actual[group][segment]);
            }
        }
    }

    // Frozen pre-optimization reference, intentionally retaining duplicate work.
    private static List<List<MapPoint[]>> ReferenceMatchingSegments(MapPoint root)
    {
        var segments = new SortedDictionary<string, List<MapPoint[]>>(StringComparer.Ordinal);
        foreach (var path in MapPathPruning.FindAllPaths(root))
        for (int i = 0; i < path.Count - 1; i++)
        {
            if (!(path[i].Children.Count > 1 || path[i].coord.row == 0)) continue;
            for (int j = 2; j < path.Count - i; j++)
            {
                var end = path[i + j]; if (end.parents.Count < 2) continue;
                var segment = path.Skip(i).Take(j + 1).ToArray();
                var first = segment[0]; var last = segment[^1];
                string key = (first.coord.row == 0 ? $"{first.coord.row}-{last.coord.col},{last.coord.row}-"
                    : $"{first.coord.col},{first.coord.row}-{last.coord.col},{last.coord.row}-")
                    + string.Join(",", segment.Select(p => (int)p.PointType));
                if (!segments.TryGetValue(key, out var matches)) segments[key] = new() { segment };
                else if (!matches.Any(old => Enumerable.Range(1, old.Length - 2).Any(k => ReferenceEquals(old[k], segment[k])))) matches.Add(segment);
            }
        }
        return segments.Values.Where(group => group.Count > 1).ToList();
    }
}
