using System.Collections.Concurrent;
using System.Numerics;

namespace Nosl.Worker;

/// <summary>An exact nonnegative rational; no floating point enters proposal correction.</summary>
internal readonly record struct ShuffleRational
{
    internal BigInteger Numerator { get; }
    internal BigInteger Denominator { get; }

    internal ShuffleRational(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        BigInteger divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor; Denominator = denominator / divisor;
    }

    internal ShuffleRational Multiply(BigInteger numerator, BigInteger denominator) =>
        new(Numerator * numerator, Denominator * denominator);
}

internal sealed record ConditionalShuffleFactor(int Bound, int Index, ulong BucketStart,
    ulong BucketSize, ulong MaxBucketSize);

/// <summary>
/// Proposal for an already identified native initial shuffle, not a root sampler.
/// PhysicalPermutation contains original physical-card indices; RawWords and Factors
/// follow native Fisher-Yates order (bound n, n-1, ..., 2).
/// </summary>
internal sealed class ConditionalShufflePlan
{
    internal IReadOnlyList<int> PhysicalPermutation { get; }
    internal IReadOnlyList<ulong> RawWords { get; }
    internal IReadOnlyList<ConditionalShuffleFactor> Factors { get; }
    internal ShuffleRational PrefixProbability { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope { get; }

    internal ConditionalShufflePlan(int[] permutation, ulong[] words, ConditionalShuffleFactor[] factors,
        ShuffleRational prefixProbability, ShuffleRational nativeToProposalRatio, ShuffleRational envelope)
    {
        PhysicalPermutation = Array.AsReadOnly(permutation);
        RawWords = Array.AsReadOnly(words);
        Factors = Array.AsReadOnly(factors);
        PrefixProbability = prefixProbability;
        NativeToProposalRatio = nativeToProposalRatio;
        Envelope = envelope;
    }

    /// <summary>
    /// Accepts with exactly (native/proposal)/Envelope. The caller must separately
    /// verify all remaining public evidence. Envelope is constant only when the
    /// public root fixes the deck multiset and the constrained draw prefix.
    /// </summary>
    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        foreach (var factor in Factors)
            if (factor.BucketSize != factor.MaxBucketSize
                && ConditionalShuffleProposal.UniformBelow(factor.MaxBucketSize, nextWord) >= factor.BucketSize)
                return false;
        return true;
    }
}

/// <summary>
/// Exact proposal math for an explicitly declared independent raw-word tape law.
/// This is not valid for the original common-seed PRNG prior. A caller must prove
/// that every compatible world has this ordered prefix of the native permutation;
/// merely observing those draws after arbitrary reorder hooks is insufficient.
/// </summary>
internal static class ConditionalShuffleProposal
{
    internal const int NativePrecisionBits = 53;
    private sealed record BucketTable(ulong[] Starts, ulong Maximum);
    private static readonly ConcurrentDictionary<(int Bound, int Bits), BucketTable> Buckets = new();

    /// <summary>
    /// initialSignatures must describe the actual input ordering to Fisher-Yates
    /// (after native stable sorting where applicable). Equal signatures remain
    /// distinct physical cards. Null denotes an incompatible prefix or a proposed
    /// permutation with zero native mass under a small test-only precision.
    /// </summary>
    internal static ConditionalShufflePlan? Create(IReadOnlyList<string> initialSignatures,
        IReadOnlyList<string> observedPrefix, Func<ulong> nextWord,
        int precisionBits = NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(initialSignatures);
        ArgumentNullException.ThrowIfNull(observedPrefix);
        ArgumentNullException.ThrowIfNull(nextWord);
        ValidatePrecision(precisionBits);
        if (initialSignatures.Any(s => s is null) || observedPrefix.Any(s => s is null))
            throw new ArgumentException("Shuffle signatures must be non-null");
        if (observedPrefix.Count > initialSignatures.Count) return null;

        // Freeze caller-owned collections before invoking its random-word source.
        string[] initial = initialSignatures.ToArray(), prefix = observedPrefix.ToArray();
        var multiplicities = initial.GroupBy(s => s, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var prefixProbability = new ShuffleRational(1, 1);
        for (int i = 0; i < prefix.Length; i++)
        {
            if (!multiplicities.TryGetValue(prefix[i], out int count) || count == 0) return null;
            prefixProbability = prefixProbability.Multiply(count, initial.Length - i);
            multiplicities[prefix[i]] = count - 1;
        }

        var remaining = Enumerable.Range(0, initial.Length).ToList();
        var permutation = new int[initial.Length];
        for (int i = 0; i < permutation.Length; i++)
        {
            int[] candidates = i < prefix.Length
                ? remaining.Where(id => StringComparer.Ordinal.Equals(initial[id], prefix[i])).ToArray()
                : remaining.ToArray();
            int selected = candidates[(int)UniformBelow((ulong)candidates.Length, nextWord)];
            permutation[i] = selected;
            remaining.Remove(selected);
        }

        // Each physical permutation has exactly one descending Fisher-Yates path.
        int[] working = Enumerable.Range(0, initial.Length).ToArray();
        var factors = new ConditionalShuffleFactor[Math.Max(0, initial.Length - 1)];
        var words = new ulong[factors.Length];
        var ratio = prefixProbability;
        var envelope = prefixProbability;
        ulong domain = 1UL << precisionBits;
        int lowBits = 64 - precisionBits;
        ulong lowMask = (1UL << lowBits) - 1;
        for (int i = initial.Length - 1, step = 0; i > 0; i--, step++)
        {
            int index = Array.IndexOf(working, permutation[i], 0, i + 1);
            var factor = Factor(i + 1, index, precisionBits);
            if (factor.BucketSize == 0) return null;
            factors[step] = factor;
            ulong high = factor.BucketStart + UniformBelow(factor.BucketSize, nextWord);
            words[step] = (high << lowBits) | (nextWord() & lowMask);
            (working[i], working[index]) = (working[index], working[i]);
            ratio = ratio.Multiply((BigInteger)factor.Bound * factor.BucketSize, domain);
            envelope = envelope.Multiply((BigInteger)factor.Bound * factor.MaxBucketSize, domain);
        }
        return new(permutation, words, factors, prefixProbability, ratio, envelope);
    }

    internal static ConditionalShuffleFactor Factor(int bound, int index,
        int precisionBits = NativePrecisionBits)
    {
        ValidatePrecision(precisionBits);
        if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound));
        if (index < 0 || index >= bound) throw new ArgumentOutOfRangeException(nameof(index));
        var table = Buckets.GetOrAdd((bound, precisionBits), key => BuildBuckets(key.Bound, key.Bits));
        return new(bound, index, table.Starts[index], table.Starts[index + 1] - table.Starts[index], table.Maximum);
    }

    private static BucketTable BuildBuckets(int bound, int precisionBits)
    {
        ulong domain = 1UL << precisionBits;
        double increment = Math.ScaleB(1d, -precisionBits);
        var starts = new ulong[checked(bound + 1)];
        starts[bound] = domain;
        for (int index = 1; index < bound; index++)
        {
            ulong low = starts[index - 1], high = domain;
            while (low < high)
            {
                ulong middle = low + (high - low) / 2;
                // Exactly the operations in MegaRandom.NextDouble and NextInner,
                // including floating multiplication rounding at bucket boundaries.
                int result = (int)((double)middle * increment * (double)bound);
                if (result < index) low = middle + 1; else high = middle;
            }
            starts[index] = low;
        }
        ulong maximum = 0;
        for (int i = 0; i < bound; i++) maximum = Math.Max(maximum, starts[i + 1] - starts[i]);
        return new(starts, maximum);
    }

    /// <summary>Uniform integer from independent full-width words, without modulo bias.</summary>
    internal static ulong UniformBelow(ulong bound, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
        if (bound == 1) return 0;
        ulong threshold = unchecked(0UL - bound) % bound;
        ulong word;
        do { word = nextWord(); } while (word < threshold);
        return word % bound;
    }

    private static void ValidatePrecision(int bits)
    {
        if (bits is < 1 or > NativePrecisionBits) throw new ArgumentOutOfRangeException(nameof(bits));
    }
}
