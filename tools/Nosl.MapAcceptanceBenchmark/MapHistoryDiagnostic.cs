using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

/// <summary>Fixed work on exactly the eight existing public roots; never a root/source search.</summary>
internal static class MapHistoryDiagnostic
{
    internal static void Run(string declarationPath, string existingReportPath)
    {
        string raw = File.ReadAllText(declarationPath);
        var spec = PublicJson.Read<MapHistoryDeclaration>(raw);
        if (!spec.SourceDrawSeeds.SequenceEqual(Enumerable.Range(11001, 8).Select(n => (ulong)n))
            || spec.CandidatesPerRoot != 256 || spec.MaximumCompleteCandidates != 2048)
            throw new ArgumentException("The map-history diagnostic requires exactly 256 candidates on each existing root 11001..11008");
        byte[] bytes = File.ReadAllBytes(existingReportPath);
        if (Hash(bytes) != spec.ExistingReportSha256) throw new ArgumentException("Existing report digest changed; do not replace roots");
        using var report = JsonDocument.Parse(bytes);
        var document = report.RootElement;
        var prior = spec.Prior.Freeze();
        if (PublicJson.Read<NativeTapePrior>(document.GetProperty("prior").GetRawText()).Freeze().Identity != prior.Identity)
            throw new ArgumentException("Existing report prior differs from the declaration");
        var attempts = document.GetProperty("attempts").EnumerateArray().ToArray();
        var records = document.GetProperty("records").EnumerateArray().ToArray();
        if (records.Length != 8 || attempts.Length != 8 || !attempts.Select(a => a.GetProperty("sourceDrawSeed").GetUInt64()).SequenceEqual(spec.SourceDrawSeeds))
            throw new ArgumentException("Existing report does not contain the declared eight roots in order");
        Console.WriteLine(PublicJson.Serialize(new { stage = "predeclared_map_history_work", declaration = spec,
            declarationSha256 = Hash(Encoding.UTF8.GetBytes(raw)), priorIdentity = prior.Identity,
            readsOnlyPublicEvidence = true, fullPosteriorMeasured = false, formalTraining = false, trainable = false }));
        var random = new Rng(spec.CandidateOracleSeed, "nosl-public-map-history-work-v1");
        int completed = 0, activeRoot = 0, activeCandidate = 0;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            for (int r = 0; r < 8; r++)
            {
                activeRoot = r; activeCandidate = 0;
                var evidence = PublicRunEvidenceJson.Read(records[r].GetProperty("public_input").GetProperty("public_evidence").GetRawText());
                if (!NativeInitialPrefixCondition.TryCreate(new("fixture", null, [], evidence), prior, out var condition, out var why)
                    || condition!.PublicMapHistory is not { } history)
                    throw new InvalidOperationException($"Existing root {spec.SourceDrawSeeds[r]} lacks map-history certificate: {why}");
                var survival = new int[history.CertifiedSliceCount];
                int actSurvivors = 0, matches = 0, misses = 0;
                long nativeWords = 0, cells = 0; int maxWords = 0;
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < spec.CandidatesPerRoot; i++)
                {
                    // One fresh complete joint candidate. Wrong-act maps integrate
                    // out, exactly as production Prepare. No success quota/retry cap
                    // can alter the predeclared work, even if every candidate misses.
                    activeCandidate = i + 1; cancellation.Token.ThrowIfCancellationRequested();
                    var candidateTimer = Stopwatch.StartNew();
                    var recipe = new NativeTapeRecipe(random.NextUnsignedLong(), 0, 0, 0, 0);
                    var candidate = NativeComponentRejection.Evaluate(() =>
                    {
                        var acts = ActDefinition.GetRandomList(recipe.IndependentRunSeed);
                        if (acts.Count != 3 || acts[0].GetType() != typeof(Overgrowth) && acts[0].GetType() != typeof(Underdocks)
                            || acts[1] is not Hive || acts[2] is not Glory)
                            throw new InvalidOperationException("Reviewed native initial act pool changed");
                        if (condition.TargetActType is not null && acts[0].GetType() != condition.TargetActType) return -1;
                        var rng = new RunRngSet(recipe.IndependentRunSeed);
                        var map = StandardActMap.CreateFor(acts[0], StandardActMap.CreateRng(rng.Seed, 0),
                            new AscensionManager(10), hasSecondBoss: false);
                        return history.MatchingSlicePrefix(map);
                    }, random.NextUnsignedLong, cancellation.Token);
                    completed++; cancellation.Token.ThrowIfCancellationRequested();
                    nativeWords += candidate.Trace.Count; cells += candidate.DistinctCells; maxWords = Math.Max(maxWords, candidate.Trace.Count);
                    if (candidate.Value >= 0) actSurvivors++;
                    for (int slice = 0; slice < candidate.Value; slice++) survival[slice]++;
                    if (candidate.Value == history.CertifiedSliceCount) matches++; else misses++;
                    Console.WriteLine(PublicJson.Serialize(new { stage = "map_history_candidate", sourceDrawSeed = spec.SourceDrawSeeds[r],
                        candidate = i + 1, matchedSlicePrefix = candidate.Value, actMatched = candidate.Value >= 0,
                        allCertifiedSlicesMatched = candidate.Value == history.CertifiedSliceCount,
                        nativeWords = candidate.Trace.Count, distinctCells = candidate.DistinctCells, seconds = candidateTimer.Elapsed.TotalSeconds }));
                }
                Console.WriteLine(PublicJson.Serialize(new { stage = "map_history_root_complete", sourceDrawSeed = spec.SourceDrawSeeds[r],
                    observedAct = condition.TargetActType?.Name, publicEvidenceSha256 = Hash(Encoding.UTF8.GetBytes(PublicJson.Serialize(evidence))),
                    observedSlices = evidence.Events.Count(e => e.Payload is PublicMapObserved), history.CertifiedSliceCount,
                    history.SuffixFallbackReason, completeCandidates = spec.CandidatesPerRoot, runSeedDraws = spec.CandidatesPerRoot,
                    actSurvivors, wrongActEarlyExits = spec.CandidatesPerRoot - actSurvivors,
                    firstSliceSurvivors = survival[0], matches, singleTrialExhaustions = misses, fixed256BlockExhausted = matches == 0,
                    nativeWords, distinctCells = cells, maximumTrialWords = maxWords, seconds = timer.Elapsed.TotalSeconds,
                    slices = history.MapEventOrdinals.Select((ordinal, index) => new { mapEventOrdinal = ordinal, survivingCandidates = survival[index] }).ToArray() }));
            }
            Console.WriteLine(PublicJson.Serialize(new { stage = "map_history_work_complete", spec.ExperimentId,
                completeCandidates = completed, fullPosteriorMeasured = false, formalTraining = false, trainable = false }));
        }
        catch (Exception exception)
        {
            Console.WriteLine(PublicJson.Serialize(new { stage = "map_history_work_unresolved", completeCandidates = completed,
                sourceDrawSeed = spec.SourceDrawSeeds[activeRoot], candidate = activeCandidate,
                cancelled = exception is OperationCanceledException, errorType = exception.GetType().FullName,
                error = exception.Message, exhausted = false, fullPosteriorMeasured = false, trainable = false }));
            throw;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record MapHistoryDeclaration(string ExperimentId, ulong[] SourceDrawSeeds,
        string ExistingReportSha256, ulong CandidateOracleSeed, int CandidatesPerRoot,
        int MaximumCompleteCandidates, NativeTapePrior Prior);
}
