using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

public sealed record TeacherOptions
{
    public string Mode { get; init; } = "T0";
    public string ContinuationPolicyId { get; init; } = PublicContinuationPolicies.LegacyId;
    public ulong[] ExplorationSeeds { get; init; } = [];
    public ulong[] EvaluationSeeds { get; init; } = [101, 102, 103, 104];
    public int MaxDecisions { get; init; } = 200;
    public int TreeDepth { get; init; } = 4;
    public int MaxPosteriorAttempts { get; init; } = 256;
    public double UctExploration { get; init; } = 10;
    public bool FormalLabels { get; init; }
    public void Validate()
    {
        _ = PublicContinuationPolicies.Create(ContinuationPolicyId);
        if (Mode is not ("T0" or "T1") || EvaluationSeeds.Length == 0 || MaxDecisions <= 0 || TreeDepth < 0 || MaxPosteriorAttempts <= 0
            || !double.IsFinite(UctExploration) || UctExploration < 0) throw new ArgumentException("Invalid teacher options");
        if (EvaluationSeeds.Distinct().Count() != EvaluationSeeds.Length || ExplorationSeeds.Distinct().Count() != ExplorationSeeds.Length
            || ExplorationSeeds.Intersect(EvaluationSeeds).Any()) throw new ArgumentException("Exploration and independent evaluation worlds must be unique and disjoint");
        if (Mode == "T1" && ExplorationSeeds.Length == 0) throw new ArgumentException("T1 requires separate exploration worlds");
        if (Mode == "T0" && ExplorationSeeds.Length != 0) throw new ArgumentException("T0 has no exploration phase");
    }
}
public sealed record TeacherCandidate(PublicAction Action, RolloutOutcome[] Outcomes, BatchEvaluation Evaluation);
public sealed record TeacherCosts(int RootCandidates, int WorldsAllocated, int WorldsCompleted, long RolloutDecisions,
    double ElapsedSeconds, double CloneSeconds, double SettlementSeconds, long PeakWorkerMemoryBytes, int ExplorationSamplingFailures);
public sealed record TeacherResult(DecisionPacket PublicRoot, string TeacherVersion, string ContinuationVersion,
    string ObjectiveVersion, TeacherCandidate[] Candidates, int ExplorationWorlds, int EvaluationWorlds,
    int FrozenTreeNodes, TeacherCosts Costs, string Scope, string[] Warnings, RankingEvidence Ranking);

/// <summary>Offline private executor. Every policy call crosses the public DTO-only signature.</summary>
public static class CombatTeacher
{
    public const string Version = "nosl-full-combat-teacher-v1";
    public static Task<TeacherResult> EvaluateAsync(CombatSession source, TeacherOptions? options = null,
        ObjectiveProfile? profile = null)
        => EvaluateAsync(new CombatSessionTeacherSource(source), options, profile);

    internal static async Task<TeacherResult> EvaluateAsync(ITeacherSource source, TeacherOptions? options = null,
        ObjectiveProfile? profile = null)
    {
        options ??= new(); profile ??= ObjectiveProfile.Candidate; options.Validate(); profile.Validate();
        if (options.FormalLabels) profile.AssertFormalLabelsAllowed();
        var root = source.Observe();
        if (root.Status is not ("player_decision" or "card_choice")) throw new NotSupportedException("An active public decision is required");
        var timer = Stopwatch.StartNew(); var costs = new CostAccumulator();
        IPublicContinuationPolicy policy = PublicContinuationPolicies.Create(options.ContinuationPolicyId);
        int frozenNodes = 0;
        if (options.Mode == "T1")
        {
            var tree = new PublicTreeSearch(options.UctExploration, options.TreeDepth, policy);
            foreach (var seed in options.ExplorationSeeds)
            {
                var backups = new List<(string Key, int Action, int Visits, double Value)[]>();
                try
                {
                    await using var world = await TimedSample(source, seed, costs, options.MaxPosteriorAttempts);
                    foreach (var action in root.Actions)
                    {
                        var trace = new List<(string Key, int Action)>();
                        RolloutOutcome outcome;
                        await using (var branch = await TimedFork(world, costs))
                            outcome = await RolloutAsync(branch, action, tree, options.MaxDecisions, costs, trace);
                        var evaluation = ObjectiveEvaluator.Evaluate(outcome, profile);
                        // Missing outcomes and unresolved resources never masquerade as zero returns.
                        // Cleanup must succeed before a branch contributes to the public tree.
                        if (evaluation.Cost is double cost) backups.Add(tree.Backup(trace, -cost));
                    }
                }
                catch (Exception)
                {
                    // A failed sampled-world cleanup invalidates this sample's backups too.
                    foreach (var backup in backups.AsEnumerable().Reverse()) tree.UndoBackup(backup);
                    costs.ExplorationFailures++;
                }
            }
            policy = tree.Freeze(); frozenNodes = ((FrozenPublicTreePolicy)policy).NodeCount;
        }
        var outcomes = root.Actions.Select(_ => new List<RolloutOutcome>()).ToArray();
        foreach (var seed in options.EvaluationSeeds)
        {
            var sampleOutcomes = new RolloutOutcome[root.Actions.Length];
            ITeacherWorld? world = null;
            try
            {
                world = await TimedSample(source, seed, costs, options.MaxPosteriorAttempts);
                for (int i = 0; i < root.Actions.Length; i++)
                {
                    ITeacherWorld? branch = null;
                    try
                    {
                        branch = await TimedFork(world, costs);
                        sampleOutcomes[i] = await RolloutAsync(branch, root.Actions[i], policy, options.MaxDecisions, costs);
                    }
                    catch (Exception e) { sampleOutcomes[i] = Incomplete(e is OperationCanceledException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError,
                        source, policy.Id, 0, root.Observation!.Turn, "branch_setup:" + e.Message); }
                    finally
                    {
                        if (branch is not null)
                            try { await branch.DisposeAsync(); }
                            catch (Exception e) { sampleOutcomes[i] = Incomplete(e is OperationCanceledException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError,
                                source, policy.Id, 0, root.Observation!.Turn, "branch_cleanup:" + e.GetType().Name + ": " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                var kind = e is PosteriorSamplingException or OperationCanceledException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError;
                for (int i = 0; i < sampleOutcomes.Length; i++)
                    sampleOutcomes[i] = Incomplete(kind, source, policy.Id, 0, root.Observation!.Turn, "belief_sampling:" + e.Message);
            }
            finally
            {
                if (world is not null)
                    try { await world.DisposeAsync(); }
                    catch (Exception e)
                    {
                        for (int i = 0; i < sampleOutcomes.Length; i++)
                            sampleOutcomes[i] = Incomplete(e is OperationCanceledException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError,
                                source, policy.Id, 0, root.Observation!.Turn,
                                "world_cleanup:" + e.GetType().Name + ": " + e.Message);
                    }
            }
            // Each requested seed contributes exactly one outcome per root action,
            // including failures found only while releasing its owned runtime.
            for (int i = 0; i < sampleOutcomes.Length; i++) outcomes[i].Add(sampleOutcomes[i]);
        }
        var rows = root.Actions.Select((action, i) => new TeacherCandidate(action, outcomes[i].ToArray(),
            ObjectiveEvaluator.EvaluateBatch(outcomes[i], options.EvaluationSeeds.Length, profile))).ToArray();
        timer.Stop();
        var warnings = new List<string> { source.PriorWarning, "Candidate objective is development-only; no formal labels or trained weights" };
        if(costs.ExplorationFailures>0) warnings.Add($"Exploration sampling/branch failures: {costs.ExplorationFailures}; frozen fallback policy remains explicit");
        if (rows.Any(r => r.Outcomes.Any(o => !o.IsTrueTerminal))) warnings.Add("Incomplete probability mass retained; affected target/ranking masks are false");
        if (rows.Any(r => r.Evaluation.ExpectedCost is null)) warnings.Add("Some utility values unresolved; missing resource prices are not zero");
        return new(root, Version + ":" + options.Mode, policy.Id, profile.Id, rows,
            options.ExplorationSeeds.Length, options.EvaluationSeeds.Length, frozenNodes,
            new(root.Actions.Length, rows.Sum(x => x.Outcomes.Length), rows.Sum(x => x.Outcomes.Count(o => o.IsTrueTerminal)),
                costs.Decisions, timer.Elapsed.TotalSeconds, costs.CloneSeconds, costs.SettlementSeconds,
                Process.GetCurrentProcess().PeakWorkingSet64, costs.ExplorationFailures), source.PosteriorProfile, warnings.ToArray(), Ranking(source, rows, profile));
    }
    private static RankingEvidence Ranking(ITeacherSource source, TeacherCandidate[] candidates, ObjectiveProfile profile)
    {
        var bound = source.RankingSupport(profile);
        return TeacherRanking.Evaluate(candidates, profile, bound?.Lower, bound?.Upper);
    }
    private static async Task<ITeacherWorld> TimedSample(ITeacherSource source, ulong seed, CostAccumulator costs, int attempts)
    { var t = Stopwatch.StartNew(); try { return await source.SampleWorldAsync(seed, attempts); } finally { costs.CloneSeconds += t.Elapsed.TotalSeconds; } }
    private static async Task<ITeacherWorld> TimedFork(ITeacherWorld source, CostAccumulator costs)
    { var t = Stopwatch.StartNew(); try { return await source.ForkForContinuationAsync(); } finally { costs.CloneSeconds += t.Elapsed.TotalSeconds; } }
    private sealed class CostAccumulator { public int ExplorationFailures; public long Decisions; public double CloneSeconds; public double SettlementSeconds; }

    private static async Task<RolloutOutcome> RolloutAsync(ITeacherWorld branch, PublicAction rootAction,
        IPublicContinuationPolicy policy, int cap, CostAccumulator costs, List<(string Key, int Action)>? trace = null)
    {
        int actions = 0, lastTurn = branch.Observe().Observation!.Turn;
        try
        {
            var packet = await branch.StepAsync(rootAction); actions++; costs.Decisions++;
            while (packet.Status is "player_decision" or "card_choice" && actions < cap)
            {
                lastTurn = packet.Observation!.Turn;
                var action = policy is PublicTreeSearch search ? search.Select(packet, actions - 1, trace!) : policy.Choose(packet);
                packet = await branch.StepAsync(action); actions++; costs.Decisions++;
            }
            if (packet.Status != "terminal_settled") return Incomplete(packet.Status == "engine_error" ? TerminalKind.EngineError : TerminalKind.ComputeTruncated,
                branch, policy.Id, actions, lastTurn, "Decision budget exhausted before a verified settled endpoint");
            // A terminal-settled packet already certifies settlement. Keep its cost even
            // if recording those settled facts subsequently fails.
            try { return await branch.RecordSettledAsync(policy.Id, lastTurn); }
            finally { costs.SettlementSeconds += branch.SettlementSeconds; }
        }
        catch (Exception e)
        { return Incomplete(e is OperationCanceledException ? TerminalKind.ComputeTruncated : TerminalKind.EngineError,
            branch, policy.Id, actions, lastTurn, e.GetType().Name + ": " + e.Message); }
    }
    private static InventoryQuantity[] Inventory(IEnumerable<string?> items) => items.Where(x => x is not null)
        .GroupBy(x => x!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray();
    private static RolloutOutcome Incomplete(TerminalKind kind, ITeacherContext s, string policy, int actions, int turn, string detail) => new()
    { TerminalKind = kind, HpAtCombatStart = s.StartHp, MaxHpStart = s.StartMaxHp, InventoryStart = Inventory(s.StartPotions),
        PlayerTurnsElapsed = turn, AtomicActionsExecuted = actions, ContinuationPolicyId = policy, Detail = detail, PermanentChangesComplete = false };

    private sealed class PublicTreeSearch(double exploration, int depth, IPublicContinuationPolicy fallback) : IPublicContinuationPolicy
    {
        private sealed class Node(PublicAction[] actions)
        { public PublicAction[] Actions = actions; public int[] Visits = new int[actions.Length]; public double[] Values = new double[actions.Length]; }
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly IPublicContinuationPolicy _fallback = fallback;
        public string Id => _fallback.Id == PublicContinuationPolicies.LegacyId
            ? "nosl-public-uct-exploration-v1" : "nosl-public-uct-exploration-v2-rules-v2";
        public PublicAction Choose(DecisionPacket packet) => throw new InvalidOperationException("Exploration requires public depth and trace");
        public PublicAction Select(DecisionPacket packet, int decisionDepth, List<(string Key, int Action)> trace)
        {
            if (decisionDepth >= depth) return _fallback.Choose(packet);
            var key = FrozenPublicTreePolicy.InformationKey(packet);
            if (!_nodes.TryGetValue(key, out var node)) _nodes[key] = node = new(packet.Actions);
            int selected = Array.FindIndex(node.Visits, n => n == 0);
            if (selected < 0)
            {
                double total = node.Visits.Sum();
                selected = Enumerable.Range(0, node.Actions.Length).OrderByDescending(i => node.Values[i] / node.Visits[i]
                    + exploration * Math.Sqrt(Math.Log(total + 1) / node.Visits[i])).First();
            }
            trace.Add((key, selected)); return node.Actions[selected];
        }
        public (string Key, int Action, int Visits, double Value)[] Backup(List<(string Key, int Action)> trace, double value)
        {
            var previous = trace.Select(x => (x.Key, x.Action, _nodes[x.Key].Visits[x.Action], _nodes[x.Key].Values[x.Action])).ToArray();
            foreach (var (key, action) in trace) { _nodes[key].Visits[action]++; _nodes[key].Values[action] += value; }
            return previous;
        }
        public void UndoBackup((string Key, int Action, int Visits, double Value)[] previous)
        {
            // Restore exact prior values; subtracting floating returns can leave rounding residue.
            foreach (var (key, action, visits, value) in previous) { _nodes[key].Visits[action] = visits; _nodes[key].Values[action] = value; }
        }
        public FrozenPublicTreePolicy Freeze()
        {
            // Never freeze an unvisited action as an estimated zero or as the optimum.
            var choices = _nodes.Where(x => x.Value.Visits.Any(v => v > 0)).ToDictionary(x => x.Key, x =>
            { var n = x.Value; int i = Enumerable.Range(0, n.Actions.Length).Where(i => n.Visits[i] > 0).OrderByDescending(i => n.Values[i] / n.Visits[i]).First(); return n.Actions[i]; }, StringComparer.Ordinal);
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", choices.OrderBy(x => x.Key).Select(x => x.Key + PublicJson.Serialize(x.Value)))))).ToLowerInvariant()[..16];
            string family = _fallback.Id == PublicContinuationPolicies.LegacyId
                ? "nosl-public-uct-frozen-v1" : PublicContinuationPolicies.ReviewedTreeId;
            return new(family + ":" + digest, choices, _fallback);
        }
    }
}
