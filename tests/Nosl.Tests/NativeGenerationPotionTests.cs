using System.Reflection;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeGenerationPotionTests
{
    private static NaturalSourceOptions Cohort => new(Runs: 100, MaxFloors: 12, MaxRoots: 200,
        MaxRootsPerCombat: 8, SeedPrefix: "nosl-m5-natural-proof-20261001", SourceRunPrefix: "native-potion-regression");

    [Fact]
    public async Task FixedNativeCohortSettlesEveryNewCandidateAndErasesPrivateCarryInState()
    {
        var baseline = await NaturalSourceCollector.CollectAsync(Cohort);
        int accepted = 0, candidates = 0, worlds = 0, nonempty = 0;
        var families = new HashSet<string>();
        var report = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(Cohort, async (root, boundary) =>
        {
            if (!boundary.StartPotions.Any(NativeGenerationPotionMemory.IsGenerationPotion)) return;
            CombatSession imported;
            try { imported = CombatSession.ImportNative(root, boundary); }
            catch (NotSupportedException) { return; }
            await using (imported)
            {
                accepted++;
                Assert.True(boundary.IsStable);
                Assert.Equal(NativeGenerationPotionMemory.Profile, BeliefSampler.PosteriorProfileFor(imported));
                string source = PublicJson.Serialize(imported.Observe()), streams = Streams(imported);
                var sourceDraw = boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards.ToArray();
                var sourceDeck = boundary.State.Players.Single().Deck.Cards.ToArray();
                var result = await CombatTeacher.EvaluateAsync(imported, new() { EvaluationSeeds = [101, 102], MaxDecisions = 200 });
                Assert.Equal(NativeGenerationPotionMemory.Profile, result.Scope);
                Assert.Equal(root.PublicRoot.Actions.Length, result.Candidates.Length);
                Assert.All(result.Candidates, candidate => Assert.All(candidate.Outcomes, outcome =>
                {
                    Assert.True(outcome.IsTrueTerminal, outcome.Detail);
                    Assert.True(outcome.HpEventDiagnosticsComplete);
                    Assert.True(outcome.ResourceProvenanceComplete);
                }));
                Assert.Equal(result.Costs.WorldsAllocated, result.Costs.WorldsCompleted);
                candidates += result.Candidates.Length; worlds += result.Costs.WorldsCompleted;
                if (families.Add(root.Encounter + ":" + string.Join(",", boundary.StartPotions)))
                {
                    await using var changed = imported.ForkExact();
                    Reverse(changed.State.Players.Single().PlayerCombatState!.DrawPile);
                    Reverse(changed.State.Players.Single().Deck);
                    changed.ReseedFuture(999999);
                    changed.State.Players.Single().Odds.LoadFromSerializable(new()
                        { CardRarityOddsValue = .75f, PotionRewardOddsValue = .9f });
                    Assert.Equal(source, PublicJson.Serialize(changed.Observe()));
                    await using var a = await BeliefSampler.SampleWorldAsync(imported, 81);
                    await using var b = await BeliefSampler.SampleWorldAsync(changed, 81);
                    Assert.Equal(Streams(a), Streams(b));
                    Assert.NotEqual(streams, Streams(a));
                    Assert.Equal(Deck(a), Deck(b)); Assert.Equal(Draw(a), Draw(b));
                    var packet = a.Observe();
                    var potion = packet.Actions.First(x => x.Kind == "potion"
                        && NativeGenerationPotionMemory.IsGenerationPotion(packet.Observation!.Potions[x.Slot]));
                    await Both(a, b, potion);
                    Assert.Equal("card_choice", a.Observe().Status);
                    Assert.False(BeliefSampler.UsesConditionalChoicePosterior(a));
                    Assert.Equal(BeliefSampler.UnsupportedProfile, BeliefSampler.PosteriorProfileFor(a));
                    await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(a, 82));
                    // The frozen teacher policy cancels optional generation. This path
                    // deliberately selects a card and keeps that world's coroutine.
                    var choice = a.Observe().Actions.First(x => x.Selection is { Length: 1 });
                    string selected = a.Observe().Observation!.Choice!.Candidates[choice.Selection![0]].Id;
                    await Both(a, b, choice);
                    Assert.Contains(a.Observe().Observation!.Hand, c => c.Id == selected && c.Cost == 0);
                    Assert.False(BeliefSampler.UsesExchangeablePosterior(a));
                    await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(a, 82));
                    await FinishTogether(a, b); nonempty++;
                }
                Assert.Equal(source, PublicJson.Serialize(imported.Observe()));
                Assert.Equal(streams, Streams(imported));
                Assert.Equal(sourceDraw, boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards);
                Assert.Equal(sourceDeck, boundary.State.Players.Single().Deck.Cards);
            }
        });
        Assert.All(report.Runs, run => Assert.Null(run.Error));
        Assert.Equal(PublicJson.Serialize(baseline), PublicJson.Serialize(report));
        Assert.Equal(24, accepted);
        Assert.Equal(candidates * 2, worlds);
        Assert.True(nonempty >= 3);
    }

    [Theory]
    [InlineData("AttackPotion", "HSP7P798XTVC", false)]
    [InlineData("AttackPotion", "XEB2TX5C4XL6", true)]
    [InlineData("PowerPotion", "HSP7P798XTVC", true)]
    [InlineData("PowerPotion", "XEB2TX5C4XL6", false)]
    public async Task RealPotionChoiceAndCancellationPreserveActualSourceProjectionParity(string potion, string seed, bool cancel)
    {
        await using var source = await CombatSession.CreateAsync(new(Seed: seed,
            Encounter: "ShrinkerBeetleAndFuzzyWurmCrawler", Potions: [potion], Hp: 200, MaxHp: 200));
        Assert.IsType<RunState>(source.State.RunState);
        await using var projection = source.ForkExact();
        Assert.True(projection.State.IsProjection);
        var use = source.Observe().Actions.First(x => x.Kind == "potion");
        int hand = source.Observe().Observation!.Hand.Length;
        await Both(source, projection, use);
        var packet = source.Observe();
        Assert.Equal("card_choice", packet.Status);
        Assert.Equal(potion, packet.Observation!.Choice!.Source);
        Assert.Equal(3, packet.Observation.Choice.Candidates.Length);
        var eligible = potion == "AttackPotion" ? NativeGenerationPotionMemory.Attacks : NativeGenerationPotionMemory.Powers;
        Assert.All(packet.Observation.Choice.Candidates, c => Assert.Contains(c.Id, eligible));
        Assert.Empty(new PublicRulePolicy().Choose(packet).Selection!);
        var selection = packet.Actions.First(x => x.Selection!.Length == (cancel ? 0 : 1));
        await Both(source, projection, selection);
        Assert.Equal(hand + (cancel ? 0 : 1), source.Observe().Observation!.Hand.Length);
        Assert.Contains(source.Observe().Observation!.History, e => e.Kind == "potion_used" && e.Detail == potion);
        await FinishTogether(source, projection);
    }

    public static IEnumerable<object[]> GeneratedCards => NativeGenerationPotionMemory.Attacks
        .Concat(NativeGenerationPotionMemory.Powers).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(GeneratedCards))]
    public async Task EveryPinnedDescendantExecutesThroughSourceProjectionSettlement(string id)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck:
            ["StrikeSilent", "DefendSilent", "Survivor", "Reflex", "Tactician", "Shiv"], Hp: 500, MaxHp: 500, EnemyHp: 300));
        Assert.True(NativeGenerationPotionMemory.PoolsMatch(source.State.Players.Single()));
        Assert.Equal(23, NativeGenerationPotionMemory.Attacks.Length);
        Assert.Equal(16, NativeGenerationPotionMemory.Powers.Length);
        await using var projection = source.ForkExact();
        await Generate(source, id); await Generate(projection, id);
        Same(source, projection);
        await Play(source, projection, id);
        await FinishTogether(source, projection, 1000);
    }

    [Fact]
    public async Task ToolsAndMasterPlannerKeepDelayedNestedSlyChoicesInTheOwnedCoroutine()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck:
            ["Survivor", "Reflex", "Tactician", "StrikeSilent", "DefendSilent", "Shiv"], Hp: 500, MaxHp: 500, EnemyHp: 300));
        await using var projection = source.ForkExact();
        foreach (string id in new[] { "MasterPlanner", "ToolsOfTheTrade" })
        {
            await Generate(source, id); await Generate(projection, id);
            await Play(source, projection, id);
        }
        await Play(source, projection, "Survivor");
        Assert.Contains(source.Observe().Observation!.Discard, c => c.Id == "Survivor" && c.Keywords.Contains("Sly"));
        await Both(source, projection, source.Observe().Actions.Single(x => x.Kind == "end_turn"));
        Assert.Equal("card_choice", source.Observe().Status);
        Assert.Equal("ToolsOfTheTradePower", source.Observe().Observation!.Choice!.Source);
        int survivor = Array.FindIndex(source.Observe().Observation!.Choice!.Candidates, c => c.Id == "Survivor");
        Assert.True(survivor >= 0);
        await Both(source, projection, source.Observe().Actions.Single(x => x.Selection!.SequenceEqual(new[] { survivor })));
        Assert.Equal("card_choice", source.Observe().Status);
        Assert.Equal("Survivor", source.Observe().Observation!.Choice!.Source);
        await ResolveChoices(source, projection);
        await FinishTogether(source, projection, 1000);
    }

    [Theory]
    [InlineData("AttackPotion")]
    [InlineData("PowerPotion")]
    public async Task CertificateChecksActualCharacterAndAscensionAndRejectsUsedOrPendingGeneration(string potion)
    {
        await using var source = await CombatSession.CreateAsync(new(Encounter: "ToadpolesWeak",
            Deck: ["Survivor", "StrikeSilent", "DefendSilent"], Potions: [potion]));
        source.Knowledge.Events.Insert(1, new(NativeEntryAssets.EventKind,
            PublicJson.Serialize(NativeEntryAssets.Capture(source.InitialAssets, source.StartHp, source.StartPotions))));
        NaturalSourceBoundary Boundary() => new(source.State, source.Room, source.Knowledge, source.StartHp,
            source.StartMaxHp, source.StartGold, source.StartPotions, source.InitialAssets,
            source.Observe().Actions[0].Revision, !source.HasPendingChoice, true);
        NaturalSourceRoot Root() => new(source.Observe(), "fixture", "fixture/combat-1", 0, 0, 1,
            "Monster", "ToadpolesWeak", "audit-only", source.StartHp, source.StartMaxHp, source.StartGold,
            source.State.Players.Single().Deck.Cards.Select(PublicViews.Card).ToArray(), [], PublicContinuationPolicies.LegacyId);
        Assert.Equal(NativeGenerationPotionMemory.Profile, NativeBeliefCertificate.Certify(Root(), Boundary()).StableProfile);
        // This increment intentionally has no conditional prior for a generation-
        // potion entry, even when its immediate pending action is a reviewed card.
        await using (var imported = CombatSession.ImportNative(Root(), Boundary()))
        await using (var origin = imported.ForkExact())
        {
            var packet = imported.Observe();
            var action = packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == "Survivor");
            await imported.StepAsync(action);
            Assert.Equal("card_choice", imported.Observe().Status);
            Assert.Equal(BeliefSampler.UnsupportedProfile, BeliefSampler.PosteriorProfileFor(imported));
            await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(imported, 82));
            var pendingRoot = Root() with { PublicRoot = imported.Observe() };
            var pendingBoundary = Boundary() with { IsStable = false, ChoiceReplay = new(origin,
                [action], [PublicJson.Serialize(packet), PublicJson.Serialize(imported.Observe())]) };
            Assert.Equal("native_choice:reviewed_stable_origin_required", (await Assert.ThrowsAsync<NotSupportedException>(
                () => CombatSession.ImportNativeAsync(pendingRoot, pendingBoundary))).Message);
        }
        var player = source.State.Players.Single();
        var character = typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        character.SetValue(player, ModelDb.Character<Ironclad>());
        Assert.Equal("native_certificate:actual_silent_a10_required", Assert.Throws<NotSupportedException>(
            () => NativeBeliefCertificate.Certify(Root(), Boundary())).Message);
        character.SetValue(player, ModelDb.Character<Silent>());
        var level = typeof(AscensionManager).GetField("_level", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (int invalid in new[] { 9, 11 })
        {
            level.SetValue(source.State.RunState.Ascension, invalid);
            Assert.Equal(10, source.Observe().Observation!.Ascension);
            Assert.Equal("native_certificate:actual_silent_a10_required", Assert.Throws<NotSupportedException>(
                () => NativeBeliefCertificate.Certify(Root(), Boundary())).Message);
        }
        level.SetValue(source.State.RunState.Ascension, 10);
        await source.StepAsync(source.Observe().Actions.First(x => x.Kind == "potion"));
        await Assert.ThrowsAsync<NotSupportedException>(() => CombatSession.ImportNativeAsync(Root(), Boundary()));
        // Cancellation creates no card, so this isolates the consumed-history guard
        // from the unchanged card and power allowlists.
        await source.StepAsync(source.Observe().Actions.Single(x => x.Selection is { Length: 0 }));
        Assert.Equal("native_certificate:generation_potion_already_used", Assert.Throws<NotSupportedException>(
            () => NativeBeliefCertificate.Certify(Root(), Boundary())).Message);
    }

    private static async Task Generate(CombatSession session, string id)
    {
        var player = session.State.Players.Single();
        var card = (CardModel)ModelDb.All<CardModel>().Single(c => c.GetType().Name == id).MutableClone();
        card.AssignOwner(player); card.MakeTemporaryFreeThisTurn();
        await CardPileCmd.Generate(session.State, card, PileType.Hand, player);
    }
    private static async Task Play(CombatSession a, CombatSession b, string id)
    {
        var packet = a.Observe();
        await Both(a, b, packet.Actions.First(x => x.Kind == "play" && packet.Observation!.Hand[x.Slot].Id == id));
        await ResolveChoices(a, b);
    }
    private static async Task ResolveChoices(CombatSession a, CombatSession b)
    {
        for (int i = 0; a.HasPendingChoice && i < 20; i++)
            await Both(a, b, a.Observe().Actions.First(x => x.Selection is { Length: > 0 }));
        Assert.False(a.HasPendingChoice);
    }
    private static void Same(CombatSession a, CombatSession b) => Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
    private static async Task Both(CombatSession a, CombatSession b, PublicAction action)
    {
        Same(a, b); await a.StepAsync(action); await b.StepAsync(action); Same(a, b);
    }
    private static async Task FinishTogether(CombatSession a, CombatSession b, int limit = 300)
    {
        var policy = new PublicRulePolicy();
        for (int i = 0; i < limit && a.Observe().Status != "terminal_settled"; i++)
        {
            var packet = a.Observe();
            var action = packet.Status == "card_choice" ? packet.Actions.First(x => x.Selection is { Length: > 0 }) : policy.Choose(packet);
            await Both(a, b, action);
        }
        Assert.Equal("terminal_settled", a.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await a.SettleAsync()), PublicJson.Serialize(await b.SettleAsync()));
        Assert.Equal(PublicJson.Serialize(a.FinalAssets), PublicJson.Serialize(b.FinalAssets));
        Assert.True(a.Knowledge.OutcomeLedger.HpComplete);
    }
    private static void Reverse(CardPile pile)
    {
        var cards = pile.Cards.Reverse().ToArray();
        foreach (var card in cards) pile.RemoveInternal(card);
        foreach (var card in cards) pile.AddInternal(card);
    }
    private static string Deck(CombatSession s) => PublicJson.Serialize(s.State.Players.Single().Deck.Cards.Select(PublicViews.Card));
    private static string Draw(CombatSession s) => PublicJson.Serialize(s.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(PublicViews.Card));
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new
    {
        run = s.State.RunState.Rng.ToSerializable(), player = s.State.Players.Single().PlayerRng.ToSerializable().Rngs,
        enemies = s.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });
}
