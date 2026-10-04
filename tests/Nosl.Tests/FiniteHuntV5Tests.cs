using System.Collections.Immutable;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class FiniteHuntV5Tests
{
    private static Scenario Tiny => new(Seed: "fresh-full-v5-hunt-2026-10-03", Deck: ["TheHunt"], EnemyHp: 5);
    private static HuntEvaluationOptions Options => new() { EvaluationSeeds = [7101, 7102], MaxPosteriorAttempts = 16 };

    [Fact]
    public async Task FreshFullV5PairsRetainActualRewardAndOnlyWholePlanLabels()
    {
        var record = await FiniteHuntDatasetV5.GenerateAsync(Tiny, Options,
            "fresh-constructed-hunt-v5", "fresh-constructed-hunt-v5/combat", "fresh-constructed-hunt-v5/paired");
        string json = PublicJson.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var input = root.GetProperty("public_input");
        var context = input.GetProperty("controller_context");
        var anchor = context.GetProperty("anchor");
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, input.GetProperty("schema_version").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, anchor.GetProperty("schema_version").GetString());
        Assert.Equal(input.GetProperty("public_evidence").GetRawText(), anchor.GetProperty("public_evidence").GetRawText());
        Assert.Equal(input.GetProperty("observation").GetRawText(), anchor.GetProperty("observation").GetRawText());
        Assert.Equal(PublicRunContext.ObservationSchema, input.GetProperty("observation").GetProperty("schema").GetString());
        Assert.False(input.GetProperty("observation").GetProperty("runContext").GetProperty("completeFromRunStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("observation").GetProperty("runContext").GetProperty("combatEntryIndex").ValueKind);
        var evidence = PublicRunEvidenceJson.Read(input.GetProperty("public_evidence").GetRawText());
        Assert.False(evidence.CompleteFromRunStart);
        Assert.IsType<PublicEvidenceGap>(evidence.Events[0].Payload);
        Assert.DoesNotContain(evidence.Events, e => e.Payload is PublicMapObserved);
        Assert.True(Assert.IsType<PublicCombatDecision>(evidence.Events[^1].Payload).HistoryCompleteFromCombatStart);
        Assert.DoesNotContain(Tiny.Seed, input.GetRawText());
        var plan = root.GetProperty("targets").GetProperty("plan");
        Assert.Equal(1, plan.GetProperty("specified_success_probability").GetDouble());
        Assert.Equal(0, plan.GetProperty("extra_net_hp_loss").GetDouble());
        Assert.Equal(2, plan.GetProperty("allocated_worlds").GetInt32());
        foreach (var action in root.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.All(action.GetProperty("masks").EnumerateObject(), value => Assert.False(value.Value.GetBoolean()));
            Assert.Equal(0, action.GetProperty("allocated_worlds").GetInt32());
        }
        var audit = root.GetProperty("audit_only");
        Assert.False(audit.GetProperty("native_run").GetBoolean());
        Assert.False(audit.GetProperty("trainable").GetBoolean());
        Assert.Equal(2, audit.GetProperty("sampled_public_roots").GetArrayLength());
        Assert.Equal(4, audit.GetProperty("terminal_public_evidence").GetArrayLength());
        var evaluation = PublicJson.Read<HuntEvaluationResult>(audit.GetProperty("paired_evidence").GetRawText());
        Assert.All(evaluation.Worlds, pair =>
        {
            foreach (var trajectory in new[] { pair.Baseline, pair.Plan })
            {
                Assert.Equal(1, trajectory.ActualExtraCardRewardsOffered);
                Assert.Equal(1, trajectory.FatalPlayerTurn);
                Assert.True(trajectory.Outcome.SettlementComplete);
                Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, trajectory.Objective.Status);
                Assert.Null(trajectory.Objective.Cost);
                Assert.Contains(trajectory.Outcome.PermanentChanges,
                    change => change.Kind == "earned_extra_reward_opportunity:CardReward" && change.Amount == 1);
            }
        });
        foreach (var terminal in audit.GetProperty("terminal_public_evidence").EnumerateArray())
        {
            var events = PublicRunEvidenceJson.Read(terminal.GetProperty("publicEvidence").GetRawText()).Events;
            Assert.Equal(PublicRunEvidenceJson.Serialize(evidence), PublicRunEvidenceJson.Serialize(new(
                PublicRunEvidence.CompleteMapVersion, false, events.Take(evidence.Events.Length).ToImmutableArray())));
            Assert.Contains(events, e => e.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.PowerChanged, Model: "TheHuntPower", Amount: > 0 });
            Assert.IsType<PublicOwnerEnded>(events[^1].Payload);
        }
        await Export("fresh-finite-hunt-v5-success.raw.json", json);
    }

    [Fact]
    public async Task FreshDecisionTruncationRetainsAllUnknownMass()
    {
        var options = Options with { MaxDecisionsPerPolicy = 1 };
        var record = await FiniteHuntDatasetV5.GenerateAsync(Tiny with { Deck = ["TheHunt", "DefendSilent"], EnemyHp = 50 },
            options, "fresh-v5-truncated", "fresh-v5-truncated/combat", "fresh-v5-truncated/paired");
        string json = PublicJson.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        var plan = doc.RootElement.GetProperty("targets").GetProperty("plan");
        Assert.Equal(2, plan.GetProperty("allocated_worlds").GetInt32());
        Assert.Equal(0, plan.GetProperty("success_completed_worlds").GetInt32());
        Assert.Equal(0, plan.GetProperty("paired_completed_worlds").GetInt32());
        Assert.All(plan.GetProperty("masks").EnumerateObject(), value => Assert.False(value.Value.GetBoolean()));
        var evaluation = PublicJson.Read<HuntEvaluationResult>(doc.RootElement.GetProperty("audit_only").GetProperty("paired_evidence").GetRawText());
        Assert.All(evaluation.Worlds, pair =>
        {
            Assert.Equal(TerminalKind.ComputeTruncated, pair.Baseline.Outcome.TerminalKind);
            Assert.Equal(TerminalKind.ComputeTruncated, pair.Plan.Outcome.TerminalKind);
        });
        await Export("fresh-finite-hunt-v5-truncated.raw.json", json);
    }

    [Fact]
    public async Task TrueSettledLossIsKnownFailureRatherThanMissingMass()
    {
        var record = await FiniteHuntDatasetV5.GenerateAsync(Tiny with { Hp = 4, EnemyHp = 50 },
            Options, "fresh-v5-loss", "fresh-v5-loss/combat", "fresh-v5-loss/paired");
        string json = PublicJson.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        var plan = doc.RootElement.GetProperty("targets").GetProperty("plan");
        Assert.Equal(0, plan.GetProperty("specified_success_probability").GetDouble());
        Assert.Equal(0, plan.GetProperty("extra_net_hp_loss").GetDouble());
        Assert.All(plan.GetProperty("masks").EnumerateObject(), value => Assert.True(value.Value.GetBoolean()));
        var evaluation = PublicJson.Read<HuntEvaluationResult>(doc.RootElement.GetProperty("audit_only").GetProperty("paired_evidence").GetRawText());
        Assert.All(evaluation.Worlds, pair =>
        {
            foreach (var trajectory in new[] { pair.Baseline, pair.Plan })
            {
                Assert.Equal(TerminalKind.Loss, trajectory.Outcome.TerminalKind);
                Assert.False(trajectory.Outcome.SpecifiedFinishSuccess);
                Assert.False(trajectory.Outcome.EarnedBonus);
                Assert.False(trajectory.Outcome.DeadlineMet);
                Assert.Equal(EvaluationStatus.Scored, trajectory.Objective.Status);
            }
            Assert.Equal("aborted", pair.Plan.Controller.Status);
            Assert.Equal("public_hp_safety_guard", pair.Plan.Controller.ExitReason);
        });
        await Export("fresh-finite-hunt-v5-loss.raw.json", json);
    }

    [Theory]
    [InlineData("sampling", TerminalKind.ComputeTruncated)]
    [InlineData("sample_public", TerminalKind.EngineError)]
    [InlineData("branch_public", TerminalKind.EngineError)]
    [InlineData("branch_fork", TerminalKind.EngineError)]
    [InlineData("branch_cleanup", TerminalKind.EngineError)]
    [InlineData("world_cleanup", TerminalKind.EngineError)]
    public async Task OwnedFailuresCannotDropDrawsOrLeaveCompletedLabels(string fault, TerminalKind expected)
    {
        await using var source = await CombatSession.CreateAsync(Tiny);
        string before = PublicJson.Serialize(ConstructedHuntV5View.Observe(source));
        var sampled = new HashSet<CombatSession>();
        var execution = new HuntExecution
        {
            Observe = session =>
            {
                var packet = ConstructedHuntV5View.Observe(session);
                bool alter = !ReferenceEquals(session, source) && (fault == "sample_public" && sampled.Contains(session)
                    || fault == "branch_public" && !sampled.Contains(session));
                return alter && packet.Observation is { } observation
                    ? packet with { Observation = observation with { Gold = observation.Gold + 1 } } : packet;
            },
            Sample = async (session, seed, attempts) =>
            {
                if (fault == "sampling") throw new PosteriorSamplingException(attempts);
                var result = await BeliefSampler.SampleWorldAsync(session, seed, attempts);
                sampled.Add(result); return result;
            },
            Fork = session => fault == "branch_fork" ? throw new IOException("injected_owned_fork_failure")
                : session.ForkForContinuationAsync(),
            Dispose = async session =>
            {
                await session.DisposeAsync();
                if (fault == "world_cleanup" && sampled.Contains(session) || fault == "branch_cleanup" && !sampled.Contains(session))
                    throw new IOException("injected_owned_cleanup_failure");
            },
        };
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, Options, execution);
        Assert.Equal(before, PublicJson.Serialize(ConstructedHuntV5View.Observe(source)));
        Assert.Equal(2, result.Worlds.Length);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal(expected, pair.Baseline.Outcome.TerminalKind);
            Assert.Equal(expected, pair.Plan.Outcome.TerminalKind);
            Assert.Null(pair.Plan.Outcome.SpecifiedFinishSuccess);
            Assert.Null(pair.Plan.Outcome.EarnedBonus);
            Assert.Null(pair.Plan.Outcome.DeadlineMet);
            Assert.Equal("unresolved", pair.Plan.Controller.Status);
        });
        Assert.Null(result.MeanSpecifiedSuccess); Assert.Null(result.MeanExtraNetHpLoss);
        Assert.False(result.Masks.MeanSpecifiedSuccess); Assert.False(result.Masks.MeanExtraNetHpLoss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneFailedPolicyKeepsItsPairedSuccessfulPolicyAndIndependentMasks(bool failPlan)
    {
        await using var source = await CombatSession.CreateAsync(Tiny);
        int forks = 0;
        var execution = new HuntExecution
        {
            Observe = ConstructedHuntV5View.Observe,
            Fork = session => ++forks % 2 == (failPlan ? 0 : 1)
                ? throw new IOException("one_policy_fork_failed") : session.ForkForContinuationAsync(),
        };
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, Options, execution);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal(failPlan ? TerminalKind.Win : TerminalKind.EngineError, pair.Baseline.Outcome.TerminalKind);
            Assert.Equal(failPlan ? TerminalKind.EngineError : TerminalKind.Win, pair.Plan.Outcome.TerminalKind);
        });
        Assert.Equal(!failPlan, result.Masks.MeanSpecifiedSuccess);
        Assert.Equal(failPlan ? null : (double?)1, result.MeanSpecifiedSuccess);
        Assert.False(result.Masks.MeanExtraNetHpLoss); Assert.Null(result.MeanExtraNetHpLoss);
    }

    [Fact]
    public async Task LaterPublicDecisionsCannotRenewOrReplaceFullV5Anchor()
    {
        await using var source = await CombatSession.CreateAsync(Tiny with { Deck = ["TheHunt", "DefendSilent"], EnemyHp = 50 });
        var options = Options with { MaxDecisionsPerPolicy = 1 };
        var result = await AnchoredHuntEvaluator.EvaluateAsync(source, options,
            new HuntExecution { Observe = ConstructedHuntV5View.Observe, EvaluatorVersion = FiniteHuntDatasetV5.EvaluatorVersion });
        var policy = new FiniteHuntPolicy(result.Anchor);
        await source.StepAsync(policy.Choose(ConstructedHuntV5View.Observe(source)));
        var current = ConstructedHuntV5View.Observe(source);
        policy.Choose(current);
        var record = FiniteHuntDatasetV5.Record(result, source.InitialScenario, options,
            "later", "later/combat", "later/paired", [], [], current, policy.Controller);
        using var doc = JsonDocument.Parse(PublicJson.Serialize(record));
        var input = doc.RootElement.GetProperty("public_input");
        var plan = doc.RootElement.GetProperty("targets").GetProperty("plan");
        Assert.Equal("unavailable", plan.GetProperty("label_scope").GetString());
        Assert.All(plan.GetProperty("masks").EnumerateObject(), p => Assert.False(p.Value.GetBoolean()));
        Assert.Equal(2, input.GetProperty("controller_context").GetProperty("deadlinePlayerTurn").GetInt32());
        Assert.NotEmpty(input.GetProperty("controller_context").GetProperty("observedEvents").EnumerateArray());
        var changed = current with { Observation = current.Observation! with
            { RunContext = current.Observation.RunContext! with { Floor = current.Observation.RunContext!.Floor + 1 } } };
        Assert.Throws<ArgumentException>(() => ConstructedHuntV5View.HuntInput(result.Anchor, changed, policy.Controller));
    }

    private static async Task Export(string name, string json)
    {
        if (Environment.GetEnvironmentVariable("NOSL_FRESH_HUNT_V5_EXPORT") is not { } directory) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), json + "\n");
    }
}
