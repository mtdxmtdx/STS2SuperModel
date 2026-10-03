using System.Numerics;
using Nosl.Worker;

namespace Nosl.Tests;

public class ConditionalShuffleProposalTests
{
    [Theory]
    [InlineData(false, 72)]
    [InlineData(true, 24)]
    public void TwoWitnessedCyclesPreserveLatentPhysicalVariantOddsAndAllNativeWordMass(bool refineUpgrade, int outcomesCount)
    {
        const int bits = 2, domain = 4;
        // Two unupgraded A copies may differ in omitted metadata, so retain their
        // physical identities. An upgraded A is a separate public conditioning key.
        // The first drawn A stays in hand; later physical membership remains latent.
        string Key(int upgrade) => refineUpgrade ? new NativePublicDrawKey("A", upgrade).Signature : "A";
        string[] ids = [Key(0), Key(0), Key(1), "B"];
        string[] prefix = [Key(0)], laterPrefix = [Key(1)];
        var native = new Dictionary<string, int>();
        for (int a = 0; a < 64; a++)
        {
            var first = NativeOrder(4, a);
            if (ids[first[0]] != prefix[0]) continue;
            int[] pool = first.Skip(1).OrderBy(index => ids[index]).ThenBy(index => index).ToArray();
            for (int b = 0; b < 16; b++)
            {
                int[] second = NativeOrder(3, b).Select(index => pool[index]).ToArray();
                if (ids[second[0]] != laterPrefix[0]) continue;
                string key = string.Join(",", first) + ":" + string.Join(",", second);
                native[key] = native.GetValueOrDefault(key) + 1;
            }
        }
        ShuffleRational? envelope = null;
        int checkedWorlds = 0;
        foreach (int[] first in Permutations([0, 1, 2, 3]).Where(order => ids[order[0]] == prefix[0]))
        {
            var firstPlan = ConditionalShuffleProposal.Create(ids, prefix, RandomForPermutation(first, ids, prefix), bits)!;
            int[] pool = first.Skip(1).OrderBy(index => ids[index]).ThenBy(index => index).ToArray();
            string[] poolIds = pool.Select(index => ids[index]).ToArray();
            foreach (int[] second in Permutations([0, 1, 2]).Where(order => poolIds[order[0]] == laterPrefix[0]))
            {
                var secondPlan = ConditionalShuffleProposal.Create(poolIds, laterPrefix, RandomForPermutation(second, poolIds, laterPrefix), bits)!;
                string key = string.Join(",", first) + ":" + string.Join(",", second.Select(index => pool[index]));
                var ratio = firstPlan.NativeToProposalRatio.Multiply(secondPlan.NativeToProposalRatio.Numerator, secondPlan.NativeToProposalRatio.Denominator);
                var bound = firstPlan.Envelope.Multiply(secondPlan.Envelope.Numerator, secondPlan.Envelope.Denominator);
                envelope ??= bound; Assert.Equal(envelope.Value, bound);
                // Refinement narrows 72 coarse physical pairs to 24. Both laws retain
                // every compatible native word and the latent same-key physical copy.
                Assert.Equal(new ShuffleRational(native[key] * outcomesCount, 1024), ratio);
                var active = firstPlan.Factors.Concat(secondPlan.Factors).Where(factor => factor.BucketSize != factor.MaxBucketSize).ToArray();
                int outcomes = active.Aggregate(1, (product, factor) => product * (int)factor.MaxBucketSize), accepted = 0;
                for (int outcome = 0; outcome < outcomes; outcome++)
                {
                    int remainder = outcome; var words = new Queue<ulong>();
                    foreach (var factor in active)
                    { words.Enqueue(RawForResidue((ulong)remainder % factor.MaxBucketSize, factor.MaxBucketSize)); remainder /= (int)factor.MaxBucketSize; }
                    if (firstPlan.AcceptCorrection(words.Dequeue) && secondPlan.AcceptCorrection(words.Dequeue)) accepted++;
                }
                Assert.Equal(new ShuffleRational(accepted, outcomes),
                    new ShuffleRational(ratio.Numerator * bound.Denominator, ratio.Denominator * bound.Numerator));
                checkedWorlds++;
            }
        }
        Assert.Equal(native.Count, checkedWorlds);
        Assert.Equal(outcomesCount, checkedWorlds);

        static int[] NativeOrder(int count, int encoded)
        {
            int[] order = Enumerable.Range(0, count).ToArray();
            for (int i = count - 1; i > 0; i--)
            {
                int high = encoded % domain; encoded /= domain;
                int index = (int)((double)high / domain * (i + 1));
                (order[i], order[index]) = (order[index], order[i]);
            }
            return order;
        }
    }

    [Theory]
    [InlineData(2, 96, 2, 15)]
    [InlineData(4, 8, 1, 90)]
    [InlineData(6, 4, 1, 180)]
    public void CorrectedDuplicateCardProposalMatchesExhaustiveNativeWordLaw(int prefixLength,
        int supportedPermutations, int probabilityNumerator, int probabilityDenominator)
    {
        const int bits = 3, domain = 1 << bits;
        string[] signatures = ["A", "A", "B", "C", "C", "D"];
        // Equal IDs may be different upgrade/enchantment copies. Keep physical
        // identities distinct even when later-turn evidence fixes the full ID cycle.
        string[] prefix = new[] { "A", "C", "A", "D", "C", "B" }.Take(prefixLength).ToArray();
        var nativeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Independent enumeration of every native primitive-word combination:
        // 8^5 worlds, not uniform permutations (8 is indivisible by 3, 5 and 6).
        for (int encoded = 0; encoded < 1 << (bits * (signatures.Length - 1)); encoded++)
        {
            int remaining = encoded;
            int[] order = Enumerable.Range(0, signatures.Length).ToArray();
            for (int i = order.Length - 1; i > 0; i--)
            {
                int high = remaining % domain; remaining /= domain;
                int index = (int)((double)high * (1d / domain) * (i + 1));
                (order[i], order[index]) = (order[index], order[i]);
            }
            if (!Matches(order, signatures, prefix)) continue;
            string key = string.Join(",", order);
            nativeCounts[key] = nativeCounts.GetValueOrDefault(key) + 1;
        }
        Assert.Equal(supportedPermutations, nativeCounts.Count);
        // A uniform accepted-permutation implementation would pass support and
        // packet matching, but fail this distribution check.
        Assert.True(nativeCounts.Values.Distinct().Count() > 1);

        int checkedPermutations = 0;
        foreach (int[] order in Permutations(Enumerable.Range(0, signatures.Length).ToArray()))
        {
            if (!Matches(order, signatures, prefix)) continue;
            var random = RandomForPermutation(order, signatures, prefix);
            var plan = ConditionalShuffleProposal.Create(signatures, prefix, random, bits);
            Assert.NotNull(plan);
            Assert.Equal(order, plan.PhysicalPermutation);
            Assert.Equal(order, ApplyRawWords(plan.RawWords, signatures.Length, bits));
            Assert.Equal(new ShuffleRational(probabilityNumerator, probabilityDenominator), plan.PrefixProbability);

            // Enumerate all correction-Bernoulli outcomes too. This exercises
            // the actual correction method, including its early rejection.
            var active = plan.Factors.Where(f => f.BucketSize != f.MaxBucketSize).ToArray();
            int correctionSpace = active.Aggregate(1, (product, f) => checked(product * (int)f.MaxBucketSize));
            int accepted = 0;
            for (int encoded = 0; encoded < correctionSpace; encoded++)
            {
                int remaining = encoded;
                var correctionWords = new Queue<ulong>();
                foreach (var factor in active)
                {
                    ulong residue = (ulong)remaining % factor.MaxBucketSize;
                    remaining /= (int)factor.MaxBucketSize;
                    correctionWords.Enqueue(RawForResidue(residue, factor.MaxBucketSize));
                }
                if (plan.AcceptCorrection(() => correctionWords.Dequeue())) accepted++;
            }
            BigInteger envelopeProduct = plan.Factors.Aggregate(BigInteger.One,
                (product, factor) => product * factor.MaxBucketSize);
            int nativeMass = nativeCounts[string.Join(",", order)];
            Assert.Equal((BigInteger)nativeMass * correctionSpace, accepted * envelopeProduct);
            Assert.Equal(new ShuffleRational(accepted, correctionSpace),
                new ShuffleRational(plan.NativeToProposalRatio.Numerator * plan.Envelope.Denominator,
                    plan.NativeToProposalRatio.Denominator * plan.Envelope.Numerator));
            checkedPermutations++;
        }
        Assert.Equal(nativeCounts.Count, checkedPermutations);
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(53, 3)]
    [InlineData(53, 12)]
    public void BucketEndpointsUseNativeFloatingPointRounding(int bits, int bound)
    {
        ulong domain = 1UL << bits, total = 0, maximum = 0;
        for (int index = 0; index < bound; index++)
        {
            var factor = ConditionalShuffleProposal.Factor(bound, index, bits);
            Assert.Equal(total, factor.BucketStart);
            Assert.True(factor.BucketSize > 0);
            Assert.Equal(index, Map(factor.BucketStart, bits, bound));
            Assert.Equal(index, Map(factor.BucketStart + factor.BucketSize - 1, bits, bound));
            if (factor.BucketStart > 0) Assert.True(Map(factor.BucketStart - 1, bits, bound) < index);
            total += factor.BucketSize; maximum = Math.Max(maximum, factor.BucketSize);
        }
        Assert.Equal(domain, total);
        Assert.Equal(maximum, ConditionalShuffleProposal.Factor(bound, 0, bits).MaxBucketSize);
        if (bits == 53 && bound == 3)
        {
            // ceil(2 * 2^53 / 3) is 6004799503160662, but native double
            // multiplication already rounds the preceding sample to exactly 2.
            Assert.Equal(6004799503160661UL, ConditionalShuffleProposal.Factor(3, 2).BucketStart);
        }
        if (bits == 3)
            for (ulong high = 0; high < domain; high++)
            {
                var factor = ConditionalShuffleProposal.Factor(bound, Map(high, bits, bound), bits);
                Assert.InRange(high, factor.BucketStart, factor.BucketStart + factor.BucketSize - 1);
            }
    }

    [Fact]
    public void ProductionWordsRetainLowBitsAndReplayThePhysicalPermutation()
    {
        string[] signatures = ["A", "A", "B", "C", "D", "E"];
        string[] prefix = ["A", "D", "A"];
        int[] target = [1, 4, 0, 5, 2, 3];
        var plan = ConditionalShuffleProposal.Create(signatures, prefix,
            RandomForPermutation(target, signatures, prefix));
        Assert.NotNull(plan);
        Assert.Equal(new ShuffleRational(1, 60), plan.PrefixProbability);
        Assert.Equal(target, ApplyRawWords(plan.RawWords, signatures.Length, 53));
        Assert.All(plan.RawWords, word => Assert.Equal(2047UL, word & 2047UL));
    }

    [Fact]
    public void EmptyIncompatibleAndMalformedInputsDoNotInventAProposal()
    {
        ulong Never() => throw new InvalidOperationException("No randomness should be needed");
        var empty = ConditionalShuffleProposal.Create([], [], Never);
        Assert.NotNull(empty); Assert.Empty(empty.RawWords); Assert.Empty(empty.PhysicalPermutation);
        Assert.Equal(new ShuffleRational(1, 1), empty.Envelope);
        Assert.True(empty.AcceptCorrection(Never));
        var singleton = ConditionalShuffleProposal.Create(["A"], ["A"], Never);
        Assert.NotNull(singleton); Assert.Equal([0], singleton.PhysicalPermutation);
        Assert.Null(ConditionalShuffleProposal.Create(["A"], ["A", "A"], Never));
        Assert.Null(ConditionalShuffleProposal.Create(["A", "B"], ["A", "A"], Never));
        Assert.Null(ConditionalShuffleProposal.Create(["A"], ["B"], Never));
        Assert.Throws<ArgumentException>(() => ConditionalShuffleProposal.Create([null!], [], Never));
        Assert.Throws<ArgumentException>(() => ConditionalShuffleProposal.Create(["A"], [null!], Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionalShuffleProposal.Create([], [], Never, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionalShuffleProposal.Create([], [], Never, 54));
        Assert.Throws<ArgumentNullException>(() => ConditionalShuffleProposal.Create(null!, [], Never));
        Assert.Throws<ArgumentNullException>(() => ConditionalShuffleProposal.Create([], null!, Never));
        Assert.Throws<ArgumentNullException>(() => ConditionalShuffleProposal.Create([], [], null!));

        // With two primitive values, a bound-three index can have zero mass.
        // The uniform proposal may select it; it must then reject explicitly.
        int zero = Enumerable.Range(0, 3).Single(i => ConditionalShuffleProposal.Factor(3, i, 1).BucketSize == 0);
        int[] zeroOrder = [0, 1, 2];
        (zeroOrder[2], zeroOrder[zero]) = (zeroOrder[zero], zeroOrder[2]);
        string[] ids = ["A", "B", "C"];
        Assert.Null(ConditionalShuffleProposal.Create(ids, [], RandomForPermutation(zeroOrder, ids, []), 1));
    }

    [Fact]
    public void BoundedIntegerDrawRejectsModuloBias()
    {
        var words = new Queue<ulong>([0, 1, 2, 3, 4, 5, 16]);
        Assert.Equal(6UL, ConditionalShuffleProposal.UniformBelow(10, () => words.Dequeue()));
        Assert.Empty(words);
        Assert.Equal(0UL, ConditionalShuffleProposal.UniformBelow(1, () => throw new InvalidOperationException()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionalShuffleProposal.UniformBelow(0, () => 1));
        Assert.Equal(ulong.MaxValue - 1,
            ConditionalShuffleProposal.UniformBelow(ulong.MaxValue, () => ulong.MaxValue - 1));
    }

    private static int Map(ulong high, int bits, int bound) =>
        (int)((double)high * Math.ScaleB(1d, -bits) * (double)bound);

    private static int[] ApplyRawWords(IReadOnlyList<ulong> words, int count, int bits)
    {
        int[] order = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1, step = 0; i > 0; i--, step++)
        {
            int index = Map(words[step] >> (64 - bits), bits, i + 1);
            (order[i], order[index]) = (order[index], order[i]);
        }
        return order;
    }

    private static bool Matches(int[] order, string[] signatures, string[] prefix) =>
        prefix.Select((signature, index) => signatures[order[index]] == signature).All(match => match);

    private static ulong RawForResidue(ulong residue, ulong bound) => bound + residue;

    private static Func<ulong> RandomForPermutation(int[] order, string[] signatures, string[] prefix)
    {
        var remaining = Enumerable.Range(0, signatures.Length).ToList();
        var words = new Queue<ulong>();
        for (int i = 0; i < order.Length; i++)
        {
            int[] candidates = i < prefix.Length
                ? remaining.Where(id => signatures[id] == prefix[i]).ToArray() : remaining.ToArray();
            if (candidates.Length > 1)
                words.Enqueue(RawForResidue((ulong)Array.IndexOf(candidates, order[i]), (ulong)candidates.Length));
            remaining.Remove(order[i]);
        }
        return () => words.Count > 0 ? words.Dequeue() : ulong.MaxValue;
    }

    private static IEnumerable<int[]> Permutations(int[] values, int start = 0)
    {
        if (start == values.Length) { yield return values.ToArray(); yield break; }
        for (int i = start; i < values.Length; i++)
        {
            (values[start], values[i]) = (values[i], values[start]);
            foreach (var permutation in Permutations(values, start + 1)) yield return permutation;
            (values[start], values[i]) = (values[i], values[start]);
        }
    }
}
