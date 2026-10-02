using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class FiniteHuntDatasetTests
{
    [Fact]
    public async Task RealPairedEvidenceExportsOnlyWholePlanLabelsAndPublicAnchor()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], EnemyHp: 5));
        var evaluation = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [11, 12] });
        string json = PublicJson.Serialize(TeacherDataset.RecordFiniteHunt(evaluation, "hunt-test", "hunt-test/combat", "hunt-test/family"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("nosl.student.public.v2", root.GetProperty("public_input").GetProperty("schema_version").GetString());
        var context = root.GetProperty("public_input").GetProperty("controller_context");
        Assert.Equal("active", context.GetProperty("status").GetString());
        Assert.Equal(1, context.GetProperty("startPlayerTurn").GetInt32());
        Assert.Equal(2, context.GetProperty("deadlinePlayerTurn").GetInt32());
        Assert.Empty(context.GetProperty("observedEvents").EnumerateArray());
        foreach (string forbidden in new[] { "evaluationSeeds", "eligibility", "extraExpectedLossInterval", "worldIndex", "meanSpecifiedSuccess" })
            Assert.DoesNotContain(forbidden, root.GetProperty("public_input").GetRawText());
        var plan = root.GetProperty("targets").GetProperty("plan");
        Assert.Equal(1, plan.GetProperty("specified_success_probability").GetDouble());
        Assert.Equal(0, plan.GetProperty("extra_net_hp_loss").GetDouble());
        Assert.Equal(2, plan.GetProperty("allocated_worlds").GetInt32());
        foreach (var action in root.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.All(action.GetProperty("masks").EnumerateObject(), p => Assert.False(p.Value.GetBoolean()));
            foreach (string count in new[] { "allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds" })
                Assert.Equal(0, action.GetProperty(count).GetInt32());
        }
        var audit = root.GetProperty("audit_only");
        Assert.False(audit.GetProperty("candidate_evaluation_performed").GetBoolean());
        Assert.True(audit.GetProperty("independent_final_evaluation").GetBoolean());
        Assert.Equal(2, audit.GetProperty("n_independent_eval").GetInt32());
        Assert.Equal(new ulong[] { 11, 12 }, audit.GetProperty("sampler_seeds").EnumerateArray().Select(x => x.GetUInt64()).ToArray());
        Assert.Empty(audit.GetProperty("exploration_seeds").EnumerateArray());
        Assert.Equal(0, audit.GetProperty("n_exploration").GetInt32());
        Assert.Equal(0, audit.GetProperty("n_error").GetInt32());
        Assert.Equal(0, audit.GetProperty("n_unresolved").GetInt32());
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("baseline"), 2, 2, 0, 0, 0);
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("plan"), 2, 2, 0, 0, 0);
        var costs = audit.GetProperty("costs");
        Assert.Equal("policy_world_execution", costs.GetProperty("cost_unit").GetString());
        Assert.Equal(4, costs.GetProperty("worlds_allocated").GetInt32());
        Assert.Equal(4, costs.GetProperty("worlds_completed").GetInt32());
        Assert.Equal(4, costs.GetProperty("rollout_decisions").GetInt32());
        Assert.Equal(JsonValueKind.Null, costs.GetProperty("elapsed_seconds").ValueKind);
        var versions = audit.GetProperty("versions");
        foreach (var (version, field) in new[] { ("simulator", "simulator_commit"), ("rules", "rules_version"),
            ("endpoint", "label_endpoint"), ("teacher", "teacher_version"), ("objective", "objective_version"),
            ("controller", "controller_version"), ("sampler", "sampler_version"), ("baseline", "baseline_version") })
        {
            Assert.False(string.IsNullOrWhiteSpace(audit.GetProperty(field).GetString()));
            Assert.Equal(audit.GetProperty(field).GetString(), versions.GetProperty(version).GetString());
        }
        Assert.Equal(evaluation.Anchor.TemplateId, audit.GetProperty("continuation_version").GetString());
        Assert.Equal(FiniteHuntPolicy.Version, versions.GetProperty("continuation").GetString());
        Assert.Equal(FiniteHuntDataset.Version, versions.GetProperty("dataset").GetString());
        Assert.Equal(root.GetProperty("public_input").GetProperty("schema_version").GetString(), versions.GetProperty("public_schema").GetString());
        Assert.Equal(source.Observe().Observation!.Schema, versions.GetProperty("observation_schema").GetString());
        string? export = Environment.GetEnvironmentVariable("NOSL_HUNT_FIXTURE_PATH");
        if (export is not null)
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "finite-hunt-record-v2.jsonl"), json + "\n");
            await source.StepAsync(source.Observe().Actions.First(x => x.Kind == "play"));
            var settled = await source.SettleAsync();
            var terminal = new { decision_status = "terminal", terminal_public = new { result = settled.Result,
                events = settled.Events, extra_card_rewards_offered = source.Room.GeneratedRewards.SelectMany(x => x.ExtraRewards).Count(x => x.GetType().Name == "CardReward") } };
            await File.WriteAllTextAsync(Path.Combine(export, "finite-hunt-terminal-v2.json"), PublicJson.Serialize(terminal) + "\n");
        }
    }

    [Fact]
    public async Task IncompleteMassAndLaterBoundariesHaveNoInventedPlanTargets()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt", "DefendSilent"], EnemyHp: 50));
        var evaluation = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [51, 52], MaxDecisionsPerPolicy = 1 });
        var initial = PublicJson.Serialize(TeacherDataset.RecordFiniteHunt(evaluation, "hunt-unknown", "combat", "family"));
        using var root = JsonDocument.Parse(initial);
        var plan = root.RootElement.GetProperty("targets").GetProperty("plan");
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("specified_success_probability").ValueKind);
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("extra_net_hp_loss").ValueKind);
        Assert.Equal(2, plan.GetProperty("allocated_worlds").GetInt32());
        var audit = root.RootElement.GetProperty("audit_only");
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("baseline"), 2, 0, 2, 0, 0);
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("plan"), 2, 0, 2, 0, 0);
        Assert.Equal(4, audit.GetProperty("n_unresolved").GetInt32());
        Assert.Equal(0, audit.GetProperty("n_error").GetInt32());
        Assert.Equal(4, audit.GetProperty("costs").GetProperty("worlds_allocated").GetInt32());
        Assert.Equal(0, audit.GetProperty("costs").GetProperty("worlds_completed").GetInt32());
        var policy = new FiniteHuntPolicy(evaluation.Anchor);
        var next = await source.StepAsync(policy.Choose(source.Observe()));
        policy.Choose(next);
        string later = PublicJson.Serialize(TeacherDataset.RecordFiniteHunt(evaluation, "hunt-unknown", "combat", "family", next, policy.Controller));
        using var continuation = JsonDocument.Parse(later);
        Assert.Equal("unavailable", continuation.RootElement.GetProperty("targets").GetProperty("plan").GetProperty("label_scope").GetString());
        var context = continuation.RootElement.GetProperty("public_input").GetProperty("controller_context");
        Assert.Equal(2, context.GetProperty("deadlinePlayerTurn").GetInt32());
        Assert.NotEmpty(context.GetProperty("observedEvents").EnumerateArray());
        string? export = Environment.GetEnvironmentVariable("NOSL_HUNT_FIXTURE_PATH");
        if (export is not null)
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "finite-hunt-unresolved-v2.jsonl"), initial + "\n");
            await File.WriteAllTextAsync(Path.Combine(export, "finite-hunt-continuation-v2.jsonl"), later + "\n");
        }
    }

    [Fact]
    public async Task PairedAccountingRetainsErrorsAndOtherUnknownMassAndRejectsMissingDraws()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], EnemyHp: 5));
        var evaluation = await AnchoredHuntEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [91] });
        var pair = Assert.Single(evaluation.Worlds);
        RolloutOutcome Unknown(TerminalKind kind, string policy) => new()
        {
            TerminalKind = kind, ContinuationPolicyId = policy, HpAtCombatStart = source.StartHp, MaxHpStart = source.StartMaxHp,
        };
        var diagnostic = evaluation with
        {
            Worlds = [pair with
            {
                Baseline = pair.Baseline with { Outcome = Unknown(TerminalKind.EngineError, evaluation.Anchor.BaselinePolicyId) },
                Plan = pair.Plan with { Outcome = Unknown(TerminalKind.PolicyNonterminating, evaluation.Anchor.TemplateId) },
            }],
            Masks = new(false, false, false, false, false), MeanSpecifiedSuccess = null, MeanExtraNetHpLoss = null,
        };
        using var doc = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.RecordFiniteHunt(diagnostic, "audit-test", "combat", "family")));
        var audit = doc.RootElement.GetProperty("audit_only");
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("baseline"), 1, 0, 0, 1, 0);
        AssertCounts(audit.GetProperty("plan_counts").GetProperty("plan"), 1, 0, 0, 0, 1);
        Assert.Equal(1, audit.GetProperty("n_error").GetInt32());
        Assert.Equal(1, audit.GetProperty("n_unresolved").GetInt32());
        Assert.Equal(2, audit.GetProperty("costs").GetProperty("worlds_allocated").GetInt32());
        Assert.Equal(0, audit.GetProperty("costs").GetProperty("worlds_completed").GetInt32());
        Assert.Throws<ArgumentException>(() => TeacherDataset.RecordFiniteHunt(evaluation with { Worlds = [] }, "run", "combat", "family"));
        Assert.Throws<ArgumentException>(() => TeacherDataset.RecordFiniteHunt(evaluation with { Worlds = [pair with { WorldIndex = 1 }] }, "run", "combat", "family"));
        Assert.Throws<ArgumentException>(() => TeacherDataset.RecordFiniteHunt(evaluation with
            { Audit = evaluation.Audit with { IndependentEvaluationSeeds = [91, 91] }, Worlds = [pair, pair with { WorldIndex = 1 }] }, "run", "combat", "family"));
    }

    private static void AssertCounts(JsonElement counts, int allocated, int completed, int truncated, int errors, int other)
    {
        Assert.Equal(allocated, counts.GetProperty("allocated_worlds").GetInt32());
        Assert.Equal(completed, counts.GetProperty("completed_worlds").GetInt32());
        Assert.Equal(truncated, counts.GetProperty("truncated_worlds").GetInt32());
        Assert.Equal(errors, counts.GetProperty("error_worlds").GetInt32());
        Assert.Equal(other, counts.GetProperty("other_worlds").GetInt32());
        Assert.Equal(allocated, completed + truncated + errors + other);
    }

    [Fact]
    public async Task SamePublicInformationDifferentPrivateSeedsHasIdenticalStudentInputAndTargets()
    {
        await using var a = await CombatSession.CreateAsync(new(Seed: "private-A", Deck: ["TheHunt"], EnemyHp: 5));
        await using var b = await CombatSession.CreateAsync(new(Seed: "private-B", Deck: ["TheHunt"], EnemyHp: 5));
        var options = new HuntEvaluationOptions { EvaluationSeeds = [81] };
        var first = TeacherDataset.RecordFiniteHunt(await AnchoredHuntEvaluator.EvaluateAsync(a, options), "same", "combat", "family");
        var second = TeacherDataset.RecordFiniteHunt(await AnchoredHuntEvaluator.EvaluateAsync(b, options), "same", "combat", "family");
        Assert.Equal(PublicJson.Serialize(first), PublicJson.Serialize(second));
    }

    [Fact]
    public async Task PublicContextRejectsReplacementDeadlineAndNonExtendingHistory()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt"], EnemyHp: 5));
        var packet = source.Observe(); var anchor = FiniteHuntPolicy.Anchor(packet);
        Assert.Throws<ArgumentException>(() => HuntStudentContext.Input(anchor with { DeadlinePlayerTurn = 9 }, packet));
        Assert.Throws<ArgumentException>(() => HuntStudentContext.Input(anchor,
            packet with { Observation = packet.Observation! with { History = [] } }));
    }
}
