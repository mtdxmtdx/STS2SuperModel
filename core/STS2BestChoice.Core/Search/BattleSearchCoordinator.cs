using System.Collections.Immutable;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Search;

/// <summary>
/// Runs the existing finite-horizon battle search independently for each
/// objective. Each worker owns its search session and state cache, so the
/// searches can use separate CPU threads without sharing mutable simulation
/// state. The coordinator deliberately does not change the search tree or
/// introduce GPU-specific state.
/// </summary>
public sealed record BattleObjectiveSearchResults(
    ImmutableArray<BattleSearchResult> Results)
{
    public BattleSearchResult? For(ObjectiveKind objective) =>
        Results.FirstOrDefault(result => result.Objective == objective);
}

public static class BattleSearchCoordinator
{
    public static async Task<BattleObjectiveSearchResults> SearchAllObjectivesAsync(
        CombatSnapshot snapshot,
        SearchOptions? options = null,
        TimeSpan? budget = null,
        IEnemyIntentForecastProvider? intentProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var effectiveOptions = options ?? SearchOptions.Default;
        var effectiveBudget = budget ?? effectiveOptions.InitialBudget;
        if (effectiveBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));

        var tasks = Enum.GetValues<ObjectiveKind>()
            .Select(objective => Task.Run(
                () => new BattleSearchSession(snapshot, effectiveOptions, intentProvider)
                    .Search(objective, effectiveBudget),
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new BattleObjectiveSearchResults(results.ToImmutableArray());
    }

    public static BattleObjectiveSearchResults SearchAllObjectives(
        CombatSnapshot snapshot,
        SearchOptions? options = null,
        TimeSpan? budget = null,
        IEnemyIntentForecastProvider? intentProvider = null,
        CancellationToken cancellationToken = default) =>
        SearchAllObjectivesAsync(snapshot, options, budget, intentProvider, cancellationToken)
            .GetAwaiter()
            .GetResult();
}
