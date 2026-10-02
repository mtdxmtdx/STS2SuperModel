using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

if (args is ["--joint-act", var jointDeclaration])
{
    await JointActDiagnostic.RunAsync(jointDeclaration);
    return;
}

// Standalone, predeclared component diagnostic. No root search, teacher labels,
// posterior acceptance claim, content restriction, or production admission.
if (args.Length != 1) throw new ArgumentException("Pass a predeclared experiment JSON path");
string declaration = File.ReadAllText(args[0]);
var spec = PublicJson.Read<Declaration>(declaration);
if (spec.SourceDrawSeeds.Length is < 1 or > 16 || spec.SourceDrawSeeds.Distinct().Count() != spec.SourceDrawSeeds.Length
    || spec.CandidateTrials is < 1 or > 4096 || spec.BlockSize < 1 || spec.CandidateTrials % spec.BlockSize != 0)
    throw new ArgumentException("Invalid bounded component declaration");
var legacy = spec.Prior.Freeze();
if (legacy.Execution.PublicMapObservationProfile is not null) throw new ArgumentException("Declare the original source profile");
var canonical = (legacy with { Execution = legacy.Execution with
    { PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } }).Freeze();
Console.WriteLine(PublicJson.Serialize(new { stage = "predeclared", declaration = spec,
    declarationSha256 = Hash(declaration), legacyPriorIdentity = legacy.Identity, canonicalPriorIdentity = canonical.Identity }));
var roots = new List<(ulong SourceDraw, NativePublicInitialMapCondition Old, NativePublicInitialMapCondition New)>();
foreach (ulong draw in spec.SourceDrawSeeds)
{
    var recipe = legacy.Draw(new Rng(draw, "nosl-native-tape-source-draw-v1"));
    DecisionPacket oldRoot, newRoot; string oldRandom, newRandom;
    await using (var source = await NativeRunWorld.OpenLabelTapeAsync(legacy.Execution, recipe, NativeLabelTape.ForDeclaredPrior(legacy, recipe)))
    {
        if (source is null) throw new InvalidOperationException($"Predeclared source {draw} has no root; do not replace it");
        oldRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe())); oldRandom = RandomState(source.NativeRun);
    }
    await using (var source = await NativeRunWorld.OpenLabelTapeAsync(canonical.Execution, recipe, NativeLabelTape.ForDeclaredPrior(canonical, recipe)))
    {
        if (source is null) throw new InvalidOperationException($"Predeclared canonical source {draw} has no root; do not replace it");
        newRoot = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe())); newRandom = RandomState(source.NativeRun);
    }
    PublicEvidenceInput.ValidateProfile(canonical.Execution, newRoot);
    if (oldRandom != newRandom || PublicJson.Serialize(Coarsen(oldRoot)) != PublicJson.Serialize(newRoot))
        throw new InvalidOperationException("The profile changed more than public map option enumeration");
    if (!NativePublicInitialMapCondition.TryCreate(oldRoot.PublicEvidence, legacy, out var oldCondition, out var oldReason))
        throw new InvalidOperationException($"Predeclared original source {draw} lacks a map certificate; do not replace it: {oldReason}");
    if (!NativePublicInitialMapCondition.TryCreate(newRoot.PublicEvidence, canonical, out var newCondition, out var newReason))
        throw new InvalidOperationException($"Predeclared canonical source {draw} lacks a map certificate; do not replace it: {newReason}");
    roots.Add((draw, oldCondition!, newCondition!));
    Console.WriteLine(PublicJson.Serialize(new { stage = "paired_source_verified", sourceDrawSeed = draw,
        oldPublicRootSha256 = Hash(PublicJson.Serialize(oldRoot)), canonicalPublicRootSha256 = Hash(PublicJson.Serialize(newRoot)),
        nativeRandomIdentical = true, onlyMapOptionEnumerationChanged = true }));
}
var random = new Rng(spec.CandidateOracleSeed, "nosl-canonical-map-acceptance-diagnostic-v1");
var observations = roots.Select(_ => new List<(bool Old, bool New)>()).ToArray();
long nativeWords = 0; var timer = Stopwatch.StartNew();
for (int i = 0; i < spec.CandidateTrials; i++)
{
    ulong runSeed = random.NextUnsignedLong();
    var recipe = new NativeTapeRecipe(runSeed, 0, 0, 0, 0);
    var candidate = NativeComponentRejection.Evaluate(() => new RunState(recipe.IndependentRunSeed, ascensionLevel: 10), random.NextUnsignedLong);
    nativeWords += candidate.Trace.Count;
    for (int r = 0; r < roots.Count; r++)
    {
        bool oldMatch = roots[r].Old.MatchesMap(candidate.Value.Map), newMatch = roots[r].New.MatchesMap(candidate.Value.Map);
        if (oldMatch && !newMatch) throw new InvalidOperationException("Fine event escaped its coarsening");
        observations[r].Add((oldMatch, newMatch));
    }
}
Console.WriteLine(PublicJson.Serialize(new { stage = "completed", spec.ExperimentId, spec.CandidateTrials,
    spec.BlockSize, nativeWords, seconds = timer.Elapsed.TotalSeconds, formalTraining = false, trainable = false,
    fullPosteriorMeasured = false, roots = roots.Select((root, r) => new
    {
        sourceDrawSeed = root.SourceDraw, legacyMatches = observations[r].Count(x => x.Old),
        canonicalMatches = observations[r].Count(x => x.New), legacyEventSubsetVerified = true,
        blocks = observations[r].Chunk(spec.BlockSize).Select((block, i) => new
        {
            index = i, legacyFirstAcceptTrial = Index(block, x => x.Old), canonicalFirstAcceptTrial = Index(block, x => x.New),
        }).ToArray(),
    }).ToArray() }));

static int? Index((bool Old, bool New)[] block, Func<(bool Old, bool New), bool> predicate)
{ int index = Array.FindIndex(block, x => predicate(x)); return index < 0 ? null : index + 1; }
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string RandomState(RunState run) => JsonSerializer.Serialize(new
    { run = run.Rng.ToSerializable(), players = run.Players.Select(p => p.PlayerRng.ToSerializable()).ToArray() },
    new JsonSerializerOptions { IncludeFields = true });
static DecisionPacket Coarsen(DecisionPacket packet)
{
    var evidence = packet.PublicEvidence!;
    return packet with { PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart,
        evidence.Events.Select(e => e.Payload is PublicMapObserved map
            ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, new PublicMapObserved(map.Current, map.Nodes, map.Edges,
                map.Options.OrderBy(o => o.Coordinate.Row).ThenBy(o => o.Coordinate.Col).ToImmutableArray())) : e).ToImmutableArray()) };
}
internal sealed record Declaration(string ExperimentId, ulong[] SourceDrawSeeds, ulong CandidateOracleSeed,
    int CandidateTrials, int BlockSize, NativeTapePrior Prior);
