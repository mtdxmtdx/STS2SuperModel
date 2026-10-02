using System.Collections.ObjectModel;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>A public shape, not a native move identifier or source-state feature.</summary>
internal enum NativePublicMonsterIntentShape { Attack, AttackDebuff, AttackBuff, Status }
internal sealed record NativePublicMonsterRollTarget(int Slot, string Model, int RollOrdinal,
    long IntentEventOrdinal, NativePublicMonsterIntentShape Shape);
internal sealed record NativePublicMonsterIntentInput(int CombatIndex, long OwnerOrdinal, string EntryJson,
    IReadOnlyList<string> InitialRoster, IReadOnlyList<NativePublicMonsterRollTarget> Targets);

/// <summary>
/// Detached targets from complete, source-certified player-turn publications. The
/// startup closure and reviewed transition closure also exclude intent replacement,
/// additional rolls and extra turns. Unknown history only stops acceleration.
/// </summary>
internal sealed class NativePublicMonsterBranchIntentCondition
{
    internal const string Version = "nosl.public-monster-branch-intents.v1";
    internal IReadOnlyDictionary<int, NativePublicMonsterIntentInput> Combats { get; }
    internal int EligibleRollCount => Combats.Values.Sum(input => input.Targets.Count);
    internal int EligibleBranchCount => Combats.Values.Sum(input => input.Targets.Count(target =>
        target.Model == "LeafSlimeS" || target.RollOrdinal > 1));

    private NativePublicMonsterBranchIntentCondition(Dictionary<int, NativePublicMonsterIntentInput> combats)
        => Combats = new ReadOnlyDictionary<int, NativePublicMonsterIntentInput>(combats);

    internal static NativePublicMonsterBranchIntentCondition Create(DecisionPacket root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var inputs = new Dictionary<int, NativePublicMonsterIntentInput>();
        if (root.PublicEvidence is not { } evidence) return new(inputs);
        var prefixes = NativePublicCombatPrefixCondition.Create(root);
        // Construct this certificate from the exact same detached root. Its scan
        // preserves the reviewed transition closure across native reshuffles.
        var transitions = NativePublicReshuffleCondition.Create(root);
        foreach (var prefix in prefixes.Combats.Values.Where(input => input.Shuffle is not null))
        {
            var events = evidence.Events.Where(item => item.OwnerOrdinal == prefix.OwnerOrdinal).ToArray();
            if (events[0].Payload is not PublicOwnerStarted
                { ParentOwnerOrdinal: null, CompleteFromOwnerStart: true }) continue;
            int startup = Array.FindIndex(events, item => item.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
            if (startup < 0) continue;
            var firstIntents = events.Skip(startup + 1).TakeWhile(IsIntent).ToArray();
            if (firstIntents.Length == 0 || !firstIntents.Select(item => ((PublicCombatFact)item.Payload).TargetSlot)
                .SequenceEqual(Enumerable.Range(0, firstIntents.Length).Select(slot => (int?)slot))) continue;
            var roster = firstIntents.Select(item => ((PublicCombatFact)item.Payload).Model!).ToArray();
            var targets = new List<NativePublicMonsterRollTarget>();
            int expectedTurn = 1;
            // The scan endpoint excludes its first unproved transition. A missing
            // audit retains the earlier first-cycle certificate; startup remains
            // independently proved even when a later scan cannot advance.
            long certifiedThrough = transitions.CombatAudits.TryGetValue(prefix.CombatIndex, out var audit)
                ? audit.ThroughEventOrdinal : prefix.DrawPrefix?.ThroughEventOrdinal ?? -1;
            long through = Math.Max(firstIntents[^1].EventOrdinal, certifiedThrough);
            for (int i = startup; i < events.Length && events[i].EventOrdinal <= through; i++)
            {
                if (events[i].Payload is not PublicCombatFact { FactKind: PublicCombatFactKind.PlayerTurnStarted } turn)
                {
                    if (IsIntent(events[i])) break; // Unpaired/repeated publication is not another roll.
                    continue;
                }
                if (turn.Turn != expectedTurn) break;
                var intents = events.Skip(i + 1).TakeWhile(IsIntent).ToArray();
                if (intents.Length == 0 || intents.Any(item => item.EventOrdinal > through)) break;
                var facts = intents.Select(item => (PublicCombatFact)item.Payload).ToArray();
                int[] slots = facts.Select(fact => fact.TargetSlot ?? -1).ToArray();
                if (slots.Any(slot => slot < 0 || slot >= roster.Length)
                    || !slots.SequenceEqual(slots.Distinct().Order())
                    || facts.Any(fact => roster[fact.TargetSlot!.Value] != fact.Model)) break;
                var batch = new List<NativePublicMonsterRollTarget>();
                bool safe = true;
                for (int j = 0; j < facts.Length; j++)
                {
                    var fact = facts[j];
                    if (fact.Model is not ("SludgeSpinner" or "LeafSlimeS")) continue;
                    if (Shape(fact.Model, fact.Intents) is not { } shape
                        || expectedTurn == 1 && fact.Model == "SludgeSpinner" && shape != NativePublicMonsterIntentShape.AttackDebuff)
                    { safe = false; break; }
                    batch.Add(new(fact.TargetSlot!.Value, fact.Model, expectedTurn, intents[j].EventOrdinal, shape));
                }
                if (!safe) break;
                targets.AddRange(batch); expectedTurn++; i += intents.Length;
            }
            if (targets.Any(target => target.Model == "LeafSlimeS" || target.RollOrdinal > 1))
                inputs.Add(prefix.CombatIndex, new(prefix.CombatIndex, prefix.OwnerOrdinal, prefix.EntryJson!,
                    Array.AsReadOnly(roster), targets.AsReadOnly()));
        }
        return new(inputs);
    }

    private static bool IsIntent(PublicRunEvidenceEvent item) => item.Payload is PublicCombatFact
        { FactKind: PublicCombatFactKind.IntentPublished };
    private static NativePublicMonsterIntentShape? Shape(string model, IReadOnlyList<PublicIntent> intents)
    {
        // Damage depends on public powers and native damage calculation. The kind
        // sequence uniquely identifies these sealed graphs; full replay still
        // verifies every published numeric value, repeat count, and later fact.
        if (intents.Count == 1 && intents[0] is { Kind: "Attack", Damage: >= 0, Repeats: 1 })
            return NativePublicMonsterIntentShape.Attack;
        if (model == "LeafSlimeS" && intents.Count == 1 && intents[0] is { Kind: "StatusCard", Damage: null, Repeats: null })
            return NativePublicMonsterIntentShape.Status;
        if (model == "SludgeSpinner" && intents.Count == 2
            && intents[0] is { Kind: "Attack", Damage: >= 0, Repeats: 1 }
            && intents[1] is { Damage: null, Repeats: null })
            return intents[1].Kind switch
            { "Debuff" => NativePublicMonsterIntentShape.AttackDebuff, "Buff" => NativePublicMonsterIntentShape.AttackBuff, _ => null };
        return null;
    }
}
