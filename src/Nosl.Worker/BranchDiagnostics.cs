using Nosl.Contracts;

namespace Nosl.Worker;

public sealed record ProbeSample(string Result, int? FinalHp);
public sealed record CandidateProbe(PublicAction Action, ProbeSample[] Outcomes, int Completed, int Truncated, int Errors);

// M2 isolation diagnostic only: no V4 utility, search, training labels, or optimality claim.
public static class BranchDiagnostics
{
    public static async Task<CandidateProbe[]> EvaluateAsync(CombatSession source, ulong[] samplerSeeds, int maxDecisions=200)
    {
        if(samplerSeeds.Length==0 || maxDecisions<=0) throw new ArgumentException("Positive diagnostic budget required");
        var actions=source.Observe().Actions;
        var rows=actions.Select(_=>new List<ProbeSample>()).ToArray();
        foreach(var seed in samplerSeeds)
        {
            await using var world=BeliefSampler.SampleWorld(source,seed);
            for(int i=0;i<actions.Length;i++)
            {
                await using var branch=world.ForkExact();
                try
                {
                    var packet=await branch.StepAsync(actions[i]); int steps=1;
                    while(packet.Status is "player_decision" or "card_choice" && steps<maxDecisions)
                    { packet=await branch.StepAsync(PublicDiagnosticPolicy.Choose(packet)); steps++; }
                    if(packet.Status=="terminal_settled")
                    { var facts=await branch.SettleAsync(); rows[i].Add(new(facts.Result,facts.FinalHp)); }
                    else rows[i].Add(new("compute_truncated",null));
                }
                catch { rows[i].Add(new("engine_error",null)); }
            }
        }
        return actions.Select((a,i)=>new CandidateProbe(a,rows[i].ToArray(),rows[i].Count(x=>x.Result is "win" or "loss"),rows[i].Count(x=>x.Result=="compute_truncated"),rows[i].Count(x=>x.Result=="engine_error"))).ToArray();
    }
}
