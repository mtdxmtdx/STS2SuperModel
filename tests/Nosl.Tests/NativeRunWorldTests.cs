using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeRunWorldTests
{
    [Fact]
    public async Task NativeOpeningMatchesCollectorAndForkOwnsIndependentRunThroughSettlement()
    {
        const string seed = "owned-native-opening";
        var collected = await NaturalSourceCollector.CollectAsync(new(MaxRoots: 1, SeedPrefix: seed,
            ContinuationPolicyId: PublicContinuationPolicies.ReviewedId));
        Assert.Null(Assert.Single(collected.Runs).Error);
        await using var world = await NativeRunWorld.OpenAsync(new(), seed + ":0", 0);
        Assert.NotNull(world);
        Assert.False(world.IsConstructedLifecycleFixture);
        Assert.IsType<RunState>(world.NativeRun);
        Assert.Equal(PublicJson.Serialize(Assert.Single(collected.Roots).PublicRoot), PublicJson.Serialize(world.Observe()));
        Assert.Contains(world.SourceTrace, t => t.Kind == "event_choice");
        Assert.Contains(world.SourceTrace, t => t.Kind == "map_choice");
        var original = PublicJson.Serialize(world.Observe());
        var external = world.Observe();
        external.Observation!.Hand[0] = external.Observation.Hand[0] with { Id = "EXTERNAL_MUTATION" };
        Assert.Equal(original, PublicJson.Serialize(world.Observe()));
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        Assert.NotSame(world.NativeCombatRoom, fork.NativeCombatRoom);
        Assert.NotSame(world.NativeRun.Players.Single(), fork.NativeRun.Players.Single());
        var outcome = await Finish(fork);
        Assert.True(outcome.SettlementComplete);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.Equal((double)outcome.HpAfterSettlement!, outcome.HpAtCombatStart
            - outcome.CumulativeHpDamage!.Value + outcome.HealingReceived!.Value + outcome.OtherHpAdjustment!.Value);
        Assert.Equal(original, PublicJson.Serialize(world.Observe()));
        Assert.DoesNotContain(fork.SourceTrace, t => t.Kind == "reward_choice");
        Assert.DoesNotContain(outcome.PermanentChanges, p => p.Kind.StartsWith("earned_combat_reward_opportunity:"));
        if (outcome.TerminalKind == TerminalKind.Win) Assert.NotEmpty(fork.StandardRewardOpportunities);
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await Finish(world)));
    }

    [Fact]
    public async Task SlotLawIncludesPendingChoicesAndSourceHorizonStopsOnlyBeforeSelection()
    {
        const string seed = "owned-native-choice-slot";
        var collected = await NaturalSourceCollector.CollectAsync(new(MaxFloors: 4, MaxRoots: 40,
            MaxRootsPerCombat: 40, SeedPrefix: seed, ContinuationPolicyId: PublicContinuationPolicies.ReviewedId));
        Assert.Null(Assert.Single(collected.Runs).Error);
        const int slot = 3;
        Assert.True(collected.Roots.Length > slot);
        await using var world = await NativeRunWorld.OpenAsync(new(), seed + ":0", slot);
        Assert.NotNull(world);
        Assert.Equal(slot + 1, world.EligibleSlotsVisited);
        Assert.Equal(PublicJson.Serialize(collected.Roots[slot].PublicRoot), PublicJson.Serialize(world.Observe()));
        await using var horizon = await NativeRunWorld.OpenAsync(new(SourceDecisionHorizon: world.SourceDecisions), seed + ":0", slot);
        Assert.NotNull(horizon);
        Assert.Null(await NativeRunWorld.OpenAsync(new(SourceDecisionHorizon: world.SourceDecisions - 1), seed + ":0", slot));
        Assert.Null(await NativeRunWorld.OpenAsync(new(MaxFloors: 1), seed + ":0", int.MaxValue));
        var outcome = await Finish(horizon);
        Assert.True(outcome.IsTrueTerminal);
        await using var fork = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await Finish(fork)));

        async Task ChoiceFixture(RunState run, RunDriver driver)
        {
            SetDeck(run.Players.Single(), Enumerable.Repeat(nameof(Nightmare), 5).ToArray());
            await driver.RunOneInjectedCombatAsync(RoomType.Monster, run.Act.MonsterEncounterCandidates.First().IdEntry);
        }
        await using var stable = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(), "owned-choice-slots", 0, ChoiceFixture);
        await using var pending = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(), "owned-choice-slots", 1, ChoiceFixture);
        Assert.NotNull(stable); Assert.NotNull(pending);
        await stable.StepAsync(PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(stable.Observe()));
        Assert.Equal("card_choice", pending.Observe().Status);
        Assert.Equal(2, pending.EligibleSlotsVisited);
        Assert.Equal(PublicJson.Serialize(stable.Observe()), PublicJson.Serialize(pending.Observe()));
    }

    [Fact]
    public async Task UnreviewedCardAndPendingCoroutineUseReplayWithoutNativeWhitelist()
    {
        Assert.DoesNotContain(nameof(Nightmare), NativeBeliefCertificate.Cards);
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-constructed-nightmare", 0, NightmareFixture);
        Assert.NotNull(world);
        Assert.True(world.IsConstructedLifecycleFixture);
        var packet = world.Observe();
        var nightmare = packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == nameof(Nightmare));
        Assert.Equal("card_choice", (await world.StepAsync(nightmare)).Status);
        string before = PublicJson.Serialize(world.Observe());
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.Equal(before, PublicJson.Serialize(fork.Observe()));
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        await fork.StepAsync(fork.Observe().Actions[0]);
        Assert.Equal(before, PublicJson.Serialize(world.Observe()));
        await world.StepAsync(world.Observe().Actions[0]);
        // End-turn resumes native delayed Nightmare copies, preserving its real
        // selected card/power owner without serializing private power payloads.
        await world.StepAsync(world.Observe().Actions.Single(a => a.Kind == "end_turn"));
        await fork.StepAsync(fork.Observe().Actions.Single(a => a.Kind == "end_turn"));
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await Finish(world)), PublicJson.Serialize(await Finish(fork)));
    }

    [Fact]
    public async Task ConstructedNativeBossKeepsConcreteRunAndRewardPools()
    {
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-constructed-boss", 0, BossFixture);
        Assert.NotNull(world);
        Assert.Equal(RoomType.Boss, world.NativeCombatRoom.RoomType);
        Assert.Same(world.NativeRun, world.NativeCombatRoom.Engine.State.RunState);
        Assert.Contains(world.NativeRun.Act.BossEncounterCandidates, e => e.Name == world.Encounter);
        await world.StepAsync(world.Observe().Actions.Single(a => a.Kind == "end_turn"));
        static bool NativePhaseChanged(NativeRunWorld session) => session.NativeCombatRoom.Engine.State.Enemies.Single().Monster switch
        {
            CeremonialBeast beast => beast.IsInSecondPhase,
            LagavulinMatriarch matriarch => matriarch.IsShellAwake,
            _ => false,
        };
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 10 && !NativePhaseChanged(world); i++) await world.StepAsync(policy.Choose(world.Observe()));
        Assert.True(NativePhaseChanged(world), "Constructed boss fixture must reach its native second phase before replay");
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.True(NativePhaseChanged(fork));
        var outcome = await Finish(world);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.Contains("constructed_native_lifecycle_fixture", outcome.Detail);
        Assert.NotEmpty(world.NativeCombatRoom.GeneratedRewards);
        Assert.NotEmpty(world.StandardRewardOpportunities);
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await Finish(fork)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructedForcedOwnersSettleAtNativeReturnOrBeforeCombatRewardChoice(bool rewards)
    {
        async Task Fixture(RunState run, RunDriver driver)
        {
            SetDeck(run.Players.Single(), Enumerable.Repeat(nameof(GrandFinale), 5).ToArray());
            run.Players.Single().Creature.SetMaxHpInternal(100);
            run.Players.Single().Creature.SetCurrentHpInternal(20);
            EventModel model = rewards ? (EventModel)ModelDb.Event<DenseVegetation>().MutableClone()
                : (EventModel)ModelDb.Event<BattlewornDummy>().MutableClone();
            var room = new EventRoom(() => model);
            run.PushRoom(room); await room.Enter(run);
            // These are explicit constructed owner setup choices, never a claim
            // that the fixed natural source script chose this event or path.
            await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == (rewards ? "REST" : "SETTING_1")));
            if (rewards) await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == "FIGHT"));
            await driver.DriveEventAsync(room);
        }
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-constructed-owner", 0, Fixture);
        Assert.NotNull(world);
        await using var fork = await world.ForkForContinuationAsync();
        var outcome = await Finish(world);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.DoesNotContain(world.SourceTrace, t => t.Kind == "reward_choice");
        if (rewards)
        {
            Assert.NotEmpty(world.StandardRewardOpportunities);
            Assert.DoesNotContain(outcome.PermanentChanges, p => p.Kind.StartsWith("earned_event_reward_opportunity:"));
        }
        else
        {
            Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
            Assert.True(Assert.IsType<EventRoom>(world.NativeRun.CurrentRoom).Event.IsFinished);
            Assert.Contains(outcome.PermanentChanges, p => p.Kind == "earned_event_reward_opportunity:PotionReward" && p.Amount == 1);
            Assert.Empty(outcome.InventoryEnd);
        }
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await Finish(fork)));
    }

    [Fact]
    public async Task CancellationAndEngineFailuresPropagateInsteadOfBecomingAbsentSlots()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeRunWorld.OpenAsync(new(), "owned-cancelled", 0, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-engine-failure", 0, (_, _) => throw new InvalidOperationException("fixture_engine_fault")));
        using var during = new CancellationTokenSource();
        await using var world = await NativeRunWorld.OpenAsync(new(), "owned-cancel-during", 0, during.Token);
        Assert.NotNull(world);
        var action = world.Observe().Actions[0];
        during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.StepAsync(action));
    }

    [Fact]
    public async Task CancellationConcurrentWithSignalReplacementCannotStrandAnOwnedStep()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            await using var world = await NativeRunWorld.OpenAsync(new(), "owned-cancel-race", 0, cancellation.Token);
            Assert.NotNull(world);
            var action = world.Observe().Actions.Single(a => a.Kind == "end_turn");
            var step = Task.Run(async () =>
            {
                try { await world.StepAsync(action); }
                catch (OperationCanceledException) { }
            });
            var cancel = Task.Run(cancellation.Cancel);
            // Completion may win the race; otherwise cancellation must release
            // the newly installed signal even if the old signal was already done.
            await Task.WhenAll(step, cancel).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(nameof(PunchOff), "I_CAN_TAKE_THEM")]
    [InlineData(nameof(TheLanternKey), "KEEP_THE_KEY")]
    public async Task RewardEnabledForcedLossReturnsToItsActualOwnerBeforeSettlement(string owner, string firstOption)
    {
        async Task Fixture(RunState run, RunDriver driver)
        {
            var player = run.Players.Single();
            SetDeck(player, [nameof(DefendSilent)]);
            player.Creature.SetCurrentHpInternal(1);
            var model = (EventModel)ModelDb.All<EventModel>().Single(e => e.GetType().Name == owner).MutableClone();
            var room = new EventRoom(() => model);
            run.PushRoom(room); await room.Enter(run);
            await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == firstOption));
            await model.ChooseOption(model.CurrentOptions.Single(o => o.Key == "FIGHT"));
            await driver.DriveEventAsync(room);
        }
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-constructed-owner-loss", 0, Fixture);
        Assert.NotNull(world);
        var outcome = await Finish(world);
        Assert.Equal(TerminalKind.Loss, outcome.TerminalKind);
        var ownerAfter = Assert.IsType<EventRoom>(world.NativeRun.CurrentRoom).Event;
        Assert.True(ownerAfter.IsFinished);
        Assert.False(ownerAfter.IsAwaitingForcedCombat);
        Assert.Empty(ownerAfter.ForcedCombatExtraRewards);
        Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
        Assert.Empty(world.StandardRewardOpportunities);
        Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
        Assert.DoesNotContain(outcome.PermanentChanges, p => p.Kind.StartsWith("earned_"));
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.DoesNotContain(world.SourceTrace, t => t.Kind == "reward_choice");
    }

    [Fact]
    public async Task ForcedTimeoutReturnsWithoutInventingAnEventRewardOpportunity()
    {
        async Task Fixture(RunState run, RunDriver driver)
        {
            SetDeck(run.Players.Single(), [nameof(DefendSilent)]);
            run.Players.Single().Creature.SetMaxHpInternal(1000);
            run.Players.Single().Creature.SetCurrentHpInternal(1000);
            var room = new EventRoom(() => (EventModel)ModelDb.Event<BattlewornDummy>().MutableClone());
            run.PushRoom(room); await room.Enter(run);
            await driver.DriveEventAsync(room);
        }
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(),
            "owned-constructed-owner-timeout", 0, Fixture);
        Assert.NotNull(world);
        var outcome = await Finish(world);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind); // Native timed escape wins the combat, not the tier reward.
        Assert.True(Assert.IsType<EventRoom>(world.NativeRun.CurrentRoom).Event.IsFinished);
        Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
        Assert.Empty(world.StandardRewardOpportunities);
        Assert.DoesNotContain(outcome.PermanentChanges, p => p.Kind.StartsWith("earned_"));
        Assert.Empty(outcome.InventoryEnd);
    }

    private static async Task NightmareFixture(RunState run, RunDriver driver)
    {
        SetDeck(run.Players.Single(), [nameof(Nightmare), nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale)]);
        run.Players.Single().Creature.SetMaxHpInternal(1000);
        run.Players.Single().Creature.SetCurrentHpInternal(1000);
        await driver.RunOneInjectedCombatAsync(RoomType.Monster, run.Act.MonsterEncounterCandidates.First().IdEntry);
    }

    private static async Task BossFixture(RunState run, RunDriver driver)
    {
        SetDeck(run.Players.Single(), Enumerable.Repeat(nameof(GrandFinale), 5).ToArray());
        run.Players.Single().Creature.SetMaxHpInternal(1000);
        run.Players.Single().Creature.SetCurrentHpInternal(1000);
        await driver.RunOneInjectedCombatAsync(RoomType.Boss, run.Act.BossEncounterCandidates.First().IdEntry);
    }

    private static void SetDeck(Player player, string[] cards)
    {
        foreach (var old in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(old);
        foreach (var id in cards)
        {
            var card = (CardModel)ModelDb.All<CardModel>().Single(c => c.GetType().Name == id).MutableClone();
            card.AssignOwner(player); player.Deck.AddInternal(card);
        }
    }

    private static async Task<RolloutOutcome> Finish(ITeacherWorld world)
    {
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        int lastTurn = 0;
        for (int i = 0; i < 600 && world.Observe().Status != "terminal_settled"; i++)
        {
            var packet = world.Observe(); lastTurn = packet.Observation!.Turn;
            await world.StepAsync(policy.Choose(packet));
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        return await world.RecordSettledAsync(policy.Id, lastTurn);
    }
}
