using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeFloatBranchProposalTests
{
    public static IEnumerable<object[]> WeightCases()
    {
        yield return [new float[] { 1f, 1f }];
        yield return [new float[] { 0f, 0f, 1f, 0f, 2f }];
        yield return [new float[] { 0.1f, 0.1f, 0.1f }];
        yield return [new float[] { 0.1f, 0.7f, 0.2f, 0.3f }];
        yield return [new float[] { 16777216f, 1f, 1f }];
        yield return [new float[] { 0f, float.Epsilon }];
        yield return [new float[] { float.Epsilon, 2f * float.Epsilon, 3f * float.Epsilon }];
        yield return [new float[] { float.MaxValue / 2f, float.MaxValue / 2f }];
        yield return [new float[] { float.Epsilon, float.MaxValue }];
    }

    [Theory]
    [MemberData(nameof(WeightCases))]
    public void BucketsMatchEveryReducedPrecisionWordAndRealRngAtProductionBoundaries(float[] weights)
    {
        float total = weights.Sum();
        // The oracle uses the real native Rng and an injected raw word. Reducing
        // precision just leaves the other significant bits of that word zero.
        for (int bits = 1; bits <= 8; bits++)
        {
            ulong domain = 1UL << bits;
            int[] selected = Enumerable.Range(0, (int)domain)
                .Select(high => NativeSelect((ulong)high << (64 - bits), total, weights)).ToArray();
            ulong end = 0;
            for (int index = 0; index < weights.Length; index++)
            {
                var bucket = NativeFloatBranchProposal.Bucket(total, weights, index, bits);
                Assert.Equal(end, bucket.Start);
                Assert.Equal((ulong)selected.Count(value => value == index), bucket.Size);
                foreach (ulong high in Enumerable.Range(0, (int)domain).Select(value => (ulong)value))
                {
                    Assert.Equal(index == selected[high], high >= bucket.Start && high - bucket.Start < bucket.Size);
                    Assert.Equal(selected[high], NativeFloatBranchProposal.Select(high, total, weights, bits));
                }
                end += bucket.Size;
            }
            Assert.Equal((ulong)selected.Count(value => value == weights.Length), domain - end);
        }

        const ulong productionDomain = 1UL << 53;
        ulong previousEnd = 0;
        for (int index = 0; index < weights.Length; index++)
        {
            var bucket = NativeFloatBranchProposal.Bucket(total, weights, index);
            Assert.Equal(previousEnd, bucket.Start);
            if (bucket.Size != 0)
            {
                Assert.Equal(index, NativeSelect(bucket.Start << 11, total, weights));
                Assert.Equal(index, NativeSelect(((bucket.Start + bucket.Size - 1) << 11) | 2047, total, weights));
            }
            if (bucket.Start != 0)
                Assert.True(NativeSelect(((bucket.Start - 1) << 11) | 2047, total, weights) < index);
            previousEnd += bucket.Size;
            if (previousEnd < productionDomain)
                Assert.True(NativeSelect(previousEnd << 11, total, weights) > index);
        }
        if (previousEnd < productionDomain)
        {
            Assert.Equal(weights.Length, NativeSelect(previousEnd << 11, total, weights));
            Assert.Equal(weights.Length, NativeSelect(ulong.MaxValue, total, weights));
        }
    }

    [Fact]
    public void FloatRoundingAndInclusiveZeroEndpointHaveTheirExactNativeMass()
    {
        const ulong domain = 1UL << 53;
        // Float rounding includes the midpoint above 1f, as well as 1f itself.
        var first = NativeFloatBranchProposal.Bucket(2f, [1f, 1f], 0);
        Assert.Equal(new NativeFloatBranchBucket(0, (1UL << 52) + (1UL << 28) + 1), first);
        Assert.NotEqual(new ShuffleRational(1, 2), new ShuffleRational(first.Size, domain));

        Assert.Equal(new NativeFloatBranchBucket(0, 1), NativeFloatBranchProposal.Bucket(1f, [0f, 1f], 0));
        Assert.Equal(new NativeFloatBranchBucket(0, (1UL << 52) + 1),
            NativeFloatBranchProposal.Bucket(float.Epsilon, [0f, float.Epsilon], 0));
        Assert.Equal(0UL, NativeFloatBranchProposal.Bucket(1f, [0f, 0f, 1f], 1).Size);

        float[] fractions = [0.1f, 0.1f, 0.1f];
        var last = NativeFloatBranchProposal.Bucket(fractions.Sum(), fractions, 2);
        Assert.True(last.Start + last.Size < domain);
        Assert.Equal(fractions.Length, NativeSelect(ulong.MaxValue, fractions.Sum(), fractions));

        float[] ordered = [16777216f, 1f, 1f];
        float summed = ordered.Sum(), sequential = ordered.Aggregate(0f, (sum, weight) => sum + weight);
        Assert.Equal(16777218f, summed);
        Assert.Equal(16777216f, sequential);
        Assert.NotEqual(NativeFloatBranchProposal.Bucket(summed, ordered, 0),
            NativeFloatBranchProposal.Bucket(sequential, ordered, 0));
    }

    [Theory]
    [InlineData(0, 0UL, 1UL)]
    [InlineData(2, 1UL, 4UL)]
    public void ProposalEnumeratesEveryRawPreimageIncludingAllDiscardedBits(int index, ulong start, ulong size)
    {
        // A large total leaves tiny, fully enumerable buckets at native precision.
        float[] weights = [0f, 0f, 1f, 2251799813685248f];
        float total = weights.Sum();
        var words = new HashSet<ulong>();
        for (ulong offset = 0; offset < size; offset++)
        for (ulong low = 0; low < 2048; low++)
        {
            var samples = new Queue<ulong>();
            if (size > 1) samples.Enqueue(size + offset);
            samples.Enqueue(low);
            var plan = NativeFloatBranchProposal.Create(total, weights, index, size, () => samples.Dequeue());
            Assert.NotNull(plan);
            Assert.Empty(samples);
            Assert.Equal(new NativeFloatBranchBucket(start, size), plan.Bucket);
            Assert.Equal(((start + offset) << 11) | low, plan.RawWord);
            Assert.Equal(index, NativeSelect(plan.RawWord, total, weights));
            Assert.True(words.Add(plan.RawWord));
            Assert.Equal(new ShuffleRational(size, 1UL << 53), plan.NativeToProposalRatio);
            Assert.Equal(plan.NativeToProposalRatio, plan.Envelope);
            Assert.True(plan.AcceptCorrection(Never));
        }
        Assert.Equal(size * 2048, (ulong)words.Count);
    }

    [Theory]
    [InlineData(1f, 1f, 1)]
    [InlineData(1f, 2f, 1)]
    [InlineData(0f, 1f, 0)]
    public void RootConstantEnvelopeCorrectsAllNativeBucketBias(float first, float second, int index)
    {
        const int bits = 3;
        const ulong domain = 1UL << bits, envelope = 5;
        float[] weights = [first, second];
        float total = weights.Sum();
        var bucket = NativeFloatBranchProposal.Bucket(total, weights, index, bits);
        for (ulong offset = 0; offset < bucket.Size; offset++)
        {
            var samples = new Queue<ulong>();
            if (bucket.Size > 1) samples.Enqueue(bucket.Size + offset);
            samples.Enqueue(ulong.MaxValue);
            var plan = NativeFloatBranchProposal.Create(total, weights, index, envelope, () => samples.Dequeue(), bits)!;
            Assert.Empty(samples);
            Assert.Equal(bucket.Start + offset, plan.RawWord >> (64 - bits));
            Assert.Equal((1UL << (64 - bits)) - 1, plan.RawWord & ((1UL << (64 - bits)) - 1));
            Assert.Equal(new ShuffleRational(bucket.Size, domain), plan.NativeToProposalRatio);
            Assert.Equal(new ShuffleRational(envelope, domain), plan.Envelope);
            ulong accepted = 0;
            for (ulong residue = 0; residue < envelope; residue++)
            {
                int calls = 0;
                if (plan.AcceptCorrection(() => { calls++; return envelope + residue; })) accepted++;
                Assert.Equal(bucket.Size == envelope ? 0 : 1, calls);
            }
            Assert.Equal(bucket.Size, accepted);
            // Conditional 1/Size times acceptance Size/Envelope is constant,
            // even when a different hidden context produces a different bucket.
            Assert.Equal(new ShuffleRational(1, envelope), new ShuffleRational(accepted, bucket.Size * envelope));
        }
    }

    [Fact]
    public void InputsAreFrozenAndImpossibleOrInvalidPlansDoNotDraw()
    {
        float[] weights = [1f, 1f];
        var expected = NativeFloatBranchProposal.Bucket(2f, weights, 1, 3);
        var samples = new Queue<ulong>([expected.Size, ulong.MaxValue]);
        var plan = NativeFloatBranchProposal.Create(2f, weights, 1, 8, () =>
        {
            Array.Fill(weights, float.NaN);
            return samples.Dequeue();
        }, 3)!;
        Assert.Empty(samples);
        Assert.Equal(expected, plan.Bucket);
        Assert.Equal(1, NativeSelect(plan.RawWord, 2f, [1f, 1f]));

        Assert.Null(NativeFloatBranchProposal.Create(1f, [0f, 0f, 1f], 1, 8, Never, 3));
        Assert.Null(NativeFloatBranchProposal.Create(3f, [1f, 1f, 1f], 2, 2, Never, 1));
        Assert.Throws<InvalidOperationException>(() => NativeFloatBranchProposal.Create(2f, [1f, 1f], 0, 4, Never, 3));
        Assert.Throws<InvalidOperationException>(() => NativeFloatBranchProposal.Create(2f, [1f, 1f], 0, 9, Never, 3));
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeFloatBranchProposal.Create(invalid, [1f], 0, 8, Never, 3));
        foreach (float invalid in new[] { -1f, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => NativeFloatBranchProposal.Create(1f, [invalid], 0, 8, Never, 3));
        Assert.Throws<ArgumentException>(() => NativeFloatBranchProposal.Create(1f, [], 0, 8, Never, 3));
        Assert.Throws<ArgumentNullException>(() => NativeFloatBranchProposal.Create(1f, null!, 0, 8, Never, 3));
        Assert.Throws<ArgumentNullException>(() => NativeFloatBranchProposal.Create(1f, [1f], 0, 8, null!, 3));
        foreach (int index in new[] { -1, 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeFloatBranchProposal.Create(1f, [1f], index, 8, Never, 3));
        foreach (int bits in new[] { 0, 54 })
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeFloatBranchProposal.Create(1f, [1f], 0, 8, Never, bits));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeFloatBranchProposal.Select(8, 1f, [1f], 3));
        Assert.Throws<ArgumentNullException>(() => plan.AcceptCorrection(null!));
    }

    private static int NativeSelect(ulong raw, float totalWeight, IReadOnlyList<float> weights)
    {
        using var scope = LabelRandomScope.Enter(_ => raw);
        float residual = new Rng().NextFloat(totalWeight);
        for (int index = 0; index < weights.Count; index++)
        {
            residual -= weights[index];
            if (residual <= 0f) return index;
        }
        return weights.Count;
    }

    private static ulong Never() => throw new InvalidOperationException("No random draw should be needed");
}
