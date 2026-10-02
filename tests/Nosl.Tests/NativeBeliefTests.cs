using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;

namespace Nosl.Tests;

public sealed class NativeBeliefTests
{
    private const string SourceSeed = "nosl-m5-natural-proof-20261001";

    [Fact]
    public async Task ActualNativeCarryInImportsWithoutFreshSetupAndLeavesWholeSourceRunUnchanged()
    {
        var options = new NaturalSourceOptions(Runs: 2, MaxFloors: 8, MaxRoots: 40, MaxRootsPerCombat: 8,
            SeedPrefix: SourceSeed, SourceRunPrefix: "native-bridge-regression");
        var baseline = await NaturalSourceCollector.CollectAsync(options);
        var accepted = new List<string>(); var rejected = new List<string>();
        var labeled = new HashSet<string>();
        var observed = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(options, async (root, boundary) =>
        {
            CombatSession imported;
            try { imported = CombatSession.ImportNative(root, boundary); }
            catch (NotSupportedException e) { rejected.Add(e.Message); return; }
            await using (imported)
            {
                accepted.Add(root.Encounter);
                var sourceOrder = boundary.State.Players[0].PlayerCombatState!.DrawPile.Cards.ToArray();
                string sourceRng = JsonSerializer.Serialize(boundary.State.RunState.Rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });
                Assert.True(imported.HasNativeProvenance);
                Assert.Throws<NotSupportedException>(() => imported.InitialScenario);
                Assert.NotSame(boundary.State, imported.State);
                Assert.NotSame(boundary.Room, imported.Room);
                Assert.Equal(PublicJson.Serialize(root.PublicRoot), PublicJson.Serialize(imported.Observe()));
                Assert.Equal(root.StartHp, imported.StartHp);
                Assert.Equal(root.StartGold, imported.StartGold);
                Assert.Equal(boundary.InitialAssets.Deck, imported.InitialAssets.Deck);
                Assert.True(BeliefSampler.UsesExchangeablePosterior(imported));
                await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleByReplayAsync(imported, 5));
                await using var sample = await BeliefSampler.SampleWorldAsync(imported, 81);
                Assert.Equal(PublicJson.Serialize(root.PublicRoot), PublicJson.Serialize(sample.Observe()));
                if (labeled.Add(root.Encounter))
                {
                    var result = await CombatTeacher.EvaluateAsync(imported, new() { EvaluationSeeds = [101, 102], MaxDecisions = 200 });
                    Assert.Equal(NativeBeliefCertificate.Profile, result.Scope);
                    Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o => Assert.True(o.IsTrueTerminal, o.Detail)));
                    Assert.True(result.Costs.WorldsCompleted > 0);
                    Assert.Equal("MASKED_NO_CERTIFIED_UTILITY_SUPPORT", result.Ranking.Method);
                }
                Assert.Equal(sourceOrder, boundary.State.Players[0].PlayerCombatState!.DrawPile.Cards);
                Assert.Equal(sourceRng, JsonSerializer.Serialize(boundary.State.RunState.Rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true }));
            }
        });
        Assert.Equal(PublicJson.Serialize(baseline), PublicJson.Serialize(observed));
        Assert.Equal(16, accepted.Count);
        Assert.Equal(8, accepted.Count(x => x == "ToadpolesWeak"));
        Assert.Equal(8, accepted.Count(x => x == "SeapunkWeak"));
        Assert.Equal(24, rejected.Count);
    }

    [Fact]
    public async Task SamePublicNativeHistoryErasesHiddenOrderAndFutureStreamsBeforePolicyContinuation()
    {
        await NaturalSourceCollector.CollectWithNativeBoundaryAsync(new(MaxRoots: 1, SeedPrefix: SourceSeed), async (root, boundary) =>
        {
            await using var original = CombatSession.ImportNative(root, boundary);
            await using var perturbed = original.ForkExact();
            var pile = perturbed.State.Players[0].PlayerCombatState!.DrawPile;
            var cards = pile.Cards.Reverse().ToArray();
            foreach (var card in cards) pile.RemoveInternal(card);
            foreach (var card in cards) pile.AddInternal(card);
            perturbed.ReseedFuture(999999);
            // Unobserved native reward pity affects only unclaimed offer identities in
            // this family; it must not affect public strategy or scored terminal assets.
            perturbed.State.Players[0].Odds.LoadFromSerializable(new()
            { CardRarityOddsValue = .75f, PotionRewardOddsValue = .9f });
            Assert.Equal(PublicJson.Serialize(original.Observe()), PublicJson.Serialize(perturbed.Observe()));
            await using var a = await BeliefSampler.SampleWorldAsync(original, 31337);
            await using var b = await BeliefSampler.SampleWorldAsync(perturbed, 31337);
            Assert.Equal(Draw(a), Draw(b));
            Assert.Equal(Streams(a), Streams(b));
            Assert.NotEqual(Streams(original), Streams(a));
            var orders = new HashSet<string>();
            for (ulong seed = 1; seed <= 16; seed++)
            {
                await using var world = await BeliefSampler.SampleWorldAsync(original, seed);
                orders.Add(Draw(world));
            }
            Assert.True(orders.Count > 1);
            var policy = new PublicRulePolicy();
            for (int i = 0; i < 200 && a.Observe().Status != "terminal_settled"; i++)
            {
                var pa = a.Observe(); var pb = b.Observe();
                Assert.Equal(PublicJson.Serialize(pa), PublicJson.Serialize(pb));
                var aa = policy.Choose(pa); var ab = policy.Choose(pb);
                Assert.Equal(PublicJson.Serialize(aa), PublicJson.Serialize(ab));
                await a.StepAsync(aa); await b.StepAsync(ab);
            }
            Assert.Equal("terminal_settled", a.Observe().Status);
            Assert.Equal(PublicJson.Serialize(await a.SettleAsync()), PublicJson.Serialize(await b.SettleAsync()));
        });
    }

    [Fact]
    public async Task CertificateIsFailClosedForEntryAssetsHistoryPrivateRoleAndChoiceReplay()
    {
        await NaturalSourceCollector.CollectWithNativeBoundaryAsync(new(MaxRoots: 1, SeedPrefix: SourceSeed), async (root, boundary) =>
        {
            Assert.Throws<NotSupportedException>(() => CombatSession.ImportNative(root, boundary with { HistoryComplete = false }));
            Assert.Throws<NotSupportedException>(() => CombatSession.ImportNative(root,
                boundary with { InitialAssets = boundary.InitialAssets with { MaxEnergy = 4 } }));
            Assert.Throws<NotSupportedException>(() => CombatSession.ImportNative(root, boundary with { StartPotions = ["PowerPotion", null] }));
            Assert.Throws<NotSupportedException>(() => CombatSession.ImportNative(root with { Encounter = "SludgeSpinnerWeak" }, boundary));
            await using var native = CombatSession.ImportNative(root, boundary);
            await using var wrongRole = native.ForkExact();
            ((Toadpole)wrongRole.State.Enemies[0].Monster!).IsFront = false;
            Assert.False(BeliefSampler.UsesExchangeablePosterior(wrongRole));
            await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(wrongRole, 4));
            var packet = native.Observe();
            var survivor = packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == "Survivor");
            Assert.Equal("card_choice", (await native.StepAsync(survivor)).Status);
            await Assert.ThrowsAsync<NotSupportedException>(() => native.ReplayToChoiceAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(native, 4));
            await native.StepAsync(native.Observe().Actions.First());
            Assert.Equal("player_decision", native.Observe().Status);
        });
    }

    [Fact]
    public async Task PrototypeExportNamesItsNativeProfileAndCannotPromoteFormalTraining()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NativeBeliefPrototype.CollectAsync(new(MaxRoots: 201), new()));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeBeliefPrototype.CollectAsync(new(MaxRoots: 1), new() { FormalLabels = true }));
        var output = await NativeBeliefPrototype.CollectAsync(new(MaxRoots: 1, SeedPrefix: SourceSeed),
            new() { EvaluationSeeds = [101], MaxDecisions = 200 });
        using var json = JsonDocument.Parse(PublicJson.Serialize(output));
        var report = json.RootElement;
        Assert.Equal(1, report.GetProperty("naturalSourceRoots").GetInt32());
        Assert.Equal(1, report.GetProperty("naturalLabeledRoots").GetInt32());
        var record = report.GetProperty("records")[0];
        Assert.DoesNotContain(SourceSeed, record.GetProperty("public_input").GetRawText());
        var audit = record.GetProperty("audit_only");
        Assert.Equal("natural", audit.GetProperty("source_kind").GetString());
        Assert.Equal(NativeBeliefCertificate.Profile, audit.GetProperty("posterior_profile").GetString());
        Assert.True(audit.GetProperty("native_run").GetBoolean());
        Assert.False(audit.GetProperty("formal_labels").GetBoolean());
        Assert.False(audit.GetProperty("trainable").GetBoolean());
        Assert.False(audit.GetProperty("source_seed_conditioning").GetBoolean());
        Assert.Empty(record.GetProperty("targets").GetProperty("pairwise").EnumerateArray());
    }

    private static string Draw(CombatSession s) => string.Join("|", s.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))));
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new
    {
        run = s.State.RunState.Rng.ToSerializable(),
        player = s.State.Players[0].PlayerRng.ToSerializable().Rngs,
        enemies = s.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });
}
