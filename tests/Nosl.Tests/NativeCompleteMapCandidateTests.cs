using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeCompleteMapCandidateTests
{
    private static NativeTapeCollectionOptions Options => new()
    {
        Prior = new()
        {
            SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 1, EligibleDecisionsPerCombat = 1,
            Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
                PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
        },
        SourceDrawSeeds = [11001], CollectionId = "fresh-api-contract-fixture",
        WallBudgetSeconds = 30, EventPermutationMaxTrials = 1,
    };
    private static TeacherOptions Teacher => new()
    {
        Mode = "T0", ContinuationPolicyId = PublicContinuationPolicies.ReviewedId,
        EvaluationSeeds = [402, 401], MaxPosteriorAttempts = 1, MaxDecisions = 1,
    };
    private static string Receipt()
    {
        var identity = JsonNode.Parse(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()))!.AsObject();
        identity["wrapper_source_sha256"] = new string('a', 64);
        identity["vendor_source_sha256"] = new string('b', 64);
        // Receipt byte identity must retain insignificant JSON whitespace too.
        return "\n " + identity.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    private static JsonObject Request(string? receipt = null) => new()
    {
        ["op"] = "native_complete_map_candidates", ["purpose"] = "bounded-pilot",
        ["options"] = JsonNode.Parse(PublicJson.Serialize(Options)),
        ["teacherOptions"] = JsonNode.Parse(PublicJson.Serialize(Teacher)),
        ["build_receipt_json"] = receipt ?? Receipt(),
    };
    private static async Task<JsonElement> Collect(JsonObject request, CancellationToken token = default)
    {
        using var input = JsonDocument.Parse(request.ToJsonString());
        var result = await NativeCompleteMapDataset.CollectAsync(input.RootElement, token);
        using var output = JsonDocument.Parse(PublicJson.Serialize(result));
        return output.RootElement.Clone();
    }

    private static async Task<JsonElement> WorkerRequest(JsonObject request)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(CombatSession).Assembly.Location);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync(request.ToJsonString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
            using var doc = JsonDocument.Parse(await output);
            return doc.RootElement.Clone();
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task FreshApiBindsNativeCompletePacketRuntimeAndEveryEvaluationSeedInOrder()
    {
        string receipt = Receipt();
        var identity = await WorkerRequest(new() { ["op"] = "native_complete_map_runtime_identity" });
        Assert.Equal(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()), identity.GetRawText());
        var report = await WorkerRequest(Request(receipt));
        if (Environment.GetEnvironmentVariable("NOSL_COMPLETE_MAP_FIXTURE_EXPORT") is { Length: > 0 } export)
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "fresh-map-api-report.json"), report.GetRawText() + "\n");
            await File.WriteAllTextAsync(Path.Combine(export, "fresh-map-api-raw.jsonl"),
                string.Join("\n", report.GetProperty("records").EnumerateArray().Select(r => r.GetRawText())) + "\n");
        }
        Assert.Equal(NativeCompleteMapDataset.ReportSchemaVersion, report.GetProperty("schema_version").GetString());
        Assert.Equal(receipt, report.GetProperty("build_receipt_json").GetString());
        Assert.Equal(NativeCompleteMapDataset.Hash(receipt), report.GetProperty("build_receipt_sha256").GetString());
        Assert.False(report.GetProperty("trainable").GetBoolean());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        Assert.Equal(NativeCompleteMapDataset.RawSchemaVersion, record.GetProperty("schema_version").GetString());
        Assert.Equal(NativeCompleteMapDataset.RawRecordKind, record.GetProperty("record_kind").GetString());
        Assert.Equal(new[] { "audit_only", "public_input", "record_kind", "schema_version", "targets" },
            record.EnumerateObject().Select(p => p.Name).Order());
        var input = record.GetProperty("public_input"); var audit = record.GetProperty("audit_only");
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, input.GetProperty("schema_version").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, input.GetProperty("public_evidence").GetProperty("schemaVersion").GetString());
        Assert.True(input.GetProperty("public_evidence").GetProperty("completeFromRunStart").GetBoolean());
        Assert.Equal(NativeCompleteMapDataset.Hash(input.GetRawText()), audit.GetProperty("public_state_digest").GetString());
        Assert.Equal("bounded-pilot", audit.GetProperty("purpose").GetString());
        Assert.Equal("natural_under_explicit_label_tape_prior", audit.GetProperty("source_kind").GetString());
        Assert.True(audit.GetProperty("native_run").GetBoolean());
        Assert.True(audit.GetProperty("independent_final_evaluation").GetBoolean());
        foreach (string flag in new[] { "trainable", "formal_labels", "objective_calibrated", "source_seed_conditioning" })
            Assert.False(audit.GetProperty(flag).GetBoolean());
        Assert.Equal(0, audit.GetProperty("n_exploration").GetInt32());
        Assert.Empty(audit.GetProperty("exploration_seeds").EnumerateArray());
        var versions = audit.GetProperty("versions");
        Assert.Equal(new[] { "continuation", "controller", "dataset", "endpoint", "map_profile", "map_support", "objective",
            "observation_schema", "posterior_profile", "public_evidence", "public_schema", "rules", "sampler", "simulator",
            "source_prior", "source_script", "teacher" }, versions.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(NativeTapeReplayDataset.ImplementationFor(Options.Prior), versions.GetProperty("sampler").GetString());
        Assert.Equal("owned-native-map-rewards-tape-marginalized-v1-public-evidence-v2", versions.GetProperty("posterior_profile").GetString());
        Assert.Equal(Options.Prior.Identity, versions.GetProperty("source_prior").GetString());
        Assert.Equal(PublicJson.Serialize(NativeCompleteMapBuildReceipt.Validate(receipt).Dependencies()),
            audit.GetProperty("runtime_dependencies").GetRawText());
        Assert.Equal(NativeCompleteMapDataset.Hash(receipt), audit.GetProperty("build_receipt_sha256").GetString());
        var samples = audit.GetProperty("outcome_samples").EnumerateArray().ToArray();
        Assert.Equal(input.GetProperty("candidate_actions").GetArrayLength(), samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(i, samples[i].GetProperty("action_index").GetInt32());
            Assert.Equal(Teacher.EvaluationSeeds, samples[i].GetProperty("world_seeds").EnumerateArray().Select(v => v.GetUInt64()));
            Assert.Equal(Teacher.EvaluationSeeds.Length, samples[i].GetProperty("outcomes").GetArrayLength());
        }
        var actions = record.GetProperty("targets").GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(samples.Length * Teacher.EvaluationSeeds.Length, audit.GetProperty("costs").GetProperty("worlds_allocated").GetInt32());
        Assert.Equal(actions.Sum(a => a.GetProperty("error_worlds").GetInt32()), audit.GetProperty("n_error").GetInt32());
        Assert.Equal(actions.Sum(a => a.GetProperty("truncated_worlds").GetInt32() + a.GetProperty("other_worlds").GetInt32()),
            audit.GetProperty("n_unresolved").GetInt32());
        var attempt = Assert.Single(report.GetProperty("attempts").EnumerateArray());
        Assert.Equal("recorded", attempt.GetProperty("status").GetString());
        Assert.Equal(0, attempt.GetProperty("raw_record_index").GetInt32());
        Assert.Equal(Options.CollectionId + "/draw:0", attempt.GetProperty("draw_id").GetString());
        Assert.Equal(attempt.GetProperty("draw_id").GetString(), audit.GetProperty("draw_id").GetString());
        Assert.Equal(input.GetRawText(), attempt.GetProperty("provenance").GetProperty("public_input").GetRawText());
        Assert.Equal(audit.GetProperty("actual_seed").GetString(), attempt.GetProperty("provenance").GetProperty("audit_only").GetProperty("actual_seed").GetString());
        Assert.DoesNotContain("runtime_dependencies", input.GetRawText());
        Assert.DoesNotContain("actual_seed", input.GetRawText());
        Assert.DoesNotContain("source_draw_seed", input.GetRawText());
    }

    [Fact]
    public async Task ApiPreflightAndCancelledDenominatorCannotPromoteDiagnosticRows()
    {
        var mutations = new Action<JsonObject>[]
        {
            r => r["purpose"] = "engineering-fixture",
            r => r["records"] = new JsonArray(),
            r => r["options"]!["publicRoot"] = new JsonObject(),
            r => r["options"]!["prior"]!["execution"]!["graph"] = new JsonObject(),
            r => r["options"]!["prior"]!["schemaVersion"] = NativeTapePrior.RewardsVersion,
            r => r["options"]!["prior"]!["execution"]!["outsideCombatScript"] = NaturalSourceCollector.ScriptVersion,
            r => r["options"]!["prior"]!["execution"]!["publicCombatHistoryMode"] = PublicRunContext.UnavailableHistoryMode,
            r => r["options"]!["afterSourceDisposedForTests"] = true,
            r => r["options"]!["enableConditioning"] = false,
            r => r["options"]!["compareUnconditioned"] = true,
            r => r["options"]!["sourceDrawSeeds"] = new JsonArray(402),
            r => r["teacherOptions"]!["formalLabels"] = true,
            r => r["teacherOptions"]!["mode"] = "T1",
            r => r["teacherOptions"]!["continuationPolicyId"] = PublicContinuationPolicies.LegacyId,
            r => r["teacherOptions"]!["explorationSeeds"] = new JsonArray(501),
        };
        foreach (var mutate in mutations)
        {
            var request = Request(); mutate(request);
            await Assert.ThrowsAnyAsync<Exception>(() => Collect(request));
        }
        foreach (string key in new[] { "worker_assembly_sha256", "core_assembly_sha256", "upstream_commit", "runtime_version", "vendor_source_sha256" })
        {
            var receipt = JsonNode.Parse(Receipt())!.AsObject();
            receipt[key] = key.EndsWith("sha256") ? new string('f', 64) : "incorrect";
            if (key == "vendor_source_sha256") receipt[key] = "not-a-sha256";
            await Assert.ThrowsAsync<ArgumentException>(() => Collect(Request(receipt.ToJsonString())));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(Request(Receipt().Replace("\"upstream_commit\":", "\"extra\":\"value\",\"upstream_commit\":"))));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(Request(Receipt().Replace("\"upstream_commit\":", "\"upstream_commit\":\"duplicate\",\"upstream_commit\":"))));
        var cancelledRequest = Request(); cancelledRequest["options"]!["sourceDrawSeeds"] = new JsonArray(11001, 11002);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await Collect(cancelledRequest, cancellation.Token);
        Assert.Empty(cancelled.GetProperty("records").EnumerateArray());
        var attempts = cancelled.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        for (int i = 0; i < attempts.Length; i++)
        {
            Assert.Equal("not_executed", attempts[i].GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, attempts[i].GetProperty("raw_record_index").ValueKind);
            Assert.Equal(Options.CollectionId + "/draw:" + i, attempts[i].GetProperty("draw_id").GetString());
            Assert.True(attempts[i].GetProperty("provenance").GetProperty("audit_only").GetProperty("native_run").GetBoolean());
            Assert.NotEmpty(attempts[i].GetProperty("provenance").GetProperty("audit_only").GetProperty("actual_seed").GetString()!);
        }
        // A second, one-world native API smoke locks the diagnostic route in quarantine.
        var diagnostic = await WorkerRequest(new()
        {
            ["op"] = "native_tape_replay", ["options"] = JsonNode.Parse(PublicJson.Serialize(Options)),
            ["teacherOptions"] = JsonNode.Parse(PublicJson.Serialize(Teacher with { EvaluationSeeds = [401] })),
        });
        using var doc = JsonDocument.Parse(diagnostic.GetRawText());
        Assert.Equal("nosl.native-tape-replay-report.v1", doc.RootElement.GetProperty("schema_version").GetString());
        Assert.False(doc.RootElement.TryGetProperty("build_receipt_json", out _));
        var record = Assert.Single(doc.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal(NativeTapeReplayDataset.DatasetFor(Options.Prior), record.GetProperty("schema_version").GetString());
        Assert.Equal("native_map_rewards_tape_replay_development_candidate", record.GetProperty("record_kind").GetString());
        var audit = record.GetProperty("audit_only");
        Assert.False(audit.GetProperty("trainable").GetBoolean());
        Assert.True(audit.GetProperty("engineering_smoke").GetBoolean());
        Assert.False(audit.TryGetProperty("purpose", out _));
        Assert.False(audit.TryGetProperty("runtime_dependencies", out _));
        Assert.False(audit.GetProperty("outcome_samples")[0].TryGetProperty("world_seeds", out _));
        var rejected = Request(); rejected["record"] = JsonNode.Parse(record.GetRawText());
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(rejected));
    }
    [Fact]
    public async Task SourceCleanupFailureRetainsCapturedRootAndAllRequestedDrawProvenance()
    {
        var options = Options with { SourceDrawSeeds = [11001, 11002] };
        var contract = NativeCompleteMapDataset.CollectionContract.Create(options, Teacher, "bounded-pilot", Receipt());
        using var cancellation = new CancellationTokenSource();
        int releasedSources = 0;
        var result = await NativeTapeReplayDataset.CollectCompleteMapCandidatesAsync(options, Teacher, contract,
            cancellation.Token, afterSourceDisposedForTests: () =>
            {
                releasedSources++;
                cancellation.Cancel();
                throw new InvalidOperationException("injected_after_source_disposal");
            });
        using var doc = JsonDocument.Parse(PublicJson.Serialize(result));
        var report = doc.RootElement;
        Assert.Equal(1, releasedSources);
        Assert.Equal(2, report.GetProperty("sourceDrawsRequested").GetInt32());
        Assert.Equal(1, report.GetProperty("existingRoots").GetInt32());
        Assert.Equal(0, report.GetProperty("recordedRoots").GetInt32());
        Assert.Equal(0, report.GetProperty("allocatedCandidateWorlds").GetInt32());
        Assert.Empty(report.GetProperty("records").EnumerateArray());
        var attempts = report.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal("failed", attempts[0].GetProperty("status").GetString());
        Assert.Contains("injected_after_source_disposal", attempts[0].GetProperty("detail").GetString());
        Assert.Equal("not_executed", attempts[1].GetProperty("status").GetString());
        for (int i = 0; i < attempts.Length; i++)
        {
            Assert.Equal(JsonValueKind.Null, attempts[i].GetProperty("raw_record_index").ValueKind);
            Assert.Equal(options.CollectionId + "/draw:" + i, attempts[i].GetProperty("draw_id").GetString());
            Assert.Equal(options.SourceDrawSeeds[i], attempts[i].GetProperty("source_draw_seed").GetUInt64());
            var recipe = options.Prior.Draw(new Rng(options.SourceDrawSeeds[i], "nosl-native-tape-source-draw-v1"));
            var audit = attempts[i].GetProperty("provenance").GetProperty("audit_only");
            Assert.Equal(recipe.SourceIdentity, audit.GetProperty("actual_seed").GetString());
            Assert.Equal("native-map-rewards-tape-source-v1:" + NativePilotDataset.SourceIdentity(recipe.SourceIdentity),
                audit.GetProperty("source_run_group").GetString());
            Assert.True(audit.GetProperty("native_run").GetBoolean());
            Assert.Equal("natural_under_explicit_label_tape_prior", audit.GetProperty("source_kind").GetString());
        }
        var provenance = attempts[0].GetProperty("provenance");
        var input = provenance.GetProperty("public_input");
        var capturedAudit = provenance.GetProperty("audit_only");
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, input.GetProperty("schema_version").GetString());
        Assert.Equal(NativeCompleteMapDataset.Hash(input.GetRawText()), capturedAudit.GetProperty("public_state_digest").GetString());
        var entry = input.GetProperty("observation").GetProperty("history").EnumerateArray()
            .Single(e => e.GetProperty("kind").GetString() == NativeEntryAssets.EventKind).GetProperty("detail").GetString()!;
        string combat = capturedAudit.GetProperty("source_run_group").GetString() + "/public-entry:" + NativeCompleteMapDataset.Hash(entry);
        Assert.Equal(combat, capturedAudit.GetProperty("source_combat_id").GetString());
        Assert.Equal(combat + "/tape-root-family", capturedAudit.GetProperty("branch_family").GetString());
        Assert.False(attempts[1].GetProperty("provenance").TryGetProperty("public_input", out _));
    }
}
