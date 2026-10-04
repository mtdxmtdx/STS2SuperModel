using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Random;

// Test-only paired-binary diagnostic. Run this exact harness once with each
// frozen Worker dependency set. It never returns private state to a policy.
internal static class PublicSnapshotTraceDiagnostic
{
    private static readonly JsonSerializerOptions StateJson = new() { IncludeFields = true };
    private sealed record Declaration(ulong[] SourceDrawSeeds, int MaxDecisions, NativeTapePrior Prior);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string State(object value) => JsonSerializer.Serialize(value, StateJson);

    internal static async Task RunAsync(string declarationPath)
    {
        string declaration = File.ReadAllText(declarationPath);
        var spec = PublicJson.Read<Declaration>(declaration);
        if (spec.SourceDrawSeeds.Length is < 1 or > 4 || spec.SourceDrawSeeds.Distinct().Count() != spec.SourceDrawSeeds.Length
            || spec.MaxDecisions is < 1 or > 200) throw new ArgumentException("Invalid bounded trace fixture declaration");
        var prior = spec.Prior.Freeze();
        Console.WriteLine(PublicJson.Serialize(new { stage = "declaration", declarationSha256 = Hash(declaration),
            priorIdentity = prior.Identity, sourceDrawSeeds = spec.SourceDrawSeeds,
            fixtureScope = "one direct source continuation and its independent native replay fork per fixed inspected recipe; not posterior worlds or all teacher branches" }));
        foreach (ulong draw in spec.SourceDrawSeeds)
        {
            var recipe = prior.Draw(new Rng(draw, "nosl-native-tape-source-draw-v1"));
            var tape = NativeLabelTape.ForDeclaredPrior(prior, recipe);
            await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, tape)
                ?? throw new InvalidOperationException($"Declared source {draw} has no root; no replacement is permitted");
            string rootPacket = PublicJson.Serialize(source.Observe());
            string rootState = Capture(source, tape);
            await using var fork = (NativeRunWorld)await source.ForkForContinuationAsync();
            if (ReferenceEquals(source.NativeRun, fork.NativeRun) || ReferenceEquals(source.NativeCombatRoom, fork.NativeCombatRoom)
                || ReferenceEquals(source.NativeRun.Players.Single(), fork.NativeRun.Players.Single()))
                throw new InvalidOperationException("Native fork shares a mutable owner");
            if (Capture(source, tape) != rootState || PublicJson.Serialize(source.Observe()) != rootPacket)
                throw new InvalidOperationException("Fork setup changed the source");
            // The fork owns its tape; inspect its existing private field only in
            // this diagnostic, never install/copy state into either runtime.
            var forkTape = (NativeLabelTape)Field(fork, "_labelTape")!;
            var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
            int decisions = 0;
            while (source.Observe().Status != "terminal_settled" && decisions < spec.MaxDecisions)
            {
                string sourceState = Capture(source, tape), forkState = Capture(fork, forkTape);
                if (sourceState != forkState) throw new InvalidOperationException($"Owned replay differs at {draw}/{decisions}");
                var packet = source.Observe();
                // Both binaries take precisely the same public DTO-only policy path.
                var action = decisions == 0 ? packet.Actions[0] : policy.Choose(packet);
                Console.WriteLine(PublicJson.Serialize(new { stage = "decision", sourceDrawSeed = draw, recipe,
                    decision = decisions, action, snapshot = JsonSerializer.Deserialize<JsonElement>(sourceState),
                    independentForkSnapshot = JsonSerializer.Deserialize<JsonElement>(forkState) }));
                await fork.StepAsync(action);
                if (Capture(source, tape) != sourceState || PublicJson.Serialize(source.Observe()) != PublicJson.Serialize(packet))
                    throw new InvalidOperationException("Advancing the fork changed the source");
                await source.StepAsync(action);
                decisions++;
            }
            if (source.Observe().Status != "terminal_settled" || fork.Observe().Status != "terminal_settled")
                throw new InvalidOperationException("Declared trace fixture did not reach settlement");
            string settled = Capture(source, tape), forkSettled = Capture(fork, forkTape);
            var outcome = await source.RecordSettledAsync(policy.Id, 0);
            if (settled != forkSettled || PublicJson.Serialize(outcome) != PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)))
                throw new InvalidOperationException("Native replay terminal facts differ");
            Console.WriteLine(PublicJson.Serialize(new { stage = "settled", sourceDrawSeed = draw, recipe, decisions, outcome,
                snapshot = JsonSerializer.Deserialize<JsonElement>(settled),
                independentForkSnapshot = JsonSerializer.Deserialize<JsonElement>(forkSettled), sourceUnchangedByFork = true }));
        }
    }

    private static string Capture(NativeRunWorld world, NativeLabelTape tape)
    {
        var combat = world.NativeCombatRoom.Engine.State;
        var digest = new CombatStateDescriptionBuilder();
        CombatStateDescription.AppendExactState(ref digest, combat);
        var random = new
        {
            run = world.NativeRun.Rng.ToSerializable(),
            players = world.NativeRun.Players.Select(p => p.PlayerRng.ToSerializable()).ToArray(),
            monsters = combat.SpawnedEnemies.Select(e => new
            {
                e.CombatId, e.SlotName, model = e.Monster?.GetType().Name,
                rng = e.Monster?.Rng?.ToSerializable(), nextMove = e.Monster?.NextMove?.Id,
                moves = e.Monster?.MoveStateMachine?.StateLog.Select(s => s.Id).ToArray(),
            }).ToArray(),
        };
        var tapeState = new
        {
            recipe = Field(tape, "_recipe"), tape.DistinctCells, tape.ConditionedCells, tape.MapCells,
            tape.RewardsCells, tape.PublicPrefixEventsChecked,
            fullState = Partition(tape), rewards = Partition(Field(tape, "_rewardsOracle")),
            map = Partition(Field(tape, "_mapOracle")),
        };
        string packet = PublicJson.Serialize(world.Observe());
        return State(new
        {
            publicPacketSha256 = Hash(packet), publicPacketBytes = Encoding.UTF8.GetByteCount(packet),
            sourceTraceSha256 = Hash(PublicJson.Serialize(world.SourceTrace)), sourceTraceEvents = world.SourceTrace.Count,
            rngStateSha256 = Hash(State(random)), nativeCombatStateDigest = digest.Build(),
            tapeStateSha256 = Hash(State(tapeState)), tape.DistinctCells, tape.ConditionedCells, tape.MapCells, tape.RewardsCells,
        });
    }

    private static object? Partition(object? partition) => partition is null ? null : new
    {
        // Canonicalize set/dictionary enumeration only in audit output. These
        // reads do not draw a word or change the live tape. Recipe plus exact
        // visited addresses/forced words defines the observed oracle state.
        visited = ((IEnumerable)Field(partition, "_visited")!).Cast<object>().Select(State).Order(StringComparer.Ordinal).ToArray(),
        overrides = ((IEnumerable)Field(partition, "_overrides")!).Cast<object>().Select(State).Order(StringComparer.Ordinal).ToArray(),
    };

    private static object? Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
        ?? (value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is null
            ? throw new InvalidOperationException("Diagnostic field missing: " + name) : null);
}
