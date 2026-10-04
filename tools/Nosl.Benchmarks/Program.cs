using System.Diagnostics;
using System.Reflection;
using Sts2Sim.Core.Combat;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

// Engineering timings only: never export these repeated executions as training examples.
if (args[0] == "--map-profile")
{
    foreach (int level in new[] { 0, 10 }) foreach (var act in new ActDefinition[] { new Overgrowth(), new Underdocks(), new Hive(), new Glory() })
    for (int index = 0; index < 8; index++)
    {
        var rng = new Rng(new RunRngSet("nosl-map-profile:" + index).Seed, $"act_{act.Index + 1}_map");
        var counts = act.GetMapPointTypes(rng, new AscensionManager(level));
        long bytes = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
        var map = new StandardActMap(rng, act.BaseNumberOfRooms, counts, hasSecondBoss: level == 10 && act.Index == 2);
        timer.Stop();
        string Point(MapPoint p) => $"{p.coord.col},{p.coord.row}";
        var state = new { starts = map.startMapPoints.Select(Point).ToArray(),
            points = map.GetAllMapPoints().Concat(new[] { map.StartingMapPoint, map.BossMapPoint }).Concat(map.SecondBossMapPoint is {} boss ? new[] { boss } : Array.Empty<MapPoint>()).Select(p => new
            { point = Point(p), p.PointType, p.CanBeModified, parents = p.parents.Select(Point).ToArray(), children = p.Children.Select(Point).ToArray() }).ToArray(),
            rng = rng.ToSerializable() };
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { IncludeFields = true });
        Console.WriteLine(PublicJson.Serialize(new { metric = "map", level, act = act.GetType().Name, index, seconds = timer.Elapsed.TotalSeconds,
            bytes = GC.GetTotalAllocatedBytes(true)-bytes, stateSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant() }));
    }
    return;
}
bool teacherCheck = args[0] == "--teacher-check";
bool rootCheck = args[0] == "--root-check";
var fixtures = PublicJson.Read<Fixture[]>(File.ReadAllText(teacherCheck || rootCheck ? args[1] : args[0]));
int iterations = !teacherCheck && !rootCheck && args.Length > 1 ? int.Parse(args[1]) : 2;
ModelDb.Init(ContentRegistry.AllTypes);
await using (var warm = await CombatSession.CreateAsync(fixtures[0].Scenario))
{ await using var sample = await BeliefSampler.SampleWorldAsync(warm, 1); }
var metadata = Stopwatch.StartNew();
long before = GC.GetTotalAllocatedBytes(true);
for (int repeat = 0; repeat < 100; repeat++) foreach (var type in ContentRegistry.AllTypes) _ = ModelDb.GetId(type);
metadata.Stop();
Console.WriteLine(PublicJson.Serialize(new { metric = "all_model_ids_100x", seconds = metadata.Elapsed.TotalSeconds, bytes = GC.GetTotalAllocatedBytes(true)-before }));
var construction = new List<object>();
for (int iteration = 0; iteration < 5; iteration++)
{
    var timer = Stopwatch.StartNew();
    var run = new RunState("nosl-profile:" + iteration, [new Overgrowth(), new Hive(), new Glory()], 10);
    double runSeconds = timer.Elapsed.TotalSeconds; timer.Restart();
    var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
    construction.Add(new {runSeconds, playerSeconds = timer.Elapsed.TotalSeconds});
}
Console.WriteLine(PublicJson.Serialize(new { metric = "native_construction_components", measurements = construction }));
foreach (var fixture in fixtures)
{
    var create = Stopwatch.StartNew();
    await using var source = await CombatSession.CreateAsync(fixture.Scenario);
    double createSeconds = create.Elapsed.TotalSeconds;
    var trace = new List<string> { PublicJson.Serialize(source.Observe()) }; var actions = new List<PublicAction>();
    for (int step = 0; step < fixture.Step; step++)
    {
        var action = new PublicRulePolicy().Choose(source.Observe()); actions.Add(action);
        await source.StepAsync(action); trace.Add(PublicJson.Serialize(source.Observe()));
    }
    string original = PublicJson.Serialize(source.Observe());
    var sourcePacket = source.Observe();
    var originalInput = new { schema_version = "nosl.student.public.v1", observation = sourcePacket.Observation,
        history_complete = true, controller_context = new { status = "inactive" },
        candidate_actions = sourcePacket.Actions, legal_mask = sourcePacket.Actions.Select(_ => true).ToArray() };
    string originalInputHash = Hash(PublicJson.Serialize(originalInput));
    if (originalInputHash != fixture.RootSha) throw new InvalidOperationException($"Fixture {fixture.Index} no longer matches its original recorded public root");
    if (rootCheck)
    {
        Console.WriteLine(PublicJson.Serialize(new { metric = "root_check", fixture.Index, originalInputHash, sourceRandomSha256 = RandomState(source) }));
        continue;
    }
    bool fast = BeliefSampler.UsesExchangeablePosterior(source);
    if (teacherCheck)
    {
        var timer = Stopwatch.StartNew();
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [101,102], MaxDecisions = 200 });
        string outcomes = PublicJson.Serialize(result.Candidates.Select(c => new { c.Action, c.Outcomes, c.Evaluation }).ToArray());
        double teacherSeconds = timer.Elapsed.TotalSeconds;
        string sourceRandom = RandomState(source);
        await using var verificationWorld = await BeliefSampler.SampleWorldAsync(source, 101);
        string sampledRandom = RandomState(verificationWorld);
        var publicTrace = new List<string>(); var policy = new PublicRulePolicy();
        for (int decision = 0; decision < 200 && verificationWorld.Observe().Status != "terminal_settled"; decision++)
        {
            var packet = verificationWorld.Observe();
            var action = decision == 0 ? packet.Actions[0] : policy.Choose(packet);
            publicTrace.Add(PublicJson.Serialize(new { packet, action }));
            await verificationWorld.StepAsync(action);
        }
        if (verificationWorld.Observe().Status != "terminal_settled") throw new InvalidOperationException("Trace check truncated");
        publicTrace.Add(PublicJson.Serialize(await verificationWorld.SettleAsync()));
        if (original != PublicJson.Serialize(source.Observe()) || sourceRandom != RandomState(source)) throw new InvalidOperationException("Source changed during teacher check");
        Console.WriteLine(PublicJson.Serialize(new { metric = "teacher_check", fixture.Index, fast, seconds = teacherSeconds,
            completed = result.Costs.WorldsCompleted, allocated = result.Costs.WorldsAllocated,
            sourceRandomSha256 = sourceRandom, sampledRandomSha256 = sampledRandom, settledRandomSha256 = RandomState(verificationWorld),
            publicAndTerminalTraceSha256 = Hash(string.Join("\n", publicTrace)), outcomesSha256 = Hash(outcomes) }));
        continue;
    }
    var samples = new List<object>();
    for (int iteration = 0; iteration < iterations; iteration++)
    {
        ulong seed = (ulong)(101 + iteration);
        var timer = Stopwatch.StartNew();
        await using var world = await BeliefSampler.SampleWorldAsync(source, seed, 256);
        double sampleSeconds = timer.Elapsed.TotalSeconds; timer.Restart();
        int forks = fast ? 100 : 2;
        string? forkRoot = null; double forkSeconds = 0;
        for (int i = 0; i < forks; i++)
        {
            timer.Restart(); await using var branch = await world.ForkForContinuationAsync();
            forkSeconds += timer.Elapsed.TotalSeconds;
            forkRoot = PublicJson.Serialize(branch.Observe());
        }
        double continuationForkSeconds = forkSeconds / forks;
        timer.Restart();
        for (int i = 0; i < 100; i++)
        { await using var branch = world.ForkExact(); }
        double exactForkSeconds = timer.Elapsed.TotalSeconds / 100;
        if (forkRoot != original) throw new InvalidOperationException("Fork public root changed");
        int? proposals = null; double? proposalSetupSeconds = null;
        if (!fast)
        {
            proposals = 0; proposalSetupSeconds = 0;
            var rng = new Rng(seed, "nosl-independent-setup-prior-v1");
            for (int attempt = 0; attempt < 256; attempt++)
            {
                proposals++; timer.Restart();
                await using var proposed = await CombatSession.CreateAsync(fixture.Scenario with { Seed = $"NOSL-REPLAY:{rng.NextUnsignedLong():X16}" });
                proposalSetupSeconds += timer.Elapsed.TotalSeconds;
                bool accepted = true;
                for (int step = 0; step <= actions.Count; step++)
                {
                    if (PublicJson.Serialize(proposed.Observe()) != trace[step]) { accepted = false; break; }
                    if (step < actions.Count) await proposed.StepAsync(actions[step]);
                }
                if (accepted) break;
            }
        }
        samples.Add(new { seed, sampleSeconds, continuationForkSeconds, exactForkSeconds, proposals, proposalSetupSeconds });
    }
    if (original != PublicJson.Serialize(source.Observe())) throw new InvalidOperationException("Source mutated by profiling");
    Console.WriteLine(PublicJson.Serialize(new { metric = "constructed_root", fixture.Index, fixture.Step, fixture.Category, fast,
        createSeconds, publicRootSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant(), samples }));
}
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string RandomState(CombatSession session)
{
    // Engineering verification only; this private audit never crosses a policy boundary.
    var state = (CombatState)typeof(CombatSession).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    return Hash(JsonSerializer.Serialize(new { run = state.RunState.Rng.ToSerializable(),
        players = state.Players.Select(p => p.PlayerRng.ToSerializable()).ToArray(),
        monsters = state.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray() }, new JsonSerializerOptions { IncludeFields = true }));
}
record Fixture(int Index, int Step, string Category, Scenario Scenario, string RootSha);
