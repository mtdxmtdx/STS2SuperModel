using System.Collections.Immutable;
using System.Diagnostics;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Scoring;
using STS2BestChoice.Core.Simulation;

namespace STS2BestChoice.Core.Search;

public sealed class CombatSearchSession
{
    private readonly CombatSnapshot _snapshot;
    private readonly SearchOptions _options;
    private readonly DeterministicSimulator _simulator = new();
    private readonly CombatScorer _scorer;
    private readonly FrontierScheduler _frontier = new();
    private readonly Dictionary<string, StateVisit> _visited = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionLine> _reliable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionLine> _restricted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionLine> _deaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionLine> _infinite = new(StringComparer.Ordinal);
    private readonly List<PolicyCandidate> _policyCandidates = [];
    private readonly bool _captureTerminalStates;
    private readonly List<TurnTerminalCandidate> _terminalCandidates = [];
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private long _nextNodeId;
    private long _expandedNodes;
    private bool _cancelled;
    private SearchStopReason _stopReason = SearchStopReason.BudgetSlice;

    public CombatSearchSession(CombatSnapshot snapshot, SearchOptions? options = null)
        : this(snapshot, MutableCombatState.FromSnapshot(snapshot), options, captureTerminalStates: false)
    {
    }

    internal CombatSearchSession(
        CombatSnapshot snapshot,
        MutableCombatState initialState,
        SearchOptions? options = null)
        : this(snapshot, initialState, options, captureTerminalStates: true)
    {
    }

    private CombatSearchSession(
        CombatSnapshot snapshot,
        MutableCombatState initialState,
        SearchOptions? options,
        bool captureTerminalStates)
    {
        _snapshot = snapshot;
        _options = options ?? SearchOptions.Default;
        _captureTerminalStates = captureTerminalStates;
        if (_options.MaximumExpandedNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumExpandedNodes must be positive.");
        _scorer = new CombatScorer(snapshot, _options);
        var state = initialState.Clone();
        var cycleKey = state.CycleKeyWithoutProgress();
        var ancestors = ImmutableDictionary<string, CycleMark>.Empty.Add(
            cycleKey,
            new CycleMark(state.Enemies.Sum(static enemy => enemy.Hp), state.Player.Hp, 0));
        Enqueue(new SearchNode(
            NextNodeId(),
            state,
            ImmutableArray<ActionStep>.Empty,
            ImmutableArray<StateCheckpoint>.Empty,
            ancestors,
            0m,
            1m,
            -1,
            ImmutableArray<ChanceMark>.Empty));
    }

    public string SnapshotFingerprint => _snapshot.Fingerprint;

    internal ImmutableArray<TurnTerminalCandidate> GetTerminalCandidates()
    {
        if (!_captureTerminalStates)
            throw new InvalidOperationException("Terminal state capture was not enabled for this session.");
        return _terminalCandidates.ToImmutableArray();
    }

    public SearchResults Continue(TimeSpan? budget = null, CancellationToken cancellationToken = default)
    {
        if (_cancelled) return BuildResults(wasCancelled: true);
        var slice = budget ?? _options.InitialBudget;
        if (slice <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        if (_expandedNodes >= _options.MaximumExpandedNodes)
        {
            _stopReason = SearchStopReason.ExpandedNodeLimit;
            return BuildResults(wasCancelled: false);
        }
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < slice &&
               _expandedNodes < _options.MaximumExpandedNodes &&
               _frontier.TryDequeue(out var node))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _cancelled = true;
                _stopReason = SearchStopReason.Cancelled;
                break;
            }
            Expand(node);
        }

        if (_cancelled)
            _stopReason = SearchStopReason.Cancelled;
        else if (_frontier.ActiveCount == 0)
            _stopReason = SearchStopReason.FrontierExhausted;
        else if (_expandedNodes >= _options.MaximumExpandedNodes)
            _stopReason = SearchStopReason.ExpandedNodeLimit;
        else
            _stopReason = SearchStopReason.BudgetSlice;

        return BuildResults(_cancelled);
    }

    public void Cancel() => _cancelled = true;

    private void Expand(SearchNode node)
    {
        _expandedNodes++;
        AddTerminalCandidate(node);
        if (node.Steps.Length >= _options.MaximumActions)
        {
            var restricted = node.State.Clone();
            restricted.Restrictions.Add(new RestrictionReason(
                "uncalculable_action_limit",
                $"Action line exceeded the {_options.MaximumActions}-step safety limit."));
            AddRestricted(node with { State = restricted });
            return;
        }

        foreach (var action in GenerateActions(node.State))
        {
            var steps = node.Steps.Add(action.Step);
            var outcomes = action.Card is not null
                ? _simulator.PlayCardOutcomes(node.State, action.Card, action.TargetId, action.Choice)
                : _simulator.UsePotionOutcomes(node.State, action.Potion!, action.TargetId);
            foreach (var outcome in outcomes)
            {
                var nextState = outcome.State;
                for (var riskIndex = node.State.Risks.Count; riskIndex < nextState.Risks.Count; riskIndex++)
                    nextState.Risks[riskIndex] = nextState.Risks[riskIndex] with { ActionIndex = steps.Length - 1 };
                var checkpoints = node.Checkpoints.Add(CreateCheckpoint(nextState));
                var cycleKey = nextState.CycleKeyWithoutProgress();
                if (TryCreateInfinite(node, nextState, steps, cycleKey, out var infinite))
                {
                    AddInfinite(infinite);
                    continue;
                }

                var chanceRootLength = node.ChanceRootLength;
                var chancePath = node.ChancePath;
                if (outcome.Kind == OutcomeKind.Stochastic || outcomes.Length > 1)
                {
                    if (chanceRootLength < 0) chanceRootLength = steps.Length;
                    chancePath = chancePath.Add(new ChanceMark(outcome.Label, outcome.Probability));
                }
                var exactKey = nextState.ExactKey() + (chancePath.Length == 0
                    ? string.Empty
                    : "|CP:" + string.Join('>', chancePath.Select(static mark => mark.Label)));
                if (_visited.TryGetValue(exactKey, out var visit) &&
                    visit.ActionCount <= steps.Length &&
                    visit.PotionCost <= nextState.PotionCostSpent)
                    continue;
                _visited[exactKey] = new StateVisit(steps.Length, nextState.PotionCostSpent);

                var ancestors = node.Ancestors.SetItem(
                    cycleKey,
                    new CycleMark(nextState.Enemies.Sum(static enemy => enemy.Hp), nextState.Player.Hp, steps.Length));
                Enqueue(new SearchNode(
                    NextNodeId(),
                    nextState,
                    steps,
                    checkpoints,
                    ancestors,
                    node.PriorityHint + action.Priority,
                    node.Probability * outcome.Probability,
                    chanceRootLength,
                    chancePath));
            }
        }
    }

    private void AddTerminalCandidate(SearchNode node)
    {
        var endStep = new ActionStep(ActionKind.EndTurn, "END_TURN", "END_TURN", "End Turn");
        var outcomes = _simulator.ProjectToNextPlayerTurnOutcomes(node.State);
        foreach (var outcome in outcomes)
        {
            var terminalState = outcome.State;
            var steps = node.Steps.Add(endStep);
            var chanceRootLength = node.ChanceRootLength;
            var chancePath = node.ChancePath;
            if (outcome.Kind == OutcomeKind.Stochastic || outcomes.Length > 1)
            {
                if (chanceRootLength < 0) chanceRootLength = node.Steps.Length;
                chancePath = chancePath.Add(new ChanceMark(outcome.Label, outcome.Probability));
            }
            var line = CreateLine(terminalState, steps, node.Checkpoints);
            var probability = node.Probability * outcome.Probability;
            if (_captureTerminalStates)
            {
                _terminalCandidates.Add(new TurnTerminalCandidate(
                    line,
                    terminalState.Clone(),
                    probability,
                    chanceRootLength,
                    chancePath.Select(static mark => mark.Label).ToImmutableArray()));
            }
            if (chanceRootLength >= 0)
            {
                _policyCandidates.Add(new PolicyCandidate(
                    line with { OutcomeKind = OutcomeKind.Stochastic },
                    probability,
                    chanceRootLength,
                    chancePath));
                continue;
            }
            if (line.Confidence != PredictionConfidence.Reliable)
            {
                AddRestrictedLine(line);
                continue;
            }
            if (line.Terminal!.PlayerDied)
            {
                AddBest(_deaths, line.Terminal.StateKey, line);
                continue;
            }
            AddBest(_reliable, line.Terminal.StateKey, line);
        }
    }

    private void AddRestricted(SearchNode node)
    {
        var terminalState = _simulator.ProjectToNextPlayerTurn(node.State);
        AddRestrictedLine(CreateLine(terminalState, node.Steps, node.Checkpoints));
    }

    private ActionLine CreateLine(MutableCombatState terminalState, ImmutableArray<ActionStep> steps, ImmutableArray<StateCheckpoint> checkpoints)
    {
        var confidence = terminalState.Confidence;
        var terminal = _scorer.Terminal(terminalState);
        var score = confidence == PredictionConfidence.Uncalculable ? null : _scorer.Score(terminalState);
        return new ActionLine(
            steps,
            confidence,
            terminalState.Restrictions.Distinct().ToImmutableArray(),
            score,
            terminal,
            Checkpoints: checkpoints)
        {
            RiskTimeline = new RiskTimeline(terminalState.Risks.Distinct().ToImmutableArray())
        };
    }

    private IEnumerable<GeneratedAction> GenerateActions(MutableCombatState state)
    {
        foreach (var card in state.Hand)
        {
            if (!DeterministicSimulator.IsCardPlayableNow(state, card) ||
                DeterministicSimulator.EnergyCostToPlay(state, card) > state.Player.Energy) continue;
            var targets = Targets(card.Target, state);
            var resolvedChoices = BuildDynamicChoices(state, card);
            var choices = resolvedChoices.Length == 0
                ? [null]
                : resolvedChoices.Cast<ChoiceSpec?>();
            foreach (var target in targets)
            foreach (var choice in choices)
            {
                var label = choice is null ? card.Name : $"{card.Name} → {choice.Label}";
                var step = new ActionStep(
                    ActionKind.PlayCard,
                    card.InstanceId,
                    card.ModelId,
                    label,
                    target,
                    choice?.Id);
                var priority = ActionPriority(card.Effects, card.PriorityHint, state, target);
                if (DeterministicSimulator.EnergyCostToPlay(state, card) == 0)
                    priority += 2m;
                yield return new GeneratedAction(
                    step,
                    card,
                    null,
                    target,
                    choice,
                    priority);
            }
        }

        foreach (var potion in state.Potions.Where(static potion => potion.IsUsable))
        {
            foreach (var target in Targets(potion.Target, state))
            {
                yield return new GeneratedAction(
                    new ActionStep(ActionKind.UsePotion, potion.InstanceId, potion.ModelId, potion.Name, target),
                    null,
                    potion,
                    target,
                    null,
                    ActionPriority(potion.Effects, potion.PriorityHint, state, target) - potion.OpportunityCost * 0.1m);
            }
        }
    }

    internal static ImmutableArray<ChoiceSpec> BuildDynamicChoices(MutableCombatState state, CardState card)
    {
        if (!card.Choices.IsDefaultOrEmpty) return card.Choices;

        if (card.Effects.FirstOrDefault(static effect => effect.Kind == EffectKind.CopyChosenHandCard) is { } copy)
            return state.Hand
                .Where(candidate => candidate.InstanceId != card.InstanceId && copy.StatusId switch
                {
                    "COLORLESS" => candidate.IsColorless,
                    "ATTACK_OR_POWER" => candidate.CardType is "Attack" or "攻击" or "Power" or "能力",
                    _ => false
                })
                .DistinctBy(static candidate => candidate.InstanceId, StringComparer.Ordinal)
                .Select(candidate => new ChoiceSpec(
                    candidate.InstanceId,
                    $"复制 {candidate.Name}",
                    ImmutableArray<EffectSpec>.Empty,
                    candidate.InstanceId))
                .ToImmutableArray();

        if (card.Effects.Any(static effect => effect.Kind == EffectKind.ModifySelectedHandCard))
        {
            var mutation = card.Effects.First(static effect => effect.Kind == EffectKind.ModifySelectedHandCard);
            var verb = mutation.StatusId == "ADD_REPLAY_AND_COST"
                ? $"添加重放并增加{mutation.XBonus}点耗能"
                : mutation.StatusId == "ADD_REPLAY" ? "添加重放"
                : mutation.StatusId == "ADD_RETAIN" ? "添加保留" : "添加虚无";
            return state.Hand
                .Where(candidate => candidate.InstanceId != card.InstanceId)
                .Select(candidate => new ChoiceSpec(
                    candidate.InstanceId,
                    $"{verb}：{candidate.Name}",
                    ImmutableArray<EffectSpec>.Empty,
                    candidate.InstanceId))
                .ToImmutableArray();
        }

        if (card.Effects.FirstOrDefault(static effect => effect.Kind == EffectKind.ChooseDiscardToHand) is { } discardChoice)
        {
            var discardCandidates = state.DiscardPile.Where(candidate => discardChoice.StatusId switch
            {
                "ATTACK" => candidate.CardType is "Attack" or "攻击",
                "SKILL" => candidate.CardType is "Skill" or "技能",
                _ => true
            }).ToArray();
            return discardCandidates
                .DistinctBy(static candidate => candidate.InstanceId, StringComparer.Ordinal)
                .Select(candidate => new ChoiceSpec(
                    candidate.InstanceId,
                    $"{candidate.Name} → 加入手牌",
                    ImmutableArray<EffectSpec>.Empty,
                    candidate.InstanceId))
                .ToImmutableArray();
        }

        if (!card.Effects.Any(static effect => effect.Kind == EffectKind.ChooseHandToDrawTop))
            return card.SafeChoices;

        var (candidates, exact) = DeterministicSimulator.PreviewHandChoiceCandidates(state, card);
        return candidates
            .DistinctBy(static candidate => candidate.InstanceId, StringComparer.Ordinal)
            .Select(candidate => new ChoiceSpec(
                candidate.InstanceId,
                $"{candidate.Name} → 放到抽牌堆顶部",
                ImmutableArray<EffectSpec>.Empty,
                candidate.InstanceId,
                exact ? null : "抽牌需要未知洗牌；当前仅列出结算前手牌，未覆盖洗牌后新抽到牌的选择。"))
            .ToImmutableArray();
    }

    private static IEnumerable<string?> Targets(TargetKind targetKind, MutableCombatState state) => targetKind switch
    {
        TargetKind.Enemy => state.Enemies.Where(static enemy => enemy.IsAlive).Select(static enemy => (string?)enemy.Id),
        _ => [null]
    };

    private static decimal ActionPriority(
        ImmutableArray<EffectSpec> effects,
        decimal hint,
        MutableCombatState state,
        string? targetId)
    {
        decimal value = hint;
        foreach (var effect in effects)
        {
            value += effect.Kind switch
            {
                EffectKind.Damage => effect.Amount,
                EffectKind.Draw => effect.Amount * 4m,
                EffectKind.GainEnergy => effect.Amount * 5m,
                EffectKind.Block => effect.Amount * 0.8m,
                EffectKind.ApplyStatus => Math.Abs(effect.FutureValuePerTurn) * 3m + Math.Abs(effect.Amount),
                _ => 0m
            };
        }
        if (targetId is not null)
        {
            var target = state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId);
            var damage = effects.Where(static effect => effect.Kind == EffectKind.Damage).Sum(static effect => effect.Amount);
            if (target is not null && damage >= target.Hp + target.Block) value += 50m;
        }
        return value;
    }

    private bool TryCreateInfinite(
        SearchNode node,
        MutableCombatState next,
        ImmutableArray<ActionStep> steps,
        string cycleKey,
        out ActionLine line)
    {
        line = null!;
        if (next.Confidence != PredictionConfidence.Reliable ||
            !node.Ancestors.TryGetValue(cycleKey, out var mark))
            return false;

        var enemyHp = next.Enemies.Sum(static enemy => enemy.Hp);
        var strictBenefit = enemyHp < mark.EnemyHp || next.Player.Hp > mark.PlayerHp;
        var resourcesNotWorse = next.Player.Hp >= mark.PlayerHp && next.PotionCostSpent == node.State.PotionCostSpent;
        if (!strictBenefit || !resourcesNotWorse) return false;

        var cycle = steps.Skip(mark.StepIndex).ToImmutableArray();
        if (cycle.Length == 0) return false;
        line = new ActionLine(
            steps,
            PredictionConfidence.Reliable,
            ImmutableArray<RestrictionReason>.Empty,
            null,
            null,
            true,
            cycle,
            node.Checkpoints.Add(CreateCheckpoint(next)));
        return true;
    }

    private void AddInfinite(ActionLine line)
    {
        var key = string.Join('>', line.SafeInfiniteCycle.Select(static step =>
            $"{step.Kind}:{step.SourceInstanceId}:{step.TargetId}:{step.ChoiceId}"));
        if (!_infinite.ContainsKey(key)) _infinite.Add(key, line);
    }

    private void AddRestrictedLine(ActionLine line)
    {
        var key = string.Join('>', line.Steps.Select(static step =>
            $"{step.Kind}:{step.SourceInstanceId}:{step.TargetId}:{step.ChoiceId}"));
        if (!_restricted.TryGetValue(key, out var current) || IsBetterRepresentative(line, current))
            _restricted[key] = line;
    }

    private static void AddBest(Dictionary<string, ActionLine> destination, string key, ActionLine line)
    {
        if (!destination.TryGetValue(key, out var current) || IsBetterRepresentative(line, current))
            destination[key] = line;
    }

    private static bool IsBetterRepresentative(ActionLine candidate, ActionLine current)
    {
        var candidateCost = candidate.Score?.PotionOpportunityCost ?? decimal.MaxValue;
        var currentCost = current.Score?.PotionOpportunityCost ?? decimal.MaxValue;
        return candidateCost < currentCost ||
               candidateCost == currentCost && candidate.Steps.Length < current.Steps.Length;
    }

    private void Enqueue(SearchNode node) => _frontier.Enqueue(
        node,
        balancedPriority: -(double)(node.State.DamageDealt + node.PriorityHint - node.State.PotionCostSpent),
        damagePriority: -(double)(node.State.DamageDealt + node.PriorityHint),
        survivalPriority: -(double)(node.State.Player.Hp + node.State.Player.Block + node.PriorityHint * 0.1m));

    private SearchResults BuildResults(bool wasCancelled)
    {
        var top = _options.TopCount;
        var reliable = _reliable.Values;
        var balanced = RankBalanced(reliable, top);
        var damage = RankDamage(reliable, top);
        var loss = RankMinimumLoss(reliable, top);
        var estimated = _restricted.Values.Where(static line =>
            line.Confidence == PredictionConfidence.Estimated &&
            line.Score is not null &&
            line.Terminal is { PlayerDied: false });
        var estimatedBalanced = RankBalanced(estimated, top);
        var estimatedDamage = RankDamage(estimated, top);
        var estimatedLoss = RankMinimumLoss(estimated, top);

        return new SearchResults(
            balanced,
            damage,
            loss,
            estimatedBalanced,
            estimatedDamage,
            estimatedLoss,
            _restricted.Values
                .OrderByDescending(static line => line.Score?.BalancedScore ?? decimal.MinValue)
                .Take(50).ToImmutableArray(),
            _deaths.Values
                .OrderBy(static line => line.Score!.PlayerHpLoss)
                .ThenByDescending(static line => line.Score!.EffectiveDamage)
                .Take(top).ToImmutableArray(),
            _infinite.Values.Take(20).ToImmutableArray(),
            new SearchProgress(
                _lifetime.Elapsed,
                _expandedNodes,
                _frontier.ActiveCount,
                _stopReason == SearchStopReason.FrontierExhausted,
                wasCancelled,
                _snapshot.Fingerprint)
            {
                StopReason = _stopReason
            },
            BuildPolicies(top));
    }

    private ImmutableArray<PolicyLine> BuildPolicies(int top)
    {
        return _policyCandidates
            .Where(static candidate => candidate.Line.Score is not null)
            .GroupBy(static candidate => string.Join('>', candidate.Line.Steps
                .Take(candidate.ChanceRootLength)
                .Select(static step => $"{step.Kind}:{step.SourceInstanceId}:{step.TargetId}:{step.ChoiceId}")), StringComparer.Ordinal)
            .SelectMany(group => Enum.GetValues<ObjectiveKind>().Select(objective =>
            {
                var candidates = group.ToArray();
                var prefixLength = candidates[0].ChanceRootLength;
                var selected = candidates
                    .GroupBy(static candidate => string.Join('>', candidate.ChancePath.Select(static mark => mark.Label)), StringComparer.Ordinal)
                    .Select(outcomes => outcomes
                        .OrderByDescending(candidate => ObjectiveRank(candidate.Line, objective))
                        .ThenBy(static candidate => candidate.Line.Steps.Length)
                        .First())
                    .ToArray();
                var mass = selected.Sum(static candidate => candidate.Probability);
                if (mass <= 0m) mass = 1m;
                var branches = selected.Select(candidate => new PolicyBranch(
                    string.Join(" → ", candidate.ChancePath.Select(static mark => mark.Label)),
                    candidate.Probability,
                    candidate.Line.Steps.Skip(prefixLength).ToImmutableArray(),
                    candidate.Line.Confidence,
                    candidate.Line.RiskTimeline.Events)).ToImmutableArray();
                var risks = selected.SelectMany(static candidate => candidate.Line.RiskTimeline.Events).Distinct().ToImmutableArray();
                if (mass < 0.999999m)
                    risks = risks.Add(new RiskEvent(
                        PredictionRiskReason.ProbabilityMassIncomplete,
                        PredictionRiskSeverity.Estimated,
                        $"时间预算内仅完成 {mass:P2} 的机会分支。"));
                var confidence = selected.Max(static candidate => candidate.Line.Confidence);
                if (mass < 0.999999m && confidence == PredictionConfidence.Reliable)
                    confidence = PredictionConfidence.Estimated;
                return new PolicyLine(
                    selected[0].Line.Steps.Take(prefixLength).ToImmutableArray(),
                    branches,
                    confidence,
                    Distribution(selected, mass, static line => line.Score!.BalancedScore),
                    Distribution(selected, mass, static line => line.Score!.EffectiveDamage),
                    Distribution(selected, mass, static line => line.Score!.PlayerHpLoss),
                    risks,
                    selected[0].Line.SafeCheckpoints.Take(prefixLength).ToImmutableArray(),
                    prefixLength - 1,
                    objective);
            }))
            .GroupBy(static policy => policy.Objective)
            .SelectMany(group => group
                .OrderBy(static policy => PolicyDeathProbability(policy))
                .ThenByDescending(static policy => PolicyObjectiveExpectedValue(policy))
                .Take(top))
            .ToImmutableArray();
    }

    private static (int Outcome, decimal Value) ObjectiveRank(ActionLine line, ObjectiveKind objective) =>
        (OutcomePriority(line), objective switch
    {
        ObjectiveKind.HighestDamage => line.Score!.EffectiveDamage,
        ObjectiveKind.MinimumLoss => -line.Score!.PlayerHpLoss,
        _ => line.Score!.BalancedScore
    });

    private static decimal PolicyDeathProbability(PolicyLine policy) => policy.Objective switch
    {
        ObjectiveKind.HighestDamage => policy.DamageDistribution?.DeathProbability ?? 1m,
        ObjectiveKind.MinimumLoss => policy.LossDistribution?.DeathProbability ?? 1m,
        _ => policy.BalancedDistribution?.DeathProbability ?? 1m
    };

    private static decimal PolicyObjectiveExpectedValue(PolicyLine policy) => policy.Objective switch
    {
        ObjectiveKind.HighestDamage => policy.DamageDistribution?.Expected ?? decimal.MinValue,
        ObjectiveKind.MinimumLoss => -(policy.LossDistribution?.Expected ?? decimal.MaxValue),
        _ => policy.BalancedDistribution?.Expected ?? decimal.MinValue
    };

    private static DistributionSummary Distribution(
        IEnumerable<PolicyCandidate> candidates,
        decimal mass,
        Func<ActionLine, decimal> value)
    {
        var array = candidates.ToArray();
        var expected = array.Sum(candidate => value(candidate.Line) * candidate.Probability) / mass;
        var exact = array.All(static candidate => candidate.Line.RiskTimeline.Events.All(static risk => risk.Reason != PredictionRiskReason.ChanceBranchSampled));
        decimal intervalLow = expected;
        decimal intervalHigh = expected;
        if (!exact && array.Length > 1)
        {
            var variance = array.Sum(candidate =>
            {
                var delta = value(candidate.Line) - expected;
                return delta * delta;
            }) / (array.Length - 1);
            var margin = (decimal)(1.96 * Math.Sqrt((double)(variance / array.Length)));
            intervalLow = expected - margin;
            intervalHigh = expected + margin;
        }
        return new DistributionSummary(
            expected,
            array.Min(candidate => value(candidate.Line)),
            array.Max(candidate => value(candidate.Line)),
            array.Where(static candidate => candidate.Line.Terminal?.PlayerDied == true).Sum(static candidate => candidate.Probability) / mass,
            array.Length,
            exact,
            intervalLow,
            intervalHigh,
            mass);
    }

    private static ImmutableArray<ActionLine> RankBalanced(IEnumerable<ActionLine> lines, int top) => lines
            .OrderByDescending(OutcomePriority)
            .ThenByDescending(static line => line.Score!.BalancedScore)
            .ThenByDescending(static line => line.Score!.EffectiveDamage)
            .ThenBy(static line => line.Score!.PotionOpportunityCost)
            .ThenBy(static line => line.Steps.Length)
            .Take(top).ToImmutableArray();

    private static ImmutableArray<ActionLine> RankDamage(IEnumerable<ActionLine> lines, int top) => lines
            .OrderByDescending(OutcomePriority)
            .ThenByDescending(static line => line.Score!.EffectiveDamage)
            .ThenBy(static line => line.Score!.PlayerHpLoss)
            .ThenBy(static line => line.Score!.PotionOpportunityCost)
            .ThenBy(static line => line.Steps.Length)
            .Take(top).ToImmutableArray();

    private static ImmutableArray<ActionLine> RankMinimumLoss(IEnumerable<ActionLine> lines, int top) => lines
            .OrderByDescending(OutcomePriority)
            .ThenBy(static line => line.Score!.PlayerHpLoss)
            .ThenByDescending(static line => line.Score!.EffectiveDamage)
            .ThenBy(static line => line.Score!.PotionOpportunityCost)
            .ThenBy(static line => line.Steps.Length)
            .Take(top).ToImmutableArray();

    private static int OutcomePriority(ActionLine line) => line.Terminal switch
    {
        { PlayerDied: true } => 0,
        { CombatWon: true } => 2,
        not null => 1,
        _ => 0
    };

    private long NextNodeId() => Interlocked.Increment(ref _nextNodeId);

    private static StateCheckpoint CreateCheckpoint(MutableCombatState state) => new(
        state.Player.Hp,
        state.Player.Block,
        state.Player.Energy,
        state.Enemies.ToImmutableDictionary(static enemy => enemy.Id, static enemy => enemy.Hp),
        state.Enemies.ToImmutableDictionary(static enemy => enemy.Id, static enemy => enemy.Block),
        state.Hand.Select(static card => card.InstanceId).ToImmutableArray(),
        state.Potions.Select(static potion => potion.InstanceId).ToImmutableArray());

    private sealed record SearchNode(
        long Id,
        MutableCombatState State,
        ImmutableArray<ActionStep> Steps,
        ImmutableArray<StateCheckpoint> Checkpoints,
        ImmutableDictionary<string, CycleMark> Ancestors,
        decimal PriorityHint,
        decimal Probability,
        int ChanceRootLength,
        ImmutableArray<ChanceMark> ChancePath);

    private sealed record GeneratedAction(
        ActionStep Step,
        CardState? Card,
        PotionState? Potion,
        string? TargetId,
        ChoiceSpec? Choice,
        decimal Priority);

    private readonly record struct StateVisit(int ActionCount, decimal PotionCost);
    private readonly record struct CycleMark(decimal EnemyHp, decimal PlayerHp, int StepIndex);
    private readonly record struct ChanceMark(string Label, decimal Probability);
    private sealed record PolicyCandidate(ActionLine Line, decimal Probability, int ChanceRootLength, ImmutableArray<ChanceMark> ChancePath);

    private sealed class FrontierScheduler
    {
        private readonly PriorityQueue<SearchNode, double> _balanced = new();
        private readonly PriorityQueue<SearchNode, double> _damage = new();
        private readonly PriorityQueue<SearchNode, double> _survival = new();
        private readonly HashSet<long> _expanded = [];
        private readonly HashSet<long> _active = [];
        private int _turn;

        public int ActiveCount => _active.Count;

        public void Enqueue(SearchNode node, double balancedPriority, double damagePriority, double survivalPriority)
        {
            _balanced.Enqueue(node, balancedPriority);
            _damage.Enqueue(node, damagePriority);
            _survival.Enqueue(node, survivalPriority);
            _active.Add(node.Id);
        }

        public bool TryDequeue(out SearchNode node)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var queue = (_turn++ % 3) switch
                {
                    0 => _balanced,
                    1 => _damage,
                    _ => _survival
                };
                while (queue.TryDequeue(out var candidate, out _))
                {
                    if (!_expanded.Add(candidate.Id)) continue;
                    _active.Remove(candidate.Id);
                    node = candidate;
                    return true;
                }
            }
            node = null!;
            return false;
        }
    }
}

internal sealed record TurnTerminalCandidate(
    ActionLine Line,
    MutableCombatState State,
    decimal Probability,
    int ChanceRootLength,
    ImmutableArray<string> ChancePath);
