using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Random;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativeConstructedRubyDrawClosureTests(ITestOutputHelper output)
{
    private static string[] MixedDeck => ["StrikeSilent", "StrikeSilent+", "StrikeSilent", "DefendSilent", "DefendSilent+",
        "DefendSilent", "Neutralize+", "Survivor", "DeadlyPoison", "BladeDance+", "DaggerThrow", "Backflip",
        "Acrobatics", "DodgeAndRoll", "Dash", "PoisonedStab"];
    private static NativeConstructedTapePrior Prior => new()
    {
        Setup = new() { Encounter = "RubyRaiders", Gold = 110, Potions = ["FirePotion", "BlockPotion"], Relics = [], Deck = MixedDeck },
        SourcePolicyId = PublicContinuationPolicies.ReviewedId, SourceDecisionHorizon = 48, RootSelection = "first_player_turn_2",
    };

    private static async Task<DecisionPacket> Root()
    {
        var prior = Prior;
        var recipe = prior.Draw(new Rng(44101, NativeConstructedTapePrior.SourceDrawDomain));
        await using var source = await NativeRunWorld.OpenConstructedLabelTapeAsync(prior, recipe,
            NativeLabelTape.ForConstructedPrior(prior, recipe));
        Assert.NotNull(source);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
    }

    [Theory]
    [InlineData("AxeRubyRaider", 31, 18, 0, 0, "SWING_2")]
    [InlineData("AssassinRubyRaider", 44, 0, 0, 0, "KILLSHOT_MOVE")]
    [InlineData("BruteRubyRaider", 19, 0, 6, 0, "BEAT_MOVE")]
    [InlineData("CrossbowRubyRaider", 32, 6, 0, 0, "RELOAD_MOVE")]
    [InlineData("TrackerRubyRaider", 27, 0, 0, 2, "HOUNDS_MOVE")]
    public async Task EveryNativeRubyMoveAndPowerLifecycleLeavesEveryPhysicalCardAndPileUnchanged(
        string id, int damage, int block, int strength, int frail, string nextMove)
    {
        // Mechanic fixture, not a source prior or posterior simplification. Use
        // the unchanged full mixed deck and execute each model's complete cycle.
        await using var session = await CombatSession.CreateAsync(new(Seed: "ruby-pile-audit:" + id,
            Enemy: id, Deck: MixedDeck, Hp: 1000, MaxHp: 1000));
        var state = session.State; var player = state.Players.Single(); var monster = state.Enemies.Single().Monster!;
        var piles = player.PlayerCombatState!.AllPiles;
        var cards = piles.Select(pile => pile.Cards.ToArray()).ToArray();
        var metadata = piles.Select(pile => PublicJson.Serialize(pile.Cards.Select(PublicViews.Card).ToArray())).ToArray();
        string shuffle = JsonSerializer.Serialize(state.RunState.Rng.Shuffle.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });
        Type type = monster.GetType(); Assert.True(type.IsSealed);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetBaseDefinition().DeclaringType == typeof(AbstractModel));
        Assert.Equal(typeof(MonsterModel), type.GetMethod(nameof(MonsterModel.AfterAddedToRoom))!.DeclaringType);
        void AssertPiles()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                Assert.True(cards[i].SequenceEqual(piles[i].Cards, ReferenceEqualityComparer.Instance), id + ": physical pile " + piles[i].Type);
                Assert.Equal(metadata[i], PublicJson.Serialize(piles[i].Cards.Select(PublicViews.Card).ToArray()));
            }
            Assert.Equal(shuffle, JsonSerializer.Serialize(state.RunState.Rng.Shuffle.ToSerializable(), new JsonSerializerOptions { IncludeFields = true }));
        }
        for (int move = 0; move < 4; move++)
        {
            await monster.PerformMove(); monster.RollMove(state.PlayerCreatures); AssertPiles();
        }
        Assert.Equal(1000 - damage, player.Creature.CurrentHp); Assert.Equal(block, monster.Creature.Block);
        Assert.Equal(strength, monster.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
        Assert.Equal(frail, player.Creature.GetPower<FrailPower>()?.Amount ?? 0);
        Assert.Equal(nextMove, monster.NextMove!.StateId);
        // The only introduced expiring power ticks and removes through the real
        // enemy-side hook, whose application/removal hooks also preserve cards.
        if (frail > 0) Assert.True(player.Creature.GetPower<FrailPower>()!.SkipNextDurationTick);
        for (int tick = 0; tick < 3; tick++)
        {
            await Hook.AfterSideTurnEnd(state, CombatSide.Enemy, state.Enemies); AssertPiles();
            // Native PowerCmd.Apply sets the first-tick skip for player debuffs.
            Assert.Equal(Math.Max(0, frail - tick), player.Creature.GetPower<FrailPower>()?.Amount ?? 0);
            if (player.Creature.GetPower<FrailPower>() is { } power) Assert.False(power.SkipNextDurationTick);
        }
        Assert.Null(player.Creature.GetPower<FrailPower>());
        Assert.Equal(strength, monster.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
    }

    [Fact]
    public async Task ConstructedOptInExtendsOnlyTheCertifiedPublicDrawsAndRetainsLegacyV11Behavior()
    {
        var prior = Prior; var root = await Root(); string before = PublicJson.Serialize(root);
        Assert.Equal("709783dcf45ceb01218ffecc02a740048460005c99676a28fdedfbe3f92b5712", prior.Identity);
        var evidence = root.PublicEvidence!;
        var first = evidence.Events.First(e => e.Payload is PublicCombatDecision);
        var opening = root with { PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart,
            evidence.Events.TakeWhile(e => e.EventOrdinal <= first.EventOrdinal).ToImmutableArray()) };
        var initial = NativePublicCombatPrefixCondition.CreateConstructed(opening, prior).Combats[0].Shuffle!;
        var ownerEvents = evidence.Events.Where(e => e.OwnerOrdinal == first.OwnerOrdinal).ToArray();
        _ = NativePublicDrawPrefixCondition.Extend(initial, ownerEvents, first, null, out var legacy);
        Assert.Equal("nosl.public-first-draw-cycle.v11", legacy.CertificateVersion);
        Assert.Equal(8, legacy.DrawPrefixCount); Assert.Equal("draw_cycle_enemy_turn_not_certified", legacy.StopReason);
        var input = NativePublicCombatPrefixCondition.CreateConstructed(root, prior).Combats[0];
        Assert.Equal(NativePublicDrawPrefixCondition.ConstructedRubyVersion, input.DrawPrefix!.CertificateVersion);
        Assert.Equal("observed_prefix_complete", input.DrawPrefix.StopReason); Assert.Equal(13, input.DrawPrefix.DrawPrefixCount);
        Assert.Equal(ownerEvents.Where(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardDrawn })
            .Select(e => NativePublicDrawKey.From(((PublicCombatFact)e.Payload).Cards.Single())), input.Shuffle!.DrawPrefixKeys);
        Assert.Equal(before, PublicJson.Serialize(root));

        var source = new NativeConstructedTapeSource(root, prior);
        foreach (ulong seed in new ulong[] { 74101, 74102 })
        {
            await using var world = await source.SampleWorldAsync(seed, 16);
            Assert.Equal(before, PublicJson.Serialize(world.Observe()));
            await using var fork = await world.ForkForContinuationAsync();
            Assert.Equal(before, PublicJson.Serialize(fork.Observe()));
        }
        Assert.Equal(2, source.ProposalAudit.Count(row => row.Status == "accepted"));
        Assert.All(source.ProposalAudit, row => Assert.InRange(row.Attempt, 1, 16));
        output.WriteLine(PublicJson.Serialize(source.ProposalAudit));
    }

    [Theory]
    [InlineData("unknown_power", "draw_cycle_power_not_certified:SuckPower")]
    [InlineData("hidden_generation", "draw_cycle_generation_not_certified")]
    [InlineData("visible_generation", "draw_cycle_generation_not_certified")]
    [InlineData("hand_end_effect", "draw_cycle_hand_end_effect_not_certified")]
    [InlineData("unknown_card_play", "draw_cycle_play_not_certified:BladeDance")]
    [InlineData("changed_listener", "draw_cycle_listener_set_changed")]
    public async Task RubyOptInRetainsEveryUncertifiedCardPowerGenerationAndListenerBoundary(string change, string reason)
    {
        var root = await Root(); var evidence = root.PublicEvidence!; var events = evidence.Events.ToArray();
        int actionIndex = Array.FindIndex(events, e => e.Payload is PublicCombatActionTaken { Action.Kind: "end_turn" });
        var end = (PublicCombatActionTaken)events[actionIndex].Payload;
        if (change is "unknown_power" or "hidden_generation" or "visible_generation")
        {
            int index = change == "unknown_power"
                ? Array.FindIndex(events, actionIndex, e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.PowerChanged })
                : actionIndex + 1;
            PublicEvidencePayload payload = change switch
            {
                "unknown_power" => new PublicCombatFact(PublicCombatFactKind.PowerChanged, targetSlot: -2, model: "SuckPower", amount: 2),
                "hidden_generation" => new PublicCombatFact(PublicCombatFactKind.HiddenCardGenerated),
                _ => new PublicCombatFact(PublicCombatFactKind.CardGenerated, cards: [root.Observation!.Hand[0]]),
            };
            events[index] = new(events[index].EventOrdinal, events[index].OwnerOrdinal, payload);
        }
        else
        {
            long target = end.DecisionEventOrdinal;
            if (change == "unknown_card_play") target = ((PublicCombatActionTaken)events.Where(e => e.Payload is PublicCombatActionTaken
                { Action.Kind: "play" }).Skip(1).First().Payload).DecisionEventOrdinal;
            int index = Array.FindIndex(events, e => e.EventOrdinal == target);
            var decision = (PublicCombatDecision)events[index].Payload; var observation = decision.Observation;
            if (change == "changed_listener") observation = observation with { Relics = [.. observation.Relics, "BagOfPreparation"] };
            else
            {
                var hand = observation.Hand.ToArray();
                int slot = change == "unknown_card_play" ? ((PublicCombatActionTaken)events[index + 1].Payload).Action.Slot : 0;
                hand[slot] = hand[slot] with { Id = change == "unknown_card_play" ? "BladeDance" : "Burn" };
                observation = observation with { Hand = hand };
            }
            events[index] = new(events[index].EventOrdinal, events[index].OwnerOrdinal, new PublicCombatDecision(decision.Status,
                observation, decision.Actions, decision.HistoryThroughEventOrdinal, decision.HistoryCompleteFromCombatStart));
        }
        root = root with { PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart, events.ToImmutableArray()) };
        var input = NativePublicCombatPrefixCondition.CreateConstructed(root, Prior).Combats[0];
        Assert.Equal(reason, input.DrawPrefix!.StopReason); Assert.True(input.DrawPrefix.DrawPrefixCount < 13);
    }
}
