using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

public sealed record RegenEvaluationOptions
{
    public ulong[] EvaluationSeeds { get; init; } = [101, 102];
    public int MaxDecisionsPerPolicy { get; init; } = 64;
    public int MaxPosteriorAttempts { get; init; } = 256;
    public double FamilywiseAlpha { get; init; } = .05;
    public void Validate()
    {
        if (EvaluationSeeds is null || EvaluationSeeds.Length == 0 || EvaluationSeeds.Distinct().Count() != EvaluationSeeds.Length
            || MaxDecisionsPerPolicy < 1 || MaxPosteriorAttempts < 1
            || !double.IsFinite(FamilywiseAlpha) || FamilywiseAlpha <= 0 || FamilywiseAlpha >= 1)
            throw new ArgumentException("Unique fixed evaluation seeds, finite budgets and alpha in (0,1) required");
    }
}
public sealed record RegenDecisionTrace(int PlayerTurn, int FixedDeadlinePlayerTurn, string Status,
    string? ExitReason, int Hp, int Regen, decimal Block, PublicAction Action);
public sealed record RegenTrajectory(RolloutOutcome Outcome, OutcomeEvaluation Objective,
    RegenDecisionTrace[] PublicTrace, RegenPublicController Controller);
public sealed record RegenPairedWorld(int WorldIndex, RegenTrajectory Baseline, RegenTrajectory Plan);
public sealed record RegenEvaluationAudit(ulong[] IndependentEvaluationSeeds, string SamplingMethod, string EvaluatorVersion,
    string ObjectiveProfileId, bool FormalLabelsAllowed, int MaxDecisionsPerPolicy, int MaxPosteriorAttempts, double FamilywiseAlpha);
public sealed record RegenEvaluationResult(RegenPlanAnchor Anchor, RegenPairedWorld[] Worlds,
    BatchEvaluation BaselineSummary, BatchEvaluation PlanSummary, double? MeanExtraNetHpLoss,
    EstimateInterval ExtraExpectedLossInterval, EstimateInterval ExcessDeathProbabilityInterval,
    bool? SafetyAcceptable, Eligibility Eligibility, bool SettledHpComparisonMask, bool AbsoluteUtilityComparisonMask,
    RegenEvaluationAudit Audit, string[] Limitations);

/// <summary>Fixed whole-plan native replay comparisons. No live hidden world is copied or supplied to the policy.</summary>
public static class AnchoredRegenEvaluator
{
    public static async Task<RegenEvaluationResult> EvaluateAsync(CombatSession source, RegenEvaluationOptions? options = null)
    {
        options ??= new(); options.Validate();
        options = options with { EvaluationSeeds = options.EvaluationSeeds.ToArray() };
        var root = source.Observe(); ValidatePrior(source);
        var anchor = FiniteRegenPolicy.Anchor(root);
        var rows = new List<RegenPairedWorld>();
        for (int i = 0; i < options.EvaluationSeeds.Length; i++)
        {
            if (PublicJson.Serialize(source.Observe()) != anchor.PublicSummary)
                throw new InvalidOperationException("Public anchor changed during whole-plan evaluation");
            CombatSession? world = null;
            try
            {
                world = await BeliefSampler.SampleWorldAsync(source, options.EvaluationSeeds[i], options.MaxPosteriorAttempts);
                var baseline = await RunCopy(world, anchor, false, options);
                var plan = await RunCopy(world, anchor, true, options);
                rows.Add(new(i, baseline, plan));
            }
            catch (Exception error)
            {
                var kind = error is PosteriorSamplingException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError;
                string detail = "belief_sampling:" + error.GetType().Name + ":" + error.Message;
                rows.Add(new(i, Incomplete(source, anchor, kind, [], detail, false), Incomplete(source, anchor, kind, [], detail, true)));
            }
            finally { if (world is not null) await world.DisposeAsync(); }
        }
        if (PublicJson.Serialize(source.Observe()) != anchor.PublicSummary) throw new InvalidOperationException("Source changed");
        var paired = rows.ToArray();
        bool Valid(RolloutOutcome o) => o.IsTrueTerminal && o.SettlementComplete
            && ObjectiveEvaluator.Evaluate(o).Status != EvaluationStatus.InvalidOutcome;
        var delta = paired.Select(x => Valid(x.Baseline.Outcome) && Valid(x.Plan.Outcome)
            ? (double?)(x.Baseline.Outcome.HpAfterSettlement!.Value - x.Plan.Outcome.HpAfterSettlement!.Value) : null).ToArray();
        var death = paired.Select(x => Valid(x.Baseline.Outcome) && Valid(x.Plan.Outcome)
            ? (double?)((x.Plan.Outcome.PlayerAlive == false ? 1 : 0) - (x.Baseline.Outcome.PlayerAlive == false ? 1 : 0)) : null).ToArray();
        // Regen can raise HP above the anchor; the finite verified support is maximum HP,
        // never the no-healing Hunt bound. Missing worlds keep their full interval mass.
        var lossInterval = BoundedMissingMean(delta, -root.Observation!.MaxHp, root.Observation.MaxHp, options.FamilywiseAlpha / 2);
        var deathInterval = BoundedMissingMean(death, -1, 1, options.FamilywiseAlpha / 2);
        bool? safe = deathInterval.Upper <= 0 ? true : deathInterval.Lower > 0 ? false : null;
        var baselineSummary = ObjectiveEvaluator.EvaluateBatch(paired.Select(x => x.Baseline.Outcome), paired.Length);
        var planSummary = ObjectiveEvaluator.EvaluateBatch(paired.Select(x => x.Plan.Outcome), paired.Length);
        var planContract = AnchoredBonusPlan.Begin(anchor.PublicSummary, "finite actual settled HP benefit", anchor.TemplateId,
            anchor.BaselinePolicyId, anchor.StartPlayerTurn, BonusPlanKind.SafeFiniteStall, anchor.DeadlinePlayerTurn);
        return new(anchor, paired, baselineSummary, planSummary, delta.All(x => x.HasValue) ? delta.Average(x => x!.Value) : null,
            lossInterval, deathInterval, safe, planContract.EvaluateFiniteStall(new FiniteStallEvidence(anchor.PublicSummary,
                anchor.BaselinePolicyId, anchor.TemplateId, true, lossInterval, safe)), delta.All(x => x.HasValue),
            baselineSummary.ExpectedCost.HasValue && planSummary.ExpectedCost.HasValue,
            new(options.EvaluationSeeds, "independent_declared_setup_replay_worlds_paired_policy_execution", "nosl-anchored-regen-evaluator-v1",
                ObjectiveProfile.Candidate.Id, false, options.MaxDecisionsPerPolicy, options.MaxPosteriorAttempts, options.FamilywiseAlpha),
            ["Only the reviewed three-card Finesse/Strike, empty-current-inventory, TwigSlimeS five-attack family is executable",
             "Actual engine settlement and original combat-start HP/inventory are retained; prior RegenPotion consumption is never erased",
             "Unpriced consumed inventory can mask absolute utility; strict common-inventory comparison requires a shared continuation and is not asserted here",
             "Fixed-N bounded intervals preserve unknown mass; equal observed samples are not proof of exact expectations or statistical safety",
             "Hunt success and action-value targets are not applicable to this whole-plan comparison; no learned execution or formal training is claimed"]);
    }

    private static void ValidatePrior(CombatSession source)
    {
        var setup = source.InitialScenario;
        if (source.HasNativeProvenance || setup.ForcedEvent is not null || setup.Encounter is not null
            || !(setup.Enemies ?? [setup.Enemy]).SequenceEqual(new[] { "TwigSlimeS" })
            || !(setup.Deck ?? []).Order().SequenceEqual(new[] { "Finesse", "Finesse", "StrikeSilent" }.Order())
            || (setup.Potions ?? []).Any(p => p != "RegenPotion") || (setup.Potions ?? []).Length > 1
            || (setup.Relics ?? ["RingOfTheSnake"]).Any(r => r != "RingOfTheSnake")
            || source.Observe().Observation!.MaxHp != source.StartMaxHp)
            throw new NotSupportedException("Outside reviewed constructed finite Regen replay prior");
    }

    private static async Task<RegenTrajectory> RunCopy(CombatSession world, RegenPlanAnchor anchor, bool pursuing, RegenEvaluationOptions options)
    {
        CombatSession? branch = null; var trace = new List<RegenDecisionTrace>();
        var policy = new FiniteRegenPolicy(anchor); var baseline = new PublicRulePolicy();
        try
        {
            branch = await world.ForkForContinuationAsync();
            var packet = branch.Observe();
            while (packet.Status is "player_decision" or "card_choice" && trace.Count < options.MaxDecisionsPerPolicy)
            {
                var o = packet.Observation!; var action = pursuing ? policy.Choose(packet) : baseline.Choose(packet);
                trace.Add(new(o.Turn, anchor.DeadlinePlayerTurn, pursuing ? policy.Controller.Status : "baseline",
                    pursuing ? policy.Controller.ExitReason : null, o.Hp, FiniteRegenPolicy.Regen(o), o.Block, action));
                packet = await branch.StepAsync(action);
            }
            if (packet.Status != "terminal_settled") return Incomplete(branch, anchor,
                packet.Status == "engine_error" ? TerminalKind.EngineError : TerminalKind.ComputeTruncated, trace.ToArray(),
                "Policy decision budget exhausted without true terminal settlement", pursuing, policy.Controller);
            var facts = await branch.SettleAsync();
            var outcome = RolloutRecorder.Settled(branch, facts, pursuing ? anchor.TemplateId : anchor.BaselinePolicyId,
                trace.LastOrDefault()?.PlayerTurn ?? anchor.StartPlayerTurn);
            if (facts.FinalMaxHp != facts.StartMaxHp || facts.FinalHp > facts.StartMaxHp)
                throw new InvalidOperationException("Settled facts contradict reviewed max-HP support");
            var controller = pursuing ? policy.Controller : new(anchor, "baseline", null, outcome.PlayerTurnsElapsed);
            if (pursuing && controller.Status == "active") controller = controller with { Status = "aborted",
                ExitReason = facts.Result == "loss" ? "terminal_loss" : "terminal_before_benefit_exit" };
            return new(outcome, ObjectiveEvaluator.Evaluate(outcome), trace.ToArray(), controller);
        }
        catch (Exception error)
        {
            return Incomplete(branch ?? world, anchor, TerminalKind.EngineError, trace.ToArray(),
                error.GetType().Name + ":" + error.Message, pursuing, policy.Controller);
        }
        finally { if (branch is not null) await branch.DisposeAsync(); }
    }

    private static RegenTrajectory Incomplete(CombatSession source, RegenPlanAnchor anchor, TerminalKind kind,
        RegenDecisionTrace[] trace, string detail, bool pursuing, RegenPublicController? controller = null)
    {
        var outcome = new RolloutOutcome { TerminalKind = kind, HpAtCombatStart = source.StartHp, MaxHpStart = source.StartMaxHp,
            InventoryStart = source.StartPotions.Where(x => x is not null).GroupBy(x => x!).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray(),
            ContinuationPolicyId = pursuing ? anchor.TemplateId : anchor.BaselinePolicyId, AtomicActionsExecuted = trace.Length,
            PlayerTurnsElapsed = trace.LastOrDefault()?.PlayerTurn ?? anchor.StartPlayerTurn, Detail = detail };
        var final = pursuing ? controller ?? new(anchor, "unresolved", "evaluation_incomplete", outcome.PlayerTurnsElapsed)
            : new(anchor, "baseline", null, outcome.PlayerTurnsElapsed);
        if (pursuing && final.Status == "active") final = final with { Status = "unresolved", ExitReason = "evaluation_incomplete" };
        return new(outcome, ObjectiveEvaluator.Evaluate(outcome), trace, final);
    }

    private static EstimateInterval BoundedMissingMean(double?[] values, double lower, double upper, double alpha)
    {
        if (values.Any(x => x is double v && (v < lower || v > upper))) throw new InvalidOperationException("Outside verified support");
        double radius = (upper - lower) * Math.Sqrt(Math.Log(2 / alpha) / (2 * values.Length));
        return new(Math.Max(lower, values.Average(x => x ?? lower) - radius), Math.Min(upper, values.Average(x => x ?? upper) + radius));
    }
}
