using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

internal static class JointActDiagnostic
{
    internal static async Task RunAsync(string path)
    {
        string raw = File.ReadAllText(path);
        var spec = PublicJson.Read<Declaration>(raw);
        if (spec.SourceDrawSeeds.Length is < 1 or > 16 || spec.CandidateTrials is < 1 or > 256
            || spec.SourceDrawSeeds.Distinct().Count() != spec.SourceDrawSeeds.Length)
            throw new ArgumentException("Invalid small joint-act work declaration");
        var prior = spec.Prior.Freeze();
        Console.WriteLine(PublicJson.Serialize(new { stage = "predeclared_joint_act_work", declaration = spec,
            declarationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant() }));
        var roots = new List<(ulong Draw, NativeInitialPrefixCondition Condition)>();
        foreach (ulong draw in spec.SourceDrawSeeds)
        {
            var recipe = prior.Draw(new Rng(draw, "nosl-native-tape-source-draw-v1"));
            await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe));
            if (source is null) throw new InvalidOperationException($"Predeclared source {draw} absent; do not replace it");
            var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
            if (!NativeInitialPrefixCondition.TryCreate(root, prior, out var condition, out var reason)
                || condition!.PublicMap is null || condition.TargetActType is null)
                throw new InvalidOperationException($"Predeclared source {draw} lacks joint certificate: {reason}");
            roots.Add((draw, condition));
        }
        var random = new Rng(spec.CandidateOracleSeed, "nosl-joint-public-act-map-work-v1");
        var stats = roots.Select(_ => new Stats()).ToArray();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < spec.CandidateTrials; i++)
        {
            var candidateRecipe = new NativeTapeRecipe(random.NextUnsignedLong(), 0, 0, 0, 0);
            var native = NativeComponentRejection.Evaluate(() => new RunState(candidateRecipe.IndependentRunSeed, ascensionLevel: 10), random.NextUnsignedLong);
            for (int r = 0; r < roots.Count; r++)
            {
                var condition = roots[r].Condition; var row = stats[r];
                bool actMatch = native.Value.Act.GetType() == condition.TargetActType;
                bool mapMatch = condition.PublicMap!.MatchesMap(native.Value.Map);
                if (mapMatch) row.MapOnlyMatches++;
                if (actMatch && mapMatch) row.JointMatches++;
                if (!actMatch) row.MapGenerationsAvoided++;
                if (!actMatch && mapMatch) row.WrongActMapAcceptsAvoided++;
                row.MapOnlyNativeWords += native.Trace.Count;
                var words = new Queue<ulong>(new[] { candidateRecipe.RunSeed }
                    .Concat(native.Trace.DistinctBy(w => w.State).Select(w => w.Word)));
                NativeComponentStats actual;
                try
                {
                    var plan = condition.Prepare(candidateRecipe, 1, () => words.Dequeue());
                    if (!actMatch || !mapMatch || !plan.Trace.SequenceEqual(native.Trace))
                        throw new InvalidOperationException("Joint predicate or retained native trace differs");
                    actual = plan.Stats;
                }
                catch (NativeComponentBudgetExceededException exhausted)
                {
                    if (actMatch && mapMatch) throw new InvalidOperationException("Matching joint event was rejected");
                    actual = exhausted.Stats;
                }
                long expectedWords = actMatch ? native.Trace.Count : NativeInitialPrefixProposal.MapTraceOffset;
                if (actual.CompletedTrials != 1 || actual.TotalWordDraws != expectedWords)
                    throw new InvalidOperationException("Wrong-act prefix read unexpected native map words");
                row.JointNativeWords += actual.TotalWordDraws;
            }
        }
        Console.WriteLine(PublicJson.Serialize(new { stage = "completed_joint_act_work", spec.ExperimentId,
            spec.CandidateTrials, seconds = timer.Elapsed.TotalSeconds, fullPosteriorMeasured = false,
            formalTraining = false, trainable = false, roots = roots.Select((root, i) => new
            { sourceDrawSeed = root.Draw, observedAct = root.Condition.TargetActType!.Name, work = stats[i] }).ToArray() }));
    }
    private sealed class Stats
    {
        public int MapOnlyMatches { get; set; }
        public int JointMatches { get; set; }
        public int MapGenerationsAvoided { get; set; }
        public int WrongActMapAcceptsAvoided { get; set; }
        public long MapOnlyNativeWords { get; set; }
        public long JointNativeWords { get; set; }
    }
}
