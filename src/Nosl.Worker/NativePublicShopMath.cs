using System.Numerics;

namespace Nosl.Worker;

/// <summary>Exact native float range, banker's rounding and integer sale division preimages.</summary>
internal static class NativeShopPriceMath
{
    internal static int Convert(ulong high, int basePrice, float min, float max, bool sale, int bits = 53)
    {
        float factor = (float)((double)high * Math.ScaleB(1d, -bits) * (double)(max - min) + (double)min);
        int rounded = (int)Math.Round(basePrice * factor);
        return sale ? rounded / 2 : rounded;
    }
    internal static (ulong Start, ulong Size) Bucket(int basePrice, float min, float max, bool sale, int price, int bits = 53)
    {
        if (bits is < 1 or > 53 || basePrice <= 0 || min < 0 || max < min || price < 0)
            throw new ArgumentOutOfRangeException(nameof(price));
        ulong Bound(int target)
        {
            ulong lo = 0, hi = 1UL << bits;
            while (lo < hi)
            {
                ulong mid = lo + (hi - lo) / 2;
                if (Convert(mid, basePrice, min, max, sale, bits) < target) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
        ulong start = Bound(price); return (start, Bound(checked(price + 1)) - start);
    }
    internal static ShuffleRational Mass(int basePrice, float min, float max, bool sale, int price, int bits = 53) =>
        new(Bucket(basePrice, min, max, sale, price, bits).Size, BigInteger.One << bits);
    internal static ulong Sample(int basePrice, float min, float max, bool sale, int price, Func<ulong> next, int bits = 53)
    {
        var bucket = Bucket(basePrice, min, max, sale, price, bits);
        if (bucket.Size == 0) throw new NativePublicConstraintMismatchException("Public merchant price has zero native mass");
        return NativeRewardResourceMath.SampleWord(bucket.Start, bucket.Size, bits, next);
    }
}

/// <summary>
/// Uniform whole physical permutation conditioned on the ordered filtered back draws.
/// Ineligible positions and every unpicked relic remain random. Native Fisher-Yates
/// bucket biases are retained by the same bounded p/q correction as combat shuffles.
/// </summary>
internal static class NativeShopRelicPermutationMath
{
    internal static ConditionalShufflePlan Create(IReadOnlyList<string> initial, IReadOnlySet<string> eligible,
        IReadOnlyList<string> backDraws, Func<ulong> next, int bits = 53)
    {
        if (initial.Count != initial.Distinct().Count() || eligible.Any(id => !initial.Contains(id))
            || backDraws.Count != backDraws.Distinct().Count() || backDraws.Any(id => !eligible.Contains(id)))
            throw new ArgumentException("Unique complete relic bucket and supported ordered eligible back draws required");
        int[] permutation = Enumerable.Range(0, initial.Count).ToArray();
        for (int i = permutation.Length - 1; i > 0; i--)
        {
            int at = (int)ConditionalShuffleProposal.UniformBelow((ulong)i + 1, next);
            (permutation[i], permutation[at]) = (permutation[at], permutation[i]);
        }
        // The sampled mask and order of ineligible entries are already uniform.
        // Delete target relics from the eligible order and append their reversed
        // draw order; every conditioned permutation has exactly (m)_k preimages.
        int[] eligibleOrder = permutation.Where(i => eligible.Contains(initial[i]) && !backDraws.Contains(initial[i]))
            .Concat(backDraws.Reverse().Select(id => Enumerable.Range(0, initial.Count).Single(i => initial[i] == id))).ToArray();
        int cursor = 0;
        for (int i = 0; i < permutation.Length; i++)
            if (eligible.Contains(initial[permutation[i]])) permutation[i] = eligibleOrder[cursor++];
        var probability = new ShuffleRational(1, 1);
        for (int i = 0; i < backDraws.Count; i++) probability = probability.Multiply(1, eligible.Count - i);
        return Encode(initial.Count, permutation, probability, next, bits);
    }
    private static ConditionalShufflePlan Encode(int count, int[] permutation, ShuffleRational probability,
        Func<ulong> next, int bits)
    {
        int[] working = Enumerable.Range(0, count).ToArray();
        var factors = new ConditionalShuffleFactor[Math.Max(0, count - 1)];
        var words = new ulong[factors.Length];
        var ratio = probability; var envelope = probability;
        for (int i = count - 1, step = 0; i > 0; i--, step++)
        {
            int at = Array.IndexOf(working, permutation[i], 0, i + 1);
            var f = ConditionalShuffleProposal.Factor(i + 1, at, bits);
            if (f.BucketSize == 0) throw new NativePublicConstraintMismatchException("Relic permutation has zero native precision support");
            factors[step] = f;
            words[step] = NativeRewardResourceMath.SampleWord(f.BucketStart, f.BucketSize, bits, next);
            ratio = ratio.Multiply((BigInteger)f.Bound * f.BucketSize, BigInteger.One << bits);
            envelope = envelope.Multiply((BigInteger)f.Bound * f.MaxBucketSize, BigInteger.One << bits);
            (working[i], working[at]) = (working[at], working[i]);
        }
        return new(permutation, words, factors, probability, ratio, envelope);
    }
}
