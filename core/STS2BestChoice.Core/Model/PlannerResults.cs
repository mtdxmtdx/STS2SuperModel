using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public enum PredictionConfidence
{
    Reliable,
    Estimated,
    Uncalculable
}

public enum SearchStopReason
{
    BudgetSlice,
    FrontierExhausted,
    Cancelled,
    ExpandedNodeLimit
}

public enum ObjectiveKind
{
    Balanced,
    HighestDamage,
    MinimumLoss
}

public enum ActionKind
{
    PlayCard,
    UsePotion,
    Choose,
    EndTurn
}

public sealed record RestrictionReason(string Code, string Message, string? SourceId = null);

public sealed record ActionStep(
    ActionKind Kind,
    string SourceInstanceId,
    string SourceModelId,
    string Label,
    string? TargetId = null,
    string? ChoiceId = null);

public sealed record ScoreBreakdown(
    decimal EffectiveDamage,
    decimal PlayerHpLoss,
    decimal KillAndThreatValue,
    decimal LongTermValue,
    decimal HandAndResourceValue,
    decimal PotionOpportunityCost,
    decimal BalancedScore,
    int EstimatedFutureTurns);

public sealed record ProjectedTerminal(
    decimal PlayerHp,
    decimal PlayerBlock,
    ImmutableArray<CreatureState> Enemies,
    ImmutableArray<CardState> NextHand,
    ImmutableArray<OrbState> NextOrbs,
    bool PlayerDied,
    bool CombatWon,
    string StateKey);

public sealed record StateCheckpoint(
    decimal PlayerHp,
    decimal PlayerBlock,
    int PlayerEnergy,
    ImmutableDictionary<string, decimal> EnemyHp,
    ImmutableDictionary<string, decimal> EnemyBlock,
    ImmutableArray<string> HandInstanceIds,
    ImmutableArray<string> PotionInstanceIds);

public sealed record ActionLine(
    ImmutableArray<ActionStep> Steps,
    PredictionConfidence Confidence,
    ImmutableArray<RestrictionReason> Restrictions,
    ScoreBreakdown? Score,
    ProjectedTerminal? Terminal,
    bool IsInfinite = false,
    ImmutableArray<ActionStep> InfiniteCycle = default,
    ImmutableArray<StateCheckpoint> Checkpoints = default)
{
    public OutcomeKind OutcomeKind { get; init; } = OutcomeKind.Deterministic;
    public RiskTimeline RiskTimeline { get; init; } = RiskTimeline.Empty;

    public ImmutableArray<ActionStep> SafeInfiniteCycle =>
        InfiniteCycle.IsDefault ? ImmutableArray<ActionStep>.Empty : InfiniteCycle;
    public ImmutableArray<StateCheckpoint> SafeCheckpoints =>
        Checkpoints.IsDefault ? ImmutableArray<StateCheckpoint>.Empty : Checkpoints;
}

public sealed record SearchProgress(
    TimeSpan Elapsed,
    long ExpandedNodes,
    int FrontierNodes,
    bool IsComplete,
    bool WasCancelled,
    string SnapshotFingerprint)
{
    public SearchStopReason StopReason { get; init; } = SearchStopReason.BudgetSlice;
}

public sealed record SearchResults(
    ImmutableArray<ActionLine> Balanced,
    ImmutableArray<ActionLine> HighestDamage,
    ImmutableArray<ActionLine> MinimumLoss,
    ImmutableArray<ActionLine> EstimatedBalanced,
    ImmutableArray<ActionLine> EstimatedHighestDamage,
    ImmutableArray<ActionLine> EstimatedMinimumLoss,
    ImmutableArray<ActionLine> Restricted,
    ImmutableArray<ActionLine> ProjectedDeaths,
    ImmutableArray<ActionLine> Infinite,
    SearchProgress Progress,
    ImmutableArray<PolicyLine> Policies = default);

public sealed record PolicyLine(
    ImmutableArray<ActionStep> DeterministicPrefix,
    ImmutableArray<PolicyBranch> Branches,
    PredictionConfidence Confidence,
    DistributionSummary? BalancedDistribution,
    DistributionSummary? DamageDistribution,
    DistributionSummary? LossDistribution,
    ImmutableArray<RiskEvent> Risks = default,
    ImmutableArray<StateCheckpoint> PrefixCheckpoints = default,
    int ChanceBoundaryActionIndex = -1,
    ObjectiveKind Objective = ObjectiveKind.Balanced)
{
    public ImmutableArray<RiskEvent> SafeRisks => Risks.IsDefault ? ImmutableArray<RiskEvent>.Empty : Risks;
    public ImmutableArray<StateCheckpoint> SafePrefixCheckpoints =>
        PrefixCheckpoints.IsDefault ? ImmutableArray<StateCheckpoint>.Empty : PrefixCheckpoints;
}

public sealed record ScoreWeights(
    decimal Damage = 1m,
    decimal Survival = 1m,
    decimal ThreatRemoval = 1m,
    decimal LongTerm = 1m,
    decimal HandAndResources = 1m,
    decimal PotionConservation = 1m);

public sealed record SearchOptions(
    TimeSpan InitialBudget,
    int TopCount,
    ScoreWeights Weights,
    int MaximumFutureTurns = 5,
    int MinimumFutureTurns = 1,
    int MaximumActions = 64)
{
    public int MaximumExpandedNodes { get; init; } = 2_000_000;

    public static SearchOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        5,
        new ScoreWeights());
}
