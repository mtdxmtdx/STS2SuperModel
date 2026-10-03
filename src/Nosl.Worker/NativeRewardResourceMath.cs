using System.Numerics;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

internal sealed record NativePresencePlan(bool Present, NativeResourcePlan Plan);
internal sealed record NativeResourcePlan(IReadOnlyList<ulong> RawWords, ShuffleRational NativeToProposalRatio);
internal sealed record NativePotionArm(ulong Start, ulong Size, IReadOnlyList<ConditionalShuffleFactor> Indices, bool Empty)
{
    internal BigInteger Mass(int bits) => (BigInteger)Size * (Empty ? BigInteger.One << bits
        : Indices.Aggregate(BigInteger.Zero, (sum, index) => sum + index.BucketSize));
}

/// <summary>Exact native float <= rarity, float &lt; presence, and double integer preimages.</summary>
internal static class NativeRewardResourceMath
{
    internal static ShuffleRational DisplayMass(bool forced, float threshold, bool displayed,
        ShuffleRational identity, int bits = 53)
    {
        var domain = BigInteger.One << bits;
        var success = forced ? domain : NativeRewardIdentityMath.FloatLowerBound(threshold, bits);
        return new(success * identity.Numerator + (displayed ? BigInteger.Zero : (domain - success) * identity.Denominator),
            domain * identity.Denominator);
    }

    internal static NativePresencePlan Presence(bool forced, float threshold, bool displayed,
        ShuffleRational identity, Func<ulong> nextWord, int bits = 53)
    {
        ulong domain = 1UL << bits;
        ulong successSize = forced ? domain : NativeRewardIdentityMath.FloatLowerBound(threshold, bits);
        var p = new ShuffleRational(successSize, domain);
        BigInteger trueWeight = p.Numerator * identity.Numerator;
        BigInteger falseWeight = displayed ? BigInteger.Zero : (p.Denominator - p.Numerator) * identity.Denominator;
        BigInteger total = trueWeight + falseWeight;
        if (total.IsZero) throw new NativePublicConstraintMismatchException("Public potion presence has zero native conditional mass");
        bool present = NativeNeowProposal.ExactRationalBernoulli(new(trueWeight, total), nextWord);
        var ratio = present ? new ShuffleRational(total, p.Denominator * identity.Numerator)
            : new ShuffleRational(total, p.Denominator * identity.Denominator);
        IReadOnlyList<ulong> words = forced ? [] : [SampleWord(present ? 0UL : successSize,
            present ? successSize : domain - successSize, bits, nextWord)];
        return new(present, new(words, ratio));
    }

    internal static ulong FloatUpperBound(float threshold, int bits = 53)
    {
        if (bits is < 1 or > 53 || float.IsNaN(threshold)) throw new ArgumentOutOfRangeException(nameof(bits));
        ulong low = 0, high = 1UL << bits;
        while (low < high)
        {
            ulong middle = low + (high - low) / 2;
            if ((float)((double)middle * Math.ScaleB(1d, -bits)) <= threshold) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal static IReadOnlyList<NativePotionArm> PotionArms(IReadOnlyList<PotionModel> pool, string? target, int bits = 53)
    {
        ulong rare = FloatUpperBound(0.1f, bits), uncommon = FloatUpperBound(0.35f, bits), domain = 1UL << bits;
        return new[] { (PotionRarity.Rare, 0UL, rare), (PotionRarity.Uncommon, rare, uncommon),
            (PotionRarity.Common, uncommon, domain) }.Select(arm =>
        {
            var candidates = pool.Where(potion => potion.Rarity == arm.Item1).ToArray();
            var indices = candidates.Select((potion, index) => (potion, index))
                .Where(pair => pair.potion.GetType().Name == target)
                .Select(pair => ConditionalShuffleProposal.Factor(candidates.Length, pair.index, bits)).ToArray();
            return new NativePotionArm(arm.Item2, arm.Item3 - arm.Item2, indices, target is null && candidates.Length == 0);
        }).ToArray();
    }

    internal static ShuffleRational PotionMass(IReadOnlyList<PotionModel> pool, string? target, int bits = 53) =>
        new(PotionArms(pool, target, bits).Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass(bits)), BigInteger.One << (2 * bits));

    internal static NativeResourcePlan Potion(IReadOnlyList<PotionModel> pool, string? target, Func<ulong> nextWord, int bits = 53)
    {
        var arms = PotionArms(pool, target, bits);
        BigInteger total = arms.Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass(bits));
        if (total.IsZero) throw new NativePublicConstraintMismatchException("Public potion identity has no native pool support");
        BigInteger ticket = NativeRewardIdentityMath.UniformBelow(total, nextWord);
        var selected = arms.First(arm => { if (ticket < arm.Mass(bits)) return true; ticket -= arm.Mass(bits); return false; });
        var words = new List<ulong> { SampleWord(selected.Start, selected.Size, bits, nextWord) };
        if (!selected.Empty)
        {
            ticket = NativeRewardIdentityMath.UniformBelow(selected.Indices.Aggregate(BigInteger.Zero, (s, i) => s + i.BucketSize), nextWord);
            var index = selected.Indices.First(i => { if (ticket < i.BucketSize) return true; ticket -= i.BucketSize; return false; });
            words.Add(SampleWord(index.BucketStart, index.BucketSize, bits, nextWord));
        }
        return new(words, new(total, BigInteger.One << (2 * bits)));
    }

    internal static (ulong Start, ulong Size) GoldBucket(int min, int max, int? displayed, int bits = 53)
    {
        if (bits is < 1 or > 53 || max < min || max == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(max));
        long width = (long)max - min + 1;
        long first = displayed is { } amount ? (long)amount - min : 0;
        long end = displayed is { } value ? (long)value - min + 1 : Math.Min(width, 1L - min);
        if (first < 0 || end > width || end <= first) return (0, 0);
        ulong Boundary(long index)
        {
            ulong low = 0, high = 1UL << bits;
            while (low < high)
            {
                ulong middle = low + (high - low) / 2;
                if ((long)((double)middle * Math.ScaleB(1d, -bits) * width) < index) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        ulong start = Boundary(first);
        return (start, Boundary(end) - start);
    }

    internal static NativeResourcePlan Gold(int min, int max, bool omitted, int? displayed, Func<ulong> nextWord, int bits = 53)
    {
        if (omitted)
        {
            if (displayed is not null) throw new NativePublicConstraintMismatchException("Public gold is incompatible with an omitted native reward");
            return new([], new(1, 1));
        }
        var bucket = GoldBucket(min, max, displayed, bits);
        if (bucket.Size == 0) throw new NativePublicConstraintMismatchException("Public gold has no native range support");
        return new([SampleWord(bucket.Start, bucket.Size, bits, nextWord)], new(bucket.Size, BigInteger.One << bits));
    }

    internal static ulong SampleWord(ulong start, ulong size, int bits, Func<ulong> nextWord)
    {
        int lowBits = 64 - bits;
        return ((start + ConditionalShuffleProposal.UniformBelow(size, nextWord)) << lowBits)
            | (nextWord() & ((1UL << lowBits) - 1));
    }
}
