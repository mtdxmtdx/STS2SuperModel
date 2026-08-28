using System.Collections.Immutable;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Search;

public interface IEnemyIntentForecastProvider
{
    EnemyIntentForecast Forecast(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn);

    ImmutableArray<EnemyIntentForecastOutcome> ForecastOutcomes(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn) =>
        [new EnemyIntentForecastOutcome(
            "确定敌方意图",
            1m,
            Forecast(currentEnemies, futureTurn))];
}

public sealed record EnemyIntentForecast(
    ImmutableArray<CreatureState> Enemies,
    PredictionConfidence Confidence,
    ImmutableArray<RestrictionReason> Restrictions = default)
{
    public ImmutableArray<RestrictionReason> SafeRestrictions =>
        Restrictions.IsDefault ? ImmutableArray<RestrictionReason>.Empty : Restrictions;
}

/// <summary>
/// A probability-labelled future intent forecast.  The probability must come
/// from a verified runtime/RandomForeseer source; this type does not invent a
/// distribution when the source is missing.
/// </summary>
public sealed record EnemyIntentForecastOutcome(
    string Label,
    decimal Probability,
    EnemyIntentForecast Forecast);

/// <summary>
/// Baseline provider used until a runtime or RandomForeseer intent sequence is
/// available. It preserves the captured intents but never labels them reliable
/// for a future turn.
/// </summary>
public sealed class SnapshotEnemyIntentForecastProvider : IEnemyIntentForecastProvider
{
    public EnemyIntentForecast Forecast(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn) => new(
            currentEnemies,
            PredictionConfidence.Estimated,
            [new RestrictionReason(
                "estimated_future_intent",
                $"未来第 {futureTurn} 回合暂使用当前快照中的敌方意图。")]);
}

/// <summary>
/// Uses a sequence captured by a trusted runtime adapter or an offline
/// forecaster. A missing turn deliberately falls back to the snapshot
/// provider instead of inventing an intent.
/// </summary>
public sealed class ScheduledEnemyIntentForecastProvider : IEnemyIntentForecastProvider
{
    private readonly ImmutableDictionary<int, ImmutableArray<CreatureState>> _byTurn;

    public ScheduledEnemyIntentForecastProvider(
        IEnumerable<(int FutureTurn, ImmutableArray<CreatureState> Enemies)> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        _byTurn = schedules
            .Where(static item => item.FutureTurn > 0)
            .GroupBy(static item => item.FutureTurn)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last().Enemies);
    }

    public EnemyIntentForecast Forecast(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn)
    {
        if (_byTurn.TryGetValue(futureTurn, out var enemies))
            return new EnemyIntentForecast(enemies, PredictionConfidence.Reliable);

        return new SnapshotEnemyIntentForecastProvider().Forecast(currentEnemies, futureTurn);
    }
}

/// <summary>
/// Adapter for a verified finite distribution of future enemy intents.  It is
/// deliberately separate from the snapshot fallback so callers can expose
/// exact random intent branches without weakening the existing boundary.
/// </summary>
public sealed class ProbabilisticEnemyIntentForecastProvider : IEnemyIntentForecastProvider
{
    private readonly ImmutableDictionary<int, ImmutableArray<EnemyIntentForecastOutcome>> _byTurn;

    public ProbabilisticEnemyIntentForecastProvider(
        IEnumerable<(int FutureTurn, string Label, decimal Probability, ImmutableArray<CreatureState> Enemies)> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        _byTurn = schedules
            .Where(static item => item.FutureTurn > 0 && item.Probability > 0m)
            .GroupBy(static item => item.FutureTurn)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => Normalize(group.Select(static item => new EnemyIntentForecastOutcome(
                    item.Label,
                    item.Probability,
                    new EnemyIntentForecast(item.Enemies, PredictionConfidence.Reliable))).ToImmutableArray()));
    }

    public EnemyIntentForecast Forecast(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn)
    {
        var outcomes = ForecastOutcomes(currentEnemies, futureTurn);
        return outcomes.Length == 0
            ? new SnapshotEnemyIntentForecastProvider().Forecast(currentEnemies, futureTurn)
            : outcomes.OrderByDescending(static outcome => outcome.Probability).First().Forecast;
    }

    public ImmutableArray<EnemyIntentForecastOutcome> ForecastOutcomes(
        ImmutableArray<CreatureState> currentEnemies,
        int futureTurn)
    {
        if (_byTurn.TryGetValue(futureTurn, out var outcomes)) return outcomes;
        return [new EnemyIntentForecastOutcome(
            "估算敌方意图",
            1m,
            new SnapshotEnemyIntentForecastProvider().Forecast(currentEnemies, futureTurn))];
    }

    private static ImmutableArray<EnemyIntentForecastOutcome> Normalize(
        ImmutableArray<EnemyIntentForecastOutcome> outcomes)
    {
        var mass = outcomes.Sum(static outcome => outcome.Probability);
        if (mass <= 0m) return ImmutableArray<EnemyIntentForecastOutcome>.Empty;
        return outcomes
            .Select(outcome => outcome with { Probability = outcome.Probability / mass })
            .ToImmutableArray();
    }
}
