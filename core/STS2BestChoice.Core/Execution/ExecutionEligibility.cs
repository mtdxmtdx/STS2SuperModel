using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Execution;

public static class ExecutionEligibility
{
    public static bool CanExecute(ActionLine? line) => line is not null &&
        !line.IsInfinite &&
        line.Steps.Any(static step => step.Kind != ActionKind.EndTurn);

    public static bool CanExecute(PolicyLine? policy) =>
        policy is not null && policy.DeterministicPrefix.Any(static step => step.Kind != ActionKind.EndTurn);

    public static bool RequiresStrictCheckpoints(ActionLine line) =>
        line.Confidence == PredictionConfidence.Reliable;

    public static bool RequiresStrictCheckpoints(PolicyLine policy) =>
        policy.Confidence == PredictionConfidence.Reliable &&
        !policy.SafeRisks.Any(risk => risk.ActionIndex >= 0 &&
            risk.ActionIndex <= policy.ChanceBoundaryActionIndex &&
            risk.Severity != PredictionRiskSeverity.Informational);

    // A predicted checkpoint is a diagnostic, not an execution gate. The live
    // adapter revalidates the next action immediately before it is enqueued.
    public static bool ShouldContinueAfterCheckpoint(bool matchesPrediction) => true;
}
