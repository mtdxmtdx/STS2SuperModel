using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeConstructedTapeDatasetTests
{
    private static NativeConstructedTapeCollectionOptions Options => new()
    {
        Prior = new()
        {
            Setup = new() { Encounter = "SludgeSpinnerWeak", Deck = ["StrikeSilent", "DefendSilent"] },
            SourceDecisionHorizon = 8,
        },
        CollectionId = "constructed-raw-protocol-fixture", SourceDrawSeeds = [18001], WallBudgetSeconds = 30,
    };
    private static TeacherOptions Teacher => new()
    {
        Mode = "T0", ContinuationPolicyId = PublicContinuationPolicies.ReviewedId,
        EvaluationSeeds = [702, 701], MaxPosteriorAttempts = 4, MaxDecisions = 1,
    };
    private static string Receipt()
    {
        var receipt = JsonNode.Parse(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()))!.AsObject();
        receipt["wrapper_source_sha256"] = new string('a', 64);
        receipt["vendor_source_sha256"] = new string('b', 64);
        return "\n " + receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    private static JsonObject Request(string? receipt = null)
    {
        var options = JsonNode.Parse(PublicJson.Serialize(Options))!.AsObject();
        // Authored inputs only: frozen computed law metadata belongs to output.
        var prior = options["prior"]!.AsObject();
        foreach (string key in new[] { "primitiveLaw", "primitiveImplementation", "rootLaw", "setupLaw" }) prior.Remove(key);
        return new()
        {
            ["op"] = "native_constructed_tape_candidates", ["purpose"] = NativeConstructedTapeDataset.Purpose,
            ["options"] = options, ["teacherOptions"] = JsonNode.Parse(PublicJson.Serialize(Teacher)),
            ["build_receipt_json"] = receipt ?? Receipt(),
        };
    }
    private static async Task<JsonElement> Collect(JsonObject request, CancellationToken token = default,
        NativeConstructedTapeDataset.TestHooks? hooks = null)
    {
        using var input = JsonDocument.Parse(request.ToJsonString());
        var result = await NativeConstructedTapeDataset.CollectAsync(input.RootElement, token, hooks);
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
            using var result = JsonDocument.Parse(await output);
            return result.RootElement.Clone();
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task WorkerExportsFreshConstructedV5AndEveryCandidateWithExactBindings()
    {
        string receipt = Receipt();
        var identity = await WorkerRequest(new() { ["op"] = "native_constructed_tape_runtime_identity" });
        Assert.Equal(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()), identity.GetRawText());
        var report = await WorkerRequest(Request(receipt));
        Assert.Equal(NativeConstructedTapeDataset.ReportSchemaVersion, report.GetProperty("schema_version").GetString());
        Assert.Equal(receipt, report.GetProperty("build_receipt_json").GetString());
        Assert.Equal(NativeCompleteMapDataset.Hash(receipt), report.GetProperty("build_receipt_sha256").GetString());
        string contract = report.GetProperty("collection_contract_json").GetString()!;
        Assert.Equal(NativeCompleteMapDataset.Hash(contract), report.GetProperty("collection_contract_sha256").GetString());
        using var frozen = JsonDocument.Parse(contract);
        Assert.Equal("opening", frozen.RootElement.GetProperty("options").GetProperty("prior").GetProperty("rootSelection").GetString());
        Assert.Equal(1, report.GetProperty("source_draws_requested").GetInt32());
        Assert.Equal(1, report.GetProperty("source_worlds_disposed").GetInt32());
        Assert.False(report.GetProperty("trainable").GetBoolean());
        Assert.False(report.GetProperty("formal_labels").GetBoolean());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        Assert.Equal(NativeConstructedTapeDataset.RawSchemaVersion, record.GetProperty("schema_version").GetString());
        Assert.Equal(NativeConstructedTapeDataset.RawRecordKind, record.GetProperty("record_kind").GetString());
        var input = record.GetProperty("public_input"); var audit = record.GetProperty("audit_only");
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, input.GetProperty("schema_version").GetString());
        var evidence = input.GetProperty("public_evidence");
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, evidence.GetProperty("schemaVersion").GetString());
        Assert.False(evidence.GetProperty("completeFromRunStart").GetBoolean());
        Assert.Contains("run_start_not_observed", evidence.GetRawText());
        Assert.DoesNotContain("\"kind\":\"map\"", evidence.GetRawText());
        var context = input.GetProperty("observation").GetProperty("runContext");
        Assert.False(context.GetProperty("completeFromRunStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("combatEntryIndex").ValueKind);
        Assert.Equal(NativeConstructedTapeDataset.SourceKind, audit.GetProperty("source_kind").GetString());
        foreach (string flag in new[] { "native_default_start", "source_seed_conditioning", "trainable", "formal_labels", "objective_calibrated" })
            Assert.False(audit.GetProperty(flag).GetBoolean());
        Assert.Equal(Options.Prior.Identity, audit.GetProperty("source_prior_identity").GetString());
        Assert.Equal(report.GetProperty("collection_contract_sha256").GetString(), audit.GetProperty("collection_contract_sha256").GetString());
        Assert.Equal(PublicJson.Serialize(Teacher), audit.GetProperty("teacher_options").GetRawText());
        Assert.Equal(NativeCompleteMapDataset.Hash(input.GetRawText()), audit.GetProperty("public_state_digest").GetString());
        Assert.Equal(NativeConstructedTapeDataset.RawSchemaVersion, audit.GetProperty("versions").GetProperty("dataset").GetString());
        var candidates = input.GetProperty("candidate_actions").EnumerateArray().ToArray();
        var targets = record.GetProperty("targets").GetProperty("actions").EnumerateArray().ToArray();
        var samples = audit.GetProperty("outcome_samples").EnumerateArray().ToArray();
        Assert.Equal(candidates.Length, targets.Length);
        Assert.Equal(candidates.Length, samples.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            Assert.True(input.GetProperty("legal_mask")[i].GetBoolean());
            Assert.Equal(i, targets[i].GetProperty("action_index").GetInt32());
            Assert.Equal(Teacher.EvaluationSeeds, samples[i].GetProperty("world_seeds").EnumerateArray().Select(seed => seed.GetUInt64()));
            Assert.Equal(Teacher.EvaluationSeeds.Length, samples[i].GetProperty("outcomes").GetArrayLength());
            Assert.Equal(Teacher.EvaluationSeeds.Length, targets[i].GetProperty("requested_worlds").GetInt32());
        }
        var costs = audit.GetProperty("costs");
        Assert.Equal(candidates.Length * Teacher.EvaluationSeeds.Length, costs.GetProperty("worlds_allocated").GetInt32());
        Assert.Equal(costs.GetProperty("worlds_allocated").GetInt32(), costs.GetProperty("requested_outcome_slots").GetInt32());
        var accounting = audit.GetProperty("execution_accounting");
        Assert.Equal(Teacher.EvaluationSeeds.Length, accounting.GetProperty("sample_calls").GetInt32());
        Assert.Equal(Teacher.EvaluationSeeds.Length, accounting.GetProperty("sampled_worlds_returned").GetInt32());
        Assert.Equal(candidates.Length * Teacher.EvaluationSeeds.Length, accounting.GetProperty("fork_calls").GetInt32());
        Assert.Equal(candidates.Length * Teacher.EvaluationSeeds.Length, accounting.GetProperty("candidate_worlds_returned").GetInt32());
        Assert.Equal(candidates.Length * Teacher.EvaluationSeeds.Length, accounting.GetProperty("step_calls").GetInt32());
        Assert.Equal(accounting.GetProperty("step_calls").GetInt32(), accounting.GetProperty("steps_returned").GetInt32());
        Assert.Equal(Teacher.EvaluationSeeds, accounting.GetProperty("samples").EnumerateArray().Select(sample => sample.GetProperty("worldSeed").GetUInt64()));
        Assert.Equal(accounting.GetProperty("candidate_worlds_returned").GetInt32(), report.GetProperty("candidate_worlds_returned").GetInt32());
        var attempt = Assert.Single(report.GetProperty("attempts").EnumerateArray());
        Assert.Equal(0, attempt.GetProperty("raw_record_index").GetInt32());
        Assert.True(attempt.GetProperty("source_disposed").GetBoolean());
        Assert.Equal(input.GetRawText(), attempt.GetProperty("provenance").GetProperty("public_input").GetRawText());
        Assert.DoesNotContain("actual_seed", input.GetRawText());
        Assert.DoesNotContain("source_draw_seed", input.GetRawText());
        Assert.DoesNotContain("runtime_dependencies", input.GetRawText());
    }

    [Fact]
    public async Task ProtocolPreflightRejectsSavedRootsPromotionOverridesAndMismatchedRuntime()
    {
        var mutations = new Action<JsonObject>[]
        {
            request => request["purpose"] = "bounded-pilot",
            request => request["records"] = new JsonArray(),
            request => request["options"]!["publicRoot"] = new JsonObject(),
            request => request["options"]!["afterSourceDisposed"] = true,
            request => request["options"]!["prior"]!["rootLaw"] = "selected_after_success",
            request => request["options"]!["prior"]!["execution"] = new JsonObject(),
            request => request["options"]!["prior"]!["setup"]!["seed"] = "hidden",
            request => request["options"]!["prior"]!["setup"]!["enemyHp"] = 1,
            request => request["options"]!["prior"]!["rootSelection"] = "first_success",
            request => request["options"]!["prior"]!["schemaVersion"] = NativeTapePrior.MapVersion,
            request => request["options"]!["sourceDrawSeeds"] = new JsonArray(702),
            request => request["options"]!["sourceDrawSeeds"] = new JsonArray(18001, 18001),
            request => request["options"]!["sourceDrawSeeds"] = new JsonArray(),
            request => request["options"]!["sourceDrawSeeds"] = new JsonArray(Enumerable.Range(1, 17).Select(value => JsonValue.Create(value)).Cast<JsonNode?>().ToArray()),
            request => request["options"]!["wallBudgetSeconds"] = 901,
            request => request["teacherOptions"]!["formalLabels"] = true,
            request => request["teacherOptions"]!["mode"] = "T1",
            request => request["teacherOptions"]!["explorationSeeds"] = new JsonArray(900),
            request => request["teacherOptions"]!["maxDecisions"] = 301,
            request => request["teacherOptions"]!["maxPosteriorAttempts"] = 4097,
        };
        foreach (var mutate in mutations)
        {
            var request = Request(); mutate(request);
            await Assert.ThrowsAnyAsync<Exception>(() => Collect(request));
        }
        var mismatched = JsonNode.Parse(Receipt())!.AsObject(); mismatched["worker_assembly_sha256"] = new string('f', 64);
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(Request(mismatched.ToJsonString())));
        var forced = Request(); forced["options"]!["prior"]!["setup"]!["encounter"] = "PunchOffEventEncounter";
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => Collect(forced));
        Assert.Contains("constructed_forced_owner_required", failure.Message);
        string duplicate = Request().ToJsonString().Replace("\"sourceDrawSeeds\":", "\"sourceDrawSeeds\":[],\"sourceDrawSeeds\":");
        using var document = JsonDocument.Parse(duplicate);
        await Assert.ThrowsAsync<ArgumentException>(() => NativeConstructedTapeDataset.CollectAsync(document.RootElement));
    }

    [Fact]
    public void ContractDetachesMutableSetupAndSeedArraysBeforeExecution()
    {
        var options = Options; var teacher = Teacher;
        var contract = NativeConstructedTapeDataset.CollectionContract.Create(options, teacher, NativeConstructedTapeDataset.Purpose, Receipt());
        string frozen = contract.Json; string hash = contract.Sha256; string prior = contract.Options.Prior.Identity;
        options.Prior.Setup.Deck![0] = "invalid external mutation";
        options.SourceDrawSeeds[0] = 999;
        teacher.EvaluationSeeds[0] = 888;
        Assert.Equal(frozen, contract.Json); Assert.Equal(hash, contract.Sha256);
        Assert.Equal(prior, contract.Options.Prior.Identity);
        Assert.Equal("StrikeSilent", contract.Options.Prior.Setup.Deck![0]);
        Assert.Equal((ulong)18001, contract.Options.SourceDrawSeeds[0]);
        Assert.Equal((ulong)702, contract.Teacher.EvaluationSeeds[0]);
    }

    [Fact]
    public async Task CancellationAndSourceCleanupFailureRetainEveryDrawWithoutClaimingTeacherExecution()
    {
        var request = Request(); request["options"]!["sourceDrawSeeds"] = new JsonArray(18001, 18002);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var skipped = await Collect(request, cancelled.Token);
        Assert.Empty(skipped.GetProperty("records").EnumerateArray());
        Assert.Equal(0, skipped.GetProperty("source_worlds_returned").GetInt32());
        Assert.Equal(2, skipped.GetProperty("attempts").GetArrayLength());
        Assert.All(skipped.GetProperty("attempts").EnumerateArray(), attempt =>
        {
            Assert.Equal("not_executed", attempt.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, attempt.GetProperty("execution_accounting").ValueKind);
            Assert.False(attempt.GetProperty("root_observed").GetBoolean());
            Assert.False(attempt.GetProperty("source_opened").GetBoolean());
        });
        using var cancelAfterRelease = new CancellationTokenSource();
        int sourcesReleased = 0, samplersCreated = 0;
        var failed = await Collect(request, cancelAfterRelease.Token, new(
            AfterSourceDisposed: () => { sourcesReleased++; cancelAfterRelease.Cancel(); throw new InvalidOperationException("injected_cleanup_failure_after_release"); },
            WrapPosteriorSource: source => { samplersCreated++; return source; }));
        Assert.Equal(1, sourcesReleased); Assert.Equal(0, samplersCreated);
        Assert.Equal(1, failed.GetProperty("existing_roots").GetInt32());
        Assert.Equal(1, failed.GetProperty("source_worlds_disposed").GetInt32());
        Assert.Empty(failed.GetProperty("records").EnumerateArray());
        Assert.Equal(0, failed.GetProperty("sampled_worlds_returned").GetInt32());
        var attempts = failed.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal("engine_error", attempts[0].GetProperty("status").GetString());
        Assert.Equal("after_source_disposal", attempts[0].GetProperty("stage").GetString());
        Assert.True(attempts[0].GetProperty("provenance").TryGetProperty("public_input", out _));
        var captured = attempts[0].GetProperty("provenance");
        string publicDigest = NativeCompleteMapDataset.Hash(captured.GetProperty("public_input").GetRawText());
        var capturedAudit = captured.GetProperty("audit_only");
        Assert.Equal(publicDigest, capturedAudit.GetProperty("public_state_digest").GetString());
        Assert.Equal("constructed-native-tape-public-root-v1:" + publicDigest, capturedAudit.GetProperty("public_root_alias").GetString());
        Assert.NotEmpty(capturedAudit.GetProperty("underlying_battle_alias").GetString()!);
        Assert.NotEmpty(capturedAudit.GetProperty("source_random_family_alias").GetString()!);
        Assert.Equal("not_executed", attempts[1].GetProperty("status").GetString());
        Assert.False(attempts[1].GetProperty("provenance").TryGetProperty("public_input", out _));
    }

    [Theory]
    [InlineData("posterior_exhausted")]
    [InlineData("engine_error")]
    [InlineData("computation_truncated")]
    public async Task SamplingFailuresKeepAllOutcomeSlotsButNoInventedWorldsOrValues(string failure)
    {
        bool released = false;
        var report = await Collect(Request(), hooks: new(AfterSourceDisposed: () => released = true,
            WrapPosteriorSource: source => { Assert.True(released); return new FailingSource(source, failure); }));
        var attempt = Assert.Single(report.GetProperty("attempts").EnumerateArray());
        Assert.Equal(failure, attempt.GetProperty("status").GetString());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        var audit = record.GetProperty("audit_only");
        var accounting = audit.GetProperty("execution_accounting");
        Assert.Equal(Teacher.EvaluationSeeds.Length, accounting.GetProperty("sample_calls_started").GetInt32());
        Assert.Equal(0, accounting.GetProperty("sampled_worlds_returned").GetInt32());
        Assert.Equal(0, accounting.GetProperty("fork_calls").GetInt32());
        Assert.Equal(0, accounting.GetProperty("candidate_worlds_returned").GetInt32());
        Assert.Equal(0, accounting.GetProperty("step_calls").GetInt32());
        foreach (var target in record.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.Equal(Teacher.EvaluationSeeds.Length, target.GetProperty("allocated_worlds").GetInt32());
            Assert.Equal(0, target.GetProperty("candidate_worlds_returned").GetInt32());
            Assert.Equal(0, target.GetProperty("executed_worlds").GetInt32());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.Null, target.GetProperty("win_probability").ValueKind);
            Assert.All(target.GetProperty("masks").EnumerateObject(), mask => Assert.False(mask.Value.GetBoolean()));
        }
        Assert.All(audit.GetProperty("outcome_samples").EnumerateArray(), sample =>
            Assert.Equal(Teacher.EvaluationSeeds.Length, sample.GetProperty("outcomes").GetArrayLength()));
    }

    [Fact]
    public async Task UnreachedPredeclaredRootIsAnAttemptWithoutReplacement()
    {
        var request = Request();
        request["options"]!["prior"]!["rootSelection"] = "decision_index";
        request["options"]!["prior"]!["decisionIndex"] = 2;
        request["options"]!["prior"]!["sourceDecisionHorizon"] = 1;
        var report = await Collect(request);
        Assert.Empty(report.GetProperty("records").EnumerateArray());
        var attempt = Assert.Single(report.GetProperty("attempts").EnumerateArray());
        Assert.Equal("absent", attempt.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("raw_record_index").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("execution_accounting").ValueKind);
        Assert.Equal((ulong)18001, attempt.GetProperty("source_draw_seed").GetUInt64());
    }

    [Fact]
    public async Task CancellationAfterSourceReleaseRetainsUnexecutedEvaluationSlotsWithoutEnteringSampler()
    {
        using var cancellation = new CancellationTokenSource();
        var report = await Collect(Request(), cancellation.Token,
            new(AfterSourceDisposed: cancellation.Cancel));
        var attempt = Assert.Single(report.GetProperty("attempts").EnumerateArray());
        Assert.True(attempt.GetProperty("source_disposed").GetBoolean());
        Assert.Equal("computation_truncated", attempt.GetProperty("status").GetString());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        var audit = record.GetProperty("audit_only");
        Assert.Empty(audit.GetProperty("posterior_proposals").EnumerateArray());
        var accounting = audit.GetProperty("execution_accounting");
        Assert.Equal(Teacher.EvaluationSeeds.Length, accounting.GetProperty("sample_calls").GetInt32());
        Assert.Equal(0, accounting.GetProperty("sample_calls_started").GetInt32());
        Assert.Equal(0, accounting.GetProperty("sampled_worlds_returned").GetInt32());
        Assert.All(accounting.GetProperty("samples").EnumerateArray(), sample =>
        {
            Assert.Equal("not_executed_computation_cancelled", sample.GetProperty("status").GetString());
            Assert.False(sample.GetProperty("worldReturned").GetBoolean());
            Assert.Empty(sample.GetProperty("branches").EnumerateArray());
        });
    }

    [Fact]
    public async Task SourceAliasesProtectOneBattleAcrossRootRulesAndProposalOnlyRandomness()
    {
        var baseOptions = Options with
        {
            Prior = Options.Prior with
            {
                Setup = Options.Prior.Setup with { Deck = ["DefendSilent"], Hp = 1000, MaxHp = 1000 },
            },
        };
        var provenance = new List<JsonObject>();
        var priorIdentities = new List<string>();
        var generationIdentities = new List<string>();
        string receipt = Receipt();
        string[] rules = ["opening", "first_player_turn_2", "first_player_turn_3"];
        for (int index = 0; index < rules.Length; index++)
        {
            var options = baseOptions with
            {
                CollectionId = "root-rule-" + index,
                Prior = baseOptions.Prior with { RootSelection = rules[index], SourceDecisionHorizon = 8 + index * 4 },
            };
            var contract = NativeConstructedTapeDataset.CollectionContract.Create(options, Teacher,
                NativeConstructedTapeDataset.Purpose, receipt);
            var recipe = contract.Options.Prior.Draw(new Rng(options.SourceDrawSeeds[0], NativeConstructedTapePrior.SourceDrawDomain));
            var origin = contract.Provenance(recipe);
            await using (var world = await NativeRunWorld.OpenConstructedLabelTapeAsync(contract.Options.Prior, recipe,
                NativeLabelTape.ForConstructedPrior(contract.Options.Prior, recipe)))
            {
                Assert.NotNull(world);
                var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
                Assert.Equal(index + 1, root.Observation!.Turn);
                contract.CaptureRoot(origin, root);
            }
            provenance.Add(origin); priorIdentities.Add(contract.Options.Prior.Identity);
            generationIdentities.Add(contract.SourceGenerationIdentity);
        }
        Assert.Equal(3, priorIdentities.Distinct().Count());
        Assert.Single(generationIdentities.Distinct());
        foreach (string field in new[] { "actual_seed", "source_run_group", "source_combat_id", "branch_family", "underlying_battle_alias", "source_random_family_alias" })
            Assert.Single(provenance.Select(origin => origin["audit_only"]![field]!.GetValue<string>()).Distinct());
        Assert.Equal(3, provenance.Select(origin => origin["audit_only"]!["public_root_alias"]!.GetValue<string>()).Distinct().Count());

        var original = NativeConstructedTapeDataset.CollectionContract.Create(baseOptions, Teacher,
            NativeConstructedTapeDataset.Purpose, receipt);
        var actualRecipe = original.Options.Prior.Draw(new Rng(baseOptions.SourceDrawSeeds[0], NativeConstructedTapePrior.SourceDrawDomain));
        var changedProposal = actualRecipe with { ProposalSeed = actualRecipe.ProposalSeed ^ 1UL };
        Assert.Equal(PublicJson.Serialize(original.Provenance(actualRecipe)), PublicJson.Serialize(original.Provenance(changedProposal)));
        await using (var changedWorld = await NativeRunWorld.OpenConstructedLabelTapeAsync(original.Options.Prior, changedProposal,
            NativeLabelTape.ForConstructedPrior(original.Options.Prior, changedProposal)))
        {
            Assert.NotNull(changedWorld);
            var changedOrigin = original.Provenance(changedProposal);
            original.CaptureRoot(changedOrigin, changedWorld.Observe());
            Assert.Equal(provenance[0]["public_input"]!.ToJsonString(), changedOrigin["public_input"]!.ToJsonString());
        }
        string Alias(NativeConstructedTapeDataset.CollectionContract contract, NativeTapeRecipe recipe, string field)
            => contract.Provenance(recipe)["audit_only"]![field]!.GetValue<string>();
        foreach (var changedPrior in new[]
        {
            baseOptions.Prior with { SourcePolicyId = PublicContinuationPolicies.LegacyId },
            baseOptions.Prior with { Setup = baseOptions.Prior.Setup with { Gold = 123 } },
        })
        {
            var changed = NativeConstructedTapeDataset.CollectionContract.Create(baseOptions with { Prior = changedPrior }, Teacher,
                NativeConstructedTapeDataset.Purpose, receipt);
            Assert.NotEqual(original.SourceGenerationIdentity, changed.SourceGenerationIdentity);
            Assert.NotEqual(Alias(original, actualRecipe, "underlying_battle_alias"), Alias(changed, actualRecipe, "underlying_battle_alias"));
            Assert.Equal(Alias(original, actualRecipe, "source_random_family_alias"), Alias(changed, actualRecipe, "source_random_family_alias"));
            Assert.Equal(Alias(original, actualRecipe, "source_run_group"), Alias(changed, actualRecipe, "source_run_group"));
        }
        foreach (var changedRecipe in new[] { actualRecipe with { RunSeed = actualRecipe.RunSeed ^ 1UL }, actualRecipe with { TapeSeed = actualRecipe.TapeSeed ^ 1UL } })
        {
            Assert.NotEqual(Alias(original, actualRecipe, "underlying_battle_alias"), Alias(original, changedRecipe, "underlying_battle_alias"));
            Assert.NotEqual(Alias(original, actualRecipe, "source_random_family_alias"), Alias(original, changedRecipe, "source_random_family_alias"));
            Assert.NotEqual(Alias(original, actualRecipe, "source_run_group"), Alias(original, changedRecipe, "source_run_group"));
        }
    }

    private sealed class FailingSource(ITeacherSource source, string failure) : ITeacherSource
    {
        public int StartHp => source.StartHp;
        public int StartMaxHp => source.StartMaxHp;
        public string?[] StartPotions => source.StartPotions;
        public string PosteriorProfile => source.PosteriorProfile;
        public string PriorWarning => source.PriorWarning;
        public DecisionPacket Observe() => source.Observe();
        public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => source.RankingSupport(profile);
        public Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts) => throw failure switch
        {
            "posterior_exhausted" => new PosteriorSamplingException(maxAttempts),
            "computation_truncated" => new OperationCanceledException("injected sampler cancellation"),
            _ => new InvalidOperationException("injected sampler error"),
        };
    }
}
