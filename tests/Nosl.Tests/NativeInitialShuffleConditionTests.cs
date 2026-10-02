using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class NativeInitialShuffleConditionTests
{
    private static PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
    private static PublicRelic Relic(string id) => new(id, new Dictionary<string, int> { ["isMelted"] = 0 });

    private static DecisionPacket Root(string relic = "NeowsTorment")
    {
        var deck = new[] { "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent", "Nightmare", "Neutralize", "Survivor", "AscendersBane" }.Select(Card).ToArray();
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, deck,
            [Relic("RingOfTheSnake"), Relic(relic)], ["FirePotion", null], 3, 2, 0, 0);
        var hand = deck.Take(7).ToArray();
        PublicEvent[] history = [new("combat_started", "Silent:A10"), new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. hand.Select(card => new PublicEvent("draw", PublicJson.Serialize(card))), new("player_turn", "1"),
            new("intent_published", PublicJson.Serialize(new { slot = 0, id = "SludgeSpinner", intents = Array.Empty<PublicIntent>() }))];
        return new("player_decision", new("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            hand, [], [], [new(deck[^1], 1)], [], 1, ["FirePotion", null], ["RingOfTheSnake", relic], [],
            [new(0, "SludgeSpinner", 42, 42, 0, [], [])], history, null), [new(0, "end_turn")]);
    }

    private static DecisionPacket EditEntry(DecisionPacket root, Func<NativeEntryAssets, NativeEntryAssets> edit)
    {
        var history = root.Observation!.History.ToArray();
        history[1] = history[1] with { Detail = PublicJson.Serialize(edit(PublicJson.Read<NativeEntryAssets>(history[1].Detail))) };
        return root with { Observation = root.Observation with { History = history } };
    }

    [Theory]
    [InlineData("NeowsTorment")]
    [InlineData("WingedBoots")]
    public void BroadCardMetadataAndReviewedStartupProduceImmutablePublicCondition(string relic)
    {
        var root = Root(relic);
        Assert.DoesNotContain("Nightmare", NativeBeliefCertificate.Cards);
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out var condition, out string? reason), reason);
        Assert.NotNull(condition);
        Assert.Null(reason);
        Assert.Equal(8, condition.DeckCount);
        Assert.Equal(7, condition.DrawPrefixIds.Length);
        Assert.Equal(4d / 40320d, condition.PrefixProbability, 12);
        Assert.True(condition.MatchesEntryJson(root.Observation!.History[1].Detail));
        Assert.False(condition.MatchesEntryJson(root.Observation.History[1].Detail + " "));
        Assert.True(condition.MatchesInitialPool(condition.DeckIds.Reverse()));
        Assert.False(condition.MatchesInitialPool(condition.DeckIds.Skip(1)));
        Assert.False(condition.MatchesInitialPool(condition.DeckIds.Select(id => id == "Nightmare" ? "StrikeSilent" : id)));
        var expected = condition.DrawPrefixIds;
        condition.DrawPrefixIds[0] = "mutated";
        condition.DeckIds[0] = "mutated";
        root.Observation.History[2] = new("draw", PublicJson.Serialize(Card("mutated")));
        Assert.Equal(expected, condition.DrawPrefixIds);
        Assert.DoesNotContain("mutated", condition.DeckIds);
    }

    [Fact]
    public void LaterChoiceUsesPublishedStartupRosterRatherThanCurrentCreaturesOrPowers()
    {
        var root = Root();
        var later = root with
        {
            Status = "card_choice",
            Actions = [new(18, "choose", Selection: [0])],
            Observation = root.Observation! with
            {
                Turn = 4, Hand = [], DrawCount = 1,
                Enemies = [new(2, "ToughEgg", 10, 10, 0, [new("HatchPower", 1)], [])],
                Powers = [new("StrengthPower", 2)], Orbs = [new("LightningOrb", 3, 8)],
                Choice = new("Nightmare", 1, 1, false, [Card("Nightmare")]),
                History = [.. root.Observation.History, new("action", "later-action"), new("power_changed", "later-power")],
            }
        };
        Assert.True(NativeInitialShuffleCondition.TryCreate(later, out var condition, out string? reason), reason);
        Assert.Equal(root.Observation.History[1].Detail, condition!.EntryJson);
        Assert.Equal(root.Observation.Hand.Select(card => card.Id), condition.DrawPrefixIds);
    }

    [Theory]
    [InlineData("innate", "initial_shuffle_card_reordering_not_certified")]
    [InlineData("enchantment", "initial_shuffle_card_reordering_not_certified")]
    [InlineData("affliction", "initial_shuffle_card_reordering_not_certified")]
    [InlineData("card_hook", "entry_hook_not_certified:Void.AfterCardDrawn")]
    [InlineData("relic_hook", "entry_hook_not_certified:GamblingChip.AfterSideTurnStart")]
    [InlineData("orb", "invalid_public_entry")]
    [InlineData("unknown", "unknown_entry_model:missing-model")]
    public void UnsafeEntryContentFallsBackWithoutAContentWhitelist(string change, string expectedReason)
    {
        var root = EditEntry(Root(), entry => change switch
        {
            "innate" => entry with { Deck = [entry.Deck[0] with { Keywords = ["Innate"] }, .. entry.Deck.Skip(1)] },
            "enchantment" => entry with { Deck = [entry.Deck[0] with { Enchantments = [new("Swift", 1)] }, .. entry.Deck.Skip(1)] },
            "affliction" => entry with { Deck = [entry.Deck[0] with { Affliction = new("Hexed", 1) }, .. entry.Deck.Skip(1)] },
            "card_hook" => entry with { Deck = [Card("Void"), .. entry.Deck.Skip(1)] },
            "relic_hook" => entry with { Relics = [Relic("RingOfTheSnake"), Relic("GamblingChip")] },
            "orb" => entry with { OrbSlots = 1 },
            _ => entry with { Deck = [Card("missing-model"), .. entry.Deck.Skip(1)] },
        });
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out var condition, out string? reason));
        Assert.Null(condition);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("power_changed")]
    [InlineData("hidden_card_generated")]
    [InlineData("shuffle")]
    [InlineData("choice")]
    [InlineData("action")]
    public void InterveningStartupEventsCannotBeSilentlySkipped(string kind)
    {
        var root = Root();
        root = root with { Observation = root.Observation! with
        { History = [.. root.Observation.History.Take(4), new(kind, "event"), .. root.Observation.History.Skip(4)] } };
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out string? reason));
        Assert.Equal("uninterrupted_initial_draw_history_required", reason);
    }

    [Theory]
    [InlineData(8001UL)]
    [InlineData(8002UL)]
    public async Task RetainedNativeOpeningProfilesHaveCertifiedObservedDrawPrefix(ulong sourceDraw)
    {
        var prior = new NativeRunPrior { EligibleSlots = 1, Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64) };
        var recipe = prior.Draw(new Sts2Sim.Core.Random.Rng(sourceDraw, "nosl-owned-native-run-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenAsync(prior.Execution, recipe.IndependentRunSeed, recipe.Slot);
        Assert.NotNull(world);
        var publicPacket = world.Observe();
        Assert.True(NativeInitialShuffleCondition.TryCreate(publicPacket, out var condition, out string? reason), reason);
        Assert.Equal(1d / 7207.2d, condition!.PrefixProbability, 12);
        Assert.True(condition.MatchesInitialPool(world.NativeRun.Players.Single().Deck.Cards.Select(card => card.GetType().Name)));
    }

    [Theory]
    [InlineData("Seapunk")]
    [InlineData("FuzzyWurmCrawler")]
    [InlineData("HauntedShip")]
    [InlineData("CeremonialBeast")]
    [InlineData("TheInsatiable")]
    public void AuditedFixedInitialMoveMonstersAcceptPickupOnlyOyster(string monster)
    {
        var root = Root("NutritiousOyster");
        root.Observation!.History[^1] = new("intent_published", PublicJson.Serialize(new { slot = 0, id = monster }));
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out var condition, out string? reason), reason);
        Assert.NotNull(condition);
    }

    [Theory]
    [InlineData("Nibbit")]
    [InlineData("LeafSlimeS")]
    [InlineData("Fabricator")]
    [InlineData("Flyconid")]
    [InlineData("Toadpole")]
    [InlineData("TwoTailedRat")]
    [InlineData("Wriggler")]
    public void AuditedInitialBranchesOnlyChooseAnUnperformedMove(string monster)
    {
        var root = Root("FishingRod");
        root.Observation!.History[^1] = new("intent_published", PublicJson.Serialize(new { slot = 0, id = monster }));
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out _, out string? reason), reason);
    }

    [Theory]
    [InlineData("BoomingConch", 8)]
    [InlineData("BagOfPreparation", 8)]
    [InlineData("BigMushroom", 5)]
    [InlineData("PaelsBlood", 8)]
    [InlineData("Fiddle", 8)]
    [InlineData("PollinousCore", 8)]
    [InlineData("Pocketwatch", 7)]
    public void SafeCountModifiersUseObservedPrefixWithoutInferringPrivateRoomOrCounters(string relic, int draws)
    {
        var root = Root(relic);
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        PublicEvent[] history = [root.Observation.History[0], root.Observation.History[1],
            .. entry.Deck.Take(draws).Select(card => new PublicEvent("draw", PublicJson.Serialize(card))),
            new("player_turn", "1"), root.Observation.History[^1]];
        root = root with { Observation = root.Observation with { History = history } };
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out var condition, out string? reason), reason);
        Assert.Equal(draws, condition!.DrawPrefixIds.Length);
        double expected = 1;
        var remaining = entry.Deck.GroupBy(card => card.Id).ToDictionary(group => group.Key, group => group.Count());
        for (int i = 0; i < draws; i++) expected *= remaining[entry.Deck[i].Id]-- / (double)(entry.Deck.Length - i);
        Assert.Equal(expected, condition.PrefixProbability);
    }

    [Theory]
    [InlineData("Nibbit", 0)]
    [InlineData("Nibbit", 1)]
    [InlineData("Nibbit", 2)]
    [InlineData("LeafSlimeS", 0)]
    [InlineData("Fabricator", 0)]
    [InlineData("Flyconid", 0)]
    [InlineData("Toadpole", 0)]
    [InlineData("Toadpole", 1)]
    [InlineData("TwoTailedRat", 0)]
    [InlineData("TwoTailedRat", 1)]
    [InlineData("Wriggler", 0)]
    [InlineData("Wriggler", 1)]
    [InlineData("Wriggler", 2)]
    public void NativeInitialBranchSelectionLeavesPhysicalCardOrderAndPlayerAssetsUntouched(string name, int variant)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new Sts2Sim.Core.Runs.RunState("initial-branch-proof:" + name + variant, ascensionLevel: 10);
        var player = Sts2Sim.Core.Entities.Players.Player.CreateForNewRun(
            Sts2Sim.Core.Models.ModelDb.Character<Sts2Sim.Core.Models.Characters.Silent>(), run);
        run.AddPlayer(player);
        var state = new Sts2Sim.Core.Combat.CombatState(run);
        state.AddPlayerCreature(player.Creature);
        player.ResetCombatState();
        player.PopulateCombatState(run.Rng.Shuffle);
        Type type = Sts2Sim.Core.Content.ContentRegistry.AllTypes.Single(candidate => candidate.Name == name
            && typeof(Sts2Sim.Core.Models.MonsterModel).IsAssignableFrom(candidate));
        var monster = (Sts2Sim.Core.Models.MonsterModel)Sts2Sim.Core.Models.ModelDb.Get(type).MutableClone();
        if (monster is Sts2Sim.Core.Models.Monsters.Nibbit nibbit) { nibbit.IsFront = variant == 1; nibbit.IsAlone = variant == 2; }
        if (monster is Sts2Sim.Core.Models.Monsters.Toadpole toadpole) toadpole.IsFront = variant == 1;
        if (monster is Sts2Sim.Core.Models.Monsters.TwoTailedRat rat) rat.StarterMoveIndex = variant == 1 ? 0 : -1;
        if (monster is Sts2Sim.Core.Models.Monsters.Wriggler wriggler) wriggler.StartStunned = variant == 2;
        state.AddMonster(monster, Sts2Sim.Core.Combat.CombatSide.Enemy, variant == 1 ? "wriggler2" : "wriggler1");
        var order = player.PlayerCombatState!.DrawPile.Cards.ToArray();
        int hp = player.Creature.CurrentHp;
        monster.SetUpForCombat();
        monster.RollMove(state.PlayerCreatures);
        Assert.NotNull(monster.NextMove);
        Assert.Equal(order, player.PlayerCombatState.DrawPile.Cards);
        Assert.Empty(player.PlayerCombatState.Hand.Cards);
        Assert.Empty(player.Creature.Powers);
        Assert.Equal(hp, player.Creature.CurrentHp);
    }

    [Theory]
    [InlineData("ToughEgg", "startup_monster_not_certified")]
    [InlineData("missing-model", "startup_monster_not_certified")]
    public void UnknownOrUnreviewedStartupCreatureFallsBack(string monster, string expectedReason)
    {
        var root = Root();
        root.Observation!.History[^1] = new("intent_published", PublicJson.Serialize(new { slot = 0, id = monster }));
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out string? reason));
        Assert.Equal(expectedReason, reason);
    }
    [Theory]
    [InlineData("LavaRock", false)]
    [InlineData("NeowsTorment", true)]
    [InlineData("LavaRock", true)]
    public void PublicV3AdmitsOnlyReviewedStartupExceptionsAndRetainsCoarsePhysicalIds(string relic, bool sharp)
    {
        var root = EditEntry(Root(relic), entry => entry with
        {
            Deck = sharp ? [entry.Deck[0] with { Upgrade = 1, Enchantments = [new("Sharp", 3)] }, .. entry.Deck.Skip(1)] : entry.Deck,
        });
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV2(root, out _, out _));
        Assert.True(NativeInitialShuffleCondition.TryCreatePublicCombatV3(root, out var condition, out string? reason), reason);
        Assert.True(NativeInitialHpCondition.TryCreatePublicCombatV3(root, out _, out reason), reason);
        Assert.False(NativeInitialHpCondition.TryCreate(root, out _, out _));
        Assert.Equal(2, condition!.DeckIds.Count(id => id == "StrikeSilent"));
        Assert.Equal(4d / 40320d, condition.PrefixProbability, 12);
        Assert.Contains(sharp ? "Sharp" : "LavaRock", condition.EntryJson);
    }

}
