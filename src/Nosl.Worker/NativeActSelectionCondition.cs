using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>Exact native initial act selection under the existing public encounter-origin certificate.</summary>
internal sealed class NativeActSelectionCondition
{
    internal Type TargetActType { get; }
    private NativeActSelectionCondition(Type targetActType) => TargetActType = targetActType;

    internal static NativeActSelectionCondition ForReviewedPublicAct(Type targetActType)
    {
        if (targetActType != typeof(Overgrowth) && targetActType != typeof(Underdocks))
            throw new ArgumentException("Unreviewed native initial act", nameof(targetActType));
        return new(targetActType);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeActSelectionCondition? condition, out string? reason)
    {
        condition = null;
        if (!NativeFirstEncounterCondition.TryCreate(root, prior, out var encounter, out reason)) return false;
        condition = new(encounter!.TargetActType); return true;
    }

    internal NativeActSelectionProposal Prepare(NativeTapeRecipe recipe, int maxTrials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var random = new Rng(recipe.ProposalSeed, "nosl-native-tape-conditional-act-selection-v1");
        return Prepare(recipe.IndependentRunSeed, maxTrials, random.NextUnsignedLong, cancellationToken);
    }

    internal NativeActSelectionProposal Prepare(string independentRunSeed, int maxTrials, Func<ulong> nextWord,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(independentRunSeed);
        return PrepareTrials(maxTrials,
            () => NativeComponentRejection.Evaluate(() => ActDefinition.GetRandomList(independentRunSeed), nextWord, cancellationToken),
            cancellationToken);
    }

    // Shared bounded-trial validator also permits explicit native-shape-drift
    // regression fixtures without changing or patching the native act factory.
    internal NativeActSelectionProposal PrepareTrials(int maxTrials,
        Func<NativeComponentTrial<IReadOnlyList<ActDefinition>>> draw, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var result = NativeComponentRejection.Sample("act_selection", maxTrials,
            () =>
            {
                var trial = draw();
                return new NativeComponentTrial<(IReadOnlyList<ActDefinition> Acts, int Words, int Cells)>(
                    (trial.Value, trial.Trace.Count, trial.DistinctCells), trial.Trace, trial.DistinctCells);
            },
            trial =>
            {
                // Validate after Sample accounts the completed trial, retaining
                // measured work even when native shape drift is a fatal error.
                if (trial.Words != 3 || trial.Cells != 3)
                    throw new InvalidOperationException("Native act selection changed its three distinct complete draws");
                var acts = trial.Acts;
                if (acts.Count != 3 || acts[0].GetType() != typeof(Overgrowth) && acts[0].GetType() != typeof(Underdocks)
                    || acts[1] is not Hive || acts[2] is not Glory)
                    throw new InvalidOperationException("Reviewed native act-selection pool changed");
                return acts[0].GetType() == TargetActType;
            }, cancellationToken);
        if (result.Trace.Count != 3)
            throw new InvalidOperationException("Native act selection did not consume its three complete draws");
        return new(TargetActType, result.Trace, result.Stats, maxTrials);
    }
}

internal sealed class NativeActSelectionProposal
{
    internal Type TargetActType { get; }
    internal IReadOnlyList<NativeComponentWord> Trace { get; }
    internal NativeComponentStats Stats { get; }
    internal int MaxTrials { get; }
    // The emission-conditioned trace law has p/q=1/2. Including the capped
    // kernel's abort atom gives p/q=(1/2)/(1-2^-K), still a root constant.
    internal ShuffleRational ConditionalOutputRatio => new(1, 2);
    internal ShuffleRational SuccessfulSubdensityRatio { get; }
    internal ShuffleRational Envelope => SuccessfulSubdensityRatio;

    internal NativeActSelectionProposal(Type targetActType, IReadOnlyList<NativeComponentWord> trace,
        NativeComponentStats stats, int maxTrials)
    {
        TargetActType = targetActType; Trace = Array.AsReadOnly(trace.ToArray()); Stats = stats; MaxTrials = maxTrials;
        BigInteger power = BigInteger.One << maxTrials;
        SuccessfulSubdensityRatio = new(power, 2 * (power - 1));
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}
