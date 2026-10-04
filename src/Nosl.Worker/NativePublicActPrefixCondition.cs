using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>Public act identity at a held hypothetical seed, under the independent Map law only.</summary>
internal sealed class NativePublicActPrefixCondition
{
    internal Type TargetActType { get; }
    private NativePublicActPrefixCondition(Type targetActType) => TargetActType = targetActType;

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicActPrefixCondition? condition, out string? reason)
    {
        condition = null;
        if (!NativePublicMapReconstructionCondition.TryCreate(root.PublicEvidence, prior, out _, out reason)) return false;
        NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out _);
        NativePublicWeakEncounterSequenceCondition.TryCreate(root, prior, out var weak, out _);
        NativePublicEventPermutationCondition.TryCreate(root, prior, out var events, out _);
        return TryCreateCertified(opening?.TargetActType, weak?.TargetActType, events?.TargetActType,
            out condition, out reason);
    }

    // Receives only reviewed detached certificates; map graph shape never identifies an act.
    internal static bool TryCreateCertified(Type? opening, Type? weak, Type? events,
        out NativePublicActPrefixCondition? condition, out string? reason)
    {
        condition = null;
        var acts = new[] { opening, weak, events }.OfType<Type>().Distinct().ToArray();
        reason = "one_consistent_certified_public_act_required";
        if (acts is not [var act] || act != typeof(Overgrowth) && act != typeof(Underdocks)) return false;
        condition = new(act); reason = null; return true;
    }

    internal NativePublicActPrefixPlan Prepare(NativeTapeRecipe recipe, int maxTrials,
        CancellationToken cancellationToken = default) =>
        new(recipe, NativeActSelectionCondition.ForReviewedPublicAct(TargetActType).Prepare(recipe, maxTrials, cancellationToken));

    internal NativePublicActPrefixPlan Prepare(NativeTapeRecipe recipe, int maxTrials, Func<ulong> nextWord,
        CancellationToken cancellationToken = default) =>
        new(recipe, NativeActSelectionCondition.ForReviewedPublicAct(TargetActType)
            .Prepare(recipe.IndependentRunSeed, maxTrials, nextWord, cancellationToken));
}

/// <summary>
/// For every held seed the event mass is 1/2. The bounded kernel has null mass 2^-K,
/// and successful p/q = 2^K/[2(2^K-1)], a root constant equal to its envelope.
/// No source seed, run-seed resampling, act object replacement, or Map words enter this plan.
/// </summary>
internal sealed class NativePublicActPrefixPlan
{
    internal const string CorrectionClaim = "held-hypothetical-seed-public-act-root-constant-with-null-atom-v1";
    internal NativeTapeRecipe Recipe { get; }
    internal NativeActSelectionProposal Selection { get; }
    internal IReadOnlyList<NativeComponentWord> Trace => Selection.Trace;
    internal NativeComponentStats Stats => Selection.Stats;
    internal int MaxTrials => Selection.MaxTrials;
    internal ShuffleRational NullMass => NullMassFor(MaxTrials);
    internal static ShuffleRational NullMassFor(int maxTrials) => new(1, BigInteger.One << maxTrials);
    internal ShuffleRational SuccessfulSubdensityRatio => Selection.SuccessfulSubdensityRatio;

    internal NativePublicActPrefixPlan(NativeTapeRecipe recipe, NativeActSelectionProposal selection)
    {
        ArgumentNullException.ThrowIfNull(recipe); ArgumentNullException.ThrowIfNull(selection);
        if (selection.Trace.Count != 3 || selection.Trace.Select(w => w.State).Distinct().Count() != 3
            || selection.MaxTrials < 1 || selection.Stats.CompletedTrials < 1
            || selection.Stats.CompletedTrials > selection.MaxTrials
            || selection.Stats.TotalWordDraws != 3L * selection.Stats.CompletedTrials
            || selection.Stats.TotalDistinctCells != selection.Stats.TotalWordDraws || selection.Stats.MaximumTrialWords != 3)
            throw new InvalidOperationException("Public act prefix requires complete three-cell native trials");
        Recipe = recipe; Selection = selection;
    }
}

/// <summary>A distinct owned replay, completed at the constructor's pre-map boundary even with zero Map draws.</summary>
internal sealed class NativePublicActPrefixReplay(NativePublicActPrefixPlan plan, Action<Exception> captureFailure)
{
    private RunState? _constructedRun;
    private int _words;
    private bool _attached;
    internal bool Bound => _constructedRun is not null;
    internal int ReplayedWords => _words;

    internal ulong Word(LabelRandomState state, Func<NativeComponentWord, ulong> retainedWord)
    {
        try
        {
            if (Bound || _words >= 3 || plan.Trace[_words].State != state)
                throw new InvalidOperationException("Native public act prefix changed its complete ordered RNG trace");
            return retainedWord(plan.Trace[_words++]);
        }
        catch (Exception error) { captureFailure(error); throw; }
    }

    internal void BeginMap(LabelMapGenerationContext context)
    {
        try
        {
            if (Bound)
            {
                if (!_attached || !ReferenceEquals(_constructedRun, context.Run))
                    throw new InvalidOperationException("Later map escaped its owned public act prefix");
                return;
            }
            var run = context.Run;
            // LabelRandomScope suppresses interception inside this callback, so
            // a scratch CreateRng has no provenance. Compare native state and
            // validate the actual Map origin separately; reconstruction validates
            // its complete provenance snapshot again outside suppression.
            var actualMap = context.Rng.ToSerializable();
            var expectedMap = StandardActMap.CreateRng(run.Rng.Seed, 0).ToSerializable();
            if (_words != 3 || run.Rng.StringSeed != plan.Recipe.IndependentRunSeed || run.Rng.UsesSemanticKeys
                || run.CurrentActIndex != 0 || run.TotalFloor != 0 || run.CurrentMapCoord is not null || run.Map is not null
                || run.Players.Count != 0 || run.CurrentRoomCount != 0 || run.Acts.Count != 3
                || !ReferenceEquals(run.Acts[0], context.Act) || context.Act.GetType() != plan.Selection.TargetActType
                || run.Acts[1] is not Hive || run.Acts[2] is not Glory
                || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss) || context.Rng.Counter != 0
                || actualMap.LabelProvenance is not { Law: LabelRandomProvenance.MapLawId,
                    Partition: LabelRandomProvenance.MapPartition, OriginFamily: LabelRandomProvenance.MapOrigin,
                    ActIndex: 0, RawCursor: 0, InitialSeed: not null }
                || actualMap with { LabelProvenance = null } != expectedMap with { LabelProvenance = null })
                throw new InvalidOperationException("Public act prefix escaped its native construction boundary");
            _constructedRun = run;
        }
        catch (Exception error) { captureFailure(error); throw; }
    }

    internal void Attach(RunState run)
    {
        try
        {
            if (_attached || !Bound || !ReferenceEquals(_constructedRun, run))
                throw new InvalidOperationException("Public act prefix did not finish in this owned native construction");
            _attached = true;
        }
        catch (Exception error) { captureFailure(error); throw; }
    }

    internal void ValidateCompletion()
    {
        if (!_attached || _words != 3)
        {
            var error = new InvalidOperationException("Public act prefix is incomplete");
            captureFailure(error); throw error;
        }
    }
}
