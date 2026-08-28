using System.Collections.Immutable;
using System.Diagnostics;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Simulation;

namespace STS2BestChoice.Core.Search;

/// <summary>
/// Finite-horizon battle search. The current player turn is expanded by the
/// exact turn search; projected next-turn states are then evaluated recursively.
/// Future enemy intents are currently reused from the captured snapshot and are
/// therefore reported as estimated once a second turn is evaluated.
/// </summary>
public sealed class BattleSearchSession
{
    private readonly CombatSnapshot _rootSnapshot;
    private readonly SearchOptions _options;
    private readonly IEnemyIntentForecastProvider _intentProvider;
    private readonly Dictionary<string, ImmutableArray<BattlePlan>> _cache = new(StringComparer.Ordinal);
    private Stopwatch _clock = null!;
    private TimeSpan _budget;
    private long _expandedNodes;
    private bool _incomplete;

    public BattleSearchSession(
        CombatSnapshot snapshot,
        SearchOptions? options = null,
        IEnemyIntentForecastProvider? intentProvider = null)
    {
        _rootSnapshot = snapshot;
        _options = options ?? SearchOptions.Default;
        _intentProvider = intentProvider ?? new SnapshotEnemyIntentForecastProvider();
        if (_options.MaximumFutureTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumFutureTurns must be positive.");
    }

    public string SnapshotFingerprint => _rootSnapshot.Fingerprint;

    public BattleSearchResult Search(
        ObjectiveKind objective = ObjectiveKind.Balanced,
        TimeSpan? budget = null)
    {
        _cache.Clear();
        _expandedNodes = 0;
        _incomplete = false;
        _budget = budget ?? _options.InitialBudget;
        if (_budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        _clock = Stopwatch.StartNew();

        var plans = SolveState(
            MutableCombatState.FromSnapshot(_rootSnapshot),
            depth: 0,
            objective);
        var ranked = plans
            .OrderByDescending(plan => UtilityKey(plan, objective))
            .Take(_options.TopCount)
            .ToImmutableArray();
        var stopReason = _incomplete || _clock.Elapsed >= _budget
            ? SearchStopReason.BudgetSlice
            : SearchStopReason.FrontierExhausted;
        return new BattleSearchResult(
            objective,
            ranked,
            new SearchProgress(
                _clock.Elapsed,
                _expandedNodes,
                0,
                stopReason == SearchStopReason.FrontierExhausted,
                false,
                _rootSnapshot.Fingerprint)
            {
                StopReason = stopReason
            });
    }

    private ImmutableArray<BattlePlan> SolveState(
        MutableCombatState state,
        int depth,
        ObjectiveKind objective)
    {
        if (depth >= _options.MaximumFutureTurns || IsOutOfBudget())
            return ImmutableArray<BattlePlan>.Empty;

        var cacheKey = $"{state.ExactKey()}|D:{depth}|O:{objective}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var remainingNodes = Math.Max(1, _options.MaximumExpandedNodes - (int)Math.Min(int.MaxValue, _expandedNodes));
        var turnOptions = _options with
        {
            InitialBudget = RemainingBudget(),
            MaximumExpandedNodes = remainingNodes
        };
        var turnSession = new CombatSearchSession(_rootSnapshot, state, turnOptions);
        SearchResults turnResults = default!;
        var previousExpanded = 0L;
        while (!IsOutOfBudget())
        {
            turnResults = turnSession.Continue(RemainingBudget());
            var currentExpanded = turnResults.Progress.ExpandedNodes;
            _expandedNodes += Math.Max(0, currentExpanded - previousExpanded);
            previousExpanded = currentExpanded;
            if (turnResults.Progress.IsComplete ||
                turnResults.Progress.StopReason == SearchStopReason.ExpandedNodeLimit)
                break;
        }

        if (turnResults is null || _expandedNodes == 0)
        {
            _incomplete = true;
            return ImmutableArray<BattlePlan>.Empty;
        }
        if (!turnResults.Progress.IsComplete) _incomplete = true;

        var candidates = turnSession.GetTerminalCandidates()
            .Where(static candidate => candidate.Line.Confidence != PredictionConfidence.Uncalculable)
            .ToArray();
        if (candidates.Length == 0) return ImmutableArray<BattlePlan>.Empty;

        var plans = candidates
            .GroupBy(PolicyKey, StringComparer.Ordinal)
            .Select(group => EvaluatePolicyGroup(group, depth, objective))
            .Where(static plan => plan is not null)
            .Cast<BattlePlan>()
            .ToImmutableArray();
        _cache[cacheKey] = plans;
        return plans;
    }

    private BattlePlan? EvaluatePolicyGroup(
        IEnumerable<TurnTerminalCandidate> group,
        int depth,
        ObjectiveKind objective)
    {
        var outcomes = group
            .GroupBy(ChanceKey, StringComparer.Ordinal)
            .Select(outcome =>
            {
                var alternatives = outcome
                    .Select(candidate => EvaluateCandidate(candidate, depth, objective))
                    .Where(static plan => plan is not null)
                    .Cast<BattlePlan>()
                    .OrderByDescending(plan => UtilityKey(plan, objective))
                    .ToArray();
                var representative = alternatives.FirstOrDefault();
                return representative is null
                    ? null
                    : new OutcomePlan(
                        outcome.First().Probability,
                        representative);
            })
            .Where(static outcome => outcome is not null)
            .Cast<OutcomePlan>()
            .ToArray();

        var mass = outcomes.Sum(static outcome => outcome.Probability);
        if (mass <= 0m) return null;
        var representativePlan = outcomes
            .OrderByDescending(static outcome => outcome.Probability)
            .First()
            .Plan;
        var first = group.First();
        var restrictions = outcomes
            .SelectMany(static outcome => outcome.Plan.SafeRestrictions)
            .Concat(first.Line.Restrictions)
            .Distinct()
            .ToImmutableArray();
        var confidence = outcomes.Max(static outcome => outcome.Plan.Confidence);
        if (first.ChanceRootLength >= 0 && confidence == PredictionConfidence.Reliable)
            confidence = PredictionConfidence.Estimated;

        return representativePlan with
        {
            CurrentTurn = CurrentTurnPrefix(first),
            WinProbability = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.WinProbability) / mass,
            DeathProbability = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.DeathProbability) / mass,
            ExpectedPlayerHp = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.ExpectedPlayerHp) / mass,
            ExpectedEnemyHp = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.ExpectedEnemyHp) / mass,
            ExpectedHpLoss = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.ExpectedHpLoss) / mass,
            ExpectedDamage = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.ExpectedDamage) / mass,
            ExpectedBalancedScore = outcomes.Sum(outcome => outcome.Probability * outcome.Plan.ExpectedBalancedScore) / mass,
            Confidence = confidence,
            Restrictions = restrictions
        };
    }

    private BattlePlan? EvaluateCandidate(
        TurnTerminalCandidate candidate,
        int depth,
        ObjectiveKind objective)
    {
        var immediate = ImmediatePlan(candidate);
        if (candidate.State.Player.Hp <= 0m || candidate.State.Enemies.All(static enemy => !enemy.IsAlive))
            return immediate;
        if (depth + 1 >= _options.MaximumFutureTurns || IsOutOfBudget())
            return immediate;

        var forecastOutcomes = _intentProvider.ForecastOutcomes(
            candidate.State.Enemies.ToImmutableArray(),
            depth + 1);
        var evaluated = new List<ForecastPlan>();
        foreach (var outcome in forecastOutcomes)
        {
            if (outcome.Probability <= 0m) continue;
            var forecast = outcome.Forecast;
            var futureState = candidate.State.Clone();
            ApplyIntentForecast(futureState, forecast.Enemies);
            var future = SolveState(futureState, depth + 1, objective);
            if (future.IsDefaultOrEmpty)
            {
                _incomplete = true;
                var incomplete = immediate with
                {
                    Confidence = PredictionConfidence.Estimated,
                    Restrictions = immediate.SafeRestrictions
                        .Add(new RestrictionReason(
                            "future_search_incomplete",
                            "未来回合搜索未在预算内完成。"))
                        .Concat(forecast.SafeRestrictions)
                        .Distinct()
                        .ToImmutableArray()
                };
                evaluated.Add(new ForecastPlan(outcome.Probability, incomplete));
                continue;
            }

            var best = future
                .OrderByDescending(plan => UtilityKey(plan, objective))
                .First();
            var confidence = (PredictionConfidence)Math.Max(
                (int)best.Confidence,
                (int)forecast.Confidence);
            var restrictions = immediate.SafeRestrictions
                .Concat(best.SafeRestrictions)
                .Concat(forecast.SafeRestrictions)
                .Distinct()
                .ToImmutableArray();
            evaluated.Add(new ForecastPlan(
                outcome.Probability,
                best with
                {
                    Confidence = confidence,
                    Restrictions = restrictions
                }));
        }

        var mass = evaluated.Sum(static branch => branch.Probability);
        if (mass <= 0m) return immediate;
        var representative = evaluated
            .OrderByDescending(static branch => branch.Probability)
            .First()
            .Plan;
        var weightedConfidence = evaluated.Max(static branch => branch.Plan.Confidence);
        var weightedRestrictions = immediate.SafeRestrictions
            .Concat(evaluated.SelectMany(static branch => branch.Plan.SafeRestrictions))
            .Distinct()
            .ToImmutableArray();
        if (mass < 0.999999m)
        {
            weightedConfidence = PredictionConfidence.Estimated;
            weightedRestrictions = weightedRestrictions.Add(new RestrictionReason(
                "incomplete_future_intent_probability",
                $"未来敌方意图概率质量仅覆盖 {mass:P2}。"));
        }

        var futureTurns = ImmutableArray.Create(representative.CurrentTurn)
            .AddRange(representative.SafeFutureTurns);
        return representative with
        {
            CurrentTurn = CurrentTurnPrefix(candidate),
            FutureTurns = futureTurns,
            TurnsEvaluated = representative.TurnsEvaluated + 1,
            WinProbability = Weighted(evaluated, static plan => plan.WinProbability, mass),
            DeathProbability = Weighted(evaluated, static plan => plan.DeathProbability, mass),
            ExpectedPlayerHp = Weighted(evaluated, static plan => plan.ExpectedPlayerHp, mass),
            ExpectedEnemyHp = Weighted(evaluated, static plan => plan.ExpectedEnemyHp, mass),
            ExpectedHpLoss = Weighted(evaluated, static plan => plan.ExpectedHpLoss, mass),
            ExpectedDamage = Weighted(evaluated, static plan => plan.ExpectedDamage, mass),
            ExpectedBalancedScore = Weighted(evaluated, static plan => plan.ExpectedBalancedScore, mass),
            Confidence = weightedConfidence,
            Restrictions = weightedRestrictions
        };
    }

    private BattlePlan ImmediatePlan(TurnTerminalCandidate candidate)
    {
        var score = candidate.Line.Score;
        var playerDied = candidate.State.Player.Hp <= 0m;
        var combatWon = candidate.State.Enemies.All(static enemy => !enemy.IsAlive);
        return new BattlePlan(
            CurrentTurnPrefix(candidate),
            ImmutableArray<ImmutableArray<ActionStep>>.Empty,
            combatWon ? 1m : 0m,
            playerDied ? 1m : 0m,
            candidate.State.Player.Hp,
            candidate.State.Enemies.Sum(static enemy => Math.Max(0m, enemy.Hp)),
            score?.PlayerHpLoss ?? Math.Max(0m, _rootSnapshot.Player.Hp - candidate.State.Player.Hp),
            score?.EffectiveDamage ?? candidate.State.DamageDealt,
            1,
            candidate.Line.Confidence,
            candidate.Line.Restrictions,
            score?.BalancedScore ?? 0m);
    }

    private bool IsOutOfBudget() => _clock.Elapsed >= _budget ||
                                    _expandedNodes >= _options.MaximumExpandedNodes;

    private static void ApplyIntentForecast(
        MutableCombatState state,
        ImmutableArray<CreatureState> forecastEnemies)
    {
        var byId = forecastEnemies.ToDictionary(static enemy => enemy.Id, StringComparer.Ordinal);
        for (var index = 0; index < state.Enemies.Count; index++)
        {
            var current = state.Enemies[index];
            if (byId.TryGetValue(current.Id, out var forecast))
                state.Enemies[index] = current with { Intents = forecast.Intents };
        }
    }

    private TimeSpan RemainingBudget() =>
        IsOutOfBudget() ? TimeSpan.FromMilliseconds(1) : _budget - _clock.Elapsed;

    private static ImmutableArray<ActionStep> CurrentTurnPrefix(TurnTerminalCandidate candidate)
    {
        var steps = candidate.Line.Steps;
        if (candidate.ChanceRootLength >= 0)
            return steps.Take(candidate.ChanceRootLength).ToImmutableArray();
        return steps.TakeWhile(static step => step.Kind != ActionKind.EndTurn).ToImmutableArray();
    }

    private static string PolicyKey(TurnTerminalCandidate candidate)
    {
        var steps = candidate.ChanceRootLength >= 0
            ? candidate.Line.Steps.Take(candidate.ChanceRootLength)
            : candidate.Line.Steps.TakeWhile(static step => step.Kind != ActionKind.EndTurn);
        return string.Join('>', steps.Select(static step =>
            $"{step.Kind}:{step.SourceInstanceId}:{step.TargetId}:{step.ChoiceId}"));
    }

    private static string ChanceKey(TurnTerminalCandidate candidate) =>
        candidate.ChancePath.IsDefaultOrEmpty
            ? "DETERMINISTIC"
            : string.Join('>', candidate.ChancePath);

    private static decimal Weighted(
        IEnumerable<ForecastPlan> plans,
        Func<BattlePlan, decimal> selector,
        decimal mass) => plans.Sum(plan => plan.Probability * selector(plan.Plan)) / mass;

    private static (decimal Win, decimal Safety, decimal Primary, decimal Secondary) UtilityKey(
        BattlePlan plan,
        ObjectiveKind objective) => objective switch
        {
            ObjectiveKind.HighestDamage =>
                (plan.WinProbability, -plan.DeathProbability, plan.ExpectedDamage, -plan.ExpectedHpLoss),
            ObjectiveKind.MinimumLoss =>
                (plan.WinProbability, -plan.DeathProbability, -plan.ExpectedHpLoss, plan.ExpectedDamage),
            _ =>
                (plan.WinProbability, -plan.DeathProbability, plan.ExpectedBalancedScore, plan.ExpectedDamage)
        };

    private sealed record OutcomePlan(decimal Probability, BattlePlan Plan);

    private sealed record ForecastPlan(decimal Probability, BattlePlan Plan);
}
