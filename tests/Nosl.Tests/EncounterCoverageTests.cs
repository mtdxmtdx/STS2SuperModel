using Nosl.Worker;
using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Rooms;

namespace Nosl.Tests;

public sealed class EncounterCoverageTests
{
    public static IEnumerable<object[]> NaturalEncounters => EncounterCoverage.AllEncounters
        .Where(x => !x.RequiresEventContext).Select(x => new object[] { x.Name });

    [Fact]
    public void CatalogRetainsEveryActPoolAndEveryRegisteredMonsterWithoutEquatingRegistrationWithVerification()
    {
        Assert.Equal(80, EncounterCoverage.AllEncounters.Count(x => !x.RequiresEventContext));
        Assert.Equal(3, EncounterCoverage.AllEncounters.Count(x => x.RequiresEventContext));
        Assert.Equal(5, EncounterCoverage.AllForcedEvents.Count);
        Assert.Contains(EncounterCoverage.AllForcedEvents, x => x.Event == "DenseVegetation" && x.CanonicalEncounter is null);
        Assert.All(EncounterCoverage.AllForcedEvents, x => Assert.NotEmpty(x.AdapterRequirement));
        Assert.Equal(12, EncounterCoverage.AllEncounters.Count(x => x.RoomType == RoomType.Boss));
        Assert.Equal(12, EncounterCoverage.AllEncounters.Count(x => x.RoomType == RoomType.Elite));
        Assert.Equal(ContentRegistry.AllTypes.Where(t => typeof(MonsterModel).IsAssignableFrom(t))
            .Select(t => t.Name).Order(StringComparer.Ordinal), EncounterCoverage.AllMonsters.Select(x => x.Id));
        Assert.All(EncounterCoverage.AllMonsters, x =>
        {
            Assert.NotEmpty(x.Source); Assert.NotEmpty(x.Reason); Assert.NotEqual("Verified", x.Status);
        });
        Assert.Equal(EncounterCoverage.OutOfScope, EncounterCoverage.Monster("TrainingDummy").PosteriorMode);
        Assert.Equal(EncounterCoverage.OutOfScope, EncounterCoverage.Monster("Byrdpip").PosteriorMode);
        Assert.Equal(EncounterCoverage.ReplayRequired, EncounterCoverage.Monster("Fabricator").PosteriorMode);
        Assert.Contains("Dazed", EncounterCoverage.Monster("Entomancer").GeneratedCards);
        Assert.Contains("Wound", EncounterCoverage.Monster("TestSubject").GeneratedCards);
    }

    [Theory]
    [MemberData(nameof(NaturalEncounters))]
    public async Task EveryNaturalEncounterUsesNativeFactoryAndAdvancesSixEnemyPhases(string name)
    {
        var entry = EncounterCoverage.Find(name);
        var run = EncounterCoverage.CreateRun("NOSL-ENCOUNTER-CATALOG-" + name, name);
        var player = run.Players.Single();
        player.Creature.SetMaxHpInternal(10000);
        player.Creature.SetCurrentHpInternal(10000);
        var expected = EncounterCoverage.CreateMonsters(name, run);
        var room = EncounterCoverage.CreateRoom(name, run);
        run.PushRoom(room);
        await room.Enter(run);
        room.Engine.State.CardSelectionSource=Sts2Sim.Core.Combat.CardSelectionDecisionSources.RunEngine;
        Assert.Same(entry.Definition, room.Encounter);
        Assert.Equal(entry.ActIndex, run.CurrentActIndex);
        Assert.Equal(3, run.Acts.Count);
        Assert.Equal(entry.RoomType, room.RoomType);
        Assert.Equal(expected.Select(x => x.Monster.GetType().Name).Order(StringComparer.Ordinal),
            room.Engine.State.Enemies.Select(x => x.Monster!.GetType().Name).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Select(x => x.SlotName).Order(StringComparer.Ordinal),
            room.Engine.State.Enemies.Select(x => x.SlotName).Order(StringComparer.Ordinal));
        for (int turn = 0; turn < 6 && room.Engine.IsInProgress; turn++)
            await room.Engine.EndPlayerTurnAsync();
        if (room.Engine.IsInProgress)
            Assert.Equal(7, player.PlayerCombatState!.TurnNumber);
        else
        {
            if(room.Engine.Won) Assert.NotEmpty(room.Engine.State.EscapedCreatures);
            else
            {
                Assert.Equal("TheInsatiableBoss",name); // Native instant-kill bypasses the fixture's large HP.
                Assert.True(player.Creature.IsDead);
            }
            await room.ResolveOutcomeAsync();
        }
    }

    [Theory]
    [InlineData("Nibbits", 2)]
    [InlineData("SlimesNormal", 4)]
    [InlineData("TheKin", 3)]
    [InlineData("DecimillipedeElite", 3)]
    [InlineData("KaiserCrabBoss", 2)]
    [InlineData("QueenBoss", 2)]
    public async Task NamedFormationPreservesRolesSlotsAndCorrelatedStarts(string name, int count)
    {
        var run = EncounterCoverage.CreateRun("NOSL-FORMATION", name);
        var room = EncounterCoverage.CreateRoom(name, run);
        run.PushRoom(room); await room.Enter(run);
        Assert.Equal(count, room.Engine.State.Enemies.Count);
        if (name == "Nibbits")
        {
            var monsters = room.Engine.State.Enemies.Select(x => (Nibbit)x.Monster!).ToArray();
            Assert.Single(monsters, x => x.IsFront); Assert.All(monsters, x => Assert.False(x.IsAlone));
            Assert.Equal(new[] { "front", "back" }, room.Engine.State.Enemies.Select(x => x.SlotName));
        }
        if (name == "TheKin")
            Assert.Single(room.Engine.State.Enemies.Select(x => x.Monster).OfType<KinFollower>(), x => x.StartsWithDance);
        if (name == "DecimillipedeElite")
            Assert.Equal(new[] { 0, 1, 2 }, room.Engine.State.Enemies.Select(x => ((DecimillipedeSegment)x.Monster!).StarterMoveIdx).Order());
        if (name == "QueenBoss")
            Assert.Equal(new[] { "amalgam", "queen" }, room.Engine.State.Enemies.Select(x => x.SlotName));
    }

    [Theory]
    [MemberData(nameof(NaturalEncounters))]
    public async Task EveryNaturalEncounterAdapterAndExactBranchAgreeAcrossOpeningPhases(string name)
    {
        await using var source = await CombatSession.CreateAsync(new(Seed: "NOSL-ADAPTER-" + name,
            Encounter: name, Hp: 10000, MaxHp: 10000));
        await using var branch = source.ForkExact();
        Assert.NotSame(source.Room, branch.Room);
        for (int phase = 0; phase <= 2; phase++)
        {
            Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
            if (phase == 2) break;
            await source.StepAsync(source.Observe().Actions.Single(x => x.Kind == "end_turn"));
            await branch.StepAsync(branch.Observe().Actions.Single(x => x.Kind == "end_turn"));
            for(int choice=0;source.HasPendingChoice;choice++)
            {
                Assert.True(choice<20);
                Assert.Equal(PublicJson.Serialize(source.Observe()),PublicJson.Serialize(branch.Observe()));
                var action=source.Observe().Actions[0];
                await source.StepAsync(action); await branch.StepAsync(action);
            }
        }
    }

    [Fact]
    public async Task RepeatLimitedMonsterPosteriorConditionsOnFullPublicIntentHistory()
    {
        await using var source = await CombatSession.CreateAsync(new(Enemy: "TwigSlimeM",
            Deck: ["DefendSilent"], Hp: 10000, MaxHp: 10000, EnemyHp: 1000));
        // The native fixed first sticky move forces the first attack. After one attack both
        // branches have equal weight; after two consecutive attacks only sticky is legal.
        await source.StepAsync(source.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.NotNull(source.Observe().Observation!.Enemies.Single().Intents.Single().Damage);
        int attacks = 0;
        ulong? repeatedSeed = null;
        const int samples = 256;
        for (ulong seed = 0; seed < samples; seed++)
        {
            await using var world = BeliefSampler.SampleWorld(source, seed);
            Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(world.Observe()));
            await world.StepAsync(world.Observe().Actions.Single(x => x.Kind == "end_turn"));
            if (world.Observe().Observation!.Enemies.Single().Intents.Single().Damage is not null)
            {
                attacks++;
                repeatedSeed ??= seed;
            }
        }
        Assert.InRange((double)attacks / samples, .35, .65);
        Assert.NotNull(repeatedSeed);
        await using var repeated = BeliefSampler.SampleWorld(source, repeatedSeed!.Value);
        await repeated.StepAsync(repeated.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.NotNull(repeated.Observe().Observation!.Enemies.Single().Intents.Single().Damage);
        for (ulong seed = 0; seed < 32; seed++)
        {
            await using var world = BeliefSampler.SampleWorld(repeated, seed);
            await world.StepAsync(world.Observe().Actions.Single(x => x.Kind == "end_turn"));
            Assert.Null(world.Observe().Observation!.Enemies.Single().Intents.Single().Damage);
        }
    }

    [Fact]
    public void UnsupportedEventSettlementCannotSilentlyMasqueradeAsAnOrdinaryRoom()
    {
        foreach (var entry in EncounterCoverage.AllEncounters.Where(x => x.RequiresEventContext))
        {
            var error = Assert.Throws<NotSupportedException>(() => EncounterCoverage.CreateRun("event", entry.Name));
            Assert.Contains("forced-event", error.Message);
        }
        var run = EncounterCoverage.CreateRun("wrong-act", null);
        Assert.Throws<ArgumentException>(() => EncounterCoverage.CreateRoom("QueenBoss", run));
        Assert.Throws<NotSupportedException>(() => EncounterCoverage.Find("not-in-pinned-source"));
    }
}
