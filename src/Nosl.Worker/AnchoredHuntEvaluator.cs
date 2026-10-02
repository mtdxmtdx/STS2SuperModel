using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

public sealed record HuntEvaluationOptions
{
    public ulong[] EvaluationSeeds { get; init; } = [101, 102, 103, 104];
    public int MaxDecisionsPerPolicy { get; init; } = 200;
    public int MaxPosteriorAttempts { get; init; } = 256;
    public int HpSafetyFloor { get; init; } = 10;
    public double FamilywiseAlpha { get; init; } = .05;
    public void Validate()
    {
        if (EvaluationSeeds is null || EvaluationSeeds.Length == 0 || EvaluationSeeds.Distinct().Count() != EvaluationSeeds.Length
            || MaxDecisionsPerPolicy < 1 || MaxPosteriorAttempts < 1 || HpSafetyFloor < 0
            || !double.IsFinite(FamilywiseAlpha) || FamilywiseAlpha <= 0 || FamilywiseAlpha >= 1)
            throw new ArgumentException("Fixed independent evaluation seeds and finite positive budgets required");
    }
}
public sealed record HuntDecisionTrace(int PlayerTurn, int FixedDeadlinePlayerTurn, string Status, string? ExitReason, PublicAction Action);
public sealed record HuntTrajectory(RolloutOutcome Outcome, OutcomeEvaluation Objective, HuntDecisionTrace[] PublicTrace,
    HuntPublicController Controller, int? FatalPlayerTurn, int ActualExtraCardRewardsOffered);
public sealed record HuntPairedWorld(int WorldIndex, HuntTrajectory Baseline, HuntTrajectory Plan);
public sealed record HuntEvaluationAudit(ulong[] IndependentEvaluationSeeds, string SamplingMethod, string DeltaHpSupportCertificate,
    string SpecifiedBenefit, bool FormalLabelsAllowed, int MaxDecisionsPerPolicy, int MaxPosteriorAttempts,
    double FamilywiseAlpha, string EvaluatorVersion, string ObjectiveProfileId);
public sealed record HuntEvaluationMasks(bool BaselineTerminalTargets, bool PlanTerminalTargets,
    bool MeanExtraNetHpLoss, bool MeanSpecifiedSuccess, bool UtilityComparison);
public sealed record HuntEvaluationResult(HuntPlanAnchor Anchor, HuntPairedWorld[] Worlds,
    BatchEvaluation BaselineSummary, BatchEvaluation PlanSummary, double? MeanExtraNetHpLoss,
    EstimateInterval ExtraExpectedLossInterval, EstimateInterval UnconditionalSuccessInterval,
    EstimateInterval ExcessDeathProbabilityInterval, bool? SafetyAcceptable, Eligibility Eligibility,
    bool UtilityComparisonMask, double? MeanSpecifiedSuccess, HuntEvaluationMasks Masks, string[] Limitations, HuntEvaluationAudit Audit);

/// <summary>Opt-in whole-plan evaluation. Does not alter ordinary TeacherDataset schemas or choose a deployed action.</summary>
public static class AnchoredHuntEvaluator
{
    public static async Task<HuntEvaluationResult> EvaluateAsync(CombatSession source, HuntEvaluationOptions? options = null)
    {
        options ??= new(); options.Validate();
        options = options with { EvaluationSeeds = options.EvaluationSeeds.ToArray() };
        var root = source.Observe(); ValidateScope(source, root);
        var anchor = FiniteHuntPolicy.Anchor(root, options.HpSafetyFloor);
        var rows = new List<HuntPairedWorld>();
        for (int i = 0; i < options.EvaluationSeeds.Length; i++)
        {
            if (PublicJson.Serialize(source.Observe()) != anchor.PublicSummary)
                throw new InvalidOperationException("The public anchor changed during whole-plan evaluation");
            CombatSession? world = null;
            try
            {
                world = await BeliefSampler.SampleWorldAsync(source, options.EvaluationSeeds[i], options.MaxPosteriorAttempts);
                // Both copies start from the same hypothetical sampled world, never the live source future.
                var baseline = await RunCopy(world, anchor, false, options);
                var plan = await RunCopy(world, anchor, true, options);
                rows.Add(new(i, baseline, plan));
            }
            catch (Exception error)
            {
                var kind = error is PosteriorSamplingException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError;
                string detail = "belief_sampling:" + error.GetType().Name + ":" + error.Message;
                rows.Add(new(i, Incomplete(source, anchor, kind, [], detail, false),
                    Incomplete(source, anchor, kind, [], detail, true)));
            }
            finally { if (world is not null) await world.DisposeAsync(); }
        }
        if (PublicJson.Serialize(source.Observe()) != anchor.PublicSummary)
            throw new InvalidOperationException("The source changed during whole-plan evaluation");
        var paired = rows.ToArray();
        if (paired.SelectMany(x => new[] { x.Baseline.Outcome, x.Plan.Outcome }).Where(x => x.IsTrueTerminal)
            .Any(x => x.HpAfterSettlement > root.Observation!.Hp || x.MaxHpAfterSettlement != source.StartMaxHp))
            throw new InvalidOperationException("Actual terminal facts contradict the reviewed no-healing/maxHP support");
        var a = paired.Select(x => x.Baseline.Outcome).ToArray(); var b = paired.Select(x => x.Plan.Outcome).ToArray();
        var delta = paired.Select(x => x.Baseline.Outcome.IsTrueTerminal && x.Plan.Outcome.IsTrueTerminal
            ? (double?)(x.Baseline.Outcome.HpAfterSettlement!.Value - x.Plan.Outcome.HpAfterSettlement!.Value) : null).ToArray();
        var death = paired.Select(x => x.Baseline.Outcome.IsTrueTerminal && x.Plan.Outcome.IsTrueTerminal
            ? (double?)((x.Plan.Outcome.PlayerAlive == false ? 1 : 0) - (x.Baseline.Outcome.PlayerAlive == false ? 1 : 0)) : null).ToArray();
        double alpha = options.FamilywiseAlpha / 3;
        // In this reviewed no-healing/no-maxHP-change family, both final HP values lie in [0, public anchor HP].
        var deltaInterval = BoundedMissingMean(delta, -root.Observation!.Hp, root.Observation.Hp, alpha);
        var deathInterval = BoundedMissingMean(death, -1, 1, alpha);
        var successInterval = FixedSampleEstimates.SpecifiedSuccess(b, b.Length, alpha);
        // A deliberately conservative template-specific safeguard, not a universal lexicographic death objective.
        bool? safe = deathInterval.Upper <= 0 ? true : deathInterval.Lower > 0 ? false : null;
        var baselineSummary = ObjectiveEvaluator.EvaluateBatch(a, a.Length);
        var planSummary = ObjectiveEvaluator.EvaluateBatch(b, b.Length);
        bool baselineComplete = baselineSummary.Wins + baselineSummary.Losses == a.Length && baselineSummary.InvalidOutcomes == 0;
        bool planComplete = planSummary.Wins + planSummary.Losses == b.Length && planSummary.InvalidOutcomes == 0;
        bool utilityMask = baselineSummary.ExpectedCost.HasValue && planSummary.ExpectedCost.HasValue;
        return new(anchor, paired, baselineSummary, planSummary,
            delta.All(x => x.HasValue) ? delta.Average(x => x!.Value) : null,
            deltaInterval, successInterval, deathInterval, safe,
            PreferenceGates.SpecifiedFinish(deltaInterval, successInterval, safe),
            utilityMask,
            planComplete ? (double)b.Count(x => x.TerminalKind == TerminalKind.Win && x.PlayerAlive == true
                && x.SpecifiedFinishSuccess == true && x.EarnedBonus == true && x.DeadlineMet == true) / b.Length : null,
            new(baselineComplete, planComplete, baselineComplete && planComplete, planComplete, utilityMask),
            ["Only the explicitly reviewed finite TheHunt template and no-healing mechanics family are supported",
             "Intervals use fixed-N final worlds and a Bonferroni split across three bounds; no optional stopping or root selection guarantee",
             "Unknown outcomes retain probability mass; identical samples are not an exact-support proof",
             "The public HP guard is heuristic; statistical non-increased-death admission can remain unresolved",
             "Offered extra CardReward is recorded, not selected or claimed; its future utility has no invented price",
             "Eligibility means consideration, never a required action; candidate objective is uncalibrated"],
            new(options.EvaluationSeeds.ToArray(), "independent_declared_setup_belief_worlds_paired_policy_execution",
                "finite-hunt-no-healing-v1:delta-loss-in-minus-anchor-hp-to-anchor-hp", "TheHunt fatal plus actual offered extra CardReward by fixed next-player-turn deadline", false,
                options.MaxDecisionsPerPolicy, options.MaxPosteriorAttempts, options.FamilywiseAlpha,
                "nosl-anchored-hunt-evaluator-v1", ObjectiveProfile.Candidate.Id));
    }

    private static void ValidateScope(CombatSession source, DecisionPacket packet)
    {
        if (source.HasNativeProvenance) throw new NotSupportedException("Native carry-in support is not certified for this finite template");
        if (packet.Status != "player_decision" || packet.Observation is not { } o) throw new ArgumentException("Stable public player anchor required");
        string[] cards = ["TheHunt", "StrikeSilent", "DefendSilent", "Neutralize", "Survivor", "AscendersBane", "Acrobatics", "Backflip", "Prepared", "ThinkingAhead", "Slimed"];
        string[] enemies = ["TwigSlimeS", "LeafSlimeS", "Nibbit"];
        var setup = source.InitialScenario;
        var visibleCards = o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(x => x.Card)).Concat(o.KnownDraw.Select(x => x.Card)).ToArray();
        if (setup.Encounter is not null || setup.Enemies is { Length: not 1 } || !enemies.Contains(setup.Enemies?.Single() ?? setup.Enemy)
            || (setup.Deck ?? []).Any(c => !cards.Contains(c.TrimEnd('+'))) || (setup.Deck ?? []).Count(c => c.TrimEnd('+') == "TheHunt") != 1
            || (setup.Potions ?? []).Length != 0 || (setup.Relics ?? []).Any(x => x != "RingOfTheSnake")
            || o.Enemies.Length != 1 || o.Potions.Any(x => x is not null) || o.Relics.Any(x => x != "RingOfTheSnake")
            || o.MaxHp != source.StartMaxHp || o.UnidentifiedDrawCount != 0
            || visibleCards.Any(c => !cards.Contains(c.Id) || c.Enchantments is { Length: > 0 } || c.Affliction is not null)
            || o.Exhaust.Any(c => c.Id == "TheHunt") || o.Powers.Any(p => p.Id is not ("WeakPower" or "FrailPower")))
            throw new NotSupportedException("Outside reviewed finite Hunt/no-healing support; no utility/HP bound is invented");
    }

    private static async Task<HuntTrajectory> RunCopy(CombatSession world, HuntPlanAnchor anchor, bool pursuing, HuntEvaluationOptions options)
    {
        CombatSession? branch = null;
        var trace = new List<HuntDecisionTrace>();
        var policy = new FiniteHuntPolicy(anchor); var baseline = new PublicRulePolicy();
        int? fatalTurn = null;
        try
        {
            branch = await world.ForkForContinuationAsync();
            var packet = branch.Observe();
            int eventCursor = packet.Observation!.History.Length;
            while (packet.Status is "player_decision" or "card_choice" && trace.Count < options.MaxDecisionsPerPolicy)
            {
                int turn = packet.Observation!.Turn;
                var action = pursuing ? policy.Choose(packet) : baseline.Choose(packet);
                trace.Add(new(turn, anchor.DeadlinePlayerTurn, pursuing ? policy.Controller.Status : "baseline",
                    pursuing ? policy.Controller.ExitReason : null, action));
                packet = await branch.StepAsync(action);
                var events = packet.Observation?.History ?? (await branch.SettleAsync()).Events;
                if (events.Skip(eventCursor).Any(IsHuntFatal)) fatalTurn ??= turn;
                eventCursor = events.Length;
            }
            if (packet.Status != "terminal_settled") return Incomplete(branch, anchor,
                packet.Status == "engine_error" ? TerminalKind.EngineError : TerminalKind.ComputeTruncated,
                trace.ToArray(), "Policy decision budget exhausted without true terminal settlement", pursuing, policy.Controller);
            var facts = await branch.SettleAsync();
            int offered = branch.Room.GeneratedRewards.SelectMany(x => x.ExtraRewards).Count(x => x.GetType().Name == "CardReward");
            bool won = facts.Result switch { "win" => true, "loss" => false, _ => throw new InvalidOperationException("Unknown engine terminal kind") };
            var outcome = RolloutRecorder.Settled(branch, facts, pursuing ? anchor.TemplateId : anchor.BaselinePolicyId,
                trace.LastOrDefault()?.PlayerTurn ?? anchor.StartPlayerTurn) with
            {
                SpecifiedFinishSuccess = won && fatalTurn.HasValue,
                EarnedBonus = won && fatalTurn.HasValue && offered > 0,
                DeadlineMet = won && fatalTurn.HasValue && fatalTurn.Value <= anchor.DeadlinePlayerTurn,
            };
            var controller = pursuing ? policy.Controller : new(anchor, "baseline", null, outcome.PlayerTurnsElapsed);
            if (pursuing && controller.Status == "active")
            {
                bool finished = outcome.SpecifiedFinishSuccess == true && outcome.EarnedBonus == true && outcome.DeadlineMet == true;
                controller = finished ? controller with { Status = "finished" }
                    : controller with { Status = "aborted", ExitReason = !won ? "terminal_loss"
                        : fatalTurn.HasValue && outcome.DeadlineMet == false ? "terminal_deadline_missed" : "terminal_without_designated_benefit" };
            }
            return new(outcome, ObjectiveEvaluator.Evaluate(outcome), trace.ToArray(), controller, fatalTurn, offered);
        }
        catch (Exception error)
        {
            return Incomplete(branch ?? world, anchor, TerminalKind.EngineError, trace.ToArray(),
                error.GetType().Name + ":" + error.Message, pursuing, policy.Controller);
        }
        finally { if (branch is not null) await branch.DisposeAsync(); }
    }

    private static bool IsHuntFatal(PublicEvent e)
    {
        if (e.Kind != "power_changed") return false;
        using var doc = JsonDocument.Parse(e.Detail);
        return doc.RootElement.GetProperty("id").GetString() == "TheHuntPower" && doc.RootElement.GetProperty("amount").GetDecimal() > 0;
    }
    private static InventoryQuantity[] Inventory(IEnumerable<string?> items) => items.Where(x => x is not null)
        .GroupBy(x => x!).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray();
    private static HuntTrajectory Incomplete(CombatSession source, HuntPlanAnchor anchor, TerminalKind kind, HuntDecisionTrace[] trace,
        string detail, bool pursuing, HuntPublicController? controller = null)
    {
        var outcome = new RolloutOutcome { TerminalKind = kind, HpAtCombatStart = source.StartHp, MaxHpStart = source.StartMaxHp,
            InventoryStart = Inventory(source.StartPotions), ContinuationPolicyId = pursuing ? anchor.TemplateId : anchor.BaselinePolicyId,
            AtomicActionsExecuted = trace.Length, PlayerTurnsElapsed = trace.LastOrDefault()?.PlayerTurn ?? anchor.StartPlayerTurn, Detail = detail };
        var finalController = pursuing ? controller ?? new(anchor, "unresolved", "evaluation_incomplete", outcome.PlayerTurnsElapsed)
            : new(anchor, "baseline", null, outcome.PlayerTurnsElapsed);
        if (pursuing && finalController.Status == "active")
            finalController = finalController with { Status = "unresolved", ExitReason = "evaluation_incomplete" };
        return new(outcome, ObjectiveEvaluator.Evaluate(outcome), trace, finalController, null, 0);
    }
    private static EstimateInterval BoundedMissingMean(double?[] values, double lower, double upper, double alpha)
    {
        if (values.Any(x => x is double value && (value < lower || value > upper)))
            throw new InvalidOperationException("Observed outcome contradicts reviewed HP/death support");
        double radius = (upper - lower) * Math.Sqrt(Math.Log(2 / alpha) / (2 * values.Length));
        return new(Math.Max(lower, values.Average(x => x ?? lower) - radius), Math.Min(upper, values.Average(x => x ?? upper) + radius));
    }
}
