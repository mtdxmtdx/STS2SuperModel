using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Tests;

public sealed class EnvironmentCoverageTests
{
    public static IEnumerable<object[]> Potions => EnvironmentCoverage.PotionIds.Select(id => new object[] { id });
    public static IEnumerable<object[]> Relics => EnvironmentCoverage.RelicIds.Select(id => new object[] { id });

    private static PublicAction Play(CombatSession session, string id) => session.Observe().Actions.First(
        action => action.Kind == "play" && session.Observe().Observation!.Hand[action.Slot].Id == id);
    private static PublicAction End(CombatSession session) => session.Observe().Actions.Single(action => action.Kind == "end_turn");
    private static async Task ResolveChoices(CombatSession session)
    {
        for (int count = 0; session.HasPendingChoice; count++)
        {
            Assert.True(count < 64, "A potion or relic operation exceeded 64 explicit subchoices");
            var packet = session.Observe();
            await session.StepAsync(packet.Actions.Last());
        }
    }
    private static void ReplaceHiddenWorld(CombatSession session)
    {
        var pile = session.State.Players[0].PlayerCombatState!.DrawPile;
        var reversed = pile.Cards.Reverse().ToArray();
        foreach (var card in pile.Cards.ToArray()) pile.RemoveInternal(card);
        foreach (var card in reversed) pile.AddInternal(card);
        session.ReseedFuture(991827);
    }

    [Fact]
    public void CatalogIncludesEveryPinnedPotionAndRelic_RegistrationDoesNotClaimVerification()
    {
        Assert.Equal(ContentRegistry.AllTypes.Where(t => typeof(PotionModel).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name).Order(StringComparer.Ordinal), EnvironmentCoverage.PotionIds);
        Assert.Equal(ContentRegistry.AllTypes.Where(t => typeof(RelicModel).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name).Order(StringComparer.Ordinal), EnvironmentCoverage.RelicIds);
        Assert.All(EnvironmentCoverage.Entries, item =>
        {
            Assert.Equal("ENGINE_CATALOG", item.Status);
            Assert.Equal("PINNED_UPSTREAM_TRUSTED", item.RuleAuthority);
            Assert.NotEmpty(item.AdapterRequirements);
            Assert.DoesNotContain("Verified", item.Status, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Throws<ArgumentException>(() => EnvironmentCoverage.AuditPotion("UnregisteredPotion"));
        Assert.Throws<ArgumentException>(() => EnvironmentCoverage.AuditRelic("UnregisteredRelic"));
    }

    [Theory]
    [MemberData(nameof(Relics))]
    public async Task EveryRelicPublicCounterProjectionHasValidExplicitFields(string id)
    {
        await using var session = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent"]));
        var relic = (RelicModel)ModelDb.All<RelicModel>().Single(model => model.GetType().Name == id).MutableClone();
        relic.AssignOwner(session.State.Players[0]);
        var details = PublicRelicDetails.Details(relic);
        Assert.Equal(1, details["stackCount"]);
        Assert.Equal(0, details["isWax"]);
        string json = PublicJson.Serialize(details);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rng", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        _ = PublicRelicDetails.Cards(relic);
        _ = PublicRelicDetails.SelectedModel(relic);
    }

    [Theory]
    [MemberData(nameof(Potions))]
    public async Task EveryPotionExecutesThroughRealAdapterAndExactClone(string id)
    {
        await using var source = await CombatSession.CreateAsync(new(Seed: "ENVIRONMENT-POTION-" + id,
            Deck: ["StrikeSilent", "DefendSilent", "Neutralize", "Survivor", "StrikeSilent", "DefendSilent", "StrikeSilent", "DefendSilent", "StrikeSilent", "DefendSilent"],
            Potions: [id], Hp: 40, EnemyHp: 200));
        await using var branch = source.ForkExact();
        string before = PublicJson.Serialize(source.Observe());
        if (id == "FairyInABottle")
        {
            Assert.DoesNotContain(source.Observe().Actions, action => action.Kind == "potion");
            return;
        }
        var action = source.Observe().Actions.Single(action => action.Kind == "potion");
        await branch.StepAsync(action);
        await ResolveChoices(branch);
        Assert.Equal(before, PublicJson.Serialize(source.Observe()));
        await source.StepAsync(action);
        await ResolveChoices(source);
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
        Assert.DoesNotContain(source.State.Players[0].PotionSlots, potion => potion?.GetType().Name == id);
        if (source.Observe().Status == "player_decision")
        {
            await using var after = source.ForkExact();
            await source.StepAsync(End(source));
            await after.StepAsync(End(after));
            await ResolveChoices(source); await ResolveChoices(after);
            Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(after.Observe()));
        }
    }

    [Theory]
    [InlineData("SwiftPotion")]
    [InlineData("BottledPotential")]
    [InlineData("DistilledChaos")]
    [InlineData("CureAll")]
    [InlineData("SneckoOil")]
    [InlineData("CunningPotion")]
    [InlineData("EntropicBrew")]
    [InlineData("AttackPotion")]
    public async Task PotionReplaySamplingIsIndependentOfTrueSetupSeed(string id)
    {
        // A homogeneous short deck makes the conditional initial public event non-rare.
        // This is an isolation unit test, not a throughput claim for full natural decks.
        await using var source = await CombatSession.CreateAsync(new(Deck:["StrikeSilent","StrikeSilent","StrikeSilent"], Potions: [id], EnemyHp: 200));
        await using var substituted = await CombatSession.CreateAsync(new(Seed:"DIFFERENT-PRIVATE-FUTURE",Deck:["StrikeSilent","StrikeSilent","StrikeSilent"],Potions:[id],EnemyHp:200));
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(substituted.Observe()));
        await using var a = BeliefSampler.SampleWorld(source, 873);
        await using var b = BeliefSampler.SampleWorld(substituted, 873);
        var action = a.Observe().Actions.Single(candidate => candidate.Kind == "potion");
        await a.StepAsync(action); await b.StepAsync(action);
        Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
        await ResolveChoices(a); await ResolveChoices(b);
        Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
    }

    [Theory]
    [InlineData("PenNib", "_attacksPlayed", "attacksPlayed")]
    [InlineData("HappyFlower", "_turnCounter", "turnCounter")]
    [InlineData("Nunchaku", "_attacksPlayed", "attacksPlayed")]
    public async Task InheritedPublicCounterChangesObservationAndSurvivesBranch(string id, string field, string key)
    {
        await using var source = await CombatSession.CreateAsync(new(Relics: [id], EnemyHp: 200));
        await using var branch = source.ForkExact();
        var relic = branch.State.Players[0].Relics.Single(item => item.GetType().Name == id);
        var target = relic.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
        int before = (int)target.GetValue(relic)!;
        target.SetValue(relic, before + 1); // Construct a distinct, publicly displayed prior-combat counter.
        Assert.Equal(before + 1, PublicRelicDetails.Details(relic)[key]);
        Assert.NotEqual(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
        await using var copied = branch.ForkExact();
        Assert.Equal(PublicJson.Serialize(branch.Observe()), PublicJson.Serialize(copied.Observe()));
    }

    [Theory]
    [InlineData("FirePotion", 20)]
    [InlineData("ExplosiveAmpoule", 10)]
    [InlineData("PotionShapedRock", 15)]
    public async Task DirectDamagePotionSemanticSmoke(string id, int damage)
    {
        await using var session = await CombatSession.CreateAsync(new(Potions: [id], EnemyHp: 100));
        var packet = await session.StepAsync(session.Observe().Actions.Single(action => action.Kind == "potion"));
        Assert.Equal(100 - damage, packet.Observation!.Enemies.Single().Hp);
    }

    [Fact]
    public async Task FairyPreventsDeathAutomaticallyInCloneWithoutConsumingSourceBottle()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent"], Potions: ["FairyInABottle"], Hp: 1, EnemyHp: 100));
        await using var branch = source.ForkExact();
        await branch.StepAsync(End(branch));
        Assert.Equal(21, branch.Observe().Observation!.Hp);
        Assert.All(branch.State.Players[0].PotionSlots, Assert.Null);
        Assert.Contains(source.State.Players[0].PotionSlots, potion => potion?.GetType().Name == "FairyInABottle");
        Assert.Equal(1, source.Observe().Observation!.Hp);
    }

    [Fact]
    public async Task ThreeAttackRelicsAndPenNibUsePublicCountsAcrossClones()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["StrikeSilent", "StrikeSilent", "StrikeSilent"],
            Relics: ["Kunai", "Shuriken", "OrnamentalFan", "PenNib"], EnemyHp: 100));
        await source.StepAsync(Play(source, "StrikeSilent"));
        await source.StepAsync(Play(source, "StrikeSilent"));
        await using var branch = source.ForkExact();
        await source.StepAsync(Play(source, "StrikeSilent"));
        await branch.StepAsync(Play(branch, "StrikeSilent"));
        var observed = source.Observe().Observation!;
        Assert.Contains(observed.Powers, power => power.Id == "StrengthPower" && power.Amount == 1);
        Assert.Contains(observed.Powers, power => power.Id == "DexterityPower" && power.Amount == 1);
        Assert.Equal(4, observed.Block);
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(branch.Observe()));
    }
}
