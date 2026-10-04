using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>One exact conditional native HP word, relative to the independent-word tape law.</summary>
internal sealed record NativeHpProposal(ulong RawWord, ConditionalShuffleFactor Factor,
    ulong RootMaxBucketSize, int PrecisionBits)
{
    internal ulong BucketSize => Factor.BucketSize;
    internal ShuffleRational NativeToProposalRatio => new(BucketSize, 1UL << PrecisionBits);
    internal ShuffleRational Envelope => new(RootMaxBucketSize, 1UL << PrecisionBits);

    internal bool AcceptCorrection(Func<ulong> nextWord) =>
        BucketSize == RootMaxBucketSize
        || ConditionalShuffleProposal.UniformBelow(RootMaxBucketSize, nextWord) < BucketSize;

    /// <summary>
    /// Native uniqueness excludes distinct earlier HP values, including other monster types.
    /// When every value is used, native falls back to its full range with one ordinary draw.
    /// The envelope must have been computed from the public root, never this proposal's order.
    /// </summary>
    internal static NativeHpProposal? Create(int min, int max, int target,
        IReadOnlyList<int> usedHp, ulong rootMaxBucketSize, Func<ulong> nextWord,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(usedHp);
        ArgumentNullException.ThrowIfNull(nextWord);
        int range = checked(max - min + 1);
        if (range <= 0 || usedHp.Any(hp => hp < min || hp > max)
            || !usedHp.SequenceEqual(usedHp.Distinct().Order()))
            throw new ArgumentException("HP context must contain sorted distinct in-range values");
        if (target < min || target > max) return null;
        int available = range - usedHp.Count;
        int bound = available > 0 ? available : range;
        if (available > 0 && usedHp.Contains(target)) return null;
        int index = target - min - (available > 0 ? usedHp.Count(hp => hp < target) : 0);
        var factor = ConditionalShuffleProposal.Factor(bound, index, precisionBits);
        if (factor.BucketSize == 0) return null;
        if (rootMaxBucketSize < factor.BucketSize || rootMaxBucketSize > (1UL << precisionBits))
            throw new InvalidOperationException("Native HP bucket exceeds its public-root envelope");
        int lowBits = 64 - precisionBits;
        ulong high = factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord);
        ulong raw = (high << lowBits) | (nextWord() & ((1UL << lowBits) - 1));
        return new(raw, factor, rootMaxBucketSize, precisionBits);
    }

    /// <summary>
    /// Bounds every possible creation order. Other published target HP values can remove
    /// at most m distinct values in this range; exhausted fallback has the original bound.
    /// This conservative bound is constant for the whole public root, including slot sorts.
    /// </summary>
    internal static ulong RootEnvelopeBucket(int min, int max, IEnumerable<int> otherTargetHp,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(otherTargetHp);
        int range = checked(max - min + 1);
        if (range <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        int possibleUsed = otherTargetHp.Where(hp => hp >= min && hp <= max).Distinct().Count();
        ulong maximum = 0;
        for (int bound = Math.Max(1, range - possibleUsed); bound <= range; bound++)
            maximum = Math.Max(maximum, ConditionalShuffleProposal.Factor(bound, 0, precisionBits).MaxBucketSize);
        return maximum;
    }
}
