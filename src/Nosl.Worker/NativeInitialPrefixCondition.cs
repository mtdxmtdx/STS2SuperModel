using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Joint rejection of the entire independent native initial prefix, including
/// RunSeed. No held seed or earlier oracle history can change its normalizer.
/// </summary>
internal sealed class NativeInitialPrefixCondition
{
    private const string FailureRunSeedDrawsKey = "nosl.initial-prefix.run-seed-draws";
    internal static int FailureRunSeedDraws(Exception exception) =>
        exception.Data[FailureRunSeedDrawsKey] is int count ? count : 0;
    internal Type? TargetActType { get; }
    internal NativeMapTravelCondition? MapTravel { get; }
    internal NativePublicInitialMapCondition? PublicMap { get; }
    internal int TargetEntryHp { get; }
    internal bool HasFreeTravel { get; }
    private NativeInitialPrefixCondition(Type actType, NativeMapTravelCondition? mapTravel, int hp, bool freeTravel)
    { TargetActType = actType; MapTravel = mapTravel; TargetEntryHp = hp; HasFreeTravel = freeTravel; }
    private NativeInitialPrefixCondition(NativePublicInitialMapCondition publicMap, Type? observedActType)
    { PublicMap = publicMap; HasFreeTravel = publicMap.HasFreeTravel; TargetActType = observedActType; }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeInitialPrefixCondition? condition, out string? reason)
    {
        condition = null;
        if (NativePublicInitialMapCondition.TryCreate(root.PublicEvidence, prior, out var publicMap, out reason))
        {
            // Reuse only a certified retained public opening roster. This joins
            // two necessary events; it does not force a native act or hold a seed.
            // An unavailable roster leaves both initial acts in map rejection.
            NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out _);
            condition = new(publicMap!, opening?.TargetActType); return true;
        }
        if (!NativeActSelectionCondition.TryCreate(root, prior, out var act, out reason)) return false;
        if (!NativeFirstRewardCondition.TryCreate(root, prior, out var reward, out reason)) return false;
        NativeMapTravelCondition.TryCreate(root, prior, out var map, out _);
        condition = new(act!.TargetActType, map, reward!.TargetEntryHp, reward.TargetNeowId == "WingedBoots"); return true;
    }

    internal bool MatchesMap(ActMap map)
    {
        if (PublicMap is not null) return PublicMap.MatchesMap(map);
        var (_, second) = NativeSourceMapChoice.FirstTwoChoices(map, TargetEntryHp, 70, HasFreeTravel);
        // At the certified floor/index, only a Monster point or an Unknown
        // resolving natively to Monster can supply normal encounter slot one.
        // The retained inventory excludes map edits; the startup excludes every
        // forced event roster. Unknown's native room roll remains unconditioned.
        return second.PointType is MapPointType.Monster or MapPointType.Unknown
            && (MapTravel?.MatchesMap(map) ?? true);
    }

    /// <summary>Prepare outside every active label scope or callback; only scratch cells are touched.</summary>
    internal NativeInitialPrefixProposal Prepare(NativeTapeRecipe auxiliaryRecipe, int maxTrials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auxiliaryRecipe);
        var random = new Rng(auxiliaryRecipe.ProposalSeed, "nosl-native-tape-conditional-initial-prefix-v1");
        return Prepare(auxiliaryRecipe, maxTrials, random.NextUnsignedLong, cancellationToken);
    }

    internal NativeInitialPrefixProposal Prepare(NativeTapeRecipe auxiliaryRecipe, int maxTrials,
        Func<ulong> nextWord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auxiliaryRecipe); ArgumentNullException.ThrowIfNull(nextWord);
        int runSeedDraws = 0;
        NativeComponentTrial<PrefixTrial> Draw()
        {
            // Each independent candidate includes a fresh uniform RunSeed and a
            // fresh oracle. TapeSeed is the independent implementation key for
            // still-unrevealed remainder cells; this is the ideal oracle law,
            // not a finite SHA256-seed posterior. Other recipe coordinates remain
            // unchanged because the event does not depend on those variables.
            ulong runSeed = nextWord(); runSeedDraws++;
            var selected = auxiliaryRecipe with { RunSeed = runSeed };
            return NativeComponentRejection.Evaluate(() =>
            {
                var acts = ActDefinition.GetRandomList(selected.IndependentRunSeed);
                if (acts.Count != 3 || acts[0].GetType() != typeof(Overgrowth) && acts[0].GetType() != typeof(Underdocks)
                    || acts[1] is not Hive || acts[2] is not Glory)
                    throw new InvalidOperationException("Reviewed native initial act pool changed");
                // A wrong act proves this full prefix cannot match. Unread map
                // cells integrate out; no act object or constructor is forced.
                if (TargetActType is not null && acts[0].GetType() != TargetActType)
                    return new PrefixTrial(selected, acts[0].GetType(), false);
                var runRng = new RunRngSet(selected.IndependentRunSeed);
                var mapRng = StandardActMap.CreateRng(runRng.Seed, 0);
                var map = StandardActMap.CreateFor(acts[0], mapRng, new AscensionManager(10), hasSecondBoss: false);
                return new PrefixTrial(selected, acts[0].GetType(), MatchesMap(map));
            }, nextWord, cancellationToken);
        }
        try
        {
            var result = NativeComponentRejection.Sample("initial_prefix", maxTrials, Draw,
                candidate => candidate.Match, cancellationToken);
            if (result.Trace.Count <= NativeInitialPrefixProposal.MapTraceOffset)
                throw new InvalidOperationException("Selected initial prefix omitted native map generation");
            return new(auxiliaryRecipe, result.Value.Recipe, result.Value.ActType, MapTravel is not null,
                result.Trace, result.Stats, maxTrials);
        }
        catch (Exception exception)
        {
            exception.Data[FailureRunSeedDrawsKey] = runSeedDraws;
            throw;
        }
    }

    private sealed record PrefixTrial(NativeTapeRecipe Recipe, Type ActType, bool Match);
}

/// <summary>
/// Let b be the clean-miss probability under the joint fresh RunSeed/oracle
/// prefix law. For fixed K, successful subdensity is
/// p(prefix)*1[event]*sum(b^j,j=0..K-1). Its reciprocal multiplier is both p/q
/// and the unknown root-constant envelope. If all trials complete, b=1-Z and
/// this is Z/(1-(1-Z)^K). Intrinsic native errors stop the draw; their probability
/// is not silently folded into b. External cancellation remains unresolved.
/// No numerical estimate or ratio of one is claimed. Failed words stay auxiliary.
/// </summary>
internal sealed class NativeInitialPrefixProposal
{
    internal const int MapTraceOffset = 3;
    internal const string CorrectionClaim = "joint-fresh-run-seed-oracle-prefix-root-constant-normalizer-cancels-v1";
    internal NativeTapeRecipe AuxiliaryRecipe { get; }
    internal NativeTapeRecipe SelectedRecipe { get; }
    internal Type TargetActType { get; }
    internal bool ConditionsMapTravel { get; }
    internal IReadOnlyList<NativeComponentWord> Trace { get; }
    internal NativeComponentStats Stats { get; }
    internal int MaxTrials { get; }
    internal int RunSeedDraws => Stats.CompletedTrials;

    internal NativeInitialPrefixProposal(NativeTapeRecipe auxiliaryRecipe, NativeTapeRecipe selectedRecipe,
        Type targetActType, bool conditionsMapTravel, IReadOnlyList<NativeComponentWord> trace,
        NativeComponentStats stats, int maxTrials)
    {
        AuxiliaryRecipe = auxiliaryRecipe; SelectedRecipe = selectedRecipe; TargetActType = targetActType;
        ConditionsMapTravel = conditionsMapTravel; Trace = Array.AsReadOnly(trace.ToArray()); Stats = stats; MaxTrials = maxTrials;
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }

    internal void ValidateMapBoundary(LabelMapGenerationContext context, int replayedWords)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = context.Run;
        // Constructor phase: NativeRunWorld has not attached its completed run
        // yet. The tape must bind this object here and verify it again on attach.
        if (replayedWords != MapTraceOffset || run.Rng.StringSeed != SelectedRecipe.IndependentRunSeed
            || run.Rng.UsesSemanticKeys || run.CurrentActIndex != 0 || run.TotalFloor != 0
            || run.Players.Count != 0 || run.CurrentRoomCount != 0 || run.Acts.Count != 3
            || !ReferenceEquals(run.Acts[0], context.Act) || context.Act.GetType() != TargetActType
            || run.Acts[1] is not Hive || run.Acts[2] is not Glory
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss) || context.Rng.Counter != 0)
            throw new InvalidOperationException("Initial prefix escaped its native construction boundary");
        var state = context.Rng.ToSerializable();
        if (Trace[MapTraceOffset].State != new LabelRandomState(state.state0, state.state1, state.state2, state.state3))
            throw new InvalidOperationException("Native map RNG derivation changed from the selected prefix");
    }
}
