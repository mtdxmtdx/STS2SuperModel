using System.Numerics;
using Nosl.Worker;

namespace Nosl.Tests;

public class ConditionalUnorderedPairShuffleProposalTests
{
    [Theory]
    [InlineData(4, 2, false)]
    [InlineData(4, 2, true)]
    [InlineData(5, 3, false)]
    [InlineData(5, 2, true)]
    [InlineData(5, 1, false)]
    public void EveryFiniteNativeAtomHasTheExactProposalAndAcceptedDensity(int count, int bits, bool sameKey)
    {
        string[] signatures = new[] { "A", "A", "B", "C", "C" }.Take(count).ToArray();
        string[] selected = sameKey ? ["A", "A"] : ["A", "C"];
        int domain = 1 << bits, steps = count - 1;
        // Literal native primitive enumeration, independent of proposal factors,
        // inverses and bucket tables. The resulting physical cards stay distinct.
        var native = new Dictionary<string, List<int[]>>(StringComparer.Ordinal);
        int nativeSpace = 1 << (bits * steps);
        for (int encoded = 0; encoded < nativeSpace; encoded++)
        {
            int remaining = encoded;
            var highs = new int[steps];
            int[] physical = Enumerable.Range(0, count).ToArray();
            for (int i = count - 1, step = 0; i > 0; i--, step++)
            {
                int high = remaining % domain; remaining /= domain;
                highs[step] = high;
                int index = (int)((double)high / domain * (i + 1));
                (physical[i], physical[index]) = (physical[index], physical[i]);
            }
            if (!Matches(physical, signatures, selected)) continue;
            string key = string.Join(",", physical);
            if (!native.TryGetValue(key, out var atoms)) native[key] = atoms = [];
            atoms.Add(highs);
        }

        int[][] compatible = Permutations(Enumerable.Range(0, count).ToArray())
            .Where(order => Matches(order, signatures, selected)).ToArray();
        int[] maxima = Enumerable.Range(2, steps).Reverse().Select(bound =>
            Enumerable.Range(0, domain).GroupBy(high => (int)((double)high / domain * bound))
                .Max(group => group.Count())).ToArray();
        BigInteger maximumProduct = maxima.Aggregate(BigInteger.One, (product, maximum) => product * maximum);
        var expectedEnvelope = new ShuffleRational(compatible.Length * maximumProduct, nativeSpace);
        Assert.Equal(expectedEnvelope, ConditionalUnorderedPairShuffleProposal.Envelope(signatures, selected, bits));
        Assert.Equal(expectedEnvelope, ConditionalUnorderedPairShuffleProposal.Envelope(signatures, selected.Reverse().ToArray(), bits));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int nullCount = 0;
        foreach (int[] order in compatible)
        {
            string key = string.Join(",", order);
            if (!native.TryGetValue(key, out var atoms))
            {
                // This proposal mass must be rejected once, never silently
                // retried until a supported permutation happens to be drawn.
                Assert.Null(ConditionalUnorderedPairShuffleProposal.Create(signatures, selected,
                    WordsFor(order, signatures, selected, bits, null), bits));
                nullCount++;
                continue;
            }

            foreach (int[] highs in atoms)
            {
                var plan = ConditionalUnorderedPairShuffleProposal.Create(signatures, selected,
                    WordsFor(order, signatures, selected, bits, highs), bits);
                Assert.NotNull(plan);
                Assert.Equal(order, plan.PhysicalPermutation);
                Assert.Equal(order, Replay(plan.RawWords, count, bits));
                Assert.Equal(steps, plan.RawWords.Count);
                Assert.Equal(expectedEnvelope, plan.Envelope);
                Assert.Equal(new ShuffleRational(compatible.Length, Factorial(count)), plan.PrefixProbability);
                Assert.Equal(new ShuffleRational(atoms.Count * compatible.Length, nativeSpace), plan.NativeToProposalRatio);
                ulong lowMask = ulong.MaxValue >> bits;
                for (int step = 0; step < steps; step++)
                {
                    Assert.Equal((ulong)highs[step], plan.RawWords[step] >> (64 - bits));
                    Assert.Equal(LowWord(step) & lowMask, plan.RawWords[step] & lowMask);
                }

                // Every listed high-word atom represents all 2^(lowBits*steps)
                // full native words. Compare literal full-word densities too.
                var proposalDensity = new ShuffleRational(1,
                    compatible.Length * atoms.Count * (BigInteger.One << ((64 - bits) * steps)));
                var nativeDensity = new ShuffleRational(1, BigInteger.One << (64 * steps));
                Assert.Equal(nativeDensity, Multiply(proposalDensity, plan.NativeToProposalRatio));

                // Correction depends only on this permutation, so enumerate its
                // finite Bernoulli inputs once rather than once per native atom.
                if (seen.Contains(key)) continue;
                int[] bucketSizes = BucketSizes(highs, count, domain);
                int correctionSpace = 1;
                for (int step = 0; step < steps; step++)
                    if (bucketSizes[step] != maxima[step]) correctionSpace *= maxima[step];
                int accepted = 0;
                for (int encoded = 0; encoded < correctionSpace; encoded++)
                {
                    int remainder = encoded;
                    var words = new Queue<ulong>();
                    for (int step = 0; step < steps; step++)
                    {
                        if (bucketSizes[step] == maxima[step]) continue;
                        words.Enqueue((ulong)(maxima[step] + remainder % maxima[step]));
                        remainder /= maxima[step];
                    }
                    if (plan.AcceptCorrection(words.Dequeue)) accepted++;
                }
                var acceptedDensity = Multiply(proposalDensity, new ShuffleRational(accepted, correctionSpace));
                Assert.Equal(Multiply(nativeDensity,
                    new ShuffleRational(expectedEnvelope.Denominator, expectedEnvelope.Numerator)), acceptedDensity);
                seen.Add(key);
            }
        }
        Assert.Equal(native.Count, seen.Count);
        Assert.Equal(compatible.Length, seen.Count + nullCount);
        if (domain < count) Assert.True(nullCount > 0);
        Assert.Contains(compatible, order => order[0] == 0);
        Assert.Contains(compatible, order => order[0] == 1);
        if (!sameKey) Assert.Contains(compatible, order => signatures[order[0]] == "C");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePrecisionPreservesRawLowBitsAndSnapshotsCallerEvidence(bool reverse)
    {
        string[] signatures = ["A", "A", "B", "C", "C"];
        string[] selected = ["A", "C"];
        int[] target = reverse ? [4, 1, 2, 0, 3] : [1, 4, 2, 0, 3];
        var words = WordsFor(target, signatures, selected, 53, null);
        bool mutated = false;
        var plan = ConditionalUnorderedPairShuffleProposal.Create(signatures, selected, () =>
        {
            if (!mutated) { signatures[0] = "changed"; selected[0] = "changed"; mutated = true; }
            return words();
        });
        Assert.NotNull(plan);
        Assert.Equal(target, Replay(plan.RawWords, target.Length, 53));
        Assert.Equal(new ShuffleRational(2, 5), plan.PrefixProbability);
        Assert.All(plan.RawWords, word => Assert.Equal(2047UL, word & 2047UL));
    }

    [Fact]
    public void InvalidOrUnsupportedPairsDoNotDrawRandomWords()
    {
        ulong Never() => throw new InvalidOperationException("Unexpected random draw");
        foreach (string[] initial in new[] { Array.Empty<string>(), new[] { "A" }, new[] { "A", "B" } })
        {
            Assert.Null(ConditionalUnorderedPairShuffleProposal.Envelope(initial, ["A", "A"]));
            Assert.Null(ConditionalUnorderedPairShuffleProposal.Create(initial, ["A", "A"], Never));
        }
        Assert.Null(ConditionalUnorderedPairShuffleProposal.Create(["A", "B"], ["A", "C"], Never));
        Assert.Throws<ArgumentException>(() => ConditionalUnorderedPairShuffleProposal.Create(["A", "B"], ["A"], Never));
        Assert.Throws<ArgumentException>(() => ConditionalUnorderedPairShuffleProposal.Envelope(["A", "B"], ["A", "B", "A"]));
        Assert.Throws<ArgumentException>(() => ConditionalUnorderedPairShuffleProposal.Create(["A", null!], ["A", "A"], Never));
        Assert.Throws<ArgumentException>(() => ConditionalUnorderedPairShuffleProposal.Envelope(["A", "B"], ["A", null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionalUnorderedPairShuffleProposal.Create(["A", "B"], ["A", "B"], Never, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionalUnorderedPairShuffleProposal.Envelope(["A", "B"], ["A", "B"], 54));
        Assert.Throws<ArgumentNullException>(() => ConditionalUnorderedPairShuffleProposal.Create(null!, ["A", "B"], Never));
        Assert.Throws<ArgumentNullException>(() => ConditionalUnorderedPairShuffleProposal.Envelope(["A", "B"], null!));
        Assert.Throws<ArgumentNullException>(() => ConditionalUnorderedPairShuffleProposal.Create(["A", "B"], ["A", "B"], null!));
    }

    private static Func<ulong> WordsFor(int[] order, string[] signatures, string[] selected, int bits, int[]? highs)
    {
        var words = new Queue<ulong>();
        if (selected[0] != selected[1]) words.Enqueue(signatures[order[0]] == selected[0] ? 2UL : 3UL);
        var remaining = Enumerable.Range(0, order.Length).ToList();
        for (int position = 0; position < order.Length; position++)
        {
            int[] candidates = position < 2
                ? remaining.Where(id => signatures[id] == signatures[order[position]]).ToArray()
                : remaining.ToArray();
            if (candidates.Length > 1) words.Enqueue((ulong)(candidates.Length + Array.IndexOf(candidates, order[position])));
            remaining.Remove(order[position]);
        }
        if (highs is not null)
        {
            int domain = 1 << bits;
            for (int bound = order.Length, step = 0; bound > 1; bound--, step++)
            {
                int index = (int)((double)highs[step] / domain * bound);
                int[] bucket = Enumerable.Range(0, domain)
                    .Where(high => (int)((double)high / domain * bound) == index).ToArray();
                if (bucket.Length > 1) words.Enqueue((ulong)(bucket.Length + Array.IndexOf(bucket, highs[step])));
                words.Enqueue(LowWord(step));
            }
        }
        return () => words.Count == 0 ? ulong.MaxValue : words.Dequeue();
    }

    private static int[] BucketSizes(int[] highs, int count, int domain) => highs.Select((high, step) =>
    {
        int bound = count - step, index = (int)((double)high / domain * bound);
        return Enumerable.Range(0, domain).Count(value => (int)((double)value / domain * bound) == index);
    }).ToArray();

    private static int[] Replay(IReadOnlyList<ulong> words, int count, int bits)
    {
        int[] order = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1, step = 0; i > 0; i--, step++)
        {
            int index = (int)((double)(words[step] >> (64 - bits)) * Math.ScaleB(1d, -bits) * (i + 1));
            (order[i], order[index]) = (order[index], order[i]);
        }
        return order;
    }

    private static bool Matches(int[] order, string[] signatures, string[] selected) =>
        (signatures[order[0]] == selected[0] && signatures[order[1]] == selected[1])
        || (signatures[order[0]] == selected[1] && signatures[order[1]] == selected[0]);

    private static ulong LowWord(int step) => step % 2 == 0 ? 0x0123456789ABCDEFUL : 0xFEDCBA9876543210UL;

    private static BigInteger Factorial(int count) => Enumerable.Range(1, count)
        .Aggregate(BigInteger.One, (product, value) => product * value);

    private static ShuffleRational Multiply(ShuffleRational left, ShuffleRational right) =>
        new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);

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
