using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeTapeCollectionOptions
{
    public NativeTapePrior Prior { get; init; } = new();
    public ulong[] SourceDrawSeeds { get; init; } = [8001];
    public string CollectionId { get; init; } = "native-tape-engineering";
    public bool EnableConditioning { get; init; } = true;
    public bool CompareUnconditioned { get; init; }
    public int ReferenceMaxAttempts { get; init; } = 16;
    public int InitialPrefixMaxTrials { get; init; } = 64;
    public int WallBudgetSeconds { get; init; } = 300;
}

/// <summary>Predeclared bounded experiment. New law stays quarantined from pilot data.</summary>
internal static class NativeTapeReplayDataset
{
    internal const string DatasetVersion = "nosl.native-tape-replay-development.v1";
    internal const string ImplementationVersion = "nosl-native-tape-structured-conditional-v7";

    internal static async Task<object> CollectAsync(NativeTapeCollectionOptions options, TeacherOptions teacherOptions,
        CancellationToken cancellationToken = default)
    {
        if (options.Prior is null || options.SourceDrawSeeds is null
            || teacherOptions.EvaluationSeeds is null || teacherOptions.ExplorationSeeds is null)
            throw new ArgumentException("Declared prior and seed arrays are required");
        options = options with { Prior = options.Prior.Freeze(), SourceDrawSeeds = options.SourceDrawSeeds.ToArray() };
        teacherOptions = teacherOptions with { EvaluationSeeds = teacherOptions.EvaluationSeeds.ToArray(),
            ExplorationSeeds = teacherOptions.ExplorationSeeds.ToArray() };
        teacherOptions.Validate();
        if (string.IsNullOrWhiteSpace(options.CollectionId) || options.CollectionId.Length > 100
            || options.SourceDrawSeeds is not { Length: > 0 and <= 16 }
            || options.SourceDrawSeeds.Distinct().Count() != options.SourceDrawSeeds.Length
            || options.SourceDrawSeeds.Intersect(teacherOptions.EvaluationSeeds.Concat(teacherOptions.ExplorationSeeds)).Any()
            || teacherOptions.FormalLabels || teacherOptions.EvaluationSeeds.Length > 16
            || teacherOptions.ExplorationSeeds.Length > 16 || teacherOptions.MaxPosteriorAttempts > 4096
            || teacherOptions.MaxDecisions > 300 || options.ReferenceMaxAttempts is < 1 or > 256
            || options.WallBudgetSeconds is < 1 or > 900 || options.InitialPrefixMaxTrials is < 1 or > 256)
            throw new ArgumentException("Tape engineering bounds:16 source draws,16 worlds,4096 proposals,300 decisions,900 seconds; no formal labels");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.WallBudgetSeconds));
        var prior = options.Prior;
        var records = new List<object>(); var attempts = new List<object>(); var references = new List<object>();
        var timer = Stopwatch.StartNew();
        int existing = 0, acceleratedRoots = 0, allocated = 0, settled = 0, accepted = 0;
        var publicRoots = new HashSet<string>(); var sourceRuns = new HashSet<string>();
        foreach (ulong sourceDrawSeed in options.SourceDrawSeeds)
        {
            int attemptIndex = attempts.Count;
            var recipe = prior.Draw(new Rng(sourceDrawSeed, "nosl-native-tape-source-draw-v1"));
            var attemptTimer = Stopwatch.StartNew();
            if (budget.IsCancellationRequested)
            { attempts.Add(new { sourceDrawSeed, recipe, status = "not_executed_computation_cancelled", seconds = 0d }); continue; }
            try
            {
                var sourceWorld = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
                    new NativeLabelTape(recipe), budget.Token);
                if (sourceWorld is null)
                { attempts.Add(new { sourceDrawSeed, recipe, status = "absent_under_declared_source_horizon", seconds = attemptTimer.Elapsed.TotalSeconds }); continue; }
                DecisionPacket publicRoot;
                try { publicRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(sourceWorld.Observe())); }
                finally { await sourceWorld.DisposeAsync(); }
                existing++;
                // No original world, seed, tape, or source trace enters inference.
                var source = new NativeTapeReplaySource(publicRoot, prior, options.EnableConditioning, budget.Token, options.InitialPrefixMaxTrials);
                if (source.UsesPrimitiveConditioning) acceleratedRoots++;
                var result = await CombatTeacher.EvaluateAsync(source, teacherOptions);
                string runIdentity = NativePilotDataset.SourceIdentity(recipe.SourceIdentity);
                string sourceRun = "native-tape-source-v1:" + runIdentity;
                string entry = publicRoot.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail;
                string sourceCombat = sourceRun + "/public-entry:" + Hash(entry);
                var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result, sourceRun, sourceCombat,
                    sourceCombat + "/tape-root-family", teacherOptions.EvaluationSeeds, teacherOptions.ExplorationSeeds)))!.AsObject();
                var audit = record["audit_only"]!.AsObject();
                record["schema_version"] = DatasetVersion;
                record["record_kind"] = "native_tape_replay_development_candidate";
                audit["dataset_version"] = DatasetVersion;
                audit["source_kind"] = "natural_under_explicit_label_tape_prior";
                audit["actual_seed"] = recipe.SourceIdentity;
                audit["native_source_run_identity"] = runIdentity;
                audit["native_run"] = true; audit["native_default_start"] = true;
                audit["source_prior"] = NativeTapePrior.Version;
                audit["source_prior_identity"] = prior.Identity;
                audit["declared_prior"] = JsonNode.Parse(PublicJson.Serialize(prior));
                audit["source_draw_seed"] = JsonValue.Create(sourceDrawSeed);
                audit["selected_combat_index"] = recipe.CombatIndex;
                audit["selected_decision_index"] = recipe.DecisionIndex;
                audit["source_seed_conditioning"] = false;
                audit["conditioning"] = source.UsesPrimitiveConditioning
                    ? "exact_published_packet_with_certified_primitive_proposals_and_root_constant_corrections"
                    : "exact_published_packet_plain_tape_rejection_with_entry_and_public_coordinate_filters";
                audit["conditioning_eligible"] = source.UsesPrimitiveConditioning;
                audit["initial_shuffle_conditioning_eligible"] = source.UsesConditionalShuffle;
                audit["initial_hp_conditioning_eligible"] = source.UsesConditionalHp;
                audit["neow_conditioning_eligible"] = source.UsesConditionalNeow;
                audit["first_reward_conditioning_eligible"] = source.UsesConditionalFirstReward;
                audit["first_encounter_conditioning_eligible"] = source.UsesConditionalFirstEncounter;
                audit["initial_prefix_conditioning_eligible"] = source.UsesConditionalInitialPrefix;
                audit["public_local_decision_conditioning"] = source.ConditionedPublicDecisionIndex;
                audit["public_combat_coordinate_conditioning"] = source.ConditionedPublicCombatIndex;
                audit["conditioning_reason"] = source.ConditioningReason;
                audit["formal_labels"] = false; audit["trainable"] = false; audit["engineering_smoke"] = true;
                audit["posterior_implementation"] = ImplementationVersion;
                audit["sampler_version"] = ImplementationVersion;
                audit["posterior_proposals"] = JsonNode.Parse(PublicJson.Serialize(source.ProposalAudit));
                audit["collection_id"] = options.CollectionId;
                audit["versions"]!["dataset"] = DatasetVersion;
                audit["versions"]!["sampler"] = ImplementationVersion;
                audit["versions"]!["posterior_implementation"] = ImplementationVersion;
                audit["versions"]!["source_prior"] = prior.Identity;
                records.Add(record);
                int acceptedHere = source.ProposalAudit.Count(a => a.Status == "accepted");
                accepted += acceptedHere; allocated += result.Costs.WorldsAllocated; settled += result.Costs.WorldsCompleted;
                publicRoots.Add(Hash(PublicJson.Serialize(publicRoot))); sourceRuns.Add(runIdentity);
                attempts.Add(new { sourceDrawSeed, recipe, status = "existing_root_evaluated", source.UsesConditionalShuffle,
                    source.UsesConditionalHp, source.UsesConditionalNeow, source.UsesConditionalFirstReward, source.UsesConditionalFirstEncounter, source.UsesConditionalInitialPrefix,
                    source.ConditioningReason, acceptedPosteriorDraws = acceptedHere, proposalAttempts = source.ProposalAudit.Length,
                    allocatedWorlds = result.Costs.WorldsAllocated, settledWorlds = result.Costs.WorldsCompleted,
                    seconds = attemptTimer.Elapsed.TotalSeconds });

                if (options.CompareUnconditioned)
                {
                    var reference = new NativeTapeReplaySource(publicRoot, prior, false, budget.Token);
                    var referenceOptions = teacherOptions with { MaxPosteriorAttempts = options.ReferenceMaxAttempts };
                    var referenceTimer = Stopwatch.StartNew();
                    try
                    {
                        var referenceResult = await CombatTeacher.EvaluateAsync(reference, referenceOptions);
                        references.Add(new { sourceDrawSeed, referenceOptions, proposals = reference.ProposalAudit,
                            acceptedPosteriorDraws = reference.ProposalAudit.Count(a => a.Status == "accepted"),
                            costs = referenceResult.Costs, elapsedSeconds = referenceTimer.Elapsed.TotalSeconds });
                    }
                    catch (Exception exception)
                    {
                        references.Add(new { sourceDrawSeed, referenceOptions, proposals = reference.ProposalAudit,
                            status = exception is OperationCanceledException ? "computation_cancelled" : "reference_engine_error",
                            detail = exception.GetType().Name + ": " + exception.Message,
                            elapsedSeconds = referenceTimer.Elapsed.TotalSeconds });
                    }
                }
            }
            catch (Exception exception)
            {
                if (attempts.Count > attemptIndex) attempts.RemoveRange(attemptIndex, attempts.Count - attemptIndex);
                attempts.Add(new { sourceDrawSeed, recipe,
                    status = exception is OperationCanceledException ? "computation_cancelled" : "source_engine_error",
                    detail = exception.GetType().Name + ": " + exception.Message, seconds = attemptTimer.Elapsed.TotalSeconds });
            }
        }
        return new
        {
            schema_version = "nosl.native-tape-replay-report.v1", status = "bounded_conditional_tape_engineering_not_production_admission",
            prior, priorIdentity = prior.Identity, options, teacherOptions,
            sourceDrawsRequested = options.SourceDrawSeeds.Length, existingRoots = existing, recordedRoots = records.Count,
            distinctRecordedPublicRoots = publicRoots.Count, distinctRecordedSourceRuns = sourceRuns.Count,
            acceleratedRoots, acceptedPosteriorDraws = accepted, allocatedCandidateWorlds = allocated,
            settledCandidateWorlds = settled, elapsedSeconds = timer.Elapsed.TotalSeconds,
            peakWorkerMemoryBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            formalTraining = false, trainable = false, budgetExpired = budget.IsCancellationRequested,
            attempts, references, records,
        };
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
