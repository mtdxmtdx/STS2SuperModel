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
    [InlineData("ToughEgg", "startup_monster_not_certified")]
    [InlineData("Wriggler", "startup_monster_not_certified")]
    [InlineData("Fabricator", "startup_monster_not_certified")]
    [InlineData("missing-model", "startup_monster_not_certified")]
    public void UnknownOrUnreviewedStartupCreatureFallsBack(string monster, string expectedReason)
    {
        var root = Root();
        root.Observation!.History[^1] = new("intent_published", PublicJson.Serialize(new { slot = 0, id = monster }));
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out string? reason));
        Assert.Equal(expectedReason, reason);
    }
}
