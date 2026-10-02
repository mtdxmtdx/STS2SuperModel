using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class TeacherDatasetProvenanceTests
{
    [Theory]
    [InlineData(PublicContinuationPolicies.LegacyId)]
    [InlineData(PublicContinuationPolicies.ReviewedId)]
    public async Task PublicV1RetainsItsExistingAuditShape(string continuation)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent"], EnemyHp: 5));
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [41], ContinuationPolicyId = continuation });
        using var doc = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "v1-audit", "combat", "family", [41], [])));
        var root = doc.RootElement;
        Assert.Equal("nosl.student.public.v1", root.GetProperty("public_input").GetProperty("schema_version").GetString());
        var audit = root.GetProperty("audit_only");
        Assert.False(audit.TryGetProperty("controller_version", out _));
        Assert.False(audit.TryGetProperty("sampler_version", out _));
        Assert.False(audit.TryGetProperty("constructed_event_fixture", out _));
        string[] keys = ["posterior_implementation", "teacher", "continuation", "objective", "public_schema", "observation_schema", "simulator"];
        if (continuation == PublicContinuationPolicies.ReviewedId)
        {
            keys = [.. keys, "dataset"];
            Assert.Equal(PublicContinuationPolicies.ReviewedDatasetVersion, audit.GetProperty("dataset_version").GetString());
        }
        else Assert.False(audit.TryGetProperty("dataset_version", out _));
        Assert.Equal(keys, audit.GetProperty("versions").EnumerateObject().Select(x => x.Name).ToArray());
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task ForcedEventV2ExportsStableVersionsWithFullContinuationIdentity(string mode)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: Enumerable.Repeat("GrandFinale", 5).ToArray(),
            Hp: 20, MaxHp: 100, Relics: ["MeatOnTheBone"], ForcedEvent: new("BattlewornDummy", "Glory", ["SETTING_2"])));
        var options = new TeacherOptions { Mode = mode, EvaluationSeeds = [42], ExplorationSeeds = mode == "T1" ? [41] : [],
            MaxDecisions = 100, MaxPosteriorAttempts = 8, TreeDepth = 1 };
        var result = await CombatTeacher.EvaluateAsync(source, options);
        string json = PublicJson.Serialize(TeacherDataset.Record(result, "event-audit-" + mode, "battleworn", "declared-event", options.EvaluationSeeds, options.ExplorationSeeds));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var publicInput = root.GetProperty("public_input");
        Assert.Equal(HuntStudentContext.PublicSchema, publicInput.GetProperty("schema_version").GetString());
        Assert.Equal("inactive", publicInput.GetProperty("controller_context").GetProperty("status").GetString());
        var audit = root.GetProperty("audit_only");
        var versions = audit.GetProperty("versions");
        Assert.Equal("constructed", audit.GetProperty("source_kind").GetString());
        Assert.True(audit.GetProperty("constructed_event_fixture").GetBoolean());
        Assert.Equal("nosl.dataset.forced-events.v2", audit.GetProperty("dataset_version").GetString());
        Assert.Equal(audit.GetProperty("dataset_version").GetString(), versions.GetProperty("dataset").GetString());
        Assert.Equal(result.ContinuationVersion, audit.GetProperty("continuation_version").GetString());
        Assert.Equal(result.ContinuationVersion.Split(':')[0], versions.GetProperty("continuation").GetString());
        Assert.Equal("nosl.controller.inactive.v1", versions.GetProperty("controller").GetString());
        Assert.Equal(source.Observe().Observation!.Schema, versions.GetProperty("observation_schema").GetString());
        Assert.Equal(HuntStudentContext.PublicSchema, versions.GetProperty("public_schema").GetString());
        foreach (var (version, field) in new[] { ("simulator", "simulator_commit"), ("rules", "rules_version"),
            ("endpoint", "label_endpoint"), ("teacher", "teacher_version"), ("objective", "objective_version"),
            ("controller", "controller_version"), ("sampler", "sampler_version") })
        {
            Assert.False(string.IsNullOrWhiteSpace(audit.GetProperty(field).GetString()));
            Assert.Equal(audit.GetProperty(field).GetString(), versions.GetProperty(version).GetString());
        }
        if (Environment.GetEnvironmentVariable("NOSL_FORCED_AUDIT_FIXTURE_PATH") is { } export)
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "forced-event-" + mode.ToLowerInvariant() + "-v2.jsonl"), json + "\n");
        }
    }
}
