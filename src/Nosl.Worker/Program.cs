using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;

// Sequential persistent JSONL worker; all private simulation objects stay in this process.
// Diagnostic metadata is never mixed into a DecisionPacket consumed by policy.
if(args.Length==2 && args[0]=="--audit") { ContentAudit.Write(args[1]); Console.WriteLine("COVERAGE_MANIFEST_WRITTEN"); return; }
CombatSession? session=null;
while(Console.ReadLine() is { } line)
{
    try
    {
        using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
        string op=root.GetProperty("op").GetString()!;
        object result;
        if(op=="continuation_policies")
        {
            result=new {status="available",version="nosl.continuation-policies.v1",
                supportedPolicyIds=new[] {PublicContinuationPolicies.LegacyId,PublicContinuationPolicies.ReviewedId}};
        }
        else if(op=="native_belief_prototype")
        {
            var options=root.TryGetProperty("options",out var opt)?PublicJson.Read<NaturalSourceOptions>(opt.GetRawText()):new NaturalSourceOptions();
            var teacherOptions=root.TryGetProperty("teacherOptions",out var teacherOpt)?PublicJson.Read<TeacherOptions>(teacherOpt.GetRawText()):new TeacherOptions { EvaluationSeeds=[101,102] };
            result=await NativeBeliefPrototype.CollectAsync(options,teacherOptions);
        }
        else if(op=="natural_sources")
        {
            var options=root.TryGetProperty("options",out var opt)?PublicJson.Read<NaturalSourceOptions>(opt.GetRawText()):new NaturalSourceOptions();
            var report=await NaturalSourceCollector.CollectAsync(options);
            result=new {status=report.Status, naturalRawRoots=report.NaturalRawRoots,naturalLabeledRoots=report.NaturalLabeledRoots,
                sourceDistribution=report.SourceDistribution,encounterDistribution=report.EncounterDistribution,
                unsupportedPosteriorReasons=report.UnsupportedPosteriorReasons,runs=report.Runs,
                records=report.Roots.Select(r=>r.ToSourceRecord()).ToArray()};
        }
        else if(op=="reset")
        {
            if(session is not null) await session.DisposeAsync();
            var scenario=root.TryGetProperty("scenario",out var s)?PublicJson.Read<Scenario>(s.GetRawText()):new Scenario();
            session=await CombatSession.CreateAsync(scenario); result=session.Observe();
        }
        else
        {
            if(session is null) throw new InvalidOperationException("reset required");
            if(op=="sample")
            {
                var sampled=await BeliefSampler.SampleWorldAsync(session,root.GetProperty("samplerSeed").GetUInt64(),root.TryGetProperty("maxAttempts",out var budget)?budget.GetInt32():256);
                await session.DisposeAsync(); session=sampled; result=session.Observe();
            }
            else if(op=="teacher" || op=="teacher_record")
            {
                var options=root.TryGetProperty("options",out var opt)?PublicJson.Read<TeacherOptions>(opt.GetRawText()):new TeacherOptions();
                var teaching=await CombatTeacher.EvaluateAsync(session,options);
                result=op=="teacher"?teaching:TeacherDataset.Record(teaching,
                    root.GetProperty("sourceRun").GetString()!,root.GetProperty("sourceCombat").GetString()!,
                    root.GetProperty("branchFamily").GetString()!,options.EvaluationSeeds,options.ExplorationSeeds);
            }
            else result=op switch
            {
                "observe"=>session.Observe(),
                "continue"=>await session.StepAsync(PublicContinuationPolicies.Create(root.TryGetProperty("continuationPolicyId",out var policyId)
                    ? policyId.GetString()! : PublicContinuationPolicies.LegacyId).Choose(session.Observe())),
                "actions"=>session.Observe().Actions,
                "step"=>await session.StepAsync(PublicJson.Read<PublicAction>(root.GetProperty("action").GetRawText())),
                "settle"=>await session.SettleAsync(),
                _=>throw new ArgumentException("Unknown command"),
            };
        }
        Console.WriteLine(PublicJson.Serialize(result));
    }
    catch(Exception e) { Console.WriteLine(PublicJson.Serialize(new {status=e is PosteriorSamplingException?"posterior_budget_exhausted":e is NotSupportedException?"unsupported_capability":"invalid_operation",message=e.Message})); }
}
if(session is not null) await session.DisposeAsync();
