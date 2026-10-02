using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Map;

namespace Nosl.Worker;

/// <summary>A public certificate for the first WingedBoots charge on the second map move.</summary>
internal sealed class NativeMapTravelCondition
{
    internal int TargetEntryHp { get; }
    internal int TargetMaxHp { get; }

    private NativeMapTravelCondition(int hp, int maxHp)
    { TargetEntryHp = hp; TargetMaxHp = maxHp; }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeMapTravelCondition? condition, out string? reason)
    {
        condition = null;
        if (!NativeFirstRewardCondition.TryCreate(root, prior, out var reward, out reason)) return false;
        if (!NativeFirstEncounterCondition.TryCreate(root, prior, out var encounter, out reason)) return false;
        if (reward!.TargetNeowId != "WingedBoots" || encounter!.TargetActType != typeof(Overgrowth))
        { reason = "map_travel_origin_not_certified"; return false; }
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var initialBoots = entry.Relics.Where(relic => relic.Id == "WingedBoots").ToArray();
        var currentBoots = root.Observation.RelicStates?.Where(relic => relic.Id == "WingedBoots").ToArray();
        if (initialBoots is not [var initial] || !OrdinaryBoots(initial, 0)
            || currentBoots is not [var current] || !OrdinaryBoots(current, 1))
        { reason = "public_boots_entry_zero_current_one_required"; return false; }
        condition = new(entry.Hp, entry.MaxHp); return true;
    }

    private static bool OrdinaryBoots(PublicRelic relic, int times) =>
        relic.Details is not null && relic.Details.TryGetValue("timesUsed", out int used) && used == times
        && relic.Details.TryGetValue("isWax", out int wax) && wax == 0
        && relic.Details.TryGetValue("isMelted", out int melted) && melted == 0
        && relic.Details.TryGetValue("isUsedUp", out int usedUp) && usedUp == 0
        && relic.Details.TryGetValue("stackCount", out int stack) && stack == 1;

    internal bool MatchesMap(ActMap map) => HasSecondMoveCharge(map, TargetEntryHp, TargetMaxHp);

    internal static bool HasSecondMoveCharge(ActMap map, int hp, int maxHp)
    {
        // Generation runs before players exist, so map hooks are empty. The
        // origin certificate permits no later map-mutating relic/card. Pruning
        // removes a row-one point entirely or preserves its sole Ancient parent.
        // Thus the first choice is a connected Monster regardless of starting HP.
        // Boots remains unused, exposing every row-two point for the next choice.
        // Public carry-in HP fixes that choice's priority. Keep Unknown nodes:
        // their later native room-type draw is not part of this map predicate.
        var (first, second) = NativeSourceMapChoice.FirstTwoChoices(map, hp, maxHp, freeTravel: true);
        return !first.Children.Contains(second);
    }
}
