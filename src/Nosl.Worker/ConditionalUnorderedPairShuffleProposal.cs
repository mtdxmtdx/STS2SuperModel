using System.Numerics;

namespace Nosl.Worker;

/// <summary>
/// An unordered two-card prefix proposal. Physical copies remain distinct, and
/// the underlying plan supplies the entire descending native Fisher-Yates tape.
/// </summary>
internal sealed class ConditionalUnorderedPairShufflePlan
{
    private readonly ConditionalShufflePlan orderedPlan;

    internal IReadOnlyList<int> PhysicalPermutation => orderedPlan.PhysicalPermutation;
    internal IReadOnlyList<ulong> RawWords => orderedPlan.RawWords;
    internal ShuffleRational PrefixProbability { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope { get; }

    internal ConditionalUnorderedPairShufflePlan(ConditionalShufflePlan orderedPlan,
        int distinctOrders, ShuffleRational envelope)
    {
        this.orderedPlan = orderedPlan;
        PrefixProbability = orderedPlan.PrefixProbability.Multiply(distinctOrders, 1);
        NativeToProposalRatio = orderedPlan.NativeToProposalRatio.Multiply(distinctOrders, 1);
        Envelope = envelope;
    }

    // Mixing the disjoint orders scales both p/q and its envelope equally, so
    // the ordered plan's exact correction probability remains valid.
    internal bool AcceptCorrection(Func<ulong> nextWord) => orderedPlan.AcceptCorrection(nextWord);
}

/// <summary>
/// Conditions an independent raw-word tape on the multiset of its first two
/// shuffled cards. The caller must establish that these are the actual native
/// prefix, and must check any remaining public evidence separately.
/// </summary>
internal static class ConditionalUnorderedPairShuffleProposal
{
    internal static ConditionalUnorderedPairShufflePlan? Create(
        IReadOnlyList<string> initialSignatures, IReadOnlyList<string> selectedSignatures,
        Func<ulong> nextWord, int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(initialSignatures);
        ArgumentNullException.ThrowIfNull(selectedSignatures);
        ArgumentNullException.ThrowIfNull(nextWord);

        // The word source can call arbitrary code; freeze all public evidence
        // before the order coin or the ordered proposal asks it for randomness.
        string[] initial = initialSignatures.ToArray(), selected = selectedSignatures.ToArray();
        var envelope = Envelope(initial, selected, precisionBits);
        if (envelope is null) return null;

        int distinctOrders = StringComparer.Ordinal.Equals(selected[0], selected[1]) ? 1 : 2;
        // Distinct orders have the same number of physical permutations: each
        // has c(a)*c(b)*(n-2)!. A fair coin therefore makes their disjoint union
        // uniform. The factor-two p/q adjustment also scales the common bound;
        // neither the chosen order nor physical identities can change it.
        if (distinctOrders == 2 && ConditionalShuffleProposal.UniformBelow(2, nextWord) != 0)
            (selected[0], selected[1]) = (selected[1], selected[0]);

        var ordered = ConditionalShuffleProposal.Create(initial, selected, nextWord, precisionBits);
        // At reduced test precision, some uniform physical proposals have no
        // native raw-word preimage. Preserve that rejection without resampling.
        return ordered is null ? null : new(ordered, distinctOrders, envelope.Value);
    }

    /// <summary>
    /// A root-fixed bound, requiring no random words. It depends on the initial
    /// multiset, the selected pair and precision, never its order or the sampled
    /// physical permutation. Null denotes an incompatible pair multiset.
    /// </summary>
    internal static ShuffleRational? Envelope(IReadOnlyList<string> initialSignatures,
        IReadOnlyList<string> selectedSignatures,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(initialSignatures);
        ArgumentNullException.ThrowIfNull(selectedSignatures);
        if (precisionBits is < 1 or > ConditionalShuffleProposal.NativePrecisionBits)
            throw new ArgumentOutOfRangeException(nameof(precisionBits));
        if (selectedSignatures.Count != 2)
            throw new ArgumentException("Exactly two selected signatures are required", nameof(selectedSignatures));
        if (initialSignatures.Any(signature => signature is null)
            || selectedSignatures.Any(signature => signature is null))
            throw new ArgumentException("Shuffle signatures must be non-null");
        if (initialSignatures.Count < 2) return null;

        bool same = StringComparer.Ordinal.Equals(selectedSignatures[0], selectedSignatures[1]);
        int firstCount = initialSignatures.Count(signature =>
            StringComparer.Ordinal.Equals(signature, selectedSignatures[0]));
        int secondCount = initialSignatures.Count(signature =>
            StringComparer.Ordinal.Equals(signature, selectedSignatures[1])) - (same ? 1 : 0);
        if (firstCount == 0 || secondCount <= 0) return null;

        var envelope = new ShuffleRational((BigInteger)firstCount * secondCount * (same ? 1 : 2),
            (BigInteger)initialSignatures.Count * (initialSignatures.Count - 1));
        ulong domain = 1UL << precisionBits;
        for (int bound = initialSignatures.Count; bound > 1; bound--)
            envelope = envelope.Multiply((BigInteger)bound
                * ConditionalShuffleProposal.Factor(bound, 0, precisionBits).MaxBucketSize, domain);
        return envelope;
    }
}
