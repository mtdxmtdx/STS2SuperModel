using System.Numerics;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicShopMathTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void NativePriceBucketsExhaustEveryFloatRoundedAndSalePreimage(int bits)
    {
        foreach (var (price, min, max) in new[] { (50, .95f, 1.05f), (75, .95f, 1.05f), (172, .95f, 1.05f), (225, .85f, 1.15f) })
        foreach (bool sale in new[] { false, true })
        {
            int domain = 1 << bits;
            var groups = Enumerable.Range(0, domain).GroupBy(h => NativeShopPriceMath.Convert((ulong)h, price, min, max, sale, bits));
            ulong total = 0;
            foreach (var group in groups)
            {
                var bucket = NativeShopPriceMath.Bucket(price, min, max, sale, group.Key, bits);
                Assert.Equal((ulong)group.Min(), bucket.Start); Assert.Equal((ulong)group.Count(), bucket.Size);
                Assert.Equal(new ShuffleRational(group.Count(), domain), NativeShopPriceMath.Mass(price, min, max, sale, group.Key, bits));
                total += bucket.Size;
            }
            Assert.Equal((ulong)domain, total);
            Assert.Equal(0UL, NativeShopPriceMath.Bucket(price, min, max, sale, 9999, bits).Size);
        }
    }

    [Fact]
    public void PriceBucketEndpointsAgreeWithActualNativeFloatAndSaleCalls()
    {
        const ulong domain = 1UL << 53;
        foreach (var (price, min, max) in new[] { (50, .95f, 1.05f), (75, .95f, 1.05f), (172, .95f, 1.05f), (225, .85f, 1.15f) })
        foreach (bool sale in new[] { false, true })
        for (int observed = 0; observed <= (int)(price * max) + 1; observed++)
        {
            var bucket = NativeShopPriceMath.Bucket(price, min, max, sale, observed);
            if (bucket.Size == 0) continue;
            ulong[] endpoints = [bucket.Start, bucket.Start + bucket.Size - 1,
                bucket.Start == 0 ? 0 : bucket.Start - 1, Math.Min(domain - 1, bucket.Start + bucket.Size)];
            foreach (ulong high in endpoints.Distinct())
            {
                using var label = LabelRandomScope.Enter(_ => high << 11);
                var rng = new Rng(100);
                int actual = sale && min == .95f && max == 1.05f
                    ? Sts2Sim.Core.Entities.Merchant.MerchantCardEntry.CalculateDiscountedPrice(price, rng)
                    : (int)Math.Round(price * rng.NextFloat(min, max)) / (sale ? 2 : 1);
                Assert.Equal(high >= bucket.Start && high < bucket.Start + bucket.Size, actual == observed);
                Assert.Equal(1, rng.Counter);
            }
        }
    }

    [Theory]
    [InlineData(3, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(5, 2, 1)]
    [InlineData(3, 2, 2)]
    public void WholeRelicPermutationRetainsAllInterleavingsAndExactFiniteNativeBias(int bits, int draws, int blocked)
    {
        string[] initial = new[] { "a", "b", "c" }.Concat(Enumerable.Range(0, blocked).Select(i => "blocked" + i)).ToArray();
        int count = initial.Length;
        var eligible = new HashSet<string> { "a", "b", "c" };
        string[] targets = new[] { "a", "c" }.Take(draws).ToArray();
        var proposalCounts = new Dictionary<string, int>(); var plans = new Dictionary<string, ConditionalShufflePlan>();
        // Enumerate the unbiased proposal's complete physical Fisher-Yates domain.
        foreach (int[] choices in Cartesian(Enumerable.Range(2, count - 1).Reverse().ToArray()))
        {
            ulong[] tickets = choices.Select((choice, step) => (ulong)(choice + count - step)).ToArray();
            int at = 0; var random = new Rng((ulong)(1 + choices.Aggregate(0, (n, choice) => n * count + choice)));
            var plan = NativeShopRelicPermutationMath.Create(initial, eligible, targets,
                () => at < tickets.Length ? tickets[at++] : random.NextUnsignedLong(), bits);
            string key = string.Join(",", plan.PhysicalPermutation);
            proposalCounts[key] = proposalCounts.GetValueOrDefault(key) + 1; plans[key] = plan;
            Assert.Equal(targets, plan.PhysicalPermutation.Reverse().Select(i => initial[i]).Where(eligible.Contains).Take(draws));
            var physical = Enumerable.Range(0, count).ToArray();
            for (int i = count - 1; i > 0; i--)
            {
                int index = (int)((double)(plan.RawWords[count - 1 - i] >> (64 - bits)) / (1 << bits) * (i + 1));
                (physical[i], physical[index]) = (physical[index], physical[i]);
            }
            Assert.Equal(plan.PhysicalPermutation, physical);
        }
        for (int blockedIndex = 3; blockedIndex < count; blockedIndex++)
            Assert.Equal(Enumerable.Range(0, count), plans.Values.Select(p => p.PhysicalPermutation.ToList().IndexOf(blockedIndex)).Distinct().Order());
        // Multiple ineligible relics keep every relative ordering, as well as
        // every interleaving with the eligible remainder and public targets.
        if (blocked == 2)
            Assert.Equal(2, plans.Values.Select(p => string.Join(",", p.PhysicalPermutation.Where(i => i >= 3))).Distinct().Count());
        int multiplicity = draws == 1 ? 3 : 6;
        Assert.All(proposalCounts.Values, count => Assert.Equal(multiplicity, count));
        Assert.Equal(Enumerable.Range(1, count).Aggregate(1, (n, i) => n * i) / multiplicity, proposalCounts.Count);
        var nativeCounts = new Dictionary<string, int>(); int domain = 1 << bits;
        foreach (int[] hs in Cartesian(Enumerable.Repeat(domain, count - 1).ToArray()))
        {
            int[] physical = Enumerable.Range(0, count).ToArray();
            for (int i = count - 1; i > 0; i--) { int at = (int)((double)hs[count - 1 - i] / domain * (i + 1)); (physical[i], physical[at]) = (physical[at], physical[i]); }
            string key = string.Join(",", physical); nativeCounts[key] = nativeCounts.GetValueOrDefault(key) + 1;
        }
        foreach (var (key, plan) in plans)
        {
            // q(permutation)=1/compatible; conditional word density is uniform
            // in exact native buckets. Thus p/q=compatible*p(permutation).
            Assert.Equal(new ShuffleRational(nativeCounts[key] * plans.Count, BigInteger.Pow(domain, count - 1)), plan.NativeToProposalRatio);
            Assert.True(plan.NativeToProposalRatio.Numerator * plan.Envelope.Denominator <= plan.Envelope.Numerator * plan.NativeToProposalRatio.Denominator);
        }
        if (draws == 1) Assert.True(plans.Values.Select(p => p.NativeToProposalRatio).Distinct().Count() > 1);
        Assert.Single(plans.Values.Select(p => p.Envelope).Distinct());
    }

    private static IEnumerable<int[]> Cartesian(int[] bounds)
    {
        int total = bounds.Aggregate(1, (n, b) => n * b);
        for (int ordinal = 0; ordinal < total; ordinal++)
        {
            int remaining = ordinal; var values = new int[bounds.Length];
            for (int i = bounds.Length - 1; i >= 0; i--)
            { values[i] = remaining % bounds[i]; remaining /= bounds[i]; }
            yield return values;
        }
    }
}
