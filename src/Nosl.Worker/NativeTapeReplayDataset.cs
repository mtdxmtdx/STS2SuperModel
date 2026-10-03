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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? EventPermutationMaxTrials { get; init; }
    public int WallBudgetSeconds { get; init; } = 300;
}

/// <summary>Predeclared bounded experiment. New law stays quarantined from pilot data.</summary>
internal static class NativeTapeReplayDataset
{
    internal const string DatasetVersion = "nosl.native-tape-replay-development.v1";
    internal const string ImplementationVersion = "nosl-native-tape-structured-conditional-v7";

    internal static string ImplementationFor(NativeRunExecutionOptions execution) => execution.EmitsPublicEvidence
        ? ImplementationVersion + "-public-evidence-v1" : ImplementationVersion;

    internal static string ImplementationFor(NativeTapePrior prior) => prior.UsesMapProvenance
        ? "nosl-native-map-rewards-tape-marginalized-v6-public-evidence-v2" : prior.UsesRewardsProvenance
        ? "nosl-native-rewards-state-tape-conditional-v12-public-evidence-v1" : ImplementationFor(prior.Execution);
    internal static string DatasetFor(NativeTapePrior prior) => prior.UsesMapProvenance
        ? "nosl.native-map-rewards-tape-replay-development.v1" : prior.UsesRewardsProvenance
        ? "nosl.native-rewards-tape-replay-development.v1" : DatasetVersion;

    internal static Task<object> CollectAsync(NativeTapeCollectionOptions options, TeacherOptions teacherOptions,
        CancellationToken cancellationToken = default) => CollectCoreAsync(options, teacherOptions, null, cancellationToken);

    // Only the fresh candidate API can create this prevalidated output contract.
    // The public diagnostic operation has no switch that can promote its records.
    internal static Task<object> CollectCompleteMapCandidatesAsync(NativeTapeCollectionOptions options,
        TeacherOptions teacherOptions, NativeCompleteMapDataset.CollectionContract contract,
        CancellationToken cancellationToken, Action? afterSourceDisposedForTests = null)
        => CollectCoreAsync(options, teacherOptions, contract, cancellationToken, afterSourceDisposedForTests);

    private static async Task<object> CollectCoreAsync(NativeTapeCollectionOptions options, TeacherOptions teacherOptions,
        NativeCompleteMapDataset.CollectionContract? candidateContract, CancellationToken cancellationToken,
        Action? afterSourceDisposedForTests = null)
    {
        if (options.Prior is null || options.SourceDrawSeeds is null
            || teacherOptions.EvaluationSeeds is null || teacherOptions.ExplorationSeeds is null)
            throw new ArgumentException("Declared prior and seed arrays are required");
        options = options with { Prior = options.Prior.Freeze(), SourceDrawSeeds = options.SourceDrawSeeds.ToArray(),
            EventPermutationMaxTrials = options.Prior.UsesRewardsProvenance
                ? options.EventPermutationMaxTrials ?? 256 : options.EventPermutationMaxTrials };
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
            || options.WallBudgetSeconds is < 1 or > 900 || options.InitialPrefixMaxTrials is < 1 or > 256
            || options.EventPermutationMaxTrials is < 1 or > 4096)
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
            int recordIndex = records.Count;
            var recipe = prior.Draw(new Rng(sourceDrawSeed, "nosl-native-tape-source-draw-v1"));
            var candidateProvenance = candidateContract?.SourceProvenance(recipe);
            void AddAttempt(object attempt) => attempts.Add(candidateContract is null ? attempt
                : candidateContract.Attempt(attempt, sourceDrawSeed, attemptIndex,
                    records.Count > recordIndex ? recordIndex : null, candidateProvenance!));
            var attemptTimer = Stopwatch.StartNew();
            if (budget.IsCancellationRequested)
            { AddAttempt(new { sourceDrawSeed, recipe, status = "not_executed_computation_cancelled", seconds = 0d }); continue; }
            try
            {
                var sourceWorld = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
                    NativeLabelTape.ForDeclaredPrior(prior, recipe), budget.Token);
                if (sourceWorld is null)
                { AddAttempt(new { sourceDrawSeed, recipe, status = "absent_under_declared_source_horizon", seconds = attemptTimer.Elapsed.TotalSeconds }); continue; }
                DecisionPacket publicRoot;
                try
                {
                    publicRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(sourceWorld.Observe()));
                    candidateContract?.CapturePublicRoot(candidateProvenance!, publicRoot);
                    // A successfully observed root exists even if releasing its
                    // source subsequently fails; failure still prevents labeling.
                    existing++;
                }
                finally
                {
                    await sourceWorld.DisposeAsync();
                    // Internal nonserialized exception seam: the actual world
                    // is always released before a test can inject cleanup failure.
                    afterSourceDisposedForTests?.Invoke();
                }
                // No original world, seed, tape, or source trace enters inference.
                var source = new NativeTapeReplaySource(publicRoot, prior, options.EnableConditioning, budget.Token, options.InitialPrefixMaxTrials,
                    eventPermutationMaxTrials: options.EventPermutationMaxTrials ?? 256);
                if (source.UsesPrimitiveConditioning) acceleratedRoots++;
                var result = await CombatTeacher.EvaluateAsync(source, teacherOptions);
                string runIdentity = NativePilotDataset.SourceIdentity(recipe.SourceIdentity);
                string sourceRun = (prior.UsesMapProvenance ? "native-map-rewards-tape-source-v1:" : prior.UsesRewardsProvenance ? "native-rewards-tape-source-v1:" : "native-tape-source-v1:") + runIdentity;
                string entry = publicRoot.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail;
                string sourceCombat = sourceRun + "/public-entry:" + Hash(entry);
                var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result, sourceRun, sourceCombat,
                    sourceCombat + "/tape-root-family", teacherOptions.EvaluationSeeds, teacherOptions.ExplorationSeeds)))!.AsObject();
                var audit = record["audit_only"]!.AsObject();
                record["schema_version"] = candidateContract is null ? DatasetFor(prior) : NativeCompleteMapDataset.RawSchemaVersion;
                record["record_kind"] = candidateContract is not null ? NativeCompleteMapDataset.RawRecordKind : prior.UsesMapProvenance ? "native_map_rewards_tape_replay_development_candidate" : prior.UsesRewardsProvenance ? "native_rewards_tape_replay_development_candidate" : "native_tape_replay_development_candidate";
                audit["dataset_version"] = DatasetFor(prior);
                audit["source_kind"] = "natural_under_explicit_label_tape_prior";
                audit["actual_seed"] = recipe.SourceIdentity;
                audit["native_source_run_identity"] = runIdentity;
                audit["native_run"] = true; audit["native_default_start"] = true;
                audit["source_prior"] = prior.SchemaVersion;
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
                if (prior.UsesRewardsProvenance)
                {
                    audit["neow_card_conditioning_eligible"] = source.UsesConditionalNeowCards;
                    if (source.UsesConditionalNeowPotions) audit["neow_potion_conditioning_eligible"] = true;
                    if (source.UsesConditionalPublicEventCards)
                    { audit["public_event_card_conditioning_eligible"] = true; audit["public_event_card_targets"] = source.PublicEventCardTargets; }
                    if (source.UsesConditionalPublicEventUpgrades)
                    { audit["public_event_upgrade_conditioning_eligible"] = true; audit["public_event_upgrade_targets"] = source.PublicEventUpgradeTargets; }
                    if (source.UsesConditionalPublicUnknownRooms)
                    {
                        audit["public_unknown_room_conditioning_eligible"] = true;
                        audit["public_unknown_room_targets"] = source.PublicUnknownRoomTargets;
                    }
                    if (source.UsesConditionalPublicShop)
                    {
                        audit["public_shop_conditioning_eligible"] = true;
                        audit["public_shop_offer_targets"] = source.PublicShopOfferTargets;
                        audit["public_shop_bag_targets"] = source.PublicShopBagTargets;
                    }
                    audit["public_opening_encounter_conditioning_eligible"] = source.UsesConditionalPublicOpeningEncounter;
                    audit["public_event_permutation_eligible"] = source.UsesConditionalEventPermutation;
                    audit["public_event_targets"] = source.PublicEventTargets;
                    audit["public_weak_encounter_sequence_eligible"] = source.UsesConditionalWeakEncounterSequence;
                    audit["public_weak_encounter_targets"] = source.WeakEncounterTargets;
                    audit["public_weak_encounter_prefix_length"] = source.WeakEncounterPrefixLength;
                    audit["public_resource_conditioning_eligible"] = source.UsesConditionalPublicResources;
                    audit["public_reward_conditioning_eligible"] = source.UsesConditionalPublicRewards;
                    audit["public_reward_targets"] = source.PublicRewardTargetCount;
                    audit["public_combat_prefix_conditioning_eligible"] = source.UsesConditionalPublicCombats;
                    audit["public_combat_shuffle_targets"] = source.PublicCombatShuffleTargets;
                    audit["public_combat_hp_targets"] = source.PublicCombatHpTargets;
                    audit["public_slug_intent_conditioning_eligible"] = source.UsesConditionalSlugIntents;
                    audit["public_slug_intent_targets"] = source.SlugIntentTargets;
                    audit["public_weak_formation_conditioning_eligible"] = source.UsesConditionalWeakFormation;
                    audit["public_reshuffle_conditioning_eligible"] = source.UsesConditionalReshuffles;
                    audit["public_reshuffle_targets"] = source.PublicReshuffleTargets;
                    audit["public_monster_branch_conditioning_eligible"] = source.UsesConditionalMonsterBranches;
                    audit["public_monster_roll_targets"] = source.PublicMonsterRollTargets;
                    audit["public_monster_branch_targets"] = source.PublicMonsterBranchTargets;
                    audit["public_reshuffle_conditioning"] = JsonNode.Parse(PublicJson.Serialize(source.PublicReshuffleDiagnostics));
                    // Report the actual post-reshuffle scan endpoint, not only the first-cycle stop.
                    audit["public_reshuffle_history"] = JsonNode.Parse(PublicJson.Serialize(source.PublicReshuffleHistoryDiagnostics));
                    audit["public_monster_roll_conditioning"] = JsonNode.Parse(PublicJson.Serialize(source.PublicMonsterRollDiagnostics));
                    audit["public_combat_conditioning"] = JsonNode.Parse(PublicJson.Serialize(source.PublicCombatDiagnostics));
                }
                if (prior.UsesMapProvenance)
                {
                    audit["public_complete_map_reconstruction_eligible"] = source.UsesPublicMapReconstruction;
                    audit["public_act_prefix_conditioning_eligible"] = source.UsesConditionalPublicActPrefix;
                    if (source.UsesConditionalPublicActPrefix)
                        audit["public_act_prefix_budget_source"] = "initialPrefixMaxTrials";
                    audit["map_marginalization_contract"] = NativePublicMapReconstructionCondition.SupportContract;
                    audit["public_map_observation_profile"] = prior.Execution.PublicMapObservationProfile;
                }
                audit["public_local_decision_conditioning"] = source.ConditionedPublicDecisionIndex;
                audit["public_combat_coordinate_conditioning"] = source.ConditionedPublicCombatIndex;
                audit["conditioning_reason"] = source.ConditioningReason;
                audit["formal_labels"] = false; audit["trainable"] = false; audit["engineering_smoke"] = true;
                audit["posterior_implementation"] = ImplementationFor(prior);
                audit["sampler_version"] = ImplementationFor(prior);
                audit["posterior_proposals"] = JsonNode.Parse(PublicJson.Serialize(source.ProposalAudit));
                audit["collection_id"] = options.CollectionId;
                audit["versions"]!["dataset"] = DatasetFor(prior);
                audit["versions"]!["sampler"] = ImplementationFor(prior);
                audit["versions"]!["posterior_implementation"] = ImplementationFor(prior);
                audit["versions"]!["source_prior"] = prior.Identity;
                candidateContract?.CompleteRecord(record, result, prior, teacherOptions, sourceDrawSeed, attemptIndex);
                records.Add(record);
                int acceptedHere = source.ProposalAudit.Count(a => a.Status == "accepted");
                accepted += acceptedHere; allocated += result.Costs.WorldsAllocated; settled += result.Costs.WorldsCompleted;
                publicRoots.Add(Hash(PublicJson.Serialize(publicRoot))); sourceRuns.Add(runIdentity);
                AddAttempt(new { sourceDrawSeed, recipe, status = "existing_root_evaluated", source.UsesConditionalShuffle,
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
                if (candidateContract is not null && records.Count > recordIndex)
                    records.RemoveRange(recordIndex, records.Count - recordIndex);
                AddAttempt(new { sourceDrawSeed, recipe,
                    status = exception is OperationCanceledException ? "computation_cancelled" : "source_engine_error",
                    detail = exception.GetType().Name + ": " + exception.Message, seconds = attemptTimer.Elapsed.TotalSeconds });
            }
        }
        var report = new
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
        return candidateContract is null ? report : candidateContract.Report(report);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
