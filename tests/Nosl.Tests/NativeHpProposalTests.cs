using System.Numerics;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class NativeHpProposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderDependentLikelihoodMatchesExhaustiveNativeWordsWithRootConstantEnvelope(bool reverse)
    {
        const int bits = 4, domain = 1 << bits;
        var ranges = new[] { (Min: 0, Max: 2, Target: 0), (Min: 1, Max: 3, Target: 2) };
        int[] order = reverse ? [1, 0] : [0, 1];
        var envelopeBuckets = ranges.Select((range, i) => NativeHpProposal.RootEnvelopeBucket(
            range.Min, range.Max, ranges.Where((_, j) => j != i).Select(other => other.Target), bits)).ToArray();
        int matched = 0;
        for (int first = 0; first < domain; first++)
        for (int second = 0; second < domain; second++)
        {
            var hp = new List<int>();
            bool compatible = true;
            foreach (var (id, high) in order.Zip(new[] { first, second }))
            {
                var range = ranges[id];
                int[] values = Enumerable.Range(range.Min, range.Max - range.Min + 1).Except(hp).ToArray();
                if (values.Length == 0) values = Enumerable.Range(range.Min, range.Max - range.Min + 1).ToArray();
                int rolled = values[(int)((double)high / domain * values.Length)];
                hp.Add(rolled);
                compatible &= rolled == range.Target;
            }
            if (compatible) matched++;
        }
        var ratio = new ShuffleRational(1, 1);
        var envelope = new ShuffleRational(1, 1);
        var used = new List<int>();
        foreach (int id in order)
        {
            var range = ranges[id];
            var plan = NativeHpProposal.Create(range.Min, range.Max, range.Target,
                used.Where(hp => hp >= range.Min && hp <= range.Max).Distinct().Order().ToArray(),
                envelopeBuckets[id], () => ulong.MaxValue, bits)!;
            ratio = ratio.Multiply(plan.BucketSize, domain);
            envelope = envelope.Multiply(plan.RootMaxBucketSize, domain);
            int accepted = 0;
            // Exercise the exact Bernoulli over a complete uniform residue set.
            for (ulong residue = 0; residue < plan.RootMaxBucketSize; residue++)
            {
                ulong sample = plan.RootMaxBucketSize + residue;
                if (plan.AcceptCorrection(() => sample)) accepted++;
            }
            Assert.Equal(plan.BucketSize, (ulong)accepted);
            used.Add(range.Target);
        }
        Assert.Equal(new ShuffleRational(matched, domain * domain), ratio);
        Assert.Equal(new ShuffleRational(8 * 6, domain * domain), envelope);
        Assert.Equal(reverse ? 40 : 30, matched);
    }

    [Fact]
    public void ExhaustionRetainsFullRangeAndUnsupportedUniqueHpIsRejected()
    {
        const int bits = 4;
        ulong maximum = NativeHpProposal.RootEnvelopeBucket(10, 11, [10, 11], bits);
        Assert.Null(NativeHpProposal.Create(10, 11, 10, [10], maximum, () => ulong.MaxValue, bits));
        var fallback = NativeHpProposal.Create(10, 11, 10, [10, 11], maximum, () => ulong.MaxValue, bits)!;
        Assert.Equal(2, fallback.Factor.Bound);
        Assert.Equal(0, fallback.Factor.Index);
        Assert.Equal(new ShuffleRational(1, 2), fallback.NativeToProposalRatio);
        Assert.Equal(new ShuffleRational(1, 1), fallback.Envelope);
        Assert.Equal(10, 10 + (int)((double)(fallback.RawWord >> 60) / 16 * 2));
        Assert.Throws<InvalidOperationException>(() => NativeHpProposal.Create(10, 11, 10,
            [10, 11], 1, () => ulong.MaxValue, bits));
    }
}
