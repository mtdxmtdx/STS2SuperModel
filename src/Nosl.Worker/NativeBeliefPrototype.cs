using System.Text.Json.Nodes;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Bounded development evidence; never silently promote the rest of the native registry.</summary>
public static class NativeBeliefPrototype
{
    public static async Task<object> CollectAsync(NaturalSourceOptions options, TeacherOptions teacherOptions)
    {
        teacherOptions.Validate();
        if (options.MaxRoots > 200 || options.Runs > 100 || options.MaxFloors > 20
            || teacherOptions.Mode != "T0" || teacherOptions.FormalLabels || teacherOptions.EvaluationSeeds.Length > 16
            || teacherOptions.MaxDecisions > 300)
            throw new ArgumentException("Native prototype is bounded to 200 roots / 100 runs / 20 floors and development T0 / 16 worlds / 300 decisions");
        var records = new List<object>(); var blocked = new Dictionary<string, int>();
        int labeled = 0, allocated = 0, completed = 0;
        var source = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(options, async (root, boundary) =>
        {
            CombatSession session;
            try { session = CombatSession.ImportNative(root, boundary); }
            catch (NotSupportedException e)
            {
                blocked[e.Message] = blocked.GetValueOrDefault(e.Message) + 1;
                var raw = JsonNode.Parse(PublicJson.Serialize(root.ToSourceRecord()))!.AsObject();
                raw["audit_only"]!["posterior_reason"] = e.Message;
                records.Add(raw); return;
            }
            await using (session)
            {
                var result = await CombatTeacher.EvaluateAsync(session, teacherOptions);
                var record = JsonNode.Parse(PublicJson.Serialize(TeacherDataset.Record(result, root.SourceRunGroup,
                    root.SourceCombatId, root.SourceCombatId + "/native-root-family", teacherOptions.EvaluationSeeds, teacherOptions.ExplorationSeeds)))!.AsObject();
                var audit = record["audit_only"]!.AsObject();
                var sourceAudit = JsonNode.Parse(PublicJson.Serialize(root.ToSourceRecord()))!["audit_only"]!.AsObject();
                foreach (var (key, value) in sourceAudit) audit[key] = value?.DeepClone();
                audit["posterior_supported"] = true;
                audit["posterior_reason"] = null;
                audit["posterior_profile"] = NativeBeliefCertificate.Profile;
                audit["native_import"] = "certified_detached_boundary_clone";
                audit["source_seed_conditioning"] = false;
                audit["formal_labels"] = false;
                audit["trainable"] = false;
                audit["label_status"] = "development_teacher_prototype";
                audit["teacher_label_count"] = result.Candidates.Length;
                record["schema_version"] = "nosl.native-belief-prototype.v1";
                record["record_kind"] = "natural_development_teacher_candidate";
                records.Add(record); labeled++;
                allocated += result.Costs.WorldsAllocated; completed += result.Costs.WorldsCompleted;
            }
        });
        return new
        {
            schema_version = "nosl.native-belief-prototype-report.v1",
            status = "bounded_native_development_evidence_not_formal_labels",
            posteriorProfile = NativeBeliefCertificate.Profile,
            sourceOptions = options, teacherOptions,
            naturalSourceRoots = source.Roots.Length, naturalLabeledRoots = labeled,
            rawUnlabeledRoots = source.Roots.Length - labeled,
            worldsAllocated = allocated, worldsCompleted = completed,
            source.SourceDistribution, source.EncounterDistribution, source.Runs,
            unsupportedPosteriorReasons = blocked, records,
        };
    }
}
