using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class TeacherWorldAdapterTests
{
    [Fact]
    public async Task CombatAdapterUsesExistingPosteriorForkAndSettledRecorder()
    {
        await using var session = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent", "DefendSilent"], EnemyHp: 10));
        var source = new CombatSessionTeacherSource(session);
        string original = PublicJson.Serialize(session.Observe());
        Assert.Equal(session.StartHp, source.StartHp);
        Assert.Equal(session.StartMaxHp, source.StartMaxHp);
        Assert.Equal(session.StartPotions, source.StartPotions);
        Assert.Equal(BeliefSampler.PosteriorProfileFor(session), source.PosteriorProfile);
        Assert.Equal(TeacherRanking.RestrictedSupport(session, ObjectiveProfile.Candidate), source.RankingSupport(ObjectiveProfile.Candidate));
        Assert.Equal("Declared setup prior and runtime capability limits apply; Q under a frozen public continuation, not Q-star", source.PriorWarning);

        await using var adaptedWorld = await source.SampleWorldAsync(71, 256);
        await using var directWorld = await BeliefSampler.SampleWorldAsync(session, 71, 256);
        await using var adaptedBranch = await adaptedWorld.ForkForContinuationAsync();
        await using var directBranch = await directWorld.ForkForContinuationAsync();
        var policy = new PublicRulePolicy();
        var packet = adaptedBranch.Observe();
        int lastTurn = packet.Observation!.Turn, decisions = 0;
        while (packet.Status is "player_decision" or "card_choice" && decisions++ < 100)
        {
            Assert.Equal(PublicJson.Serialize(directBranch.Observe()), PublicJson.Serialize(packet));
            lastTurn = packet.Observation!.Turn;
            var action = policy.Choose(packet);
            packet = await adaptedBranch.StepAsync(action);
            Assert.Equal(PublicJson.Serialize(await directBranch.StepAsync(action)), PublicJson.Serialize(packet));
        }
        Assert.Equal("terminal_settled", packet.Status);
        var expected = RolloutRecorder.Settled(directBranch, await directBranch.SettleAsync(), policy.Id, lastTurn);
        Assert.Equal(PublicJson.Serialize(expected), PublicJson.Serialize(await adaptedBranch.RecordSettledAsync(policy.Id, lastTurn)));
        Assert.True(adaptedBranch.SettlementSeconds >= 0);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Equal(original, PublicJson.Serialize(adaptedWorld.Observe()));
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task IndependentSourceUsesSharedAllActionTeacherAndFrozenPublicContinuation(string mode)
    {
        var source = new TrackedSource();
        var options = new TeacherOptions
        {
            Mode = mode, ExplorationSeeds = mode == "T1" ? [3, 4] : [],
            EvaluationSeeds = [11, 12, 13], MaxPosteriorAttempts = 19, MaxDecisions = 2
        };
        var result = await CombatTeacher.EvaluateAsync(source, options);

        Assert.Equal(options.ExplorationSeeds.Concat(options.EvaluationSeeds), source.Samples.Select(x => x.Seed));
        Assert.All(source.Samples, sample => Assert.Equal(19, sample.Attempts));
        Assert.Equal(source.Root.Actions, result.Candidates.Select(x => x.Action));
        Assert.Equal(source.PosteriorProfile, result.Scope);
        Assert.Equal(source.PriorWarning, result.Warnings[0]);
        Assert.Equal("FIXED_N_PAIRED_HOEFFDING_BONFERRONI", result.Ranking.Method);
        Assert.Single(result.Ranking.Intervals);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(3, candidate.Outcomes.Length);
            Assert.Equal(1, candidate.Evaluation.CompletionRate);
            Assert.NotNull(candidate.Evaluation.ExpectedCost);
            Assert.All(candidate.Outcomes, outcome =>
            {
                Assert.Equal(result.ContinuationVersion, outcome.ContinuationPolicyId);
                Assert.Equal(2, outcome.PlayerTurnsElapsed);
            });
        });
        Assert.All(source.Worlds, world =>
        {
            Assert.Equal(1, world.DisposeCount);
            Assert.Equal(source.Root.Actions.Length, world.Forks.Count);
            Assert.Equal(source.Root.Actions, world.Forks.Select(branch => branch.Actions[0]));
            Assert.All(world.Forks, branch =>
            {
                Assert.Equal(1, branch.DisposeCount);
                Assert.Equal(2, branch.Actions.Count);
                Assert.Equal(1, branch.RecordCount);
            });
        });
        // Hidden seed-dependent outcomes cannot change a frozen choice for the same DTO.
        var evaluationBranches = source.Worlds.Where(w => options.EvaluationSeeds.Contains(w.Seed)).SelectMany(w => w.Forks).ToArray();
        Assert.Single(evaluationBranches.Select(branch => branch.Actions[1]).Distinct());
        Assert.Equal(6, result.Costs.WorldsAllocated);
        Assert.Equal(6, result.Costs.WorldsCompleted);
        Assert.Equal(source.Worlds.Count * 4, result.Costs.RolloutDecisions);
        Assert.Equal(source.Worlds.Count * 2 * .125, result.Costs.SettlementSeconds);
        Assert.Equal(mode == "T1" ? 1 : 0, result.FrozenTreeNodes);
    }

    [Theory]
    [InlineData("posterior", TerminalKind.ComputeTruncated, "belief_sampling:")]
    [InlineData("sampling", TerminalKind.EngineError, "belief_sampling:")]
    [InlineData("sampling_cancel", TerminalKind.ComputeTruncated, "belief_sampling:")]
    [InlineData("fork", TerminalKind.EngineError, "branch_setup:")]
    [InlineData("fork_cancel", TerminalKind.ComputeTruncated, "branch_setup:")]
    [InlineData("step", TerminalKind.EngineError, "InvalidOperationException:")]
    [InlineData("step_cancel", TerminalKind.ComputeTruncated, "OperationCanceledException:")]
    [InlineData("record", TerminalKind.EngineError, "InvalidOperationException:")]
    [InlineData("record_cancel", TerminalKind.ComputeTruncated, "OperationCanceledException:")]
    [InlineData("budget", TerminalKind.ComputeTruncated, "Decision budget exhausted")]
    public async Task AlternateSourceRetainsFailedMassStartAnchorsAndDisposesOwnedWorlds(string failure, TerminalKind kind, string detail)
    {
        var source = new TrackedSource(failure);
        var result = await CombatTeacher.EvaluateAsync(source, new()
        {
            EvaluationSeeds = [11, 12], MaxDecisions = failure == "budget" ? 1 : 2
        });
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(2, candidate.Outcomes.Length);
            Assert.Null(candidate.Evaluation.ExpectedCost);
            Assert.All(candidate.Outcomes, outcome =>
            {
                Assert.Equal(kind, outcome.TerminalKind);
                Assert.StartsWith(detail, outcome.Detail);
                Assert.Equal(source.StartHp, outcome.HpAtCombatStart);
                Assert.Equal(source.StartMaxHp, outcome.MaxHpStart);
                Assert.Equal(new[] { new InventoryQuantity("EnergyPotion", 1), new InventoryQuantity("FruitJuice", 2) }, outcome.InventoryStart);
                Assert.False(outcome.PermanentChangesComplete);
            });
        });
        Assert.Empty(result.Ranking.Pairs);
        Assert.Empty(result.Ranking.Intervals);
        Assert.Contains("Incomplete probability mass retained; affected target/ranking masks are false", result.Warnings);
        Assert.Contains("Some utility values unresolved; missing resource prices are not zero", result.Warnings);
        Assert.Equal(4, result.Costs.WorldsAllocated);
        Assert.Equal(0, result.Costs.WorldsCompleted);
        Assert.All(source.Worlds.Concat(source.Worlds.SelectMany(w => w.Forks)), world => Assert.Equal(1, world.DisposeCount));
        if (failure == "record") Assert.Equal(.5, result.Costs.SettlementSeconds);
    }

    [Fact]
    public async Task FailedExplorationKeepsExplicitFallbackAndIndependentEvaluation()
    {
        var source = new TrackedSource { FailedSamplingSeeds = [3] };
        var result = await CombatTeacher.EvaluateAsync(source, new()
        {
            Mode = "T1", ExplorationSeeds = [3], EvaluationSeeds = [11, 12], MaxDecisions = 2
        });
        Assert.Equal(1, result.Costs.ExplorationSamplingFailures);
        Assert.Equal(0, result.FrozenTreeNodes);
        Assert.Contains("Exploration sampling/branch failures: 1; frozen fallback policy remains explicit", result.Warnings);
        Assert.All(result.Candidates, candidate => Assert.Equal(2, candidate.Evaluation.Wins));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("Incomplete probability mass"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposalFailureCommitsExactlyOneOutcomePerActionAndPreservesOtherSamples(bool sampledWorld, bool canceled)
    {
        var source = new TrackedSource
        {
            ThrowOnDispose = (seed, branch) => seed == 11 && (sampledWorld ? branch is null : branch == 0),
            CancelOnDispose = canceled
        };
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [10, 11, 12], MaxDecisions = 2 });

        Assert.Equal(new ulong[] { 10, 11, 12 }, source.Samples.Select(sample => sample.Seed));
        Assert.Equal(6, result.Costs.WorldsAllocated);
        Assert.Equal(sampledWorld ? 4 : 5, result.Costs.WorldsCompleted);
        for (int i = 0; i < result.Candidates.Length; i++)
        {
            var candidate = result.Candidates[i];
            Assert.Equal(3, candidate.Outcomes.Length);
            Assert.Equal(3, candidate.Evaluation.AssignedWorlds);
            Assert.Equal(TerminalKind.Win, candidate.Outcomes[0].TerminalKind);
            Assert.Equal(TerminalKind.Win, candidate.Outcomes[2].TerminalKind);
            if (sampledWorld || i == 0)
            {
                Assert.Equal(canceled ? TerminalKind.ComputeTruncated : TerminalKind.EngineError, candidate.Outcomes[1].TerminalKind);
                Assert.Equal((sampledWorld ? "world_cleanup:" : "branch_cleanup:")
                    + (canceled ? "OperationCanceledException: cleanup canceled" : "InvalidOperationException: cleanup failed"), candidate.Outcomes[1].Detail);
                Assert.Equal(canceled ? 0 : 1, candidate.Evaluation.EngineErrors);
                Assert.Equal(canceled ? 1 : 0, candidate.Evaluation.ComputeTruncated);
                Assert.Null(candidate.Evaluation.ExpectedCost);
            }
            else Assert.Equal(3, candidate.Evaluation.Wins);
        }
        Assert.Contains("Incomplete probability mass retained; affected target/ranking masks are false", result.Warnings);
        Assert.Empty(result.Ranking.Intervals);
        Assert.All(source.Worlds.Concat(source.Worlds.SelectMany(world => world.Forks)), world => Assert.Equal(1, world.DisposeCount));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailedExplorationCleanupCannotLeaveBackedUpValues(int failedBranch)
    {
        var source = new TrackedSource
        {
            ThrowOnDispose = (seed, branch) => seed == 3 && branch == (failedBranch < 0 ? null : failedBranch)
        };
        var result = await CombatTeacher.EvaluateAsync(source, new()
        {
            Mode = "T1", ExplorationSeeds = [3], EvaluationSeeds = [11, 12], MaxDecisions = 2
        });
        Assert.Equal(1, result.Costs.ExplorationSamplingFailures);
        Assert.Equal(0, result.FrozenTreeNodes);
        Assert.Contains("Exploration sampling/branch failures: 1; frozen fallback policy remains explicit", result.Warnings);
        Assert.All(result.Candidates, candidate => Assert.Equal(2, candidate.Evaluation.Wins));
        Assert.All(source.Worlds.Concat(source.Worlds.SelectMany(world => world.Forks)), world => Assert.Equal(1, world.DisposeCount));
    }

    [Fact]
    public async Task FailedExplorationWorldCleanupPreservesEarlierSuccessfulBackups()
    {
        var options = new TeacherOptions { Mode = "T1", ExplorationSeeds = [2], EvaluationSeeds = [11, 12], MaxDecisions = 2 };
        var baseline = await CombatTeacher.EvaluateAsync(new TrackedSource(), options);
        var source = new TrackedSource { ThrowOnDispose = (seed, branch) => seed == 3 && branch is null };
        var result = await CombatTeacher.EvaluateAsync(source, options with { ExplorationSeeds = [2, 3] });
        Assert.Equal(1, result.Costs.ExplorationSamplingFailures);
        Assert.Equal(baseline.FrozenTreeNodes, result.FrozenTreeNodes);
        Assert.Equal(baseline.ContinuationVersion, result.ContinuationVersion);
        Assert.Equal(PublicJson.Serialize(baseline.Candidates), PublicJson.Serialize(result.Candidates));
    }

    private sealed class TrackedSource(string failure = "") : ITeacherSource
    {
        public int StartHp => 60;
        public int StartMaxHp => 70;
        public string?[] StartPotions => ["FruitJuice", null, "EnergyPotion", "FruitJuice"];
        public string PosteriorProfile => "test-independent-source";
        public string PriorWarning => "Test posterior has its own declared prior";
        public List<(ulong Seed, int Attempts)> Samples { get; } = [];
        public List<TrackedWorld> Worlds { get; } = [];
        public ulong[] FailedSamplingSeeds { get; init; } = [];
        public Func<ulong, int?, bool>? ThrowOnDispose { get; init; }
        public bool CancelOnDispose { get; init; }
        public DecisionPacket Root { get; } = Packet(1, [new(0, "play", 0, 0), new(0, "end_turn")]);
        public DecisionPacket Observe() => Root;
        public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => (-100, 2000);
        public Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
        {
            Samples.Add((seed, maxAttempts));
            if (failure == "posterior" || FailedSamplingSeeds.Contains(seed)) throw new PosteriorSamplingException(maxAttempts);
            if (failure == "sampling") throw new InvalidOperationException("sample failed");
            if (failure == "sampling_cancel") throw new OperationCanceledException("sample budget expired");
            var world = new TrackedWorld(this, seed, failure);
            Worlds.Add(world);
            return Task.FromResult<ITeacherWorld>(world);
        }
    }

    private sealed class TrackedWorld(TrackedSource source, ulong seed, string failure, int? branchIndex = null) : ITeacherWorld
    {
        public int StartHp => source.StartHp;
        public int StartMaxHp => source.StartMaxHp;
        public string?[] StartPotions => source.StartPotions;
        public double SettlementSeconds => .125;
        public ulong Seed => seed;
        public int DisposeCount { get; private set; }
        public int RecordCount { get; private set; }
        public List<TrackedWorld> Forks { get; } = [];
        public List<PublicAction> Actions { get; } = [];
        public DecisionPacket Observe() => Actions.Count == 0 ? source.Observe()
            : Actions.Count == 1 ? Packet(2, [new(1, "play", 0, 0), new(1, "end_turn")])
            : new("terminal_settled", null, []);
        public Task<DecisionPacket> StepAsync(PublicAction action)
        {
            if (failure == "step") throw new InvalidOperationException("step failed");
            if (failure == "step_cancel") throw new OperationCanceledException("step budget expired");
            Assert.Contains(action, Observe().Actions);
            Actions.Add(action);
            return Task.FromResult(Observe());
        }
        public Task<ITeacherWorld> ForkForContinuationAsync()
        {
            if (failure == "fork") throw new InvalidOperationException("fork failed");
            if (failure == "fork_cancel") throw new OperationCanceledException("fork budget expired");
            var branch = new TrackedWorld(source, seed, failure, Forks.Count);
            Forks.Add(branch);
            return Task.FromResult<ITeacherWorld>(branch);
        }
        public Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn)
        {
            RecordCount++;
            Assert.Equal("terminal_settled", Observe().Status);
            if (failure == "record") throw new InvalidOperationException("record failed");
            if (failure == "record_cancel") throw new OperationCanceledException("record budget expired");
            InventoryQuantity[] inventory = [new("EnergyPotion", 1), new("FruitJuice", 2)];
            return Task.FromResult(new RolloutOutcome
            {
                TerminalKind = TerminalKind.Win, PlayerAlive = true, HpAtCombatStart = StartHp,
                HpAfterSettlement = StartHp - (int)(seed % 2) - (Actions[^1].Kind == "play" ? 0 : 10),
                MaxHpStart = StartMaxHp, MaxHpAfterSettlement = StartMaxHp,
                InventoryStart = inventory, InventoryEnd = inventory, InventorySnapshotsComplete = true,
                PermanentChangesComplete = true, SettlementComplete = true, SettlementProfileId = "test-owned-settlement",
                PlayerTurnsElapsed = lastPlayerTurn, AtomicActionsExecuted = Actions.Count, ContinuationPolicyId = policyId
            });
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (source.ThrowOnDispose?.Invoke(seed, branchIndex) == true)
            {
                if (source.CancelOnDispose) throw new OperationCanceledException("cleanup canceled");
                throw new InvalidOperationException("cleanup failed");
            }
            return ValueTask.CompletedTask;
        }
    }

    private static DecisionPacket Packet(int turn, PublicAction[] actions) => new("player_decision",
        new("nosl.public.v2", 60, 10, turn, 60, 70, 0, 3, 0,
            [new("StrikeSilent", 0, 1, 0, "Attack", [])], [], [], [], [], 0, [], [], [], [], [], null), actions);
}
