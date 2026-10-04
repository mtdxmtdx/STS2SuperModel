using System.Numerics;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeRewardIdentityArm(CardRarity? RolledRarity, ulong RarityStart,
    ulong RaritySize, IReadOnlyList<ConditionalShuffleFactor> MatchingIndices)
{
    internal BigInteger IndexMass => MatchingIndices.Aggregate(BigInteger.Zero, (sum, index) => sum + index.BucketSize);
    internal BigInteger Mass => RaritySize * IndexMass;
}

internal sealed record NativeRewardIdentityPlan(CardRarity? RolledRarity, IReadOnlyList<ulong> RawWords,
    ShuffleRational NativeToProposalRatio);

/// <summary>Exact finite preimages of native float rarity and double index conversions.</summary>
internal static class NativeRewardIdentityMath
{
    internal static IReadOnlyList<NativeRewardIdentityArm> Arms(LabelRewardCardSelectionContext context,
        string targetId, int bits = 53)
        => Arms(context.Branches, context.Thresholds, targetId, bits);

    internal static IReadOnlyList<NativeRewardIdentityArm> Arms(IReadOnlyList<LabelRewardCardBranch> branches,
        LabelCardRarityThresholds? thresholds, string targetId, int bits = 53)
    {
        if (bits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(bits));
        ulong domain = 1UL << bits;
        ulong rareEnd = thresholds is { } t ? FloatLowerBound(t.RareUpperExclusive, bits) : 0;
        ulong uncommonEnd = thresholds is { } u ? FloatLowerBound(u.UncommonUpperExclusive, bits) : 0;
        if (uncommonEnd < rareEnd) throw new InvalidOperationException("Invalid native rarity thresholds");
        if (thresholds is null && (branches.Count != 1 || branches[0].RolledRarity is not null))
            throw new InvalidOperationException("Uniform native card selection has invalid branches");
        if (thresholds is not null && !branches.Select(b => b.RolledRarity)
            .SequenceEqual(new CardRarity?[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common }))
            throw new InvalidOperationException("Native rarity branch partition changed");
        return branches.Select(branch =>
        {
            (ulong start, ulong end) = branch.RolledRarity switch
            {
                null => (0UL, domain),
                CardRarity.Rare => (0UL, rareEnd),
                CardRarity.Uncommon => (rareEnd, uncommonEnd),
                CardRarity.Common => (uncommonEnd, domain),
                _ => throw new InvalidOperationException("Unsupported native rolled rarity"),
            };
            var indices = branch.Candidates.Select((card, i) => (card, i))
                .Where(pair => pair.card.GetType().Name == targetId)
                .Select(pair => ConditionalShuffleProposal.Factor(branch.Candidates.Count, pair.i, bits)).ToArray();
            return new NativeRewardIdentityArm(branch.RolledRarity, start, end - start, Array.AsReadOnly(indices));
        }).ToArray();
    }

    internal static NativeRewardIdentityPlan Create(LabelRewardCardSelectionContext context,
        string targetId, Func<ulong> nextWord, int bits = 53)
    {
        IReadOnlyList<NativeRewardIdentityArm> arms = Arms(context, targetId, bits);
        BigInteger total = arms.Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass);
        if (total.IsZero) throw new NativePublicConstraintMismatchException("Public primary card identity has zero native conditional mass");
        BigInteger ticket = UniformBelow(total, nextWord);
        NativeRewardIdentityArm? chosen = null;
        foreach (var arm in arms)
        {
            if (ticket < arm.Mass) { chosen = arm; break; }
            ticket -= arm.Mass;
        }
        var selected = chosen ?? throw new InvalidOperationException("Incomplete native rarity partition");
        ticket = UniformBelow(selected.IndexMass, nextWord);
        ConditionalShuffleFactor? selectedIndex = null;
        foreach (var index in selected.MatchingIndices)
        {
            if (ticket < index.BucketSize) { selectedIndex = index; break; }
            ticket -= index.BucketSize;
        }
        var bucket = selectedIndex ?? throw new InvalidOperationException("Incomplete native duplicate-index partition");
        var words = new List<ulong>();
        if (context.Thresholds is not null) words.Add(SampleWord(selected.RarityStart, selected.RaritySize, bits, nextWord));
        words.Add(SampleWord(bucket.BucketStart, bucket.BucketSize, bits, nextWord));
        BigInteger domain = BigInteger.One << bits;
        // No-rarity branch uses a full dummy domain in arm counting, which cancels here.
        return new(selected.RolledRarity, words.AsReadOnly(), new(total, domain * domain));
    }

    internal static ulong FloatLowerBound(float threshold, int bits = 53)
    {
        if (bits is < 1 or > 53 || float.IsNaN(threshold)) throw new ArgumentOutOfRangeException(nameof(bits));
        ulong low = 0, high = 1UL << bits;
        double increment = Math.ScaleB(1d, -bits);
        while (low < high)
        {
            ulong middle = low + (high - low) / 2;
            if ((float)((double)middle * increment) < threshold) low = middle + 1; else high = middle;
        }
        return low;
    }

    private static ulong SampleWord(ulong start, ulong size, int bits, Func<ulong> nextWord)
    {
        int lowBits = 64 - bits;
        ulong high = start + ConditionalShuffleProposal.UniformBelow(size, nextWord);
        return (high << lowBits) | (nextWord() & ((1UL << lowBits) - 1));
    }

    internal static BigInteger UniformBelow(BigInteger bound, Func<ulong> nextWord)
    {
        if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound));
        int bits = checked((int)(bound - 1).GetBitLength());
        int words = (bits + 63) / 64;
        BigInteger mask = (BigInteger.One << bits) - 1, sample;
        do
        {
            sample = BigInteger.Zero;
            for (int i = 0; i < words; i++) sample = (sample << 64) | nextWord();
            sample &= mask;
        } while (sample >= bound);
        return sample;
    }
}
