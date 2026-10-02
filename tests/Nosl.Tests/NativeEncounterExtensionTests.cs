using System.Reflection;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;

namespace Nosl.Tests;

public sealed class NativeEncounterExtensionTests
{
    [Fact]
    public async Task FixedCohortImportsAllNewRootsAndErasesHiddenCarryInWithOwnedChoiceAndStun()
    {
        var options = new NaturalSourceOptions(Runs: 100, MaxFloors: 12, MaxRoots: 200,
            MaxRootsPerCombat: 8, SeedPrefix: "nosl-m5-natural-proof-20261001", SourceRunPrefix: "native-encounter-regression");
        int importedCount = 0, pending = 0, stunned = 0;
        var checkedFamilies = new HashSet<string>();
        var report = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(options, async (root, boundary) =>
        {
            if (!NativeEncounterMemory.Encounters.Contains(root.Encounter)) return;
            await using var imported = await CombatSession.ImportNativeAsync(root, boundary);
            importedCount++;
            Assert.Equal(boundary.IsStable ? NativeEncounterMemory.Profile : NativeEncounterMemory.ChoiceProfile,
                BeliefSampler.PosteriorProfileFor(imported));
            Assert.Equal(PublicJson.Serialize(root.PublicRoot), PublicJson.Serialize(imported.Observe()));
            bool stun = imported.State.Enemies.Any(e => e.Monster!.NextMove!.Id == "STUNNED");
            if (stun) stunned++;
            if (!boundary.IsStable) pending++;
            string family = root.Encounter + ":" + boundary.IsStable + ":" + stun;
            if (!checkedFamilies.Add(family)) return;
            string source = PublicJson.Serialize(imported.Observe()), sourceStreams = Streams(imported);
            var sourceDraw = boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards.ToArray();
            var sourceDeck = boundary.State.Players.Single().Deck.Cards.ToArray();
            var stable = boundary.IsStable ? imported : boundary.ChoiceReplay!.Origin;
            await using var changedOrigin = stable.ForkExact();
            Reverse(changedOrigin.State.Players.Single().PlayerCombatState!.DrawPile);
            Reverse(changedOrigin.State.Players.Single().Deck);
            changedOrigin.ReseedFuture(999999);
            changedOrigin.State.Players.Single().Odds.LoadFromSerializable(new()
                { CardRarityOddsValue = .75f, PotionRewardOddsValue = .9f });
            await using var changed = boundary.IsStable ? changedOrigin.ForkExact()
                : await CombatSession.ImportNativeAsync(root, boundary with
                    { ChoiceReplay = boundary.ChoiceReplay! with { Origin = changedOrigin } });
            Assert.Equal(source, PublicJson.Serialize(changed.Observe()));
            await using var a = await BeliefSampler.SampleWorldAsync(imported, 81);
            await using var b = await BeliefSampler.SampleWorldAsync(changed, 81);
            Assert.NotEqual(sourceStreams, Streams(a));
            Assert.Equal(Streams(a), Streams(b));
            Assert.Equal(Deck(a), Deck(b)); Assert.Equal(Draw(a), Draw(b));
            await FinishTogether(a, b);
            Assert.True(a.Knowledge.OutcomeLedger.HpComplete);
            Assert.Equal(source, PublicJson.Serialize(imported.Observe()));
            Assert.Equal(sourceStreams, Streams(imported));
            Assert.Equal(sourceDraw, boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards);
            Assert.Equal(sourceDeck, boundary.State.Players.Single().Deck.Cards);

            if (!boundary.IsStable) return;
            await VerifyPublicMemoryGuards(imported, stun);
        });
        Assert.All(report.Runs, r => Assert.Null(r.Error));
        Assert.Equal(200, report.Roots.Length);
        Assert.Equal(26, importedCount); Assert.Equal(2, pending); Assert.Equal(1, stunned);
        Assert.Equal(6, checkedFamilies.Count);
    }

    private static async Task VerifyPublicMemoryGuards(CombatSession imported, bool stun)
    {
        async Task Reject(Action<CombatSession> mutate)
        {
            await using var corrupted = imported.ForkExact();
            string before = PublicJson.Serialize(corrupted.Observe());
            mutate(corrupted);
            Assert.Equal(before, PublicJson.Serialize(corrupted.Observe()));
            Assert.False(BeliefSampler.UsesExchangeablePosterior(corrupted));
            await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(corrupted, 81));
        }
        await Reject(s => s.State.Enemies[0].Monster!.MoveStateMachine!.StateLog.Add(
            s.State.Enemies[0].Monster!.MoveStateMachine!.StateLog[0]));
        if (imported.State.Enemies[0].Monster is CorpseSlug)
        {
            await Reject(s => ((CorpseSlug)s.State.Enemies[0].Monster!).StarterMoveIdx += 3);
            await Reject(s => ((CorpseSlug)s.State.Enemies[0].Monster!).IsRavenous ^= true);
            if (stun)
                await Reject(s => s.State.Enemies[0].Monster!.SetMoveImmediate(new MoveState("STUNNED", _ => Task.CompletedTask,
                    new StunIntent()) { MustPerformOnceBeforeTransitioning = true, FollowUpStateId = "GLOMP_MOVE" }, true));
        }
        if (imported.State.Enemies[0].Monster is TwoTailedRat)
        {
            await Reject(s => ((TwoTailedRat)s.State.Enemies[0].Monster!).StarterMoveIndex += 3);
            await Reject(s => ((TwoTailedRat)s.State.Enemies[0].Monster!).CallForBackupCount = 1);
            await Reject(s => typeof(TwoTailedRat).GetField("_turnsUntilSummonable", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(s.State.Enemies[0].Monster, 0));
            await Reject(s => s.State.Enemies[0].AssignSlotName("first"));
            await Reject(s => ((string[])typeof(Sts2Sim.Core.Combat.CombatState)
                .GetField("_encounterSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.State)!)[0] = "fifth");
        }
        await using var duplicate = imported.ForkExact();
        var enemy = duplicate.State.Enemies[0];
        duplicate.Knowledge.Events.Add(new("intent_published", PublicJson.Serialize(new
            { slot = duplicate.Knowledge.Slot(enemy), id = enemy.Monster!.GetType().Name, intents = PublicViews.Intents(enemy) })));
        Assert.True(BeliefSampler.UsesExchangeablePosterior(duplicate));
        duplicate.Knowledge.Events.RemoveAll(e => e.Kind == "intent_published");
        Assert.False(BeliefSampler.UsesExchangeablePosterior(duplicate));
    }

    [Theory]
    [InlineData("FirePotion", false)]
    [InlineData("PowderedDemise", true)]
    public async Task OwnedSlugContinuationPreservesBothStunTimingsAndCloneFollowUp(string potion, bool delayed)
    {
        await using var actual = await CombatSession.CreateAsync(new(Seed: "native-slug-timing-v1",
            Encounter: "CorpseSlugsWeak", Potions: [potion], Hp: 150, MaxHp: 150));
        // A small deterministic damage fixture lets each real potion cause the death.
        actual.State.Enemies[0].SetCurrentHpInternal(8);
        await using var projection = actual.ForkExact();
        int victim = actual.Knowledge.Slot(actual.State.Enemies[0]);
        var use = actual.Observe().Actions.Single(x => x.Kind == "potion" && x.Target == victim);
        await Both(actual, projection, use);
        if (delayed) await Both(actual, projection, actual.Observe().Actions.Single(x => x.Kind == "end_turn"));
        var survivor = actual.State.Enemies.Single().Monster!;
        Assert.Equal("STUNNED", survivor.NextMove!.Id);
        Assert.Equal(survivor.MoveStateMachine!.StateLog.Last().Id, survivor.NextMove.FollowUpStateId);
        Assert.True(((CorpseSlug)survivor).IsRavenous);
        Assert.DoesNotContain(survivor.MoveStateMachine.StateLog, x => x.Id == "STUNNED");
        Assert.Equal(!delayed, NativeEncounterMemory.Matches(actual.State, actual.Knowledge,
            "CorpseSlugsWeak", actual.Observe().Observation!));
        string resume = survivor.NextMove.FollowUpStateId!;
        await using var afterStun = projection.ForkExact();
        Assert.Equal(resume, afterStun.State.Enemies.Single().Monster!.NextMove!.FollowUpStateId);
        await Both(actual, projection, actual.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.Equal(resume, actual.State.Enemies.Single().Monster!.NextMove!.Id);
        Assert.False(((CorpseSlug)actual.State.Enemies.Single().Monster!).IsRavenous);
        Assert.False(NativeEncounterMemory.Matches(actual.State, actual.Knowledge,
            "CorpseSlugsWeak", actual.Observe().Observation!));
        // The third independently owned clone must consume its own stun identically.
        await afterStun.StepAsync(afterStun.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.Equal(PublicJson.Serialize(projection.Observe()), PublicJson.Serialize(afterStun.Observe()));
        await FinishTogether(actual, projection);
    }

    [Fact]
    public async Task OwnedRatSummonsPreservePhysicalOrderingLifetimeTargetsAndSettlement()
    {
        await using var actual = await CombatSession.CreateAsync(new(Seed: "native-rat-summon-v1",
            Encounter: "TwoTailedRatsNormal", Hp: 300, MaxHp: 300));
        await using var projection = actual.ForkExact();
        Assert.True(projection.State.IsLiveCombat());
        for (int turn = 0; turn < 8 && actual.State.SpawnedEnemies.Count == 3; turn++)
            await Both(actual, projection, actual.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.True(actual.State.SpawnedEnemies.Count > 3);
        Assert.Equal(actual.State.SpawnedEnemies.Count, projection.State.SpawnedEnemies.Count);
        Assert.Equal(actual.State.Enemies.Count, actual.Observe().Observation!.Enemies.Select(e => e.Slot).Distinct().Count());
        Assert.True(actual.Observe().Observation!.Enemies[0].Slot >= 3);
        Assert.Equal("second", actual.State.Enemies[0].SlotName);
        Assert.Equal(actual.State.Enemies.Select(e => e.SlotName), projection.State.Enemies.Select(e => e.SlotName));
        Assert.False(NativeEncounterMemory.Matches(projection.State, projection.Knowledge, "TwoTailedRatsNormal", projection.Observe().Observation!));
        await using var summonedFork = projection.ForkExact();
        await FinishTogether(actual, projection, summonedFork);
    }

    [Fact]
    public async Task RubyAxeIdenticalAttackPhasesRequireCompletePublicPath()
    {
        await using var actual = await CombatSession.CreateAsync(new(Seed: "native-ruby-phases-v1", Hp: 200, MaxHp: 200,
            Enemies: ["BruteRubyRaider", "AxeRubyRaider", "AssassinRubyRaider"]));
        foreach (string move in new[] { "SWING_1", "SWING_2", "BIG_SWING", "SWING_1" })
        {
            Assert.Equal(move, actual.State.Enemies[1].Monster!.NextMove!.Id);
            Assert.True(NativeEncounterMemory.Matches(actual.State, actual.Knowledge, "RubyRaiders", actual.Observe().Observation!));
            await actual.StepAsync(actual.Observe().Actions.Single(x => x.Kind == "end_turn"));
        }
    }

    [Theory]
    [InlineData("CorpseSlugsWeak", "AttackPotion")]
    [InlineData("CorpseSlugsWeak", "PowerPotion")]
    [InlineData("TwoTailedRatsNormal", "AttackPotion")]
    [InlineData("TwoTailedRatsNormal", "PowerPotion")]
    [InlineData("RubyRaiders", "AttackPotion")]
    [InlineData("RubyRaiders", "PowerPotion")]
    public async Task NewEncounterEntryGenerationPriorsRemainExplicitlyUnsupported(string encounter, string potion)
    {
        await using var source = await CombatSession.CreateAsync(new(Seed: "native-generation-boundary-v1",
            Encounter: encounter, Potions: [potion]));
        source.Knowledge.Events.Insert(1, new(NativeEntryAssets.EventKind,
            PublicJson.Serialize(NativeEntryAssets.Capture(source.InitialAssets, source.StartHp, source.StartPotions))));
        var boundary = new NaturalSourceBoundary(source.State, source.Room, source.Knowledge, source.StartHp,
            source.StartMaxHp, source.StartGold, source.StartPotions, source.InitialAssets,
            source.Observe().Actions[0].Revision, true, true);
        var root = new NaturalSourceRoot(source.Observe(), "mechanism-fixture", "mechanism-fixture/combat-1", 0, 0, 1,
            "Monster", encounter, "audit-only", source.StartHp, source.StartMaxHp, source.StartGold,
            source.State.Players.Single().Deck.Cards.Select(PublicViews.Card).ToArray(), [], PublicContinuationPolicies.LegacyId);
        Assert.Equal("native_certificate:encounter_generation_potion_closure_unreviewed",
            Assert.Throws<NotSupportedException>(() => NativeBeliefCertificate.Certify(root, boundary)).Message);
    }

    private static async Task Both(CombatSession a, CombatSession b, PublicAction action)
    {
        await a.StepAsync(action); await b.StepAsync(action);
        Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
    }
    private static async Task FinishTogether(params CombatSession[] sessions)
    {
        var policy = new PublicRulePolicy();
        for (int step = 0; step < 500 && sessions[0].Observe().Status != "terminal_settled"; step++)
        {
            var packet = sessions[0].Observe();
            foreach (var s in sessions) Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(s.Observe()));
            var action = policy.Choose(packet);
            foreach (var s in sessions) await s.StepAsync(action);
        }
        Assert.Equal("terminal_settled", sessions[0].Observe().Status);
        foreach (var s in sessions)
        {
            Assert.Equal(PublicJson.Serialize(await sessions[0].SettleAsync()), PublicJson.Serialize(await s.SettleAsync()));
            Assert.Equal(PublicJson.Serialize(sessions[0].FinalAssets), PublicJson.Serialize(s.FinalAssets));
        }
    }
    private static void Reverse(CardPile pile)
    {
        var cards = pile.Cards.Reverse().ToArray();
        foreach (var card in cards) pile.RemoveInternal(card);
        foreach (var card in cards) pile.AddInternal(card);
    }
    private static string Draw(CombatSession s) => PublicJson.Serialize(s.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(PublicViews.Card));
    private static string Deck(CombatSession s) => PublicJson.Serialize(s.State.Players.Single().Deck.Cards.Select(PublicViews.Card));
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new
    {
        run = s.State.RunState.Rng.ToSerializable(), player = s.State.Players.Single().PlayerRng.ToSerializable().Rngs,
        monsters = s.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });
}
