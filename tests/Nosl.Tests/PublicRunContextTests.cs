using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class PublicRunContextTests
{
    private static PublicObservation LegacyObservation() => new("nosl.public.v2", 70, 10, 1, 70, 70,
        0, 3, 0, [], [], [], [], [], 0, [null, null], [], [], [], [], null);

    [Fact]
    public void LegacyBytesAndPriorHashesStayFrozenWhileOptInChangesIdentity()
    {
        const string observation = """{"schema":"nosl.public.v2","startHp":70,"ascension":10,"turn":1,"hp":70,"maxHp":70,"block":0,"energy":3,"stars":0,"hand":[],"discard":[],"exhaust":[],"unknownDraw":[],"knownDraw":[],"drawCount":0,"potions":[null,null],"relics":[],"powers":[],"enemies":[],"history":[],"choice":null,"counters":null,"relicStates":null,"gold":0,"startGold":0,"orbCapacity":0,"orbs":null,"pets":null,"unidentifiedDrawCount":0}""";
        const string execution = """{"maxFloors":60,"sourceDecisionHorizon":10000,"sourcePolicyId":"nosl-public-rules-v2"}""";
        const string runPrior = """{"schemaVersion":"nosl.native-run-slot-prior.v1","execution":{"maxFloors":60,"sourceDecisionHorizon":10000,"sourcePolicyId":"nosl-public-rules-v2"},"eligibleSlots":256,"finiteSeedSupport":null,"seedLaw":"uniform-uint64-hex-v1","slotLaw":"uniform-fixed-combat-decision-slot-including-choices-v1","outsideCombatScript":"nosl-natural-public-script-v2"}""";
        const string tapePrior = """{"schemaVersion":"nosl.native-state-tape-prior.v1","execution":{"maxFloors":60,"sourceDecisionHorizon":10000,"sourcePolicyId":"nosl-public-rules-v2"},"eligibleCombats":80,"eligibleDecisionsPerCombat":128,"primitiveLaw":"ideal-independent-uint64-by-native-predraw-state-with-equal-state-aliases-v1","primitiveImplementation":"sha256-address-expansion-with-explicit-conditioned-overrides-v1","rootLaw":"independent-uniform-fixed-combat-and-local-decision-indices-v1","outsideCombatScript":"nosl-natural-public-script-v2"}""";
        Assert.Equal(observation, PublicJson.Serialize(LegacyObservation()));
        Assert.Equal(observation, PublicJson.Serialize(PublicJson.Read<PublicObservation>(observation)));
        Assert.Equal(execution, PublicJson.Serialize(new NativeRunExecutionOptions()));
        Assert.Equal(runPrior, PublicJson.Serialize(new NativeRunPrior()));
        Assert.Equal(tapePrior, PublicJson.Serialize(new NativeTapePrior()));
        string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(Hash(runPrior), new NativeRunPrior().Identity);
        Assert.Equal(Hash(tapePrior), new NativeTapePrior().Identity);
        var complete = new NativeRunExecutionOptions(PublicContextProfile: PublicRunContext.Version);
        var unavailable = complete with { PublicCombatHistoryMode = PublicRunContext.UnavailableHistoryMode };
        Assert.NotEqual(new NativeTapePrior().Identity, new NativeTapePrior { Execution = complete }.Identity);
        Assert.NotEqual(new NativeTapePrior { Execution = complete }.Identity, new NativeTapePrior { Execution = unavailable }.Identity);
        Assert.DoesNotContain("publicContextProfile", PublicJson.Serialize(new NaturalSourceOptions()));
        Assert.DoesNotContain("publicCombatHistoryMode", PublicJson.Serialize(new NaturalSourceOptions()));
    }

    [Fact]
    public void ProfileValidationAndTeacherSchemaDoNotSilentlyDropContext()
    {
        Assert.Throws<ArgumentException>(() => PublicRunContext.ValidateChannel("unknown", null));
        Assert.Throws<ArgumentException>(() => PublicRunContext.ValidateChannel(PublicRunContext.Version, "guessed"));
        Assert.Throws<ArgumentException>(() => PublicRunContext.ValidateChannel(null, PublicRunContext.UnavailableHistoryMode));
        var context = new PublicRunContext(PublicRunContext.Version, 0, 2, 0, true);
        foreach (var invalid in new[] { context with { SchemaVersion = "unknown" }, context with { ActIndex = -1 },
            context with { Floor = -1 }, context with { CombatEntryIndex = -1 }, context with { CombatEntryIndex = null },
            context with { CompleteFromRunStart = false } })
            Assert.Throws<ArgumentException>(invalid.Validate);
        (context with { CompleteFromRunStart = false, CombatEntryIndex = null }).Validate();
        var observation = LegacyObservation() with { Schema = PublicRunContext.ObservationSchema, RunContext = context };
        var result = new TeacherResult(new("player_decision", observation, [new(0, "end_turn")]),
            "test-teacher", PublicContinuationPolicies.LegacyId, "test-objective", [], 0, 0, 0,
            new(0, 0, 0, 0, 0, 0, 0, 0, 0), "protocol-only", [], new([], [], "none", []));
        object Record(TeacherResult value) => TeacherDataset.Record(value, "source", "combat", "branch", [], []);
        using var document = JsonDocument.Parse(PublicJson.Serialize(Record(result)));
        var input = document.RootElement.GetProperty("public_input");
        Assert.Equal(PublicRunContext.StudentSchema, input.GetProperty("schema_version").GetString());
        Assert.Equal(0, input.GetProperty("observation").GetProperty("runContext").GetProperty("combatEntryIndex").GetInt32());
        var audit = document.RootElement.GetProperty("audit_only");
        Assert.Equal(PublicRunContext.StudentSchema, audit.GetProperty("versions").GetProperty("public_schema").GetString());
        Assert.Equal("nosl.dataset.public-run-context.v1", audit.GetProperty("dataset_version").GetString());
        Assert.Throws<ArgumentException>(() => Record(result with { PublicRoot = result.PublicRoot with
            { Observation = observation with { Schema = "nosl.public.v2" } } }));
        Assert.Throws<ArgumentException>(() => Record(result with { PublicRoot = result.PublicRoot with
            { Observation = observation with { RunContext = null } } }));
    }

    [Fact]
    public async Task NativeSourceAndOwnedReplayRememberCombatEntriesWithoutChangingLegacyPackets()
    {
        var options = new NaturalSourceOptions(MaxFloors: 8, MaxRoots: 2, MaxRootsPerCombat: 1,
            SeedPrefix: "owned-native-opening", ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion);
        var legacy = await NaturalSourceCollector.CollectAsync(options);
        var contextual = await NaturalSourceCollector.CollectAsync(options with { PublicContextProfile = PublicRunContext.Version });
        Assert.Null(Assert.Single(contextual.Runs).Error);
        Assert.Equal(2, contextual.Roots.Length);
        Assert.Equal(legacy.Roots.Length, contextual.Roots.Length);
        for (int i = 0; i < contextual.Roots.Length; i++)
        {
            var root = contextual.Roots[i];
            var context = Assert.IsType<PublicRunContext>(root.PublicRoot.Observation!.RunContext);
            Assert.Equal(new(PublicRunContext.Version, root.Act, root.Floor, i, true), context);
            var stripped = root.PublicRoot with { Observation = root.PublicRoot.Observation with { Schema = "nosl.public.v2", RunContext = null } };
            Assert.Equal(PublicJson.Serialize(legacy.Roots[i].PublicRoot), PublicJson.Serialize(stripped));
            int globalSlot = root.SourceTrace.Count(t => t.Kind == "combat_action");
            await using var world = await NativeRunWorld.OpenAsync(new(MaxFloors: 8,
                OutsideCombatScript: options.OutsideCombatScript, PublicContextProfile: PublicRunContext.Version),
                options.SeedPrefix + ":0", globalSlot);
            Assert.NotNull(world);
            Assert.Equal(PublicJson.Serialize(root.PublicRoot), PublicJson.Serialize(world.Observe()));
            await using var fork = await world.ForkForContinuationAsync();
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            using var raw = JsonDocument.Parse(PublicJson.Serialize(root.ToSourceRecord()));
            Assert.Equal(PublicRunContext.StudentSchema, raw.RootElement.GetProperty("public_input").GetProperty("schema_version").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableAndInjectedObservationChannelsNeverInventCombatHistory(bool injected)
    {
        var options = new NativeRunExecutionOptions(PublicContextProfile: PublicRunContext.Version,
            PublicCombatHistoryMode: injected ? null : PublicRunContext.UnavailableHistoryMode);
        async Task Fixture(RunState run, RunDriver driver)
        {
            var player = run.Players.Single();
            foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
            for (int i = 0; i < 5; i++)
            {
                var card = (Nightmare)ModelDb.Card<Nightmare>().MutableClone();
                card.AssignOwner(player); player.Deck.AddInternal(card);
            }
            await driver.RunOneInjectedCombatAsync(RoomType.Monster, run.Act.MonsterEncounterCandidates.First().IdEntry);
        }
        await using var world = injected
            ? await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(options, "owned-choice-slots", 0, Fixture)
            : await NativeRunWorld.OpenAsync(options, "owned-native-opening:0", 0);
        Assert.NotNull(world);
        await using var fork = await world.ForkForContinuationAsync();
        var before = Assert.IsType<PublicRunContext>(world.Observe().Observation!.RunContext);
        Assert.False(before.CompleteFromRunStart); Assert.Null(before.CombatEntryIndex);
        var action = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(world.Observe());
        await world.StepAsync(action); await fork.StepAsync(action);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(before, world.Observe().Observation!.RunContext);
        if (injected) Assert.Equal("card_choice", world.Observe().Status);
    }

    [Fact]
    public void MidrunRecorderCannotClaimCompletenessEvenWhenBeginRunIsCalled()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-context-midrun", ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        var bridge = new NaturalSourceCollector.SourceBridge(run, new(PublicContextProfile: PublicRunContext.Version),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "midrun", "audit", null, null, default,
            startsAtNativeRunBeginning: true);
        bridge.BeginRun(run);
        var context = Assert.IsType<PublicRunContext>(bridge.CaptureRunContext());
        Assert.Equal(run.TotalFloor, context.Floor);
        Assert.False(context.CompleteFromRunStart); Assert.Null(context.CombatEntryIndex);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TapeCoordinatesUseOnlyTheDeclaredPublicRecorderChannel(bool unavailable)
    {
        var prior = new NativeTapePrior { EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version,
                PublicCombatHistoryMode: unavailable ? PublicRunContext.UnavailableHistoryMode : null) };
        var sourceRecipe = prior.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1"));
        DecisionPacket packet;
        await using (var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, sourceRecipe, new(sourceRecipe)))
        { Assert.NotNull(world); packet = world.Observe(); }
        var context = packet.Observation!.RunContext!;
        Assert.Equal(!unavailable, context.CompleteFromRunStart);
        Assert.Equal(unavailable ? null : sourceRecipe.CombatIndex, context.CombatEntryIndex);
        var source = new NativeTapeReplaySource(packet, prior);
        Assert.Equal(context.CombatEntryIndex, source.ConditionedPublicCombatIndex);
        var originalRandom = new Rng(424242); var conditionedRandom = new Rng(424242);
        var seenCombatIndices = new HashSet<int>();
        for (int i = 0; i < 60; i++)
        {
            var original = prior.Draw(originalRandom);
            var proposed = source.DrawConditionedRecipe(conditionedRandom);
            Assert.Equal(original with { DecisionIndex = sourceRecipe.DecisionIndex,
                CombatIndex = unavailable ? original.CombatIndex : sourceRecipe.CombatIndex }, proposed);
            seenCombatIndices.Add(proposed.CombatIndex);
        }
        Assert.Equal(unavailable ? 3 : 1, seenCombatIndices.Count);
        Assert.Equal(originalRandom.NextUnsignedLong(), conditionedRandom.NextUnsignedLong());
        var wrongChannel = prior with { Execution = prior.Execution with
            { PublicCombatHistoryMode = unavailable ? null : PublicRunContext.UnavailableHistoryMode } };
        Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(packet, wrongChannel));
        Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(packet,
            prior with { Execution = prior.Execution with { PublicContextProfile = null, PublicCombatHistoryMode = null } }));
        Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(packet with
            { Observation = packet.Observation with { RunContext = null } }, prior));
        if (!unavailable)
            Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(packet,
                prior with { EligibleCombats = sourceRecipe.CombatIndex }));
        // The detached public clone produces the same proposal law. No source
        // recipe, private audit, run graph, or source tape is supplied to it.
        var clone = new NativeTapeReplaySource(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet)), prior);
        Assert.Equal(source.DrawConditionedRecipe(new Rng(888)), clone.DrawConditionedRecipe(new Rng(888)));
    }

}
