using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeRunCollectionOptions
{
    public NativeRunPrior Prior { get; init; } = new();
    public ulong[] SourceDrawSeeds { get; init; } = [8001];
    public string CollectionId { get; init; } = "owned-native-engineering";
}

/// <summary>
/// Bounded engineering export for the new generative law. Existing prototype or
/// pilot archives are never converted, relabeled or admitted by this operation.
/// Every predeclared source draw, including absent and failed draws, is reported.
/// </summary>
internal static class NativeRunReplayDataset
{
    internal const string DatasetVersion = "nosl.native-owned-replay-development.v1";
    internal const string ImplementationVersion = "nosl-owned-native-run-replay-v1";

    internal static async Task<object> CollectAsync(NativeRunCollectionOptions options, TeacherOptions teacherOptions,
        CancellationToken cancellationToken = default)
    {
        // Caller-owned arrays must not change a predeclared experiment across awaits.
        if (options.Prior is null || options.SourceDrawSeeds is null
            || teacherOptions.EvaluationSeeds is null || teacherOptions.ExplorationSeeds is null)
            throw new ArgumentException("Declared prior and seed arrays are required");
        options = options with { Prior = options.Prior.Freeze(), SourceDrawSeeds = options.SourceDrawSeeds.ToArray() };
        teacherOptions = teacherOptions with { EvaluationSeeds = teacherOptions.EvaluationSeeds.ToArray(),
            ExplorationSeeds = teacherOptions.ExplorationSeeds.ToArray() };
        teacherOptions.Validate();
        var prior = options.Prior.Freeze();
        if (string.IsNullOrWhiteSpace(options.CollectionId) || options.CollectionId.Length > 100
            || options.SourceDrawSeeds is not { Length: > 0 and <= 16 }
            || options.SourceDrawSeeds.Distinct().Count() != options.SourceDrawSeeds.Length
            || options.SourceDrawSeeds.Intersect(teacherOptions.EvaluationSeeds.Concat(teacherOptions.ExplorationSeeds)).Any()
            || teacherOptions.FormalLabels || teacherOptions.EvaluationSeeds.Length > 16
            || teacherOptions.ExplorationSeeds.Length > 16 || teacherOptions.MaxPosteriorAttempts > 256
            || teacherOptions.MaxDecisions > 300)
            throw new ArgumentException("Owned native engineering requires at most16 predeclared distinct source draws, disjoint evaluation/exploration seeds,16 worlds,256 proposals and300 rollout decisions; no formal labels");
        var records = new List<object>(); var attempts = new List<object>();
        int existing = 0, allocated = 0, settled = 0, acceptedSamples = 0, acceptedEvaluation = 0, acceptedExploration = 0;
        var publicRoots = new HashSet<string>(StringComparer.Ordinal);
        var sourceRuns = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        foreach (ulong sourceDrawSeed in options.SourceDrawSeeds)
        {
            int attemptIndex = attempts.Count;
            var recipe = prior.Draw(new Rng(sourceDrawSeed, "nosl-owned-native-run-source-draw-v1"));
            var attemptTimer = Stopwatch.StartNew();
            if (cancellationToken.IsCancellationRequested)
            {
                attempts.Add(new { sourceDrawSeed, recipe, status = "not_executed_computation_cancelled", seconds = 0d });
                continue;
            }
            try
            {
                var sourceWorld = await NativeRunWorld.OpenAsync(prior.Execution,
                    recipe.IndependentRunSeed, recipe.Slot, cancellationToken);
                if (sourceWorld is null)
                {
                    attempts.Add(new { sourceDrawSeed, recipe, status = "absent_under_declared_source_horizon", seconds = attemptTimer.Elapsed.TotalSeconds });
                    continue;
                }
                // Only these detached public bytes cross into posterior inference.
                DecisionPacket publicRoot;
                try { publicRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(sourceWorld.Observe())); }
                finally { await sourceWorld.DisposeAsync(); }
                // The actual source graph has been released before proposing any
                // hypothetical world. Cleanup failure cannot commit labels/costs.
                existing++;
                var source = new NativeRunReplaySource(publicRoot, prior, cancellationToken);
                var result = await CombatTeacher.EvaluateAsync(source, teacherOptions);
                string runIdentity = NativePilotDataset.SourceIdentity(recipe.IndependentRunSeed);
                string sourceRun = "native-source-v1:" + runIdentity;
                string entry = publicRoot.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail;
                // Conservative within-run grouping cannot split identical entry aliases.
                string sourceCombat = sourceRun + "/public-entry:" + Hash(entry);
                var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result, sourceRun, sourceCombat,
                    sourceCombat + "/owned-root-family", teacherOptions.EvaluationSeeds, teacherOptions.ExplorationSeeds)))!.AsObject();
                var audit = record["audit_only"]!.AsObject();
                record["schema_version"] = DatasetVersion;
                record["record_kind"] = "owned_native_replay_development_candidate";
                audit["dataset_version"] = DatasetVersion;
                audit["source_kind"] = "natural";
                audit["actual_seed"] = recipe.IndependentRunSeed; // Audit only; never passed to source above.
                audit["native_source_run_identity"] = runIdentity;
                audit["native_run"] = true;
                audit["native_default_start"] = true;
                audit["source_prior"] = NativeRunPrior.Version;
                audit["source_prior_identity"] = prior.Identity;
                audit["declared_prior"] = JsonNode.Parse(PublicJson.Serialize(prior));
                audit["source_draw_seed"] = JsonValue.Create(sourceDrawSeed);
                audit["selected_source_slot"] = recipe.Slot;
                audit["source_seed_conditioning"] = false;
                audit["conditioning"] = "exact_published_decision_packet_only";
                audit["earlier_run_histories"] = "marginalized_under_declared_source_script";
                audit["formal_labels"] = false;
                audit["trainable"] = false;
                audit["engineering_smoke"] = true;
                audit["posterior_implementation"] = ImplementationVersion;
                audit["sampler_version"] = ImplementationVersion;
                audit["posterior_proposals"] = JsonNode.Parse(PublicJson.Serialize(source.ProposalAudit));
                audit["collection_id"] = options.CollectionId;
                audit["versions"]!["dataset"] = DatasetVersion;
                audit["versions"]!["sampler"] = ImplementationVersion;
                audit["versions"]!["posterior_implementation"] = ImplementationVersion;
                audit["versions"]!["source_prior"] = prior.Identity;
                records.Add(record);
                int accepted = source.ProposalAudit.Count(a => a.Status == "accepted");
                acceptedExploration += source.ProposalAudit.Count(a => a.Status == "accepted" && a.SampleCall <= teacherOptions.ExplorationSeeds.Length);
                acceptedEvaluation += source.ProposalAudit.Count(a => a.Status == "accepted" && a.SampleCall > teacherOptions.ExplorationSeeds.Length);
                publicRoots.Add(Hash(PublicJson.Serialize(publicRoot))); sourceRuns.Add(runIdentity);
                acceptedSamples += accepted; allocated += result.Costs.WorldsAllocated; settled += result.Costs.WorldsCompleted;
                attempts.Add(new { sourceDrawSeed, recipe, status = "existing_root_evaluated", acceptedPosteriorDraws = accepted,
                    proposalAttempts = source.ProposalAudit.Length, allocatedWorlds = result.Costs.WorldsAllocated,
                    settledWorlds = result.Costs.WorldsCompleted, seconds = attemptTimer.Elapsed.TotalSeconds });
            }
            catch (Exception exception)
            {
                // Even a cleanup failure belongs to this one predeclared draw.
                if (attempts.Count > attemptIndex) attempts.RemoveRange(attemptIndex, attempts.Count - attemptIndex);
                attempts.Add(new { sourceDrawSeed, recipe,
                    status = exception is OperationCanceledException ? "computation_cancelled" : "source_engine_error",
                    detail = exception.GetType().Name + ": " + exception.Message, seconds = attemptTimer.Elapsed.TotalSeconds });
            }
        }
        return new
        {
            schema_version = "nosl.native-owned-replay-report.v1", status = "bounded_generative_prior_engineering_not_quality_acceptance",
            prior, priorIdentity = prior.Identity, options.CollectionId, teacherOptions,
            sourceDrawsRequested = options.SourceDrawSeeds.Length, existingRoots = existing, recordedRoots = records.Count,
            distinctRecordedPublicRoots = publicRoots.Count, distinctRecordedSourceRuns = sourceRuns.Count,
            acceptedPosteriorDraws = acceptedSamples, acceptedExplorationDraws = acceptedExploration,
            acceptedIndependentEvaluationDraws = acceptedEvaluation, allocatedCandidateWorlds = allocated,
            settledCandidateWorlds = settled, elapsedSeconds = timer.Elapsed.TotalSeconds,
            peakWorkerMemoryBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            formalTraining = false, trainable = false, attempts, records,
        };
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
