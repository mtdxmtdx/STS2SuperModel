using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nosl.Contracts;

namespace Nosl.Worker;

public sealed record NativePilotCollection(string CollectionId, string ProtectionRegistrySha256,
    string Purpose = "engineering-smoke");

/// <summary>A new live collection path; never converts prototype archives into pilot data.</summary>
public static class NativePilotDataset
{
    public const string DatasetVersion = "nosl.dataset.native-pilot.v1";
    public const string CollectionVersion = "nosl.native-pilot-collection.v1";
    public const string SeedNamespace = "nosl-native-pilot/";

    public static async Task<object> CollectAsync(NaturalSourceOptions options, TeacherOptions teacherOptions,
        NativePilotCollection collection)
    {
        teacherOptions.Validate();
        if (string.IsNullOrWhiteSpace(collection.CollectionId) || collection.CollectionId.Length > 100
            || collection.CollectionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || collection.ProtectionRegistrySha256.Length != 64
            || collection.ProtectionRegistrySha256.Any(c => !"0123456789abcdef".Contains(c))
            || collection.Purpose is not ("engineering-smoke" or "pilot")
            || options.SeedPrefix != SeedNamespace + collection.CollectionId || options.SourceRunPrefix != collection.CollectionId)
            throw new ArgumentException("A fresh collection ID, its exact native-pilot seed namespace, and protection registry SHA256 are required");
        if (options.MaxRoots > 200 || options.Runs > 100 || options.MaxFloors > 20
            || teacherOptions.Mode != "T0" || teacherOptions.FormalLabels || teacherOptions.EvaluationSeeds.Length > 16
            || teacherOptions.MaxDecisions > 300 || teacherOptions.MaxPosteriorAttempts > 256 || options.MaxDecisionsPerRun > 10000)
            throw new ArgumentException("Native pilot engineering is bounded to 200 roots / 100 runs / 20 floors and T0 / 16 worlds / 300 decisions");
        var records = new List<object>(); var blocked = new Dictionary<string, int>();
        int certified = 0, usable = 0, allocated = 0, completed = 0;
        var source = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(options, async (root, boundary) =>
        {
            CombatSession session;
            try { session = await CombatSession.ImportNativeAsync(root, boundary); }
            catch (NotSupportedException e)
            {
                blocked[e.Message] = blocked.GetValueOrDefault(e.Message) + 1;
                var raw = JsonNode.Parse(PublicJson.Serialize(root.ToSourceRecord()))!.AsObject();
                raw["audit_only"]!["posterior_reason"] = e.Message;
                raw["audit_only"]!["posterior_evaluation"] = "unsupported";
                // Unsupported roots still retain public/source aliases for pre-filter protection.
                raw["audit_only"]!["native_collection"] = CollectionMetadata(collection);
                records.Add(raw); return;
            }
            await using (session)
            {
                var result = await CombatTeacher.EvaluateAsync(session, teacherOptions);
                var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result, root.SourceRunGroup,
                    root.SourceCombatId, root.SourceCombatId + "/native-root-family", teacherOptions.EvaluationSeeds,
                    teacherOptions.ExplorationSeeds)))!.AsObject();
                var audit = record["audit_only"]!.AsObject();
                var sourceAudit = JsonNode.Parse(PublicJson.Serialize(root.ToSourceRecord()))!["audit_only"]!.AsObject();
                foreach (var (key, value) in sourceAudit) audit[key] = value?.DeepClone();
                audit["native_collection"] = CollectionMetadata(collection);
                audit["posterior_supported"] = true;
                audit["posterior_evaluation"] = "supported";
                audit["posterior_reason"] = null;
                audit["posterior_profile"] = BeliefSampler.PosteriorProfileFor(session);
                audit["native_import"] = session.HasPendingChoice ? "certified_owned_stable_origin_choice_replay_v1" : "certified_detached_boundary_clone_v2";
                audit["source_seed_conditioning"] = false;
                audit["formal_labels"] = false;
                audit["trainable"] = true; // Dataset-admissible only; fitting has separate exact-bound gates.
                audit["engineering_smoke"] = collection.Purpose == "engineering-smoke";
                audit["label_status"] = "fresh_native_pilot_teacher";
                audit["teacher_label_count"] = result.Candidates.Length;
                audit["dataset_version"] = DatasetVersion;
                audit["versions"]!["dataset"] = DatasetVersion;
                audit["versions"]!["native_collection"] = CollectionVersion;
                audit["natural_reachability_evidence"] = "live_native_collector:" + collection.CollectionId;
                audit["native_source_run_identity"] = SourceIdentity(root.ActualSeed);
                audit["native_source_battle_identity"] = BattleIdentity(root.ActualSeed, root.Act, root.Floor, root.Encounter);
                audit["native_public_history"] = JsonNode.Parse(PublicJson.Serialize(root.PublicRoot.Observation!.History));
                audit["native_diagnostics"] = JsonNode.Parse(PublicJson.Serialize(new
                {
                    certified_root = true, outcome_diagnostics_required = true,
                    settled_worlds = result.Candidates.Sum(c => c.Outcomes.Count(o => o.IsTrueTerminal && o.SettlementComplete)),
                    unresolved_worlds = result.Candidates.Sum(c => c.Outcomes.Count(o => !o.IsTrueTerminal || !o.SettlementComplete)),
                    complete_hp_diagnostics = result.Candidates.SelectMany(c => c.Outcomes).Where(o => o.IsTrueTerminal)
                        .All(o => o.HpEventDiagnosticsComplete),
                    complete_resource_diagnostics = result.Candidates.SelectMany(c => c.Outcomes).Where(o => o.IsTrueTerminal)
                        .All(o => o.ResourceProvenanceComplete && o.InventorySnapshotsComplete && o.PermanentChangesComplete),
                }));
                audit["costs"]!["cost_unit"] = "candidate_action_world_execution";
                record["schema_version"] = DatasetVersion;
                record["record_kind"] = "fresh_native_pilot_teacher_candidate";
                records.Add(record); certified++;
                if (record["targets"]!["actions"]!.AsArray().Any(a => a!["masks"]!.AsObject().Any(m => m.Value!.GetValue<bool>()))) usable++;
                allocated += result.Costs.WorldsAllocated; completed += result.Costs.WorldsCompleted;
            }
        });
        // The source driver records callback failures as run diagnostics. Preserve
        // every collected root's aliases even if export failed before adding it.
        static string RootKey(JsonNode audit) => audit["source_combat_id"]!.GetValue<string>() + ":" + audit["decision_index"]!.GetValue<int>();
        var emitted = records.Select(r => RootKey(JsonNode.Parse(PublicJson.Serialize(r))!["audit_only"]!)).ToHashSet();
        foreach (var root in source.Roots)
        {
            var raw = JsonNode.Parse(PublicJson.Serialize(root.ToSourceRecord()))!.AsObject();
            if (emitted.Contains(RootKey(raw["audit_only"]!))) continue;
            raw["audit_only"]!["export_error"] = "native_pilot_teacher_export_incomplete";
            raw["audit_only"]!["native_collection"] = CollectionMetadata(collection);
            records.Add(raw);
        }
        return new
        {
            schema_version = "nosl.native-pilot-report.v1", status = "fresh_native_pilot_collection_not_quality_acceptance",
            collection, sourceOptions = options, teacherOptions, posteriorImplementation = BeliefSampler.ImplementationVersion,
            naturalSourceRoots = source.Roots.Length, certifiedRoots = certified, usableRoots = usable,
            rawUnlabeledRoots = source.Roots.Length - certified, worldsAllocated = allocated, worldsCompleted = completed,
            source.SourceDistribution, source.EncounterDistribution, source.Runs,
            unsupportedPosteriorReasons = blocked, records,
        };
    }

    private static JsonNode CollectionMetadata(NativePilotCollection c) => JsonNode.Parse(PublicJson.Serialize(new
    {
        schema_version = CollectionVersion, collection_id = c.CollectionId, purpose = c.Purpose,
        protection_registry_sha256 = c.ProtectionRegistrySha256, seed_namespace = SeedNamespace + c.CollectionId,
        export_origin = "live_native_boundary", distribution_accepted = false, formal_training_authorized = false,
    }))!;

    // Length-delimited strings avoid aliases caused by caller-controlled separators.
    internal static string SourceIdentity(string seed) => Digest("nosl.native-source-run.v1", "Silent", "10", seed);
    internal static string BattleIdentity(string seed, int act, int floor, string encounter) =>
        Digest("nosl.native-source-battle.v1", SourceIdentity(seed), act.ToString(System.Globalization.CultureInfo.InvariantCulture),
            floor.ToString(System.Globalization.CultureInfo.InvariantCulture), encounter);
    private static string Digest(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Concat(parts.Select(p => Encoding.UTF8.GetByteCount(p) + ":" + p))))).ToLowerInvariant();
}
