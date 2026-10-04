using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Optional conditioning of the first two native normal encounter selections.
/// The first-reward origin certificate proves that the observed startup is normal
/// slot one. Every other root retains the full native tape fallback prior.
/// </summary>
internal sealed class NativeFirstEncounterCondition
{
    private sealed record PoolEntry(string Id, EncounterTag[] Tags);
    private static readonly PoolEntry[] OvergrowthPool =
    [
        new("FUZZY_WURM_CRAWLER_WEAK", [EncounterTag.Crawler]),
        new("NIBBITS_WEAK", [EncounterTag.Nibbit]),
        new("SHRINKER_BEETLE_WEAK", [EncounterTag.Shrinker]),
        new("SLIMES_WEAK", [EncounterTag.Slimes]),
    ];
    private static readonly PoolEntry[] UnderdocksPool =
    [
        new("CORPSE_SLUGS_WEAK", [EncounterTag.Slugs]),
        new("SEAPUNK_WEAK", [EncounterTag.Seapunk]),
        new("SLUDGE_SPINNER_WEAK", []),
        new("TOADPOLES_WEAK", []),
    ];

    internal Type TargetActType { get; }
    internal string TargetEncounterId { get; }
    internal int TargetEncounterIndex { get; }
    internal ShuffleRational Envelope => NativeFirstEncounterProposal.Probability(TargetEncounterIndex);

    private NativeFirstEncounterCondition(bool overgrowth)
    {
        TargetActType = overgrowth ? typeof(Overgrowth) : typeof(Underdocks);
        TargetEncounterIndex = overgrowth ? 0 : 2;
        TargetEncounterId = (overgrowth ? OvergrowthPool : UnderdocksPool)[TargetEncounterIndex].Id;
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeFirstEncounterCondition? condition, out string? reason)
    {
        condition = null;
        // Reuse all fresh-run, source-script, retained-origin, immutable carry-in,
        // complete public coordinate/history, and startup-identity gates. This
        // also excludes forced event fights and startup identity-changing hooks.
        // RunDriver visits Neow, then the unmodifiable first Monster row. At
        // public act0/floor3/combat1 the single FuzzyWurmCrawler or SludgeSpinner
        // therefore identifies normal slot one (including Unknown -> Monster).
        // ActDefinition's first three normal slots are weak; only Overgrowth's
        // weak pool has FuzzyWurmCrawler and only Underdocks' has SludgeSpinner.
        if (!NativeFirstRewardCondition.TryCreate(root, prior, out _, out reason)) return false;
        var history = root.Observation!.History;
        int firstTurn = Array.FindIndex(history, item => item.Kind == "player_turn" && item.Detail == "1");
        var startup = PublicJson.Read<StartupIdentity>(history[firstTurn + 1].Detail);
        condition = new(startup.Id == "FuzzyWurmCrawler");
        return true;
    }

    /// <summary>
    /// Wrong act is a proved public nonmatch. Runtime shape/stream/source drift is
    /// an engine invariant error. The tape owner must check run ownership, the two
    /// primitive pre-states, unseen-cell aliases, and exactly one completed plan.
    /// </summary>
    internal NativeFirstEncounterProposal CreateProposal(LabelNormalEncounterContext context, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(nextWord);
        var run = context.Run; var act = context.Act;
        if (run.Rng.UsesSemanticKeys || !ReferenceEquals(context.Rng, run.Rng.UpFront)
            || run.CurrentActIndex != 0 || run.TotalFloor != 1 || run.CurrentRoomCount != 0
            || run.VisitedMapCoords.Count != 1 || run.CurrentMapCoord != run.Map.StartingMapPoint.coord
            || run.Players.Count != 1 || run.Players[0].Character is not Silent
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || run.Acts.Count != 3 || !ReferenceEquals(act, run.Acts[0])
            || run.Acts[1] is not Hive || run.Acts[2] is not Glory
            || act.GetType() != typeof(Overgrowth) && act.GetType() != typeof(Underdocks))
            throw new InvalidOperationException("Encounter interception is outside the certified fresh native run generation");
        ValidatePool(act);
        if (act.GetType() != TargetActType)
            throw new NativePublicConstraintMismatchException("Native act differs from the public second encounter origin");
        return NativeFirstEncounterProposal.Create(TargetEncounterIndex, nextWord);
    }

    internal static void ValidatePool(ActDefinition act)
    {
        PoolEntry[] expected = act.GetType() == typeof(Overgrowth) ? OvergrowthPool
            : act.GetType() == typeof(Underdocks) ? UnderdocksPool
            : throw new InvalidOperationException("Unreviewed act encounter pool");
        var weak = act.MonsterEncounterCandidates.Where(encounter => encounter.IsWeak).ToArray();
        if (act.Index != 0 || act.NumberOfWeakEncounters != 3 || act.BaseNumberOfRooms != 15
            || weak.Length != 4 || weak.Distinct(ReferenceEqualityComparer.Instance).Count() != 4
            || weak.Where((encounter, index) => encounter.IdEntry != expected[index].Id
                || !encounter.Tags.SequenceEqual(expected[index].Tags)).Any()
            || weak.SelectMany(encounter => encounter.Tags).Where(tag => tag != EncounterTag.None)
                .GroupBy(tag => tag).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Reviewed weak encounter pool, ordering, or tags changed");
        // ActDefinition.PickWithoutRepeating fills its fresh bag in this order
        // with literal weight 1.0 per entry. No dynamic weight hook exists. These
        // four pairwise disjoint tags make both first draws predicate-eligible:
        // slot zero draws once from four; RemoveAt preserves the remaining order;
        // slot one draws once from three. Native boundary tests pin that weight
        // law as well as the two-word count. No later rejection loop is altered.
    }

    private sealed record StartupIdentity(int Slot, string Id);
}

internal sealed record NativeFirstEncounterBranch(int FirstIndex, ConditionalShuffleFactor First,
    ConditionalShuffleFactor Second);

/// <summary>
/// Exact conditional law of the two native UpFront words given slot-one identity.
/// Native GrabBag uses high-53-bit NextDouble times the integer total weight and
/// strict cumulative comparisons, equivalent here to the existing floor buckets.
/// Slot-zero buckets have equal Q/4 mass. Weight each possible first encounter by
/// its slot-one target bucket b_i, then sample both complete raw-word preimages.
/// Every compatible pair has p/q = sum(b_i)/(4Q), a root-constant envelope.
/// Prior combat HP and the rest of generation retain their native conditional law.
/// </summary>
internal sealed class NativeFirstEncounterProposal
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal int FirstEncounterIndex { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;

    private NativeFirstEncounterProposal(ulong[] words, int firstIndex, ShuffleRational probability)
    { RawWords = Array.AsReadOnly(words); FirstEncounterIndex = firstIndex; NativeToProposalRatio = probability; }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }

    internal static NativeFirstEncounterProposal Create(int targetIndex, Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var branches = Branches(targetIndex, precisionBits);
        ulong total = branches.Aggregate(0UL, (sum, branch) => checked(sum + branch.Second.BucketSize));
        ulong draw = ConditionalShuffleProposal.UniformBelow(total, nextWord);
        var selected = branches[^1];
        foreach (var branch in branches)
        {
            if (draw < branch.Second.BucketSize) { selected = branch; break; }
            draw -= branch.Second.BucketSize;
        }
        int lowBits = 64 - precisionBits;
        ulong Sample(ConditionalShuffleFactor factor) =>
            ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
            | (nextWord() & ((1UL << lowBits) - 1));
        return new([Sample(selected.First), Sample(selected.Second)], selected.FirstIndex,
            new ShuffleRational(total, 4UL * (1UL << precisionBits)));
    }

    internal static ShuffleRational Probability(int targetIndex, int precisionBits = 53)
    {
        var branches = Branches(targetIndex, precisionBits);
        ulong total = branches.Aggregate(0UL, (sum, branch) => checked(sum + branch.Second.BucketSize));
        return new(total, 4UL * (1UL << precisionBits));
    }

    internal static NativeFirstEncounterBranch[] Branches(int targetIndex, int precisionBits = 53)
    {
        if (precisionBits is < 2 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        if (targetIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(targetIndex));
        return Enumerable.Range(0, 4).Where(first => first != targetIndex)
            .Select(first => new NativeFirstEncounterBranch(first,
                ConditionalShuffleProposal.Factor(4, first, precisionBits),
                ConditionalShuffleProposal.Factor(3, targetIndex - (first < targetIndex ? 1 : 0), precisionBits)))
            .ToArray();
    }
}
