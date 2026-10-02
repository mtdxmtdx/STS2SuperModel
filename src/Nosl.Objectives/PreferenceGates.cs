namespace Nosl.Objectives;

public readonly record struct EstimateInterval
{
    public double Lower { get; }
    public double Upper { get; }
    public EstimateInterval(double lower, double upper)
    {
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper) throw new ArgumentException("Invalid interval");
        Lower = lower; Upper = upper;
    }
    public static EstimateInterval Exact(double value) => new(value, value);
}
public enum Eligibility { EligibleNotMandatory, Ineligible, Unresolved }

public static class PreferenceGates
{
    public static Eligibility Potion(EstimateInterval wholeCombatHpSaved, bool verifiedRescueNeed = false, double referenceHp = 9)
    {
        if (!double.IsFinite(referenceHp) || referenceHp < 0) throw new ArgumentException("Invalid potion reference");
        if (verifiedRescueNeed) return Eligibility.EligibleNotMandatory;
        if (wholeCombatHpSaved.Upper < referenceHp) return Eligibility.Ineligible;
        return wholeCombatHpSaved.Lower >= referenceHp ? Eligibility.EligibleNotMandatory : Eligibility.Unresolved;
    }

    public static Eligibility SpecifiedFinish(EstimateInterval extraExpectedLoss, EstimateInterval unconditionalSuccess,
        bool? safetyAcceptable, double budget = 5, double successFloor = .8)
    {
        if (!double.IsFinite(budget) || budget < 0 || !double.IsFinite(successFloor) || successFloor < 0 || successFloor > 1
            || unconditionalSuccess.Lower < 0 || unconditionalSuccess.Upper > 1) throw new ArgumentException("Invalid bonus thresholds");
        if (safetyAcceptable == false || extraExpectedLoss.Lower > budget || unconditionalSuccess.Upper < successFloor)
            return Eligibility.Ineligible;
        return safetyAcceptable == true && extraExpectedLoss.Upper <= budget && unconditionalSuccess.Lower >= successFloor
            ? Eligibility.EligibleNotMandatory : Eligibility.Unresolved;
    }

    // Healing is already measured by actual settled HP. It has neither Hunt's next-turn
    // deadline nor its five-HP/80% thresholds. Zero real gain is not a reason to stall.
    public static Eligibility SafeFiniteStall(EstimateInterval extraExpectedLoss, bool? safetyAcceptable)
    {
        if (safetyAcceptable == false || extraExpectedLoss.Lower >= 0) return Eligibility.Ineligible;
        return safetyAcceptable == true && extraExpectedLoss.Upper < 0
            ? Eligibility.EligibleNotMandatory : Eligibility.Unresolved;
    }
}

/// <summary>Bounds apply only at the predeclared final independent sample count, never optional stopping.</summary>
public static class FixedSampleEstimates
{
    public static EstimateInterval Mean(IEnumerable<double> samples, int plannedCount, double lowerBound, double upperBound, double alpha = .05)
    {
        var values = samples.ToArray();
        Validate(values.Length, plannedCount, lowerBound, upperBound, alpha);
        if (values.Any(x => !double.IsFinite(x) || x < lowerBound || x > upperBound)) throw new ArgumentException("Sample outside declared support");
        double mean = values.Average();
        double radius = (upperBound - lowerBound) * Math.Sqrt(Math.Log(2 / alpha) / (2 * values.Length));
        return new(Math.Max(lowerBound, mean - radius), Math.Min(upperBound, mean + radius));
    }

    public static EstimateInterval SpecifiedSuccess(IEnumerable<RolloutOutcome> outcomes, int plannedCount, double alpha = .05)
    {
        var rows = outcomes.ToArray();
        Validate(rows.Length, plannedCount, 0, 1, alpha);
        // True losses and observed failed/deadline-missed finishes remain in the denominator.
        var validTerminal = rows.Select(r => r is not null && r.IsTrueTerminal && r.SettlementComplete
            && ObjectiveEvaluator.Evaluate(r).Status != EvaluationStatus.InvalidOutcome).ToArray();
        int success = rows.Where((r, i) => validTerminal[i] && r.TerminalKind == TerminalKind.Win && r.PlayerAlive == true
            && r.SpecifiedFinishSuccess == true && r.EarnedBonus == true && r.DeadlineMet == true).Count();
        int unknown = rows.Where((r, i) => !validTerminal[i] || r.TerminalKind == TerminalKind.Win
            && (r.SpecifiedFinishSuccess is null || r.EarnedBonus is null || r.DeadlineMet is null)).Count();
        // Incomplete worlds may still succeed or fail. Do not replace missing outcomes with losses.
        double radius = Math.Sqrt(Math.Log(2 / alpha) / (2 * rows.Length));
        return new(Math.Max(0, (double)success / rows.Length - radius), Math.Min(1, (double)(success + unknown) / rows.Length + radius));
    }

    private static void Validate(int actual, int planned, double lower, double upper, double alpha)
    {
        if (planned <= 0 || actual != planned) throw new ArgumentException("Use the fixed predeclared final sample count");
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper || !double.IsFinite(alpha) || alpha <= 0 || alpha >= 1)
            throw new ArgumentException("A finite verified support and alpha in (0,1) are required");
    }
}

public enum BonusPlanKind { SpecifiedFinishNextPlayerTurn, SafeFiniteStall }
public enum BonusPlanState { Active, Aborted, Finished }

// Public context contains no counterfactual loss, teacher probabilities, scores or private world identifiers.
public sealed record PublicControllerContext(string AnchorPublicSummary, string Goal, string SelectedPlanTemplate,
    int StartPlayerTurn, int DeadlinePlayerTurn, int LastObservedPlayerTurn, BonusPlanKind Kind, BonusPlanState State,
    IReadOnlyList<string> PublicEvents);
public sealed record WholePlanEvidence(string AnchorPublicSummary, string FrozenBaselinePolicyId, string FrozenPlanTemplate,
    bool CoversWholePlanFromAnchor, EstimateInterval ExtraExpectedLoss, EstimateInterval UnconditionalSuccess, bool? SafetyAcceptable);
public sealed record FiniteStallEvidence(string AnchorPublicSummary, string FrozenBaselinePolicyId, string FrozenPlanTemplate,
    bool CoversWholePlanFromAnchor, EstimateInterval ExtraExpectedLoss, bool? SafetyAcceptable);

/// <summary>Fixed finite templates only. Does not implement arbitrary conditional counterfactual budget allocation.</summary>
public sealed class AnchoredBonusPlan
{
    public PublicControllerContext Context { get; }
    public string FrozenBaselinePolicyId { get; }
    private AnchoredBonusPlan(PublicControllerContext context, string baseline) { Context = context; FrozenBaselinePolicyId = baseline; }

    public static AnchoredBonusPlan Begin(string anchorPublicSummary, string goal, string frozenTemplate, string baselinePolicyId,
        int startPlayerTurn, BonusPlanKind kind = BonusPlanKind.SpecifiedFinishNextPlayerTurn, int? finiteStallDeadline = null)
    {
        if (new[] { anchorPublicSummary, goal, frozenTemplate, baselinePolicyId }.Any(string.IsNullOrWhiteSpace) || startPlayerTurn < 1
            || !Enum.IsDefined(kind)) throw new ArgumentException("A public anchor, frozen baseline and finite template are required");
        if (kind == BonusPlanKind.SpecifiedFinishNextPlayerTurn && finiteStallDeadline is not null)
            throw new ArgumentException("The specified-finish deadline cannot be moved");
        int deadline = kind == BonusPlanKind.SpecifiedFinishNextPlayerTurn ? checked(startPlayerTurn + 1)
            : finiteStallDeadline ?? throw new ArgumentException("Safe stalling requires its own finite deadline");
        if (deadline <= startPlayerTurn) throw new ArgumentException("Invalid finite deadline");
        return new(new(anchorPublicSummary, goal, frozenTemplate, startPlayerTurn, deadline, startPlayerTurn, kind,
            BonusPlanState.Active, Array.AsReadOnly(Array.Empty<string>())), baselinePolicyId);
    }

    public AnchoredBonusPlan Advance(int playerTurn, bool turnEnded, string publicEvent, bool bonusEarned = false, bool abort = false)
    {
        if (Context.State != BonusPlanState.Active) throw new InvalidOperationException("A completed/aborted anchored plan cannot restart");
        if (playerTurn < Context.LastObservedPlayerTurn || string.IsNullOrWhiteSpace(publicEvent)) throw new ArgumentException("Invalid public plan event");
        bool alreadyExpired = playerTurn > Context.DeadlinePlayerTurn;
        var state = alreadyExpired || abort ? BonusPlanState.Aborted : bonusEarned ? BonusPlanState.Finished
            : turnEnded && playerTurn == Context.DeadlinePlayerTurn ? BonusPlanState.Aborted : BonusPlanState.Active;
        return new(Context with { LastObservedPlayerTurn = playerTurn, State = state,
            PublicEvents = Array.AsReadOnly(Context.PublicEvents.Append(publicEvent).ToArray()) }, FrozenBaselinePolicyId);
    }

    public Eligibility Evaluate(WholePlanEvidence evidence)
    {
        if (Context.State != BonusPlanState.Active) return Eligibility.Ineligible;
        if (!evidence.CoversWholePlanFromAnchor || evidence.AnchorPublicSummary != Context.AnchorPublicSummary
            || evidence.FrozenBaselinePolicyId != FrozenBaselinePolicyId || evidence.FrozenPlanTemplate != Context.SelectedPlanTemplate)
            throw new ArgumentException("Evidence must evaluate the unchanged entire plan from its original public anchor and baseline");
        if (Context.Kind != BonusPlanKind.SpecifiedFinishNextPlayerTurn)
            return Eligibility.Unresolved; // Safety/value of finite multi-turn healing needs separate evidence; never impose 5/80 mechanically.
        return PreferenceGates.SpecifiedFinish(evidence.ExtraExpectedLoss, evidence.UnconditionalSuccess, evidence.SafetyAcceptable);
    }

    public Eligibility EvaluateFiniteStall(FiniteStallEvidence evidence)
    {
        if (Context.State != BonusPlanState.Active) return Eligibility.Ineligible;
        if (Context.Kind != BonusPlanKind.SafeFiniteStall || !evidence.CoversWholePlanFromAnchor
            || evidence.AnchorPublicSummary != Context.AnchorPublicSummary
            || evidence.FrozenBaselinePolicyId != FrozenBaselinePolicyId || evidence.FrozenPlanTemplate != Context.SelectedPlanTemplate)
            throw new ArgumentException("Finite stall evidence must cover the unchanged whole plan, anchor and baseline");
        return PreferenceGates.SafeFiniteStall(evidence.ExtraExpectedLoss, evidence.SafetyAcceptable);
    }
}
