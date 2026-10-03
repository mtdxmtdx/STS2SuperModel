using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeConstructedTapeCollectionOptions
{
    public NativeConstructedTapePrior Prior { get; init; } = new();
    public ulong[] SourceDrawSeeds { get; init; } = [8001];
    public string CollectionId { get; init; } = "constructed-native-tape-raw";
    public bool EnableConditioning { get; init; } = true;
    public int WallBudgetSeconds { get; init; } = 300;
}

/// <summary>
/// Fresh constructed sources only. The setup, root rule, draws, runtime and teacher
/// are frozen before execution; neither saved public roots nor completed rows are inputs.
/// </summary>
internal static class NativeConstructedTapeDataset
{
    internal const string RawSchemaVersion = "nosl.native-constructed-tape.raw-candidate.v1";
    internal const string RawRecordKind = "native_constructed_tape_raw_candidate";
    internal const string ReportSchemaVersion = "nosl.native-constructed-tape.raw-report.v1";
    internal const string Purpose = "bounded-constructed-raw";
    internal const string SourceKind = "constructed_under_explicit_label_tape_prior";
    internal const string ImplementationVersion = NativeConstructedTapeSource.ImplementationVersion;
    internal const string SourceDrawDomain = NativeConstructedTapePrior.SourceDrawDomain;
    private static readonly JsonSerializerOptions RequestJson = new(PublicJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // Not a protocol surface. Tests may inspect release ordering or inject an
    // owned-source failure, but cannot skip source construction or replace its root.
    internal sealed record TestHooks(Action? AfterSourceDisposed = null,
        Func<ITeacherSource, ITeacherSource>? WrapPosteriorSource = null);

    internal static object RuntimeIdentity(JsonElement request)
    {
        NativeCompleteMapDataset.RequireKeys(request, ["op"]);
        if (request.GetProperty("op").GetString() != "native_constructed_tape_runtime_identity")
            throw new ArgumentException("Constructed tape runtime identity operation required");
        return NativeCompleteMapBuildReceipt.RuntimeIdentity();
    }

    internal static Task<object> CollectAsync(JsonElement request, CancellationToken cancellationToken = default,
        TestHooks? testHooks = null)
    {
        NativeCompleteMapDataset.RequireKeys(request, ["op", "options", "teacherOptions", "purpose", "build_receipt_json"]);
        if (request.GetProperty("op").GetString() != "native_constructed_tape_candidates")
            throw new ArgumentException("Fresh constructed tape candidate operation required");
        RejectDuplicateKeys(request);
        // Read-only computed prior metadata is deliberately not accepted as an
        // input override, even though System.Text.Json would silently ignore it.
        AllowedKeys(request.GetProperty("options"), ["prior", "sourceDrawSeeds", "collectionId", "enableConditioning", "wallBudgetSeconds"]);
        var priorJson = request.GetProperty("options").GetProperty("prior");
        AllowedKeys(priorJson, ["schemaVersion", "setup", "sourcePolicyId", "sourceDecisionHorizon", "rootSelection", "decisionIndex"]);
        AllowedKeys(priorJson.GetProperty("setup"), ["encounter", "deck", "potions", "relics", "hp", "maxHp", "gold"]);
        var options = JsonSerializer.Deserialize<NativeConstructedTapeCollectionOptions>(request.GetProperty("options"), RequestJson)
            ?? throw new ArgumentException("Constructed tape collection options required");
        var teacher = JsonSerializer.Deserialize<TeacherOptions>(request.GetProperty("teacherOptions"), RequestJson)
            ?? throw new ArgumentException("Constructed tape teacher options required");
        var contract = CollectionContract.Create(options, teacher, request.GetProperty("purpose").GetString(),
            request.GetProperty("build_receipt_json").GetString());
        return CollectCoreAsync(contract, cancellationToken, testHooks);
    }

    private static async Task<object> CollectCoreAsync(CollectionContract contract, CancellationToken cancellationToken,
        TestHooks? testHooks)
    {
        var options = contract.Options;
        var prior = options.Prior;
        var teacher = contract.Teacher;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.WallBudgetSeconds));
        var timer = Stopwatch.StartNew();
        var records = new List<JsonObject>();
        var attempts = new List<object>();
        var publicRoots = new HashSet<string>(StringComparer.Ordinal);
        int openedSources = 0, existingRoots = 0, disposedSources = 0;
        int sampledWorlds = 0, allocatedBranches = 0, startedBranches = 0, completedOutcomes = 0;
        for (int drawIndex = 0; drawIndex < options.SourceDrawSeeds.Length; drawIndex++)
        {
            ulong sourceDrawSeed = options.SourceDrawSeeds[drawIndex];
            var recipe = prior.Draw(new Rng(sourceDrawSeed, SourceDrawDomain));
            var provenance = contract.Provenance(recipe);
            var attemptTimer = Stopwatch.StartNew();
            NativeConstructedTapeSource? posterior = null;
            ExecutionAccountingSource? accounting = null;
            string stage = "source_not_started", status = "not_executed", sourceStatus = "not_executed";
            string? detail = null;
            int? recordIndex = null;
            bool sourceOpened = false, rootObserved = false, sourceDisposed = false;
            try
            {
                if (budget.IsCancellationRequested) detail = "computation_cancelled_before_source_open";
                else
                {
                    stage = "source_open";
                    var sourceWorld = await NativeRunWorld.OpenConstructedLabelTapeAsync(prior, recipe,
                        NativeLabelTape.ForConstructedPrior(prior, recipe), budget.Token);
                    if (sourceWorld is null)
                    {
                        status = "absent"; sourceStatus = "absent_under_declared_root_rule_and_horizon";
                    }
                    else
                    {
                        openedSources++; sourceOpened = true;
                        DecisionPacket publicRoot;
                        try
                        {
                            stage = "source_observation";
                            publicRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(sourceWorld.Observe()));
                            ValidateRoot(publicRoot);
                            contract.CaptureRoot(provenance, publicRoot);
                            existingRoots++; rootObserved = true; sourceStatus = "observed";
                        }
                        finally
                        {
                            string previousStage = stage;
                            stage = "source_disposal";
                            await sourceWorld.DisposeAsync();
                            disposedSources++; sourceDisposed = true;
                            stage = previousStage;
                        }
                        // Even an injected failure occurs only after the real source
                        // has been released. No source graph enters the teacher.
                        stage = "after_source_disposal";
                        testHooks?.AfterSourceDisposed?.Invoke();
                        stage = "posterior_construction";
                        posterior = new NativeConstructedTapeSource(publicRoot, prior, options.EnableConditioning, budget.Token);
                        ITeacherSource teacherSource = testHooks?.WrapPosteriorSource?.Invoke(posterior) ?? posterior;
                        accounting = new(teacherSource, budget.Token);
                        stage = "teacher_evaluation";
                        var result = await CombatTeacher.EvaluateAsync(accounting, teacher);
                        stage = "record_serialization";
                        var record = contract.Record(result, provenance, posterior, accounting, sourceDrawSeed, drawIndex);
                        status = EvaluationStatus(result, accounting);
                        recordIndex = records.Count;
                        records.Add(record);
                        publicRoots.Add(record["audit_only"]!["public_state_digest"]!.GetValue<string>());
                        completedOutcomes += result.Candidates.Sum(candidate => candidate.Outcomes.Count(outcome => outcome.IsTrueTerminal && outcome.SettlementComplete));
                        stage = "complete";
                    }
                }
            }
            catch (Exception exception)
            {
                status = exception switch
                {
                    OperationCanceledException => "computation_truncated",
                    PosteriorSamplingException => "posterior_exhausted",
                    NotSupportedException => "unsupported_capability",
                    _ => "engine_error",
                };
                if (!rootObserved) sourceStatus = status;
                detail = exception.GetType().Name + ": " + exception.Message;
            }
            if (accounting is not null)
            {
                sampledWorlds += accounting.SampledWorlds;
                allocatedBranches += accounting.AllocatedBranches;
                startedBranches += accounting.StartedBranches;
            }
            attempts.Add(new
            {
                draw_id = contract.DrawId(drawIndex), source_draw_seed = sourceDrawSeed, status,
                source_status = sourceStatus, stage, detail, recipe, raw_record_index = recordIndex,
                source_opened = sourceOpened, root_observed = rootObserved, source_disposed = sourceDisposed,
                elapsed_seconds = attemptTimer.Elapsed.TotalSeconds,
                execution_accounting = accounting?.Snapshot(),
                posterior_proposals = posterior?.ProposalAudit,
                provenance,
            });
        }
        return new
        {
            schema_version = ReportSchemaVersion, status = "bounded_constructed_raw_requires_separate_quality_admission",
            purpose = Purpose, source_kind = SourceKind, selection = "predeclared_all_attempts_no_replacement",
            collection_contract_json = contract.Json, collection_contract_sha256 = contract.Sha256,
            build_receipt_json = contract.Receipt.Json, build_receipt_sha256 = contract.Receipt.Sha256,
            runtime_dependencies = contract.Receipt.Dependencies(), options, teacher_options = teacher,
            prior_identity = prior.Identity,
            source_generation_json = contract.SourceGenerationJson, source_generation_identity = contract.SourceGenerationIdentity,
            source_draws_requested = options.SourceDrawSeeds.Length,
            source_worlds_returned = openedSources, existing_roots = existingRoots, source_worlds_disposed = disposedSources,
            recorded_roots = records.Count, distinct_recorded_public_roots = publicRoots.Count,
            sampled_worlds_returned = sampledWorlds, candidate_worlds_returned = allocatedBranches,
            candidate_branches_started = startedBranches, settled_candidate_outcomes = completedOutcomes,
            elapsed_seconds = timer.Elapsed.TotalSeconds, peak_worker_memory_bytes = Process.GetCurrentProcess().PeakWorkingSet64,
            budget_expired = budget.IsCancellationRequested, trainable = false, formal_labels = false,
            attempts, records,
        };
    }

    private static string EvaluationStatus(TeacherResult result, ExecutionAccountingSource accounting)
    {
        if (result.Candidates.Any(candidate => candidate.Outcomes.Any(outcome => outcome.TerminalKind == TerminalKind.EngineError)))
            return "engine_error";
        if (accounting.Samples.Any(sample => sample.Status == "posterior_exhausted")) return "posterior_exhausted";
        return result.Candidates.Any(candidate => candidate.Outcomes.Any(outcome => !outcome.IsTrueTerminal || !outcome.SettlementComplete))
            ? "computation_truncated" : "recorded_complete";
    }

    private static void ValidateRoot(DecisionPacket root)
    {
        _ = PublicEvidenceInput.Validate(root);
        if (root.PublicEvidence is not { SchemaVersion: PublicRunEvidence.CompleteMapVersion, CompleteFromRunStart: false } evidence
            || evidence.Events[0].Payload is not PublicEvidenceGap { Reason: PublicEvidenceGapReason.RunStartNotObserved }
            || evidence.Events.Any(entry => entry.Payload is PublicMapObserved or PublicMapChosen or PublicRunStarted)
            || root.Observation?.RunContext is not { CompleteFromRunStart: false, CombatEntryIndex: null }
            || root.Status is not ("player_decision" or "card_choice") || root.Actions.Length == 0)
            throw new ArgumentException("Constructed raw roots require recorded v5 combat evidence, an unobserved run-start gap and no invented map or natural combat count");
    }

    private static void AllowedKeys(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !keys.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Allowed constructed request keys: " + string.Join(",", keys));
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate constructed request key: " + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    internal sealed class CollectionContract
    {
        internal NativeConstructedTapeCollectionOptions Options { get; }
        internal TeacherOptions Teacher { get; }
        internal NativeCompleteMapBuildReceipt Receipt { get; }
        internal string SourceGenerationJson { get; }
        internal string SourceGenerationIdentity { get; }
        private string PrimitiveNamespaceJson { get; }
        internal string Json { get; }
        internal string Sha256 { get; }
        private CollectionContract(NativeConstructedTapeCollectionOptions options, TeacherOptions teacher, NativeCompleteMapBuildReceipt receipt)
        {
            Options = options; Teacher = teacher; Receipt = receipt;
            // Stopping rules select views of a source battle; they do not create
            // a new battle for split protection. Proposal randomness is used only
            // for conditional inference and is absent from the true source tape.
            // These domain descriptors mirror the pinned unconditioned oracles;
            // changing a source generator contract requires a new version here.
            var randomDomains = new
            {
                native_run_seed = "NOSL-NATIVE-TAPE-V1:{RunSeed:X16}",
                native_state_words = "sha256-u64le:4E4F534C54415031,TapeSeed,State0,State1,State2,State3",
                rewards_words = "sha256-u64le:4E4F534C52574431,TapeSeed,InitialSeed,RawCursor",
                map_words = "sha256-u64le:4E4F534C4D415031,TapeSeed,ActIndex,InitialSeed,RawCursor",
            };
            PrimitiveNamespaceJson = PublicJson.Serialize(new
            {
                primitive_law = options.Prior.PrimitiveLaw, primitive_implementation = options.Prior.PrimitiveImplementation,
                random_domains = randomDomains,
            });
            SourceGenerationJson = PublicJson.Serialize(new
            {
                schema_version = "nosl.native-constructed-tape.source-generation.v1",
                source_prior_schema = options.Prior.SchemaVersion,
                setup = options.Prior.Setup, setup_law = options.Prior.SetupLaw,
                source_policy = options.Prior.SourcePolicyId,
                source_script = options.Prior.Execution.ResolvedOutsideCombatScript,
                primitive_law = options.Prior.PrimitiveLaw, primitive_implementation = options.Prior.PrimitiveImplementation,
                character = "Silent", ascension = 10, simulator = NativeCompleteMapBuildReceipt.UpstreamCommit,
                random_domains = randomDomains,
            });
            SourceGenerationIdentity = NativeCompleteMapDataset.Hash(SourceGenerationJson);
            Json = PublicJson.Serialize(new
            {
                schema_version = "nosl.native-constructed-tape.collection-contract.v1", purpose = Purpose,
                raw_schema = RawSchemaVersion, source_kind = SourceKind, source_draw_domain = SourceDrawDomain,
                source_draw_rule = "one_independent_recipe_per_requested_draw_no_replacement",
                source_group_semantics = "conservative_primitive_rng_family",
                options, teacher_options = teacher, sampler_version = ImplementationVersion,
                source_generation_json = SourceGenerationJson, source_generation_identity = SourceGenerationIdentity,
                build_receipt_sha256 = receipt.Sha256, runtime_dependencies = receipt.Dependencies(),
            });
            Sha256 = NativeCompleteMapDataset.Hash(Json);
        }

        internal static CollectionContract Create(NativeConstructedTapeCollectionOptions options, TeacherOptions teacher,
            string? purpose, string? receipt)
        {
            if (purpose != Purpose) throw new ArgumentException("Constructed raw purpose must be " + Purpose);
            if (options.Prior is null || options.SourceDrawSeeds is null || teacher.EvaluationSeeds is null || teacher.ExplorationSeeds is null)
                throw new ArgumentException("Declared setup prior and seed arrays are required");
            options = options with { Prior = options.Prior.Freeze(), SourceDrawSeeds = options.SourceDrawSeeds.ToArray() };
            teacher = teacher with { EvaluationSeeds = teacher.EvaluationSeeds.ToArray(), ExplorationSeeds = teacher.ExplorationSeeds.ToArray() };
            teacher.Validate();
            if (string.IsNullOrWhiteSpace(options.CollectionId) || options.CollectionId.Length > 100
                || options.SourceDrawSeeds is not { Length: > 0 and <= 16 }
                || options.SourceDrawSeeds.Distinct().Count() != options.SourceDrawSeeds.Length
                || options.SourceDrawSeeds.Intersect(teacher.EvaluationSeeds).Any()
                || options.WallBudgetSeconds is < 1 or > 900
                || teacher.Mode != "T0" || teacher.ExplorationSeeds.Length != 0 || teacher.FormalLabels
                || teacher.EvaluationSeeds.Length > 16 || teacher.MaxPosteriorAttempts > 4096 || teacher.MaxDecisions > 300)
                throw new ArgumentException("Constructed raw bounds:16 unique independent source draws,16 T0 evaluation worlds,4096 proposals,300 decisions,900 seconds; no formal labels");
            return new(options, teacher, NativeCompleteMapBuildReceipt.Validate(receipt));
        }

        internal string DrawId(int drawIndex) => Options.CollectionId + "/draw:" + drawIndex.ToString(CultureInfo.InvariantCulture);
        internal JsonObject Provenance(NativeTapeRecipe recipe)
        {
            string configuredSource = "constructed-native-tape-configured-source-v1:" + NativeCompleteMapDataset.Hash(PublicJson.Serialize(new
            {
                source_generation_identity = SourceGenerationIdentity,
                native_run_seed = recipe.IndependentRunSeed, tape_seed = recipe.TapeSeed,
            }));
            // Different setups/policies can still share primitive tape cells.
            // This broader alias intentionally keeps those correlated branches
            // together even when no identical public opening was retained.
            string randomFamily = "constructed-native-tape-random-family-v1:" + NativeCompleteMapDataset.Hash(PublicJson.Serialize(new
            {
                primitive_namespace = PrimitiveNamespaceJson,
                native_run_seed = recipe.IndependentRunSeed, tape_seed = recipe.TapeSeed,
            }));
            return new()
            {
                ["audit_only"] = new JsonObject
                {
                    ["actual_seed"] = recipe.SourceIdentity, ["source_run_group"] = randomFamily,
                    ["source_group_semantics"] = "conservative_primitive_rng_family",
                    ["underlying_battle_alias"] = configuredSource + "/combat:0",
                    ["source_random_family_alias"] = randomFamily,
                    ["source_generation_identity"] = SourceGenerationIdentity,
                    ["source_kind"] = SourceKind, ["source_prior_identity"] = Options.Prior.Identity,
                    ["native_run"] = true, ["native_default_start"] = false,
                },
            };
        }

        internal void CaptureRoot(JsonObject provenance, DecisionPacket root)
        {
            provenance["public_input"] = JsonNode.Parse(PublicJson.Serialize(PublicEvidenceInput.Extend(new
            {
                schema_version = PublicRunEvidence.CompleteMapStudentSchema, observation = root.Observation,
                history_complete = true, controller_context = new { status = "inactive" },
                candidate_actions = root.Actions, legal_mask = root.Actions.Select(_ => true).ToArray(),
            }, root)));
            var audit = provenance["audit_only"]!.AsObject();
            string entry = root.Observation!.History.Single(item => item.Kind == NativeEntryAssets.EventKind).Detail;
            string combat = audit["source_run_group"]!.GetValue<string>() + "/public-entry:" + NativeCompleteMapDataset.Hash(entry);
            audit["source_combat_id"] = combat;
            audit["branch_family"] = combat + "/tape-root-family";
            string digest = NativeCompleteMapDataset.Hash(PublicJson.Serialize(provenance["public_input"]));
            audit["public_state_digest"] = digest;
            audit["public_root_alias"] = "constructed-native-tape-public-root-v1:" + digest;
            audit["selected_public_decision_index"] = root.Actions[0].Revision;
        }

        internal JsonObject Record(TeacherResult result, JsonObject provenance, NativeConstructedTapeSource posterior,
            ExecutionAccountingSource accounting, ulong sourceDrawSeed, int drawIndex)
        {
            var sourceAudit = provenance["audit_only"]!.AsObject();
            var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result,
                sourceAudit["source_run_group"]!.GetValue<string>(), sourceAudit["source_combat_id"]!.GetValue<string>(),
                sourceAudit["branch_family"]!.GetValue<string>(), Teacher.EvaluationSeeds, Teacher.ExplorationSeeds)))!.AsObject();
            record["schema_version"] = RawSchemaVersion; record["record_kind"] = RawRecordKind;
            var audit = record["audit_only"]!.AsObject();
            foreach (var property in sourceAudit) audit[property.Key] = property.Value?.DeepClone();
            audit["dataset_version"] = RawSchemaVersion; audit["purpose"] = Purpose;
            audit["draw_id"] = DrawId(drawIndex); audit["source_draw_seed"] = JsonValue.Create(sourceDrawSeed);
            audit["declared_prior"] = JsonNode.Parse(PublicJson.Serialize(Options.Prior));
            audit["source_generation_json"] = SourceGenerationJson;
            audit["source_root_selection"] = Options.Prior.RootSelection;
            audit["source_seed_conditioning"] = false; audit["trainable"] = false; audit["formal_labels"] = false;
            audit["collection_contract_sha256"] = Sha256;
            audit["build_receipt_sha256"] = Receipt.Sha256; audit["runtime_dependencies"] = Receipt.Dependencies();
            audit["teacher_options"] = JsonNode.Parse(PublicJson.Serialize(Teacher));
            audit["posterior_implementation"] = ImplementationVersion; audit["sampler_version"] = ImplementationVersion;
            audit["conditioning_eligible"] = posterior.UsesPrimitiveConditioning; audit["conditioning_reason"] = posterior.ConditioningReason;
            audit["posterior_proposals"] = JsonNode.Parse(PublicJson.Serialize(posterior.ProposalAudit));
            audit["execution_accounting"] = JsonNode.Parse(PublicJson.Serialize(accounting.Snapshot()));
            audit["teacher_cost_semantics"] = "worlds_allocated_and_target_allocated_worlds_count_requested_outcome_slots_not_observed_allocations";
            audit["outcome_samples"] = JsonNode.Parse(PublicJson.Serialize(result.Candidates.Select((candidate, index) => new
            {
                action_index = index, world_seeds = Teacher.EvaluationSeeds, outcomes = candidate.Outcomes,
            })));
            var costs = audit["costs"]!.AsObject();
            costs["requested_outcome_slots"] = result.Candidates.Length * Teacher.EvaluationSeeds.Length;
            costs["sampled_worlds_returned"] = accounting.SampledWorlds;
            costs["candidate_worlds_returned"] = accounting.AllocatedBranches;
            costs["candidate_branches_started"] = accounting.StartedBranches;
            foreach (var target in record["targets"]!["actions"]!.AsArray())
            {
                int actionIndex = target!["action_index"]!.GetValue<int>();
                target["requested_worlds"] = Teacher.EvaluationSeeds.Length;
                target["candidate_worlds_returned"] = accounting.Samples.Sum(sample => sample.Branches.Count(branch => branch.ActionIndex == actionIndex && branch.WorldReturned));
                target["executed_worlds"] = accounting.Samples.Sum(sample => sample.Branches.Count(branch => branch.ActionIndex == actionIndex && branch.StepCalls > 0));
            }
            var versions = audit["versions"]!.AsObject();
            versions["dataset"] = RawSchemaVersion; versions["source_prior"] = Options.Prior.Identity;
            versions["sampler"] = ImplementationVersion; versions["posterior_implementation"] = ImplementationVersion;
            versions["posterior_profile"] = result.Scope;
            return record;
        }
    }

    /// <summary>
    /// Counts successful ownership transfers and attempted execution independently
    /// of the teacher's requested outcome slots. Missing samples have no branches.
    /// </summary>
    internal sealed class ExecutionAccountingSource(ITeacherSource source, CancellationToken token) : ITeacherSource
    {
        internal List<SampleExecution> Samples { get; } = [];
        internal int SampledWorlds => Samples.Count(sample => sample.WorldReturned);
        internal int AllocatedBranches => Samples.Sum(sample => sample.Branches.Count(branch => branch.WorldReturned));
        internal int StartedBranches => Samples.Sum(sample => sample.Branches.Count(branch => branch.StepCalls > 0));
        public int StartHp => source.StartHp;
        public int StartMaxHp => source.StartMaxHp;
        public string?[] StartPotions => source.StartPotions;
        public string PosteriorProfile => source.PosteriorProfile;
        public string PriorWarning => source.PriorWarning;
        public DecisionPacket Observe() => source.Observe();
        public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => source.RankingSupport(profile);
        public async Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
        {
            var sample = new SampleExecution(seed); Samples.Add(sample);
            try
            {
                token.ThrowIfCancellationRequested();
                sample.Started = true;
                var world = await source.SampleWorldAsync(seed, maxAttempts);
                sample.WorldReturned = true; sample.Status = "sampled";
                return new TrackedWorld(world, sample, null);
            }
            catch (Exception exception)
            {
                sample.Status = exception switch
                {
                    PosteriorSamplingException => "posterior_exhausted",
                    OperationCanceledException when !sample.Started => "not_executed_computation_cancelled",
                    OperationCanceledException => "computation_truncated",
                    _ => "engine_error",
                };
                sample.Detail = exception.GetType().Name + ": " + exception.Message;
                throw;
            }
        }
        internal object Snapshot() => new
        {
            sample_calls = Samples.Count, sample_calls_started = Samples.Count(sample => sample.Started),
            sampled_worlds_returned = SampledWorlds, candidate_worlds_returned = AllocatedBranches,
            fork_calls = Samples.Sum(sample => sample.Branches.Count), candidate_branches_started = StartedBranches,
            step_calls = Samples.Sum(sample => sample.Branches.Sum(branch => branch.StepCalls)),
            steps_returned = Samples.Sum(sample => sample.Branches.Sum(branch => branch.StepsReturned)), samples = Samples,
        };

        internal sealed class SampleExecution(ulong seed)
        {
            public ulong WorldSeed { get; } = seed;
            public bool Started { get; set; }
            public bool WorldReturned { get; set; }
            public string Status { get; set; } = "not_executed";
            public string? Detail { get; set; }
            public string? CleanupStatus { get; set; }
            public string? CleanupDetail { get; set; }
            public List<BranchExecution> Branches { get; } = [];
        }
        internal sealed class BranchExecution(int actionIndex)
        {
            public int ActionIndex { get; } = actionIndex;
            public bool WorldReturned { get; set; }
            public int StepCalls { get; set; }
            public int StepsReturned { get; set; }
            public bool SettlementRecorded { get; set; }
            public string? FailureStage { get; set; }
            public string? Detail { get; set; }
            public string? CleanupStatus { get; set; }
            public string? CleanupDetail { get; set; }
        }
        private sealed class TrackedWorld(ITeacherWorld world, SampleExecution sample, BranchExecution? branch) : ITeacherWorld
        {
            public int StartHp => world.StartHp;
            public int StartMaxHp => world.StartMaxHp;
            public string?[] StartPotions => world.StartPotions;
            public double SettlementSeconds => world.SettlementSeconds;
            public DecisionPacket Observe() => world.Observe();
            public async Task<ITeacherWorld> ForkForContinuationAsync()
            {
                var child = new BranchExecution(sample.Branches.Count); sample.Branches.Add(child);
                try
                {
                    var fork = await world.ForkForContinuationAsync(); child.WorldReturned = true;
                    return new TrackedWorld(fork, sample, child);
                }
                catch (Exception exception) { child.FailureStage = "fork"; child.Detail = exception.GetType().Name + ": " + exception.Message; throw; }
            }
            public async Task<DecisionPacket> StepAsync(PublicAction action)
            {
                if (branch is null) throw new InvalidOperationException("Teacher must fork a candidate before execution");
                branch.StepCalls++;
                try { var packet = await world.StepAsync(action); branch.StepsReturned++; return packet; }
                catch (Exception exception) { branch.FailureStage = "step"; branch.Detail = exception.GetType().Name + ": " + exception.Message; throw; }
            }
            public async Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn)
            {
                if (branch is null) throw new InvalidOperationException("Teacher must fork a candidate before settlement");
                try { var outcome = await world.RecordSettledAsync(policyId, lastPlayerTurn); branch.SettlementRecorded = true; return outcome; }
                catch (Exception exception) { branch.FailureStage = "settlement_record"; branch.Detail = exception.GetType().Name + ": " + exception.Message; throw; }
            }
            public async ValueTask DisposeAsync()
            {
                try
                {
                    await world.DisposeAsync();
                    if (branch is null) sample.CleanupStatus = "completed"; else branch.CleanupStatus = "completed";
                }
                catch (Exception exception)
                {
                    string detail = exception.GetType().Name + ": " + exception.Message;
                    if (branch is null) { sample.CleanupStatus = "failed"; sample.CleanupDetail = detail; }
                    else { branch.CleanupStatus = "failed"; branch.CleanupDetail = detail; }
                    throw;
                }
            }
        }
    }
}
