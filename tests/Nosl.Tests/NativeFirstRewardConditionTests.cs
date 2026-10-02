using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeFirstRewardConditionTests
{
    private static NativeTapePrior Prior => new()
    { EligibleCombats = 8, Execution = new(MaxFloors: 8, PublicContextProfile: PublicRunContext.Version) };
    private static PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
    private static PublicRelic Relic(string id) => new(id, new Dictionary<string, int>
    { ["isWax"] = 0, ["isMelted"] = 0, ["stackCount"] = 1, ["isUsedUp"] = 0 });
    private static DecisionPacket Root(string cardId = "Accelerant", string relic = "WingedBoots",
        string monster = "FuzzyWurmCrawler", int hp = 49, int gold = 106)
    {
        PublicCard[] deck = new[] { "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent",
            "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "Neutralize", "Survivor", "AscendersBane", cardId }
            .Select(Card).ToArray();
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", hp, 70, gold, deck,
            [Relic("RingOfTheSnake"), Relic(relic)], [null, null], 3, 2, 0, 0);
        var hand = deck.Take(7).ToArray();
        PublicEvent[] history = [new("combat_started", "Silent:A10"), new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. hand.Select(card => new PublicEvent("draw", PublicJson.Serialize(card))), new("player_turn", "1"),
            new("intent_published", PublicJson.Serialize(new { slot = 0, id = monster, intents = Array.Empty<PublicIntent>() }))];
        return new("player_decision", new(PublicRunContext.ObservationSchema, hp, 10, 1, hp, 70, 0, 3, 0,
            hand, [], [], [], [], 7, [null, null], ["RingOfTheSnake", relic], [],
            [new(0, monster, 42, 42, 0, [], [])], history, null,
            RunContext: new(PublicRunContext.Version, 0, 3, 1, true)), [new(0, "end_turn")]);
    }
    private static DecisionPacket EditEntry(DecisionPacket root, Func<NativeEntryAssets, NativeEntryAssets> edit)
    {
        root.Observation!.History[1] = root.Observation.History[1] with
        { Detail = PublicJson.Serialize(edit(PublicJson.Read<NativeEntryAssets>(root.Observation.History[1].Detail))) };
        return root;
    }

    [Theory]
    [InlineData("Accelerant", "WingedBoots", "FuzzyWurmCrawler", 49, 106)]
    [InlineData("DeadlyPoison", "LeadPaperweight", "SludgeSpinner", 43, 109)]
    public void ImmutableCarryInEvidenceCertifiesTheFirstReward(string card, string relic, string monster, int hp, int gold)
    {
        var root = Root(card, relic, monster, hp, gold);
        Assert.True(NativeFirstRewardCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Equal(card, condition!.TargetCardId); Assert.Equal(relic, condition.TargetNeowId);
        Assert.Equal(hp, condition.TargetEntryHp); Assert.Equal(gold - 99, condition.TargetGoldPayout);
        // Current combat state must not be substituted for the immutable entry anchor.
        root = root with { Observation = root.Observation! with { Hp = 1, Gold = 3,
            Potions = ["FirePotion", null], Relics = [], Turn = 4 } };
        Assert.True(NativeFirstRewardCondition.TryCreate(root, Prior, out var later, out reason), reason);
        Assert.Equal(condition.Envelope, later!.Envelope); Assert.Equal(hp, later.TargetEntryHp);
        root = EditEntry(root, entry => entry with { Potions = ["FirePotion", null] });
        Assert.False(NativeFirstRewardCondition.TryCreate(root, Prior, out _, out reason));
        Assert.Equal("first_reward_inventory_not_certified", reason);
    }

    [Theory]
    [InlineData("history", "complete_public_run_history_required")]
    [InlineData("coordinate", "second_combat_floor_three_required")]
    [InlineData("forced_roster", "first_reward_route_not_certified")]
    [InlineData("neow", "first_reward_neow_origin_not_certified")]
    [InlineData("gold", "first_reward_gold_outside_native_range")]
    [InlineData("upgrade", "plain_first_reward_deck_required")]
    [InlineData("other_card", "first_reward_card_not_certified")]
    [InlineData("extra_relic", "first_reward_inventory_not_certified")]
    public void UnprovedOriginsUseFallback(string change, string expected)
    {
        var root = Root(); var prior = Prior;
        switch (change)
        {
            case "history": prior = prior with { Execution = prior.Execution with
                { PublicCombatHistoryMode = PublicRunContext.UnavailableHistoryMode } }; break;
            case "coordinate": root = root with { Observation = root.Observation! with
                { RunContext = root.Observation.RunContext! with { Floor = 4 } } }; break;
            case "forced_roster": root = Root(monster: "FakeMerchantMonster"); break;
            case "neow": root = Root(relic: "Kaleidoscope"); break;
            case "gold": root = Root(gold: 115); break;
            case "upgrade": root = EditEntry(root, entry => entry with
                { Deck = [.. entry.Deck.SkipLast(1), entry.Deck[^1] with { Upgrade = 1 }] }); break;
            case "other_card": root = Root(cardId: "BouncingFlask"); break;
            case "extra_relic": root = EditEntry(root, entry => entry with
                { Relics = [.. entry.Relics, Relic("Pear")] }); break;
        }
        Assert.False(NativeFirstRewardCondition.TryCreate(root, prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(CardRarity.Common)]
    [InlineData(CardRarity.Uncommon)]
    public void FiniteFivePrimitiveProposalMatchesExhaustiveNativeJointLaw(CardRarity rarity)
    {
        const int bits = 3, domain = 1 << bits, cardCount = 3, targetIndex = 1, gold = 10;
        var buckets = NativeFirstRewardProposal.BuildBuckets(gold, rarity, cardCount, targetIndex, bits);
        int accepted = 0, space = 1 << (bits * 5);
        for (int encoded = 0; encoded < space; encoded++)
        {
            int value = encoded; int[] high = new int[5];
            for (int i = 0; i < high.Length; i++) { high[i] = value % domain; value /= domain; }
            float potion = (float)((double)high[0] / domain);
            int payout = (int)((double)high[1] / domain * 9) + 7;
            float cardRoll = (float)((double)high[2] / domain);
            float rare = 0.0149f + -0.05f;
            CardRarity resultRarity = cardRoll < rare ? CardRarity.Rare
                : cardRoll < 0.37f + rare ? CardRarity.Uncommon : CardRarity.Common;
            int card = (int)((double)high[3] / domain * cardCount);
            bool upgraded = (decimal)(float)((double)high[4] / domain) <= 0m;
            bool nativeMatch = potion >= 0.4f && payout == gold && resultRarity == rarity
                && card == targetIndex && !upgraded;
            bool inProposedBuckets = high.Select((h, i) => (ulong)h >= buckets[i].Start
                && (ulong)h < buckets[i].Start + buckets[i].Size).All(inside => inside);
            Assert.Equal(nativeMatch, inProposedBuckets);
            if (nativeMatch) accepted++;
        }
        Assert.Equal(new ShuffleRational(accepted, space), NativeFirstRewardProposal.Probability(buckets));
        BigInteger proposalSupport = buckets.Aggregate(BigInteger.One, (product, bucket) => product * bucket.Size);
        // Uniform conditional preimages give q=1/support and native p=1/domain^5
        // for each compatible complete primitive path, hence p/q=the common E.
        Assert.Equal(new ShuffleRational(proposalSupport, space), NativeFirstRewardProposal.Probability(buckets));
        var a = NativeFirstRewardProposal.Create(buckets, new Rng(111).NextUnsignedLong);
        var b = NativeFirstRewardProposal.Create(buckets, new Rng(222).NextUnsignedLong);
        Assert.Equal(a.Envelope, b.Envelope); Assert.Equal(a.NativeToProposalRatio, a.Envelope);
        Assert.True(a.AcceptCorrection(() => throw new InvalidOperationException("Root-constant correction needs no draw")));
    }

    [Fact]
    public void FloatBoundariesUseTheNativeFiftyThreeBitWrapperIncludingUpgradeZero()
    {
        const ulong domain = 1UL << 53;
        float uncommon = 0.37f + (0.0149f + -0.05f);
        foreach (float threshold in new[] { 0.4f, uncommon })
        {
            ulong start = NativeFirstRewardProposal.FloatLowerBound(threshold);
            float Draw(ulong high)
            {
                using var scope = LabelRandomScope.Enter(_ => high << 11);
                return new Rng(42).NextFloat();
            }
            Assert.True(Draw(start - 1) < threshold); Assert.True(Draw(start) >= threshold);
            Assert.True(Draw(domain - 1) >= threshold);
            // A 24-bit-preimage implementation cannot describe the native boundary.
            Assert.NotEqual((NativeFirstRewardProposal.FloatLowerBound(threshold, 24) << 29), start);
        }
        var bucket = NativeFirstRewardProposal.BuildBuckets(7, CardRarity.Common, 3, 0)[4];
        Assert.Equal(1UL, bucket.Start); Assert.Equal(domain - 1, bucket.Size);
        using (LabelRandomScope.Enter(_ => 1UL << 11)) Assert.True((decimal)new Rng(1).NextFloat() > 0m);
        using (LabelRandomScope.Enter(_ => 0UL)) Assert.Equal(0m, (decimal)new Rng(1).NextFloat());
        // Independently checked native wrapper boundaries, including float ties.
        var accelerant = NativeFirstRewardProposal.BuildBuckets(7, CardRarity.Uncommon, 35, 0);
        var poison = NativeFirstRewardProposal.BuildBuckets(10, CardRarity.Common, 20, 6);
        Assert.Equal(5404319633375231UL, accelerant[0].Size);
        Assert.Equal(3016510821892097UL, accelerant[2].Size);
        Assert.Equal(5990688432848895UL, poison[2].Size);
        Assert.Equal(257348550135457UL, accelerant[3].Size);
        Assert.Equal(450359962737049UL, poison[3].Size);
        Assert.Equal(1000799917193444UL, accelerant[1].Size);
        Assert.Equal(1000799917193443UL, poison[1].Size);
        var plan = NativeFirstRewardProposal.Create(accelerant, () => ulong.MaxValue);
        Assert.All(plan.RawWords, word => Assert.Equal(2047UL, word & 2047UL));
    }

    [Theory]
    [InlineData("Accelerant", "WingedBoots", 7)]
    [InlineData("DeadlyPoison", "LeadPaperweight", 10)]
    public async Task NativeGenerationConsumesTheCertifiedPrefixAndPreservesUnusedOffers(string cardId, string neowId, int gold)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("native-first-reward-prefix-test", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var bridge = new NaturalSourceCollector.SourceBridge(run, new(),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "fixture", "fixture", null, null, default);
        _ = new RunDriver(run, bridge);
        int beforeNeow = player.PlayerRng.Rewards.Counter;
        await RelicCmd.Obtain((RelicModel)ModelDb.Get(neowId == "WingedBoots" ? typeof(WingedBoots) : typeof(LeadPaperweight)), player);
        Assert.Equal(neowId == "LeadPaperweight" ? 6 : 0, player.PlayerRng.Rewards.Counter - beforeNeow);
        Assert.Equal(-0.05f, player.Odds.CardRarity.CurrentValue); Assert.Equal(13, player.Deck.Cards.Count);
        CardRarity rarity = cardId == "Accelerant" ? CardRarity.Uncommon : CardRarity.Common;
        var cards = player.Character.CardPool.GetUnlockedCards(player.UnlockState, false).Where(card => card.Rarity == rarity).ToArray();
        int index = Array.FindIndex(cards, card => card.GetType().Name == cardId);
        var plan = NativeFirstRewardProposal.Create(NativeFirstRewardProposal.BuildBuckets(gold, rarity, cards.Length, index),
            new Rng(909).NextUnsignedLong);
        int before = player.PlayerRng.Rewards.Counter; int scopeCalls = 0, draws = 0;
        Rng? ordinary = null; decimal hp = player.Creature.CurrentHp;
        RewardsSet rewards;
        using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("All generation draws should enter the reward scope"),
            beginCombatReward: context =>
            {
                scopeCalls++; Assert.Same(player, context.Player); Assert.Same(player.PlayerRng.Rewards, context.Rng);
                Assert.Equal(RoomType.Monster, context.RoomType); Assert.Null(context.FixedGoldAmount);
                Assert.Equal(1f, context.GoldProportion);
                ordinary = context.Rng.CloneExact();
                return LabelRandomScope.Enter(_ =>
                {
                    ulong original = ordinary.NextUnsignedLong();
                    int ordinal = draws++;
                    return ordinal < plan.RawWords.Count ? plan.RawWords[ordinal] : original;
                });
            }))
            rewards = RewardsSet.GenerateFor(player, RoomType.Monster, run);
        Assert.Equal(1, scopeCalls); Assert.Equal(11, draws); Assert.Equal(11, player.PlayerRng.Rewards.Counter - before);
        Assert.Equal(ordinary!.ToSerializable(), player.PlayerRng.Rewards.ToSerializable());
        Assert.Null(rewards.Potion); Assert.Equal(gold, rewards.Gold.Amount);
        Assert.Equal(3, rewards.Card.Options.Count); Assert.Equal(3, rewards.Card.Options.Select(card => card.Id).Distinct().Count());
        Assert.Equal(cardId, rewards.Card.Options[0].GetType().Name); Assert.Equal(0, rewards.Card.Options[0].CurrentUpgradeLevel);
        await rewards.Gold.Take();
        var decision = Assert.IsType<RewardDecision.TakeCard>(RewardDecisionClassifier.ChooseDefault(rewards));
        Assert.Same(rewards.Card.Options[0], decision.Card);
        await rewards.Card.SelectOption(decision.Card);
        Assert.Equal(99 + gold, player.Gold); Assert.Equal(hp, player.Creature.CurrentHp);
        Assert.Equal(14, player.Deck.Cards.Count); Assert.All(player.PotionSlots, Assert.Null);
    }
}
