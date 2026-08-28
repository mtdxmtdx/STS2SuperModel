using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public sealed record BattlePlan(
    ImmutableArray<ActionStep> CurrentTurn,
    ImmutableArray<ImmutableArray<ActionStep>> FutureTurns,
    decimal WinProbability,
    decimal DeathProbability,
    decimal ExpectedPlayerHp,
    decimal ExpectedEnemyHp,
    decimal ExpectedHpLoss,
    decimal ExpectedDamage,
    int TurnsEvaluated,
    PredictionConfidence Confidence,
    ImmutableArray<RestrictionReason> Restrictions = default,
    decimal ExpectedBalancedScore = 0m)
{
    public ImmutableArray<ImmutableArray<ActionStep>> SafeFutureTurns =>
        FutureTurns.IsDefault ? ImmutableArray<ImmutableArray<ActionStep>>.Empty : FutureTurns;

    public ImmutableArray<RestrictionReason> SafeRestrictions =>
        Restrictions.IsDefault ? ImmutableArray<RestrictionReason>.Empty : Restrictions;
}

public sealed record BattleSearchResult(
    ObjectiveKind Objective,
    ImmutableArray<BattlePlan> Plans,
    SearchProgress Progress);
