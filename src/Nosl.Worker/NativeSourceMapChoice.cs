using Sts2Sim.Core.Map;

namespace Nosl.Worker;

/// <summary>The declared source script's deterministic native map choice.</summary>
internal static class NativeSourceMapChoice
{
    internal static MapPoint Choose(IEnumerable<MapPoint> choices, int hp, int maxHp)
    {
        bool hurt = hp * 2 < maxHp;
        int Priority(MapPoint point) => point.PointType switch
        {
            MapPointType.RestSite when hurt => -1, MapPointType.Monster => 0,
            MapPointType.Treasure => 1, MapPointType.RestSite => 2, MapPointType.Unknown => 3,
            MapPointType.Shop => 4, MapPointType.Elite => 5, _ => 6,
        };
        return choices.OrderBy(Priority).ThenBy(point => point.coord.col).ThenBy(point => point.coord.row).First();
    }

    internal static (MapPoint First, MapPoint Second) FirstTwoChoices(ActMap map, int hp, int maxHp, bool freeTravel)
    {
        ArgumentNullException.ThrowIfNull(map);
        var firstRow = map.GetPointsInRow(1).ToArray();
        if (map is not StandardActMap || map.StartingMapPoint.PointType != MapPointType.Ancient
            || firstRow.Length == 0 || firstRow.Any(point => point.PointType != MapPointType.Monster
                || !map.StartingMapPoint.Children.Contains(point)))
            throw new InvalidOperationException("Reviewed native first map route shape changed");
        // Every first-row point is a connected Monster, so starting HP and the
        // free-travel hook cannot change the first choice or spend a Boots charge.
        var first = Choose(firstRow, hp, maxHp);
        var nextRow = map.GetPointsInRow(2).ToArray();
        var secondChoices = freeTravel && nextRow.Length > 0 ? nextRow : first.Children.ToArray();
        if (secondChoices.Length == 0) throw new InvalidOperationException("Native first map route is exhausted");
        return (first, Choose(secondChoices, hp, maxHp));
    }
}
