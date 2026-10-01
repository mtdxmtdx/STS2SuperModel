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
        if(op=="reset")
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
                var sampled=BeliefSampler.SampleWorld(session,root.GetProperty("samplerSeed").GetUInt64());
                await session.DisposeAsync(); session=sampled; result=session.Observe();
            }
            else result=op switch
            {
                "observe"=>session.Observe(),
                "actions"=>session.Observe().Actions,
                "step"=>await session.StepAsync(PublicJson.Read<PublicAction>(root.GetProperty("action").GetRawText())),
                "settle"=>await session.SettleAsync(),
                _=>throw new ArgumentException("Unknown command"),
            };
        }
        Console.WriteLine(PublicJson.Serialize(result));
    }
    catch(Exception e) { Console.WriteLine(PublicJson.Serialize(new {status=e is NotSupportedException?"unsupported_content":"invalid_operation",message=e.Message})); }
}
if(session is not null) await session.DisposeAsync();
