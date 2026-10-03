using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Fresh native producer boundary for bounded v5 raw candidates. This operation
/// accepts a prior and independent draw seeds, never a public graph or saved row.
/// Admission, objective calibration and training remain separate decisions.
/// </summary>
internal static class NativeCompleteMapDataset
{
    internal const string RawSchemaVersion = "nosl.native-complete-map.raw-candidate.v1";
    internal const string RawRecordKind = "native_complete_map_raw_candidate";
    internal const string ReportSchemaVersion = "nosl.native-complete-map.raw-report.v1";
    internal const string Purpose = "bounded-pilot";
    private const string SourceKind = "natural_under_explicit_label_tape_prior";
    private static readonly JsonSerializerOptions RequestJson = new(PublicJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static Task<object> CollectAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        RequireKeys(request, ["op", "options", "teacherOptions", "purpose", "build_receipt_json"]);
        if (request.GetProperty("op").GetString() != "native_complete_map_candidates")
            throw new ArgumentException("Fresh complete-map candidate operation required");
        RejectDuplicateKeys(request);
        var options = JsonSerializer.Deserialize<NativeTapeCollectionOptions>(request.GetProperty("options"), RequestJson)
            ?? throw new ArgumentException("Candidate collection options required");
        var teacher = JsonSerializer.Deserialize<TeacherOptions>(request.GetProperty("teacherOptions"), RequestJson)
            ?? throw new ArgumentException("Candidate teacher options required");
        var contract = CollectionContract.Create(options, teacher, request.GetProperty("purpose").GetString(),
            request.GetProperty("build_receipt_json").GetString());
        return NativeTapeReplayDataset.CollectCompleteMapCandidatesAsync(options, teacher, contract, cancellationToken);
    }

    internal static object RuntimeIdentity(JsonElement request)
    {
        RequireKeys(request, ["op"]);
        if (request.GetProperty("op").GetString() != "native_complete_map_runtime_identity")
            throw new ArgumentException("Complete-map runtime identity operation required");
        return NativeCompleteMapBuildReceipt.RuntimeIdentity();
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate candidate request key: " + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    internal static void RequireKeys(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("JSON object required");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != keys.Length || actual.Distinct(StringComparer.Ordinal).Count() != keys.Length
            || actual.Except(keys, StringComparer.Ordinal).Any())
            throw new ArgumentException("Exact keys required: " + string.Join(",", keys));
    }

    internal static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Created and validated before the shared collector draws its first source.
    // No serializer can create this contract and there is no persisted-row entry point.
    internal sealed class CollectionContract
    {
        private readonly string _collectionId;
        private readonly NativeCompleteMapBuildReceipt _receipt;

        private CollectionContract(string collectionId, NativeCompleteMapBuildReceipt receipt)
        { _collectionId = collectionId; _receipt = receipt; }

        internal static CollectionContract Create(NativeTapeCollectionOptions options, TeacherOptions teacher,
            string? purpose, string? receipt)
        {
            if (purpose != Purpose) throw new ArgumentException("Fresh candidate purpose must be bounded-pilot");
            var prior = options.Prior?.Freeze() ?? throw new ArgumentException("Declared complete-map prior required");
            if (!prior.UsesMapProvenance || prior.Execution.PublicContextProfile != PublicRunContext.Version
                || PublicRunContext.IsHistoryUnavailable(prior.Execution.PublicCombatHistoryMode)
                || prior.Execution.OutsideCombatScript != NaturalSourceCollector.BoundedEventScriptVersion
                || prior.Execution.SourcePolicyId != PublicContinuationPolicies.ReviewedId)
                throw new ArgumentException("Fresh candidates require the explicit complete-map prior, complete public context and reviewed v3 source script");
            if (teacher.Mode != "T0" || teacher.ContinuationPolicyId != PublicContinuationPolicies.ReviewedId
                || teacher.ExplorationSeeds is not { Length: 0 } || teacher.FormalLabels
                || options.CompareUnconditioned || !options.EnableConditioning)
                throw new ArgumentException("Fresh candidates require conditioned T0, reviewed continuation, no exploration, reference comparison or formal labels");
            if (string.IsNullOrWhiteSpace(options.CollectionId)) throw new ArgumentException("Collection identity required");
            return new(options.CollectionId, NativeCompleteMapBuildReceipt.Validate(receipt));
        }

        private string DrawId(int drawIndex) => _collectionId + "/draw:" + drawIndex.ToString(CultureInfo.InvariantCulture);

        internal JsonObject SourceProvenance(NativeTapeRecipe recipe)
        {
            string identity = NativePilotDataset.SourceIdentity(recipe.SourceIdentity);
            return new() { ["audit_only"] = new JsonObject
            {
                ["actual_seed"] = recipe.SourceIdentity, ["native_source_run_identity"] = identity,
                ["source_kind"] = SourceKind, ["native_run"] = true,
                ["source_run_group"] = "native-map-rewards-tape-source-v1:" + identity,
            } };
        }

        internal void CapturePublicRoot(JsonObject provenance, DecisionPacket root)
        {
            // This root has just been generated by the declared native producer,
            // serialized through the public codec and detached from its source world.
            var input = JsonNode.Parse(PublicJson.Serialize(PublicEvidenceInput.Extend(new
            {
                schema_version = PublicRunEvidence.CompleteMapStudentSchema, observation = root.Observation,
                history_complete = true, controller_context = new { status = "inactive" },
                candidate_actions = root.Actions, legal_mask = root.Actions.Select(_ => true).ToArray(),
            }, root)))!;
            provenance["public_input"] = input;
            var audit = provenance["audit_only"]!.AsObject();
            string entry = root.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail;
            string combat = audit["source_run_group"]!.GetValue<string>() + "/public-entry:" + Hash(entry);
            audit["source_combat_id"] = combat;
            audit["branch_family"] = combat + "/tape-root-family";
            audit["public_state_digest"] = Hash(PublicJson.Serialize(input));
        }

        internal void CompleteRecord(JsonObject record, TeacherResult result, NativeTapePrior prior,
            TeacherOptions teacher, ulong sourceDrawSeed, int drawIndex)
        {
            var audit = record["audit_only"]!.AsObject();
            audit["purpose"] = Purpose;
            audit["draw_id"] = DrawId(drawIndex);
            audit["dataset_version"] = RawSchemaVersion;
            audit["engineering_smoke"] = false;
            audit["runtime_dependencies"] = _receipt.Dependencies();
            audit["build_receipt_sha256"] = _receipt.Sha256;
            audit["source_draw_seed"] = JsonValue.Create(sourceDrawSeed);
            audit["outcome_samples"] = JsonNode.Parse(PublicJson.Serialize(result.Candidates.Select((candidate, index) => new
            {
                action_index = index, world_seeds = teacher.EvaluationSeeds, outcomes = candidate.Outcomes,
            }).ToArray()));
            audit["controller_version"] = "nosl.controller.inactive.v1";
            audit["versions"] = JsonNode.Parse(PublicJson.Serialize(new
            {
                public_schema = PublicRunEvidence.CompleteMapStudentSchema,
                public_evidence = PublicRunEvidence.CompleteMapVersion,
                observation_schema = result.PublicRoot.Observation!.Schema,
                map_profile = prior.Execution.PublicMapObservationProfile,
                map_support = NativePublicMapReconstructionCondition.SupportContract,
                sampler = NativeTapeReplayDataset.ImplementationFor(prior), posterior_profile = result.Scope,
                teacher = result.TeacherVersion, continuation = result.ContinuationVersion,
                objective = result.ObjectiveVersion, controller = "nosl.controller.inactive.v1",
                endpoint = audit["label_endpoint"]!.GetValue<string>(), rules = audit["rules_version"]!.GetValue<string>(),
                simulator = NativeCompleteMapBuildReceipt.UpstreamCommit,
                source_script = prior.Execution.ResolvedOutsideCombatScript, source_prior = prior.Identity,
                dataset = RawSchemaVersion,
            }));
        }

        internal object Attempt(object attempt, ulong sourceDrawSeed, int drawIndex, int? recordIndex, JsonObject provenance)
        {
            var detail = JsonNode.Parse(PublicJson.Serialize(attempt))!.AsObject();
            string sourceStatus = detail["status"]!.GetValue<string>();
            string status = sourceStatus switch
            {
                "existing_root_evaluated" => "recorded",
                "absent_under_declared_source_horizon" => "absent",
                "not_executed_computation_cancelled" => "not_executed",
                _ => "failed",
            };
            // Timing and recipe evidence stay in detail; no requested attempt is
            // selected away because its source or posterior failed to complete.
            return new { draw_id = DrawId(drawIndex), source_draw_seed = sourceDrawSeed, status,
                raw_record_index = recordIndex, detail = PublicJson.Serialize(detail), provenance = provenance.DeepClone() };
        }

        internal object Report(object report)
        {
            var result = JsonNode.Parse(PublicJson.Serialize(report))!.AsObject();
            result["schema_version"] = ReportSchemaVersion;
            result["status"] = "bounded_fresh_candidates_require_separate_quality_admission";
            result["purpose"] = Purpose;
            result["selection"] = "predeclared_all_attempts_no_replacement";
            result["build_receipt_json"] = _receipt.Json;
            result["build_receipt_sha256"] = _receipt.Sha256;
            result["runtime_dependencies"] = _receipt.Dependencies();
            result["formal_labels"] = false;
            return result;
        }
    }
}

/// <summary>
/// Exact-byte receipt binding and loaded assembly-file verification. Source
/// hashes are declared build assertions, never proof that binaries came from them.
/// </summary>
internal sealed class NativeCompleteMapBuildReceipt
{
    internal const string UpstreamCommit = "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0";
    private static readonly string[] Keys = ["upstream_commit", "wrapper_source_sha256", "vendor_source_sha256",
        "worker_assembly_sha256", "core_assembly_sha256", "runtime_version"];
    private readonly JsonObject _dependencies;
    internal string Json { get; }
    internal string Sha256 { get; }
    private NativeCompleteMapBuildReceipt(string json, JsonObject dependencies)
    { Json = json; Sha256 = NativeCompleteMapDataset.Hash(json); _dependencies = dependencies; }
    internal JsonObject Dependencies() => (JsonObject)_dependencies.DeepClone();

    internal static object RuntimeIdentity() => new
    {
        upstream_commit = UpstreamCommit,
        worker_assembly_sha256 = AssemblyHash(typeof(NativeCompleteMapDataset).Assembly.Location),
        core_assembly_sha256 = AssemblyHash(typeof(Rng).Assembly.Location),
        runtime_version = Environment.Version.ToString(),
    };

    private static string AssemblyHash(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new NotSupportedException("Candidate receipts require loaded assembly files");
        return NativeCompleteMapDataset.Hash(File.ReadAllBytes(path));
    }

    internal static NativeCompleteMapBuildReceipt Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 16384)
            throw new ArgumentException("Exact build receipt JSON string required (at most 16384 UTF-8 bytes)");
        using var doc = JsonDocument.Parse(json);
        NativeCompleteMapDataset.RequireKeys(doc.RootElement, Keys);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new ArgumentException("Build receipt values must be nonempty strings");
            if (property.Name.EndsWith("_sha256", StringComparison.Ordinal))
            {
                string sha = property.Value.GetString()!;
                if (sha.Length != 64 || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new ArgumentException("Build receipt SHA256 values must be lowercase hexadecimal");
            }
        }
        var dependencies = JsonNode.Parse(json)!.AsObject();
        var actual = JsonNode.Parse(PublicJson.Serialize(RuntimeIdentity()))!.AsObject();
        foreach (var property in actual)
            if (dependencies[property.Key]!.GetValue<string>() != property.Value!.GetValue<string>())
                throw new ArgumentException("Build receipt does not match loaded runtime: " + property.Key);
        return new(json, dependencies);
    }
}
