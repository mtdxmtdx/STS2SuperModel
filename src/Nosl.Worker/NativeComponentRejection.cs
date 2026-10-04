using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeComponentWord(LabelRandomState State, ulong Word);
internal sealed record NativeComponentStats(int CompletedTrials, long TotalWordDraws, long TotalDistinctCells,
    int MaximumTrialWords);
internal sealed record NativeComponentTrial<T>(T Value, IReadOnlyList<NativeComponentWord> Trace, int DistinctCells);
internal sealed record NativeComponentResult<T>(T Value, IReadOnlyList<NativeComponentWord> Trace, NativeComponentStats Stats);

/// <summary>Computationally inconclusive; never a contradiction of public evidence.</summary>
internal sealed class NativeComponentBudgetExceededException(string component, NativeComponentStats stats)
    : Exception($"Native {component} component exhausted its complete-trial budget")
{
    internal string Component { get; } = component;
    internal NativeComponentStats Stats { get; } = stats;
}

/// <summary>
/// Complete native component rejection, without per-trial draw/time truncation.
/// A caller claiming constant correction must prove the entire trial law depends
/// only on the fixed public root. Holding a latent prefix with a varying success
/// or abort probability invalidates that claim, even when emitted traces are fresh.
/// </summary>
internal static class NativeComponentRejection
{
    private const string PartialTrialStatsKey = "nosl.native-component.partial-trial-stats";
    private const string FailureStatsKey = "nosl.native-component.failure-stats";
    internal static NativeComponentStats? FailureStats(Exception exception) =>
        exception.Data[FailureStatsKey] as NativeComponentStats;

    internal static NativeComponentTrial<T> Evaluate<T>(Func<T> native, Func<ulong> nextWord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(native); ArgumentNullException.ThrowIfNull(nextWord);
        var cells = new Dictionary<LabelRandomState, ulong>(); var trace = new List<NativeComponentWord>();
        var requested = new HashSet<LabelRandomState>(); int attemptedWords = 0;
        using var scope = LabelRandomScope.Enter(state =>
        {
            attemptedWords++; requested.Add(state);
            cancellationToken.ThrowIfCancellationRequested();
            if (!cells.TryGetValue(state, out ulong value)) cells.Add(state, value = nextWord());
            trace.Add(new(state, value)); return value;
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            T result = native();
            return new(result, Array.AsReadOnly(trace.ToArray()), cells.Count);
        }
        catch (Exception exception)
        {
            exception.Data[PartialTrialStatsKey] = new NativeComponentStats(0, attemptedWords, requested.Count, attemptedWords);
            throw;
        }
    }

    internal static NativeComponentResult<T> Sample<T>(string component, int maxTrials,
        Func<NativeComponentTrial<T>> draw, Func<T, bool> matches, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component); ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(matches);
        if (maxTrials < 1) throw new ArgumentOutOfRangeException(nameof(maxTrials));
        long words = 0, cells = 0; int maximum = 0;
        for (int trial = 1; trial <= maxTrials; trial++)
        {
            NativeComponentTrial<T>? candidate = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only a completed false predicate permits another trial.
                candidate = draw();
                words = checked(words + candidate.Trace.Count); cells = checked(cells + candidate.DistinctCells);
                maximum = Math.Max(maximum, candidate.Trace.Count);
                // CPU-only native passes can finish after the last word callback.
                // Count that completed trial, then honor cancellation before and
                // after its predicate instead of selecting a canceled result.
                cancellationToken.ThrowIfCancellationRequested();
                bool match = matches(candidate.Value);
                cancellationToken.ThrowIfCancellationRequested();
                if (match)
                    return new(candidate.Value, candidate.Trace, new(trial, words, cells, maximum));
            }
            catch (Exception exception)
            {
                var partial = candidate is null ? exception.Data[PartialTrialStatsKey] as NativeComponentStats : null;
                exception.Data[FailureStatsKey] = new NativeComponentStats(trial - (candidate is null ? 1 : 0),
                    words + (partial?.TotalWordDraws ?? 0), cells + (partial?.TotalDistinctCells ?? 0),
                    Math.Max(maximum, partial?.MaximumTrialWords ?? 0));
                throw; // Preserve native/cancellation exception identity and unresolved status.
            }
        }
        throw new NativeComponentBudgetExceededException(component, new(maxTrials, words, cells, maximum));
    }
}
