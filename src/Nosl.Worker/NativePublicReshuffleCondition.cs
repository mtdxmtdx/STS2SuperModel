using System.Collections.ObjectModel;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Detached joint witness for one native reshuffle pool and its successive draws.</summary>
internal sealed class NativePublicReshuffleInput
{
    internal int ReshuffleOrdinal { get; }
    internal long ShuffleEventOrdinal { get; }
    internal long WitnessEventOrdinal { get; }
    internal IReadOnlyList<string> PoolIds { get; }
    internal IReadOnlyList<string> DrawPrefixIds { get; }
    internal IReadOnlyList<NativePublicDrawKey> PoolKeys { get; }
    internal IReadOnlyList<NativePublicDrawKey> DrawPrefixKeys { get; }
    internal NativePublicReshuffleInput(int ordinal, long shuffleEvent, long witnessEvent,
        IEnumerable<NativePublicDrawKey> pool, IEnumerable<NativePublicDrawKey> prefix)
    {
        ReshuffleOrdinal = ordinal; ShuffleEventOrdinal = shuffleEvent; WitnessEventOrdinal = witnessEvent;
        PoolKeys = Array.AsReadOnly(pool.ToArray()); DrawPrefixKeys = Array.AsReadOnly(prefix.ToArray());
        PoolIds = Array.AsReadOnly(PoolKeys.Select(key => key.Id).ToArray());
        DrawPrefixIds = Array.AsReadOnly(DrawPrefixKeys.Select(key => key.Id).ToArray());
    }
}

internal sealed record NativePublicReshuffleCombat(string EntryJson, IReadOnlyList<NativePublicReshuffleInput> Targets);
internal sealed record NativePublicReshuffleCombatAudit(long ThroughEventOrdinal, string StopReason);

/// <summary>
/// Public-only acceleration under the declared ideal tape law. A complete, certified
/// shuffle-to-snapshot continuation fixes input IDs as remaining IDs plus drawn IDs.
/// Unknown transitions disable only this optional acceleration; evidence is untouched.
/// </summary>
internal sealed class NativePublicReshuffleCondition
{
    internal const string Version = "nosl.public-witnessed-reshuffle.v2";
    internal IReadOnlyDictionary<int, NativePublicReshuffleCombat> Combats { get; }
    internal IReadOnlyDictionary<int, NativePublicReshuffleCombatAudit> CombatAudits { get; }
    internal int EligibleShuffleCount => Combats.Values.Sum(combat => combat.Targets.Count);

    private NativePublicReshuffleCondition(Dictionary<int, NativePublicReshuffleCombat> combats,
        Dictionary<int, NativePublicReshuffleCombatAudit>? audits = null)
    {
        Combats = new ReadOnlyDictionary<int, NativePublicReshuffleCombat>(combats);
        CombatAudits = new ReadOnlyDictionary<int, NativePublicReshuffleCombatAudit>(audits ?? []);
    }

    internal static NativePublicReshuffleCondition Create(DecisionPacket root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.PublicEvidence is not { } evidence) return new([]);
        var startups = NativePublicCombatPrefixCondition.Create(root);
        long? globalGap = evidence.Events.FirstOrDefault(item => item.OwnerOrdinal is null
            && item.Payload is PublicEvidenceGap)?.EventOrdinal;
        var owners = evidence.Events.Where(item => item.OwnerOrdinal is not null)
            .GroupBy(item => item.OwnerOrdinal!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var combats = new Dictionary<int, NativePublicReshuffleCombat>();
        var audits = new Dictionary<int, NativePublicReshuffleCombatAudit>();
        foreach (var (index, input) in startups.Combats)
        {
            if (input.Shuffle is not { } initial || input.DrawPrefix is not { } audit
                || input.EntryJson is null || input.DecisionEventOrdinal is not { } firstOrdinal) continue;
            var events = owners[input.OwnerOrdinal];
            var first = events.Single(item => item.EventOrdinal == firstOrdinal);
            var targets = NativePublicDrawPrefixCondition.FindReshuffles(initial, audit.InitialDrawCount,
                events, first, globalGap, out var through);
            audits.Add(index, new(through.ThroughEventOrdinal, through.StopReason));
            if (targets.Count > 0) combats.Add(index, new(input.EntryJson, targets));
        }
        return new(combats, audits);
    }
}
