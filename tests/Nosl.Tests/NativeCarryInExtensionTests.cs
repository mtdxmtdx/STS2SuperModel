using System.Text.Json;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Models.Powers;

namespace Nosl.Tests;

public sealed class NativeCarryInExtensionTests
{
    [Fact]
    public async Task CapturedCohortUsesPublicMemoryAndReplacesHiddenDecksAndStreamsThroughRealSettlement()
    {
        var options = new NaturalSourceOptions(Runs: 100, MaxFloors: 12, MaxRoots: 200,
            MaxRootsPerCombat: 8, SeedPrefix: "nosl-m5-natural-proof-20261001", SourceRunPrefix: "native-v2-regression");
        int accepted = 0, nativeFishingSettlements = 0;
        var families = new HashSet<string>(); var encounters = new HashSet<string>();
        var exactFishing = new Dictionary<string, (int Hp, string Assets)>();
        var randomNextMoves = new HashSet<string>();
        bool shellResetVerified = false, shrinkBindingVerified = false;
        var report = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(options, async (root, boundary) =>
        {
            CombatSession imported;
            try { imported = CombatSession.ImportNative(root, boundary); }
            catch (NotSupportedException) { return; }
            await using (imported)
            {
                // Preserve the frozen v2 family regression; the potion increment has its own cohort proof.
                if (imported.NativeCertificate!.HasGenerationPotionPrior || imported.NativeCertificate.HasEncounterExtension) return;
                accepted++; encounters.Add(root.Encounter);
                var entry = root.PublicRoot.Observation!.History[1];
                Assert.Equal(NativeEntryAssets.EventKind, entry.Kind);
                Assert.Equal(PublicJson.Serialize(NativeEntryAssets.Capture(boundary.InitialAssets,
                    boundary.StartHp, boundary.StartPotions)), entry.Detail);
                string family = root.Encounter + ":" + string.Join(",", root.PublicRoot.Observation.Relics);
                if (!families.Add(family)) return;
                var sourceDraw = boundary.State.Players[0].PlayerCombatState!.DrawPile.Cards.ToArray();
                var sourceDeck = boundary.State.Players[0].Deck.Cards.ToArray();
                string sourceRandom = RandomStreams(imported);

                await using var changed = imported.ForkExact();
                Reverse(changed.State.Players[0].PlayerCombatState!.DrawPile);
                Reverse(changed.State.Players[0].Deck);
                changed.ReseedFuture(0xfedcba9876543210);
                changed.State.Players[0].Odds.LoadFromSerializable(new()
                    { CardRarityOddsValue = .75f, PotionRewardOddsValue = .9f });
                Assert.Equal(PublicJson.Serialize(imported.Observe()), PublicJson.Serialize(changed.Observe()));
                await using var a = await BeliefSampler.SampleWorldAsync(imported, 81);
                await using var b = await BeliefSampler.SampleWorldAsync(changed, 81);
                Assert.Equal(Deck(a), Deck(b));
                Assert.Equal(Draw(a), Draw(b));
                Assert.Equal(RandomStreams(a), RandomStreams(b));
                Assert.NotEqual(sourceRandom, RandomStreams(a));
                await ContinueTogether(a, b);
                Assert.Equal(PublicJson.Serialize(a.FinalAssets), PublicJson.Serialize(b.FinalAssets));
                Assert.Equal(sourceDraw, boundary.State.Players[0].PlayerCombatState!.DrawPile.Cards);
                Assert.Equal(sourceDeck, boundary.State.Players[0].Deck.Cards);
                Assert.Equal(sourceRandom, RandomStreams(imported));

                if (root.Encounter == "ShrinkerBeetle" && !shrinkBindingVerified)
                {
                    await using var probe = imported.ForkExact();
                    await probe.StepAsync(probe.Observe().Actions.Single(x => x.Kind == "end_turn"));
                    var shrink = probe.State.Players[0].Creature.GetPower<ShrinkPower>()!;
                    Assert.NotNull(shrink);
                    Assert.Same(probe.State.Enemies.Single(), shrink.Applier);
                    Assert.NotSame(boundary.State.Enemies.Single(), shrink.Applier);
                    Assert.True(BeliefSampler.UsesExchangeablePosterior(probe));
                    shrink.Applier = probe.State.Players[0].Creature;
                    Assert.False(BeliefSampler.UsesExchangeablePosterior(probe));
                    shrinkBindingVerified = true;
                }

                if (root.Encounter == "SkulkingColonyElite")
                {
                    await using var corrupted = imported.ForkExact();
                    var hidden = typeof(HardenedShellPower).GetField("_damageReceivedThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    hidden.SetValue(corrupted.State.Enemies.Single().GetPower<HardenedShellPower>(), 10m);
                    Assert.Equal(PublicJson.Serialize(imported.Observe()), PublicJson.Serialize(corrupted.Observe()));
                    Assert.False(BeliefSampler.UsesExchangeablePosterior(corrupted));
                    await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(corrupted, 81));

                    await using var reset = imported.ForkExact();
                    var packet = reset.Observe();
                    await reset.StepAsync(packet.Actions.Single(x => x.Kind == "potion" && packet.Observation!.Potions[x.Slot] == "FirePotion"));
                    Assert.Equal(0, reset.State.Enemies.Single().GetPower<HardenedShellPower>()!.DisplayAmount);
                    Assert.True(BeliefSampler.UsesExchangeablePosterior(reset));
                    // This naturally captured root has three Defends and eight HP.
                    // Block the first attack to reach the real next reset boundary.
                    while ((packet = reset.Observe()).Actions.FirstOrDefault(x => x.Kind == "play" && packet.Observation!.Hand[x.Slot].Id == "DefendSilent") is { } defend)
                        await reset.StepAsync(defend);
                    await reset.StepAsync(reset.Observe().Actions.Single(x => x.Kind == "end_turn"));
                    Assert.Equal("player_decision", reset.Observe().Status);
                    Assert.Equal(2, reset.Observe().Observation!.Turn);
                    Assert.Equal(20, reset.State.Enemies.Single().GetPower<HardenedShellPower>()!.DisplayAmount);
                    Assert.Equal("ZOOM_MOVE_2", reset.State.Enemies.Single().Monster!.NextMove!.Id);
                    Assert.True(BeliefSampler.UsesExchangeablePosterior(reset));
                    shellResetVerified = true;
                }

                if (imported.State.Players[0].Relics.OfType<FishingRod>().SingleOrDefault()?.CombatsSeen == 2)
                {
                    await using var exact = imported.ForkExact();
                    await Continue(exact);
                    var terminal = await exact.SettleAsync();
                    Assert.Equal("win", terminal.Result);
                    var rod = exact.State.Players[0].Relics.OfType<FishingRod>().Single();
                    Assert.Equal(3, rod.CombatsSeen);
                    Assert.NotEqual(PublicJson.Serialize(exact.InitialAssets.Deck), PublicJson.Serialize(exact.FinalAssets!.Deck));
                    exactFishing.Add(root.SourceCombatId, (terminal.FinalHp, PublicJson.Serialize(exact.FinalAssets)));
                }
                if (root.Encounter == "SludgeSpinnerWeak" && randomNextMoves.Count == 0)
                {
                    // The published first move is Oil. Native CannotRepeat permits
                    // both other moves after it; independent future seeds must retain
                    // that law, not preserve the source's next random move.
                    for (ulong seed = 1; seed <= 32; seed++)
                    {
                        await using var world = await BeliefSampler.SampleWorldAsync(imported, seed);
                        await world.StepAsync(world.Observe().Actions.Single(x => x.Kind == "end_turn"));
                        Assert.Equal("player_decision", world.Observe().Status);
                        Assert.True(BeliefSampler.UsesExchangeablePosterior(world));
                        randomNextMoves.Add(world.State.Enemies.Single().Monster!.NextMove!.Id);
                    }
                    await using var corrupted = imported.ForkExact();
                    var machine = corrupted.State.Enemies.Single().Monster!.MoveStateMachine!;
                    machine.StateLog.Add(machine.StateLog[0]);
                    Assert.Equal(PublicJson.Serialize(imported.Observe()), PublicJson.Serialize(corrupted.Observe()));
                    Assert.False(BeliefSampler.UsesExchangeablePosterior(corrupted));
                    await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(corrupted, 81));
                }
            }
        }, onSettlement: settlement =>
        {
            if (!exactFishing.TryGetValue(settlement.SourceCombatId, out var exact)) return;
            Assert.Equal(exact.Hp, settlement.Hp);
            Assert.Equal(exact.Assets, PublicJson.Serialize(settlement.Assets));
            nativeFishingSettlements++;
        });
        Assert.All(report.Runs, run => Assert.Null(run.Error));
        Assert.Equal(200, report.Roots.Length);
        Assert.Equal(145, accepted);
        Assert.Equal(11, encounters.Count);
        Assert.Equal(1, nativeFishingSettlements);
        Assert.True(shellResetVerified);
        Assert.True(shrinkBindingVerified);
        Assert.Equal(new[] { "RAGE_MOVE", "SLAM_MOVE" }, randomNextMoves.Order(StringComparer.Ordinal));
    }

    private static void Reverse(CardPile pile)
    {
        var reversed = pile.Cards.Reverse().ToArray();
        foreach (var card in pile.Cards.ToArray()) pile.RemoveInternal(card);
        foreach (var card in reversed) pile.AddInternal(card);
    }

    private static async Task ContinueTogether(CombatSession a, CombatSession b)
    {
        var policy = new PublicRulePolicy();
        for (int step = 0; step < 300 && a.Observe().Status != "terminal_settled"; step++)
        {
            var pa = a.Observe(); var pb = b.Observe();
            Assert.Equal(PublicJson.Serialize(pa), PublicJson.Serialize(pb));
            if (pa.Status == "player_decision") Assert.True(BeliefSampler.UsesExchangeablePosterior(a));
            var action = policy.Choose(pa);
            Assert.Equal(PublicJson.Serialize(action), PublicJson.Serialize(policy.Choose(pb)));
            await a.StepAsync(action); await b.StepAsync(action);
        }
        Assert.Equal("terminal_settled", a.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await a.SettleAsync()), PublicJson.Serialize(await b.SettleAsync()));
    }

    private static async Task Continue(CombatSession session)
    {
        var policy = new PublicRulePolicy();
        for (int step = 0; step < 300 && session.Observe().Status != "terminal_settled"; step++)
            await session.StepAsync(policy.Choose(session.Observe()));
        Assert.Equal("terminal_settled", session.Observe().Status);
    }

    private static string Draw(CombatSession session) => PublicJson.Serialize(session.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(PublicViews.Card));
    private static string Deck(CombatSession session) => PublicJson.Serialize(session.State.Players[0].Deck.Cards.Select(PublicViews.Card));
    private static string RandomStreams(CombatSession session) => JsonSerializer.Serialize(new
    {
        run = session.State.RunState.Rng.ToSerializable(),
        player = session.State.Players[0].PlayerRng.ToSerializable().Rngs,
        monsters = session.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });
}
