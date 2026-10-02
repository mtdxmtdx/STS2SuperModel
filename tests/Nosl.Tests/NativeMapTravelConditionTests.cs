using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeMapTravelConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };

    [Fact]
    public async Task NativePublicBootsTransitionIdentifiesTheSecondMapJump()
    {
        var prior = Prior;
        var recipe = prior.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe));
        Assert.NotNull(source); var packet = source.Observe();
        Assert.True(NativeMapTravelCondition.TryCreate(packet, prior, out var condition, out var reason), reason);
        Assert.Equal(49, condition!.TargetEntryHp); Assert.Equal(70, condition.TargetMaxHp);
        Assert.True(condition.MatchesMap(source.NativeRun.Map));
        var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation!.History[1].Detail);
        Assert.Equal(0, entry.Relics.Single(relic => relic.Id == "WingedBoots").Details["timesUsed"]);
        Assert.Equal(1, packet.Observation.RelicStates!.Single(relic => relic.Id == "WingedBoots").Details["timesUsed"]);
        foreach (int used in new[] { 0, 2 })
        {
            var changed = packet with { Observation = packet.Observation with
            { RelicStates = packet.Observation.RelicStates!.Select(relic => relic.Id != "WingedBoots" ? relic
                : relic with { Details = new Dictionary<string, int>(relic.Details) { ["timesUsed"] = used } }).ToArray() } };
            Assert.False(NativeMapTravelCondition.TryCreate(changed, prior, out _, out reason));
            Assert.Equal("public_boots_entry_zero_current_one_required", reason);
        }
        var missing = packet with { Observation = packet.Observation with { RelicStates = null } };
        Assert.False(NativeMapTravelCondition.TryCreate(missing, prior, out _, out _));
        var wrongEntry = entry with { Relics = entry.Relics.Select(relic => relic.Id != "WingedBoots" ? relic
            : relic with { Details = new Dictionary<string, int>(relic.Details) { ["timesUsed"] = 1 } }).ToArray() };
        packet.Observation.History[1] = packet.Observation.History[1] with { Detail = PublicJson.Serialize(wrongEntry) };
        Assert.False(NativeMapTravelCondition.TryCreate(packet, prior, out _, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PureFactoryPreservesTheCompleteOrdinaryNativeMapAndRng(bool overgrowth)
    {
        ActDefinition act = overgrowth ? new Overgrowth() : new Underdocks();
        var ascension = new AscensionManager(10);
        var rng = StandardActMap.CreateRng(12345678, 0);
        var originalRng = new Rng(12345678, "act_1_map");
        var originalCounts = act.GetMapPointTypes(originalRng, ascension);
        var original = new StandardActMap(originalRng, act.BaseNumberOfRooms, originalCounts, hasSecondBoss: false);
        var extracted = StandardActMap.CreateFor(act, rng, ascension, hasSecondBoss: false);
        Assert.Equal(MapShape(original), MapShape(extracted));
        Assert.Equal(originalRng.ToSerializable(), rng.ToSerializable());
        Assert.All(extracted.GetPointsInRow(1), point =>
        { Assert.Equal(MapPointType.Monster, point.PointType); Assert.Contains(point, extracted.StartingMapPoint.Children); });
    }

    [Theory]
    [InlineData(49, MapPointType.Monster)]
    [InlineData(20, MapPointType.RestSite)]
    public void SharedSourceChoiceRetainsHpPriorityAndPostprocessedColumnOrder(int hp, MapPointType selected)
    {
        MapPoint[] points =
        [
            new(5, 2) { PointType = MapPointType.Monster },
            new(1, 2) { PointType = MapPointType.Monster },
            new(0, 2) { PointType = MapPointType.Unknown },
            new(6, 2) { PointType = MapPointType.RestSite },
        ];
        var choice = NativeSourceMapChoice.Choose(points, hp, 70);
        Assert.Equal(selected, choice.PointType);
        Assert.Equal(hp == 49 ? 1 : 6, choice.coord.col);
        Assert.Same(points[2], NativeSourceMapChoice.Choose([points[2], new(0, 2) { PointType = MapPointType.Shop }], hp, 70));
    }

    internal static string MapShape(ActMap map) => PublicJson.Serialize(map.GetAllMapPoints()
        .Append(map.StartingMapPoint).Append(map.BossMapPoint)
        .OrderBy(point => point.coord.row).ThenBy(point => point.coord.col)
        .Select(point => new { row = point.coord.row, col = point.coord.col, type = point.PointType,
            children = point.Children.Select(child => new { row = child.coord.row, col = child.coord.col })
                .OrderBy(child => child.row).ThenBy(child => child.col).ToArray() }).ToArray());
}
