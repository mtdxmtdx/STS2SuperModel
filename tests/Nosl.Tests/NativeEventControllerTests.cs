using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeEventControllerTests
{
    [Theory]
    [InlineData(10964912763266653587UL, 16104362297584561844UL, 2768508988275759907UL, 2)]
    [InlineData(5211578176485590920UL, 8901195201571256817UL, 5400050922907381310UL, 1)]
    public async Task FailedIndependentBathsRecipesKeepV2FailureAndReachV3Boundary(
        ulong runSeed, ulong tapeSeed, ulong proposalSeed, int combatIndex)
    {
        // Exact independent proposals from sampler seeds 201/202; these seeds
        // identify regression fixtures only and never enter the event policy.
        var recipe = new NativeTapeRecipe(runSeed, tapeSeed, proposalSeed, combatIndex, 4);
        var old = new NativeRunExecutionOptions(MaxFloors: 8, SourceDecisionHorizon: 1024);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(old, recipe, new(recipe)));
        Assert.Contains("maximum number of choices", error.Message);

        var bounded = old with { OutsideCombatScript = NaturalSourceCollector.BoundedEventScriptVersion };
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(bounded, recipe, new(recipe));
        Assert.NotNull(world);
        Assert.Equal(4, world.Observe().Actions[0].Revision);
        var choices = world.SourceTrace.Where(t => t.Kind == "event_choice")
            .Select(ReadChosen).ToArray();
        Assert.Equal(new[] { "IMMERSE", "LINGER", "EXIT_BATHS" },
            choices.Where(key => key is "IMMERSE" or "LINGER" or "EXIT_BATHS"));
        await using var replay = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(replay.Observe()));
    }

    [Fact]
    public async Task NativeEventBoundariesResetOnSameFloorAndGiveUpOnlyEndsOptionalEvent()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("bounded-public-event-lifecycle", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        var bridge = new NaturalSourceCollector.SourceBridge(run,
            new(OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [],
            "constructed-event-lifecycle", "irrelevant-to-controller", null, null, default);
        var driver = new RunDriver(run, bridge, recorder: bridge, useAvailablePotions: false);
        int floor = run.TotalFloor;
        for (int i = 0; i < 2; i++)
        {
            var room = new EventRoom(() => (EventModel)ModelDb.Event<AbyssalBaths>().MutableClone());
            run.PushRoom(room); await room.Enter(run);
            await driver.DriveEventAsync(room);
            Assert.True(room.Event.IsFinished);
            Assert.Equal(floor, run.TotalFloor);
            run.PopCurrentRoom();
        }
        Assert.Equal(new[] { "IMMERSE", "LINGER", "EXIT_BATHS", "IMMERSE", "LINGER", "EXIT_BATHS" },
            bridge.Trace.Where(t => t.Kind == "event_choice").Select(ReadChosen));

        int maxHp = player.Creature.MaxHp;
        var tablet = new EventRoom(() => (EventModel)ModelDb.Event<TabletOfTruth>().MutableClone());
        run.PushRoom(tablet); await tablet.Enter(run);
        await driver.DriveEventAsync(tablet);
        Assert.Equal(new[] { "DECIPHER", "GIVE_UP" },
            bridge.Trace.Where(t => t.Kind == "event_choice").TakeLast(2).Select(ReadChosen));
        Assert.True(tablet.Event.IsFinished);
        Assert.Equal(maxHp - 3, player.Creature.MaxHp);
        Assert.True(player.Creature.CurrentHp > 0);
    }

    private static string? ReadChosen(NaturalSourceTrace trace)
    {
        using var json = JsonDocument.Parse(trace.PublicDetail);
        return json.RootElement.GetProperty("chosen").GetString();
    }

    [Fact]
    public void BoundedControllerUsesPublicAvailabilityWithoutSkippingContinuationStages()
    {
        var bounded = new NaturalSourceCollector.PublicEventChoiceController(NaturalSourceCollector.BoundedEventScriptVersion);
        (string Key, bool IsLocked)[] page = [("LOCKED", true), ("LINGER", false), ("EXIT_BATHS", true)];
        Assert.Equal(1, bounded.Choose(page));
        Assert.Equal(1, bounded.Choose(page)); // The exit has not become legal.
        page[2] = ("EXIT_BATHS", false);
        Assert.Equal(2, bounded.Choose(page));
        bounded.BeginEvent();
        Assert.Equal(1, bounded.Choose(page)); // Same public keys in a different event.
        Assert.Equal(2, bounded.Choose(page));
        foreach (string continuation in new[] { "CONTINUE", "CONTINUE_FIGHT" })
            for (int i = 0; i < 5; i++)
                Assert.Equal(0, bounded.Choose([(continuation, false), ("LEAVE", false)]));
        // No substring guessing: unknown keys retain the declared first-choice
        // behavior and any resulting engine error still propagates to the caller.
        bounded.BeginEvent();
        Assert.Equal(0, bounded.Choose([("LINGER", false), ("EXIT_UNKNOWN", false)]));
        Assert.Equal(0, bounded.Choose([("LINGER", false), ("EXIT_UNKNOWN", false)]));
        var legacy = new NaturalSourceCollector.PublicEventChoiceController(null);
        Assert.Equal(1, legacy.Choose(page)); Assert.Equal(1, legacy.Choose(page));
    }

    [Fact]
    public async Task OmittedScriptKeepsOldPriorIdentityAndUnknownScriptFailsBeforeStartup()
    {
        var old = new NativeRunExecutionOptions(MaxFloors: 8, SourceDecisionHorizon: 1024);
        Assert.Equal("{\"maxFloors\":8,\"sourceDecisionHorizon\":1024,\"sourcePolicyId\":\"nosl-public-rules-v2\"}",
            PublicJson.Serialize(old));
        var prior = new NativeTapePrior { Execution = old, EligibleCombats = 3, EligibleDecisionsPerCombat = 8 };
        Assert.Equal("f12471f0c28821e3da1322145cf4656c56f997671589916b53446f3aa83ba519", prior.Identity);
        var changed = old with { OutsideCombatScript = NaturalSourceCollector.BoundedEventScriptVersion };
        Assert.NotEqual(prior.Identity, (prior with { Execution = changed }).Identity);
        Assert.DoesNotContain("outsideCombatScript", PublicJson.Serialize(new NaturalSourceOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeRunWorld.OpenAsync(
            old with { OutsideCombatScript = "unknown-script" }, "irrelevant", 0));
    }
}
