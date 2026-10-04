using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.Models.Powers;

namespace Nosl.Tests;

public sealed class CardCoverageTests
{
    public static IEnumerable<object[]> AuditedUpgrades => CardCoverage.SilentCards.Concat(new[] { "AscendersBane", "ThinkingAhead", "Slimed", "Shiv", "Soul", "Burn", "Dazed", "Wound", "Decay", "Doubt", "Regret", "Wither", "GeneticAlgorithm", "Rampage" }).Distinct()
        .SelectMany(id => CardCoverage.Get(id)!.MaxUpgradeLevel > 0
            ? new[] { new object[] { id, false }, new object[] { id, true } }
            : new[] { new object[] { id, false } });

    private static PublicAction Play(CombatSession session, string id) => session.Observe().Actions
        .First(a => a.Kind == "play" && session.Observe().Observation!.Hand[a.Slot].Id == id);
    private static PublicAction End(CombatSession session) => session.Observe().Actions.Single(a => a.Kind == "end_turn");
    private static string Json(CombatSession session) => PublicJson.Serialize(session.Observe());
    private static async Task ResolveChoices(CombatSession session)
    {
        int choices = 0;
        while (session.HasPendingChoice)
        {
            Assert.True(++choices <= 30, "Choice chain exceeded this finite fixture");
            var packet = session.Observe();
            Assert.NotEmpty(packet.Actions);
            await session.StepAsync(packet.Actions[0]);
        }
    }
    private static async Task<CombatSession> Fixture(string id, bool upgraded = false, params string[] companions)
    {
        // Deliberately constructed mechanism fixture, not a claimed natural encounter distribution.
        string[] fillers = companions.Length == 0
            ? ["StrikeSilent", "DefendSilent", "Neutralize", "Slice", "Shiv", "Prepared", "Backflip", "Deflect", "StrikeSilent", "DefendSilent"]
            : companions;
        var session = await CombatSession.CreateAsync(new(Deck: [id + (upgraded ? "+" : ""), .. fillers], Hp: 500, MaxHp: 500, EnemyHp: 1000));
        var player = session.State.Players[0];
        var card = player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards).First(c => c.GetType().Name == id);
        CardPileCmd.Add(card, PileType.Hand);
        player.PlayerCombatState.Energy = 20;
        if (id == "GrandFinale")
            foreach (var hidden in player.PlayerCombatState.DrawPile.Cards.ToArray()) CardPileCmd.Add(hidden, PileType.Discard);
        return session;
    }

    [Fact]
    public async Task EveryOfficialSilentCardHasExplicitAuditDisposition()
    {
        await using var session = await CombatSession.CreateAsync();
        string[] pool = ModelDb.Character<Silent>().CardPool.AllCards.Select(c => c.GetType().Name).ToArray();
        Assert.Equal(91, pool.Length);
        Assert.All(pool, id => Assert.NotNull(CardCoverage.Get(id)));
        Assert.Equal(CardCoverage.Entries.Count, CardCoverage.Entries.Select(e => e.Id).Distinct().Count());
        Assert.All(CardCoverage.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
    }

    [Theory]
    [MemberData(nameof(AuditedUpgrades))]
    public async Task BaseAndUpgradeExecuteAcrossIndependentClone(string id, bool upgraded)
    {
        // At most six cards, so Ring of the Snake makes the requested card naturally
        // available. Native replay remains valid even for reward-bearing cards.
        await using var source = await CombatSession.CreateAsync(new(
            Deck: [id + (upgraded ? "+" : ""), "StrikeSilent", "DefendSilent", "Slice", "Prepared", "Shiv"],
            Hp: 500, MaxHp: 500, EnemyHp: 1000));
        string before = Json(source);
        await using var branch = id == "TheHunt" ? await source.ForkForContinuationAsync() : source.ForkExact();
        Assert.Equal(before, Json(branch));
        var actions = branch.Observe().Actions.Where(a => a.Kind == "play" && branch.Observe().Observation!.Hand[a.Slot].Id == id).ToArray();
        if (actions.Length == 0)
        {
            Assert.Contains("Unplayable", source.Observe().Observation!.Hand.First(c => c.Id == id).Keywords);
        }
        else
        {
            await branch.StepAsync(actions[0]);
            await ResolveChoices(branch);
            Assert.Equal(before, Json(source));
            await source.StepAsync(Play(source, id));
            await ResolveChoices(source);
            Assert.Equal(Json(source), Json(branch));
        }
        // Exercise delayed powers, cleanup, generated cards, retain, and start-of-turn choices.
        await branch.StepAsync(End(branch));
        await ResolveChoices(branch);
        await source.StepAsync(End(source));
        await ResolveChoices(source);
        Assert.Equal(Json(source), Json(branch));
    }

    [Theory]
    [InlineData("Dash", 10, 13, 10, 13)]
    [InlineData("Slice", 6, 9, 0, 0)]
    [InlineData("DaggerSpray", 8, 12, 0, 0)]
    [InlineData("Shiv", 4, 6, 0, 0)]
    [InlineData("PoisonedStab", 6, 8, 0, 0)]
    [InlineData("Deflect", 0, 0, 4, 7)]
    public async Task NumericDamageAndBlockFollowPinnedCardRules(string id, int damage, int damagePlus, int block, int blockPlus)
    {
        for (int upgrade = 0; upgrade < 2; upgrade++)
        {
            await using var session = await Fixture(id, upgrade == 1);
            var after = (await session.StepAsync(Play(session, id))).Observation!;
            Assert.Equal(1000 - (upgrade == 0 ? damage : damagePlus), after.Enemies[0].Hp);
            Assert.Equal((decimal)(upgrade == 0 ? block : blockPlus), after.Block);
            if (id == "PoisonedStab") Assert.Equal(upgrade == 0 ? 3 : 4, after.Enemies[0].Powers.Single(p => p.Id == "PoisonPower").Amount);
            if (id == "Shiv") Assert.Contains(after.Exhaust, c => c.Id == "Shiv");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedGeneratedShivsAndFootworkAffectRealBlockAndExhaust(bool upgraded)
    {
        await using var session = await Fixture("CloakAndDagger", upgraded, "Footwork" + (upgraded ? "+" : ""), "BladeDance", "DefendSilent");
        await session.StepAsync(Play(session, "Footwork"));
        var after = (await session.StepAsync(Play(session, "CloakAndDagger"))).Observation!;
        Assert.Equal(upgraded ? 9m : 8m, after.Block);
        Assert.Equal(upgraded ? 2 : 1, after.Hand.Count(c => c.Id == "Shiv"));
        Assert.All(after.Hand.Where(c => c.Id == "Shiv"), c => Assert.Equal(0, c.Upgrade));
        int hp = after.Enemies[0].Hp;
        after = (await session.StepAsync(Play(session, "Shiv"))).Observation!;
        Assert.Equal(hp - 4, after.Enemies[0].Hp);
        Assert.Single(after.Exhaust, c => c.Id == "Shiv");
        after = (await session.StepAsync(Play(session, "BladeDance"))).Observation!;
        Assert.Contains(after.Exhaust, c => c.Id == "BladeDance");
        Assert.Equal((upgraded ? 2 : 1) - 1 + 3, after.Hand.Count(c => c.Id == "Shiv"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaggerThrowChoiceUsesPublicCandidatesAndRejectsInvalidSelection(bool upgraded)
    {
        await using var session = await Fixture("DaggerThrow", upgraded);
        var packet = await session.StepAsync(Play(session, "DaggerThrow"));
        Assert.Equal("card_choice", packet.Status);
        Assert.Equal(1000 - (upgraded ? 12 : 9), packet.Observation!.Enemies[0].Hp);
        Assert.Equal(1, packet.Observation.Choice!.Min);
        await Assert.ThrowsAsync<ArgumentException>(() => session.StepAsync(packet.Actions[0] with { Selection = [99] }));
        await session.StepAsync(packet.Actions[0]);
        Assert.False(session.HasPendingChoice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerTimingAndPoisonSurviveCloneAndEndTurn(bool upgraded)
    {
        await using var session = await Fixture("DodgeAndRoll", upgraded, "Footwork", "Blur", "NoxiousFumes", "DefendSilent");
        await session.StepAsync(Play(session, "Footwork"));
        var after = (await session.StepAsync(Play(session, "DodgeAndRoll"))).Observation!;
        Assert.Equal(upgraded ? 8m : 6m, after.Block);
        Assert.Equal(after.Block, after.Powers.Single(p => p.Id == "BlockNextTurnPower").Amount);
        await session.StepAsync(Play(session, "Blur"));
        await session.StepAsync(Play(session, "NoxiousFumes"));
        await using var branch = session.ForkExact();
        after = (await branch.StepAsync(End(branch))).Observation!;
        Assert.DoesNotContain(after.Powers, p => p.Id is "BlockNextTurnPower" or "BlurPower");
        // Start of next turn: retained block after the 5-damage enemy move, plus promised block.
        Assert.Equal(upgraded ? 18m : 14m, after.Block);
        Assert.Equal(2, after.Enemies[0].Powers.Single(p => p.Id == "PoisonPower").Amount);
        await session.StepAsync(End(session));
        Assert.Equal(Json(session), Json(branch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BladeOfInkExposesAndClonesGeneratedEnchantments(bool upgraded)
    {
        await using var source = await Fixture("BladeOfInk", upgraded, "DefendSilent");
        var packet = await source.StepAsync(Play(source, "BladeOfInk"));
        var shivs = packet.Observation!.Hand.Where(c => c.Id == "Shiv").ToArray();
        Assert.Equal(upgraded ? 3 : 2, shivs.Length);
        Assert.All(shivs, card => Assert.Equal(new PublicCardEffect("Inky", 1), Assert.Single(card.Enchantments!)));
        string before = Json(source);
        await using var branch = source.ForkExact();
        var after = (await branch.StepAsync(Play(branch, "Shiv"))).Observation!;
        Assert.Equal(1, after.Enemies[0].Powers.Single(p => p.Id == "WeakPower").Amount);
        Assert.Equal(before, Json(source));
        await source.StepAsync(Play(source, "Shiv"));
        Assert.Equal(Json(source), Json(branch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlyDiscardAutoPlaysWithoutSpendingItsPrintedCost(bool upgraded)
    {
        await using var session = await Fixture("Survivor", false, "Tactician" + (upgraded ? "+" : ""), "StrikeSilent");
        session.State.Players[0].PlayerCombatState!.Energy = 1;
        var packet = await session.StepAsync(Play(session, "Survivor"));
        int index = Array.FindIndex(packet.Observation!.Choice!.Candidates, c => c.Id == "Tactician");
        Assert.True(index >= 0);
        var after = (await session.StepAsync(packet.Actions.Single(a => a.Selection!.SequenceEqual(new[] { index })))).Observation!;
        Assert.Equal(upgraded ? 2 : 1, after.Energy);
        Assert.Equal(1, after.Counters!.CardsDiscarded);
        Assert.Contains(after.Discard, c => c.Id == "Tactician");
        Assert.Contains(after.History, e => e.Kind == "card_played" && e.Detail.Contains("Tactician"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XEnergyUsesTheActualResourceSnapshot(bool upgraded)
    {
        await using var session = await Fixture("Skewer", upgraded, "Malaise" + (upgraded ? "+" : ""), "DefendSilent");
        session.State.Players[0].PlayerCombatState!.Energy = 3;
        var before = session.Observe().Observation!;
        Assert.True(before.Hand.Single(c => c.Id == "Skewer").Details!.CostsXEnergy);
        var after = (await session.StepAsync(Play(session, "Skewer"))).Observation!;
        Assert.Equal(0, after.Energy);
        Assert.Equal(1000 - 3 * (upgraded ? 11 : 8), after.Enemies[0].Hp);
        session.State.Players[0].PlayerCombatState!.Energy = 2;
        after = (await session.StepAsync(Play(session, "Malaise"))).Observation!;
        Assert.Equal(0, after.Energy);
        Assert.Equal(-(upgraded ? 3 : 2), after.Enemies[0].Powers.Single(p => p.Id == "StrengthPower").Amount);
        Assert.Equal(upgraded ? 3 : 2, after.Enemies[0].Powers.Single(p => p.Id == "WeakPower").Amount);
    }

    [Fact]
    public async Task GrandFinaleUsesEngineNonCostLegality()
    {
        await using var session = await Fixture("GrandFinale");
        var player = session.State.Players[0];
        var visible = player.PlayerCombatState!.Hand.Cards.First(c => c is not GrandFinale);
        CardPileCmd.Add(visible, PileType.Draw, CardPilePosition.Top);
        var packet = session.Observe();
        Assert.DoesNotContain(packet.Actions, a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == "GrandFinale");
        await CardPileCmd.Draw(session.State, 1, player, false);
        Assert.Equal(0, session.Observe().Observation!.DrawCount);
        Assert.NotNull(Play(session, "GrandFinale"));
    }

    [Fact]
    public async Task NightmareSelectedUpgradeIsPublicAndSurvivesDelayedClone()
    {
        await using var session = await Fixture("Nightmare", false, "Shiv+", "DefendSilent");
        var packet = await session.StepAsync(Play(session, "Nightmare"));
        int index = Array.FindIndex(packet.Observation!.Choice!.Candidates, c => c.Id == "Shiv");
        await session.StepAsync(packet.Actions.Single(a => a.Selection!.SequenceEqual(new[] { index })));
        var power = session.Observe().Observation!.Powers.Single(p => p.Id == "NightmarePower");
        Assert.Equal("Shiv", power.SelectedCard);
        Assert.Equal(1, power.SelectedUpgrade);
        await using var branch = session.ForkExact();
        var after = (await branch.StepAsync(End(branch))).Observation!;
        Assert.True(after.Hand.Count(c => c.Id == "Shiv" && c.Upgrade == 1) >= 3);
        Assert.DoesNotContain(after.Powers, p => p.Id == "NightmarePower");
        await session.StepAsync(End(session));
        Assert.Equal(Json(session), Json(branch));
    }

    [Fact]
    public async Task BurstProducesTwoRealSequentialChoicesAndIndependentReplay()
    {
        // All cards start in hand: replay has exactly the same natural starting state.
        await using var session = await CombatSession.CreateAsync(new(Deck: ["Burst", "Survivor", "StrikeSilent", "DefendSilent", "Shiv"], EnemyHp: 100));
        await session.StepAsync(Play(session, "Burst"));
        var packet = await session.StepAsync(Play(session, "Survivor"));
        Assert.True(session.HasPendingChoice);
        await using var replay = await session.ReplayToChoiceAsync();
        Assert.Equal(Json(session), Json(replay));
        packet = await session.StepAsync(packet.Actions[0]);
        Assert.True(session.HasPendingChoice);
        Assert.Equal(16m, packet.Observation!.Block);
        await session.StepAsync(packet.Actions[0]);
        Assert.False(session.HasPendingChoice);
        Assert.Equal(2, session.Observe().Observation!.Counters!.CardsDiscarded);
        Assert.True(replay.HasPendingChoice);
    }

    [Fact]
    public async Task TemporaryKeywordsAndOrderedCostChangesAreObservable()
    {
        await using var source = await Fixture("BulletTime", false, "UpMySleeve", "HandTrick", "DefendSilent");
        var player = source.State.Players[0];
        var defend = player.PlayerCombatState!.Hand.Cards.Single(c => c is DefendSilent);
        CardCmd.ApplySingleTurnRetain(defend);
        CardCmd.ApplySingleTurnSly(defend);
        Assert.True(source.Observe().Observation!.Hand.Single(c => c.Id == "DefendSilent").Details!.RetainThisTurn);
        Assert.True(source.Observe().Observation!.Hand.Single(c => c.Id == "DefendSilent").Details!.SlyThisTurn);
        await source.StepAsync(Play(source, "BulletTime"));
        var sleeve = player.PlayerCombatState.Hand.Cards.Single(c => c is UpMySleeve);
        sleeve.AddEnergyCostThisCombat(-1);
        var details = source.Observe().Observation!.Hand.Single(c => c.Id == "UpMySleeve").Details!;
        Assert.Equal(new[] { "FreeThisTurn", "AddThisCombat" }, details.EnergyModifiers.Select(m => m.Kind));
        await using var clone = source.ForkExact();
        Assert.Equal(Json(source), Json(clone));
    }

    [Fact]
    public async Task MutableDamageAndAttachmentStateSeparateOtherwiseIdenticalCards()
    {
        await using var source = await Fixture("Rampage", false, "Rampage", "DefendSilent");
        var cards = source.State.Players[0].PlayerCombatState!.Hand.Cards.OfType<Rampage>().ToArray();
        Assert.Equal(2, cards.Length);
        await source.StepAsync(Play(source, "Rampage"));
        var played = source.State.Players[0].PlayerCombatState!.DiscardPile.Cards.OfType<Rampage>().Single();
        var untouched = source.State.Players[0].PlayerCombatState!.Hand.Cards.OfType<Rampage>().Single();
        Assert.Equal("15", PublicViews.Card(played).PublicState!["damage"]);
        Assert.Equal("10", PublicViews.Card(untouched).PublicState!["damage"]);
        await CardCmd.Enchant<Momentum>(untouched, 2);
        Assert.Equal("0", PublicViews.Card(untouched).PublicState!["enchantment.0.extraDamage"]);
        await source.StepAsync(Play(source, "Rampage"));
        Assert.Equal("2", PublicViews.Card(untouched).PublicState!["enchantment.0.extraDamage"]);
    }

    [Fact]
    public async Task AbundanceUnchosenDiagnosticCandidatesNeverEnterPublicSignature()
    {
        await using var source = await Fixture("Abundance", false, "DefendSilent");
        var card = source.State.Players[0].PlayerCombatState!.Hand.Cards.OfType<Abundance>().Single();
        await source.StepAsync(Play(source, "Abundance"));
        Assert.NotEmpty(card.GeneratedCandidates);
        string before = Json(source);
        Assert.DoesNotContain("generatedCandidates", before, StringComparison.OrdinalIgnoreCase);
        // Replace diagnostics that upstream never presented as a public choice. They must
        // not affect the observed card signature or the unknown-draw multiset.
        Assert.IsType<List<CardModel>>(card.GeneratedCandidates).Clear();
        Assert.Equal(before, Json(source));
    }

    [Fact]
    public async Task NativeReplayPosteriorForEvolvingDamageIgnoresSourceFuture()
    {
        // Homogeneous initial cards make this native replay conditioning event common;
        // no fixture-only energy, pile edits, or invented public history is used.
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Rampage", "Rampage", "Rampage"], EnemyHp: 100));
        await source.StepAsync(Play(source, "Rampage"));
        await using var replaced = await CombatSession.CreateAsync(new(Seed: "REPLACED-TRUE-FUTURE", Deck: ["Rampage", "Rampage", "Rampage"], EnemyHp: 100));
        await replaced.StepAsync(Play(replaced, "Rampage"));
        Assert.Equal(Json(source), Json(replaced));
        await using var a = await BeliefSampler.SampleByReplayAsync(source, 27);
        await using var b = await BeliefSampler.SampleByReplayAsync(replaced, 27);
        Assert.Equal(Json(a), Json(b));
        await a.StepAsync(End(a));
        await b.StepAsync(End(b));
        Assert.Equal(Json(a), Json(b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheHuntFatalUsesConcreteReplayAndPreservesEarnedRewards(bool upgraded)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["TheHunt" + (upgraded ? "+" : "")], EnemyHp: 5));
        Assert.Throws<NotSupportedException>(() => source.ForkExact());
        await using var branch = await source.ForkForContinuationAsync();
        await branch.StepAsync(Play(branch, "TheHunt"));
        Assert.True(source.Room.Engine.IsInProgress);
        Assert.Empty(source.Room.GeneratedRewards);
        await source.StepAsync(Play(source, "TheHunt"));
        Assert.Equal(PublicJson.Serialize(await source.SettleAsync()), PublicJson.Serialize(await branch.SettleAsync()));
        Assert.Equal(source.Room.GeneratedRewards.Count, branch.Room.GeneratedRewards.Count);
        var sourceRewards=Assert.Single(source.Room.GeneratedRewards);
        var branchRewards=Assert.Single(branch.Room.GeneratedRewards);
        Assert.IsType<Sts2Sim.Core.Rewards.CardReward>(Assert.Single(sourceRewards.ExtraRewards));
        Assert.IsType<Sts2Sim.Core.Rewards.CardReward>(Assert.Single(branchRewards.ExtraRewards));
    }
}
