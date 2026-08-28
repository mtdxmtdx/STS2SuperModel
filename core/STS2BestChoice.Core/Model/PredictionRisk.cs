using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public enum PredictionRiskReason
{
    MethodNotMirrored,
    MethodMirrorIncomplete,
    UnresolvedPlayerChoice,
    CardDrawLimitExceeded,
    UnsupportedRandomSource,
    StateCaptureIncomplete,
    ChanceBranchSampled,
    ProbabilityMassIncomplete,
    SearchBudgetExceeded
}

public enum PredictionRiskSeverity
{
    Informational,
    Estimated,
    Uncalculable
}

public sealed record RiskEvent(
    PredictionRiskReason Reason,
    PredictionRiskSeverity Severity,
    string Message,
    string? SourceId = null,
    string? Hook = null,
    int ActionIndex = -1);

public sealed record RiskTimeline(ImmutableArray<RiskEvent> Events)
{
    public static RiskTimeline Empty { get; } = new(ImmutableArray<RiskEvent>.Empty);

    public PredictionConfidence Confidence => Events.Any(static risk => risk.Severity == PredictionRiskSeverity.Uncalculable)
        ? PredictionConfidence.Uncalculable
        : Events.Any(static risk => risk.Severity == PredictionRiskSeverity.Estimated)
            ? PredictionConfidence.Estimated
            : PredictionConfidence.Reliable;

    public RiskTimeline Add(RiskEvent risk) => new(Events.Add(risk));

    public RiskTimeline AddRange(IEnumerable<RiskEvent> risks) => new(Events.AddRange(risks));

    public RiskTimeline AtAction(int actionIndex) => new(Events.Select(
        risk => risk.ActionIndex >= 0 ? risk : risk with { ActionIndex = actionIndex }).ToImmutableArray());
}
