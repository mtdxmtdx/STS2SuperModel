namespace Nosl.Worker;

/// <summary>A contiguous preimage in the significant high bits of a native raw word.</summary>
internal readonly record struct NativeFloatBranchBucket(ulong Start, ulong Size);

/// <summary>One conditional native float-branch word under the independent-word tape law.</summary>
internal sealed record NativeFloatBranchPlan(ulong RawWord, NativeFloatBranchBucket Bucket,
    int PrecisionBits, ulong EnvelopeSize)
{
    internal ShuffleRational NativeToProposalRatio => new(Bucket.Size, 1UL << PrecisionBits);
    internal ShuffleRational Envelope => new(EnvelopeSize, 1UL << PrecisionBits);

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        return Bucket.Size == EnvelopeSize
            || ConditionalShuffleProposal.UniformBelow(EnvelopeSize, nextWord) < Bucket.Size;
    }
}

/// <summary>
/// Exact preimages of Rng.NextFloat(totalWeight), followed by RandomBranchState's
/// ordered float subtraction and first residual &lt;= 0 selection. The native total
/// is supplied separately: LINQ's float Sum accumulates in double before casting.
/// </summary>
internal static class NativeFloatBranchProposal
{
    internal static NativeFloatBranchBucket Bucket(float totalWeight, IReadOnlyList<float> orderedWeights,
        int selectedIndex, int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        float[] weights = Freeze(totalWeight, orderedWeights, precisionBits);
        ValidateIndex(selectedIndex, weights.Length);
        return FindBucket(totalWeight, weights, selectedIndex, precisionBits);
    }

    /// <summary>
    /// Returns null for an empty native preimage. The envelope must be fixed for
    /// the public root, not chosen after observing this proposal's hidden context.
    /// </summary>
    internal static NativeFloatBranchPlan? Create(float totalWeight, IReadOnlyList<float> orderedWeights,
        int selectedIndex, ulong envelopeSize, Func<ulong> nextWord,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        float[] weights = Freeze(totalWeight, orderedWeights, precisionBits);
        ValidateIndex(selectedIndex, weights.Length);
        var bucket = FindBucket(totalWeight, weights, selectedIndex, precisionBits);
        if (envelopeSize < bucket.Size || envelopeSize > (1UL << precisionBits))
            throw new InvalidOperationException("Native float branch bucket exceeds its public-root envelope");
        if (bucket.Size == 0) return null;

        ulong high = bucket.Start + ConditionalShuffleProposal.UniformBelow(bucket.Size, nextWord);
        int lowBits = 64 - precisionBits;
        ulong raw = (high << lowBits) | (nextWord() & ((1UL << lowBits) - 1));
        return new(raw, bucket, precisionBits, envelopeSize);
    }

    /// <summary>
    /// Returns orderedWeights.Count if subtraction selects no branch. Even a
    /// native summed total can leave such a tail because each subtraction rounds.
    /// </summary>
    internal static int Select(ulong high, float totalWeight, IReadOnlyList<float> orderedWeights,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        float[] weights = Freeze(totalWeight, orderedWeights, precisionBits);
        if (high >= (1UL << precisionBits)) throw new ArgumentOutOfRangeException(nameof(high));
        return SelectCore(high, totalWeight, weights, Math.ScaleB(1d, -precisionBits));
    }

    private static NativeFloatBranchBucket FindBucket(float totalWeight, float[] weights,
        int selectedIndex, int precisionBits)
    {
        ulong domain = 1UL << precisionBits;
        double increment = Math.ScaleB(1d, -precisionBits);
        // Nonnegative weights make the selected index monotone in high, including
        // the Count sentinel. Search actual float operations, not ideal weight ratios.
        ulong LowerBound(int index, ulong low)
        {
            ulong high = domain;
            while (low < high)
            {
                ulong middle = low + (high - low) / 2;
                if (SelectCore(middle, totalWeight, weights, increment) < index) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        ulong start = selectedIndex == 0 ? 0 : LowerBound(selectedIndex, 0);
        return new(start, LowerBound(selectedIndex + 1, start) - start);
    }

    private static int SelectCore(ulong high, float totalWeight, float[] weights, double increment)
    {
        float residual = (float)((double)high * increment * (double)totalWeight);
        for (int index = 0; index < weights.Length; index++)
        {
            residual -= weights[index];
            if (residual <= 0f) return index;
        }
        return weights.Length;
    }

    private static float[] Freeze(float totalWeight, IReadOnlyList<float> orderedWeights, int precisionBits)
    {
        ArgumentNullException.ThrowIfNull(orderedWeights);
        if (precisionBits is < 1 or > ConditionalShuffleProposal.NativePrecisionBits)
            throw new ArgumentOutOfRangeException(nameof(precisionBits));
        if (!float.IsFinite(totalWeight) || totalWeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(totalWeight));
        // No caller-owned collection is read after invoking the random-word source.
        float[] weights = orderedWeights.ToArray();
        if (weights.Length == 0 || weights.Any(weight => !float.IsFinite(weight) || weight < 0f))
            throw new ArgumentException("Branch weights must be nonempty, finite and nonnegative", nameof(orderedWeights));
        return weights;
    }

    private static void ValidateIndex(int selectedIndex, int count)
    {
        if (selectedIndex < 0 || selectedIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));
    }
}
