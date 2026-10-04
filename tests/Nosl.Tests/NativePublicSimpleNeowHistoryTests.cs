using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicSimpleNeowHistoryTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(24203UL, nameof(GoldenPearl), 2)]
    [InlineData(24204UL, nameof(NutritiousOyster), 1)]
    public async Task InspectedRootsMatchNativePityResourcesAndTwoIndependentProposalStreams(
        ulong seed, string relic, int rewardCount)
    {
        // Fixed roots from the already inspected v7 twelve-source development
        // cohort. This is a lifecycle regression, not a fresh source search or
        // a measurement of posterior acceptance/teacher completion.
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var root = await Root(recipe);
        var evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(root.PublicEvidence!));
        Assert.Equal(relic, Assert.IsType<PublicOptionChosen>(evidence.Events[3].Payload).Key);
        var start = Assert.IsType<PublicRunStarted>(evidence.Events[0].Payload).Assets;
        var acquired = Assert.IsType<PublicOwnerEnded>(evidence.Events[4].Payload).Assets!;
        Assert.Equal((70, 70, 99), (start.Hp, start.MaxHp, start.Gold));
        Assert.Equal(relic == nameof(GoldenPearl) ? (56, 70, 249) : (67, 81, 99),
            (acquired.Hp, acquired.MaxHp, acquired.Gold));

        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Equal(rewardCount, cards.Targets.Count);
        Assert.All(cards.Targets.Values, target => Assert.NotNull(target.PublicHistory));
        Assert.All(resources.Targets.Values, target => Assert.Equal(new[] { (7, 15) }, target.GoldCertificate.Ranges));
        Assert.Equal(relic == nameof(GoldenPearl) ? new string?[] { null, "LiquidMemories" } : ["PowderedDemise"],
            resources.Targets.Values.Select(target => target.Potion));
        Assert.Equal(relic == nameof(GoldenPearl) ? new int?[] { 7, 13 } : [12],
            resources.Targets.Values.Select(target => target.Gold));
        var universal = cards.Targets.Values.Aggregate(new ShuffleRational(1, 1), (mass, target) =>
        {
            var bound = NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope;
            return mass.Multiply(bound.Numerator, bound.Denominator);
        });
        Assert.True(cards.Envelope.Numerator * universal.Denominator < cards.Envelope.Denominator * universal.Numerator);

        Player? player = null; int presences = 0, golds = 0;
        using (LabelMerchantScope.Enter(context => { player = context.Player; return null; }, _ => null))
        using (LabelRewardResourceScope.Enter(context =>
        {
            Assert.NotNull(player);
            var target = cards.Targets[presences++];
            Assert.Equal(target.Floor, player.RunState.TotalFloor);
            Assert.Same(player.PlayerRng.Rewards, context.Rng);
            Assert.Equal(RoomType.Monster, context.RoomType); Assert.False(context.Forced);
            Assert.Equal(target.PublicHistory!.PotionThreshold, context.Threshold);
            Assert.Equal(target.PublicHistory.Thresholds[0],
                player.Odds.CardRarity.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
            return new CheckOnDispose(() => Assert.Equal(
                NativePublicRewardHistoryCertificate.AdvancePotion(context.Threshold,
                    resources.Targets[target.CombatIndex].Potion is not null), player.Odds.PotionReward.CurrentValue));
        }, context =>
        {
            Assert.Same(player, context.Player); golds++;
            Assert.False(context.Omitted); Assert.Equal((7, 15), (context.Min, context.Max)); return null;
        }, _ => null))
        await using (var native = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(native);
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(native.Observe()));
            Assert.Equal(rewardCount, presences); Assert.Equal(rewardCount, golds);
        }

        foreach (ulong salt in new[] { 713582UL, 38477UL })
        {
            var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ salt };
            var tape = NativeLabelTape.ForDeclaredPrior(Prior, hypothetical, publicRewardCondition: cards,
                publicResourceCondition: resources, expectedPublicEvidence: evidence);
            await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, hypothetical, tape);
            Assert.NotNull(world); tape.ValidateProposalCompletion();
            // Owning proposals verify all three native per-card rarity thresholds
            // and native potion/gold boundaries before supplying their words.
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            Assert.Equal(3 * rewardCount, tape.ConditionedPublicRewardCards);
            Assert.Equal(rewardCount, tape.ConditionedResourcePresence);
            Assert.Equal(rewardCount, tape.ConditionedResourceGold);
            Assert.Equal(1, tape.ConditionedResourcePotions);
            Assert.Equal(cards.Envelope, tape.PublicRewardRatio);
            Assert.Equal(resources.Envelope, tape.PublicResourceRatio);
            var detached = NativePublicRewardCondition.Create(PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence)));
            Assert.Equal(cards.Envelope, detached.Envelope);
            Assert.Equal(resources.Envelope, NativePublicRewardResourceCondition.Create(evidence, detached).Envelope);
        }
    }

    [Theory]
    [InlineData(nameof(GoldenPearl))]
    [InlineData(nameof(NutritiousOyster))]
    public async Task PickupChangesOnlyNativeAssetsWithZeroRandomDrawOrPityChange(string relic)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("simple-neow-acquisition", new Overgrowth(), ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var neow = (Neow)ModelDb.Event<Neow>().MutableClone(); neow.AssignOwner(player); neow.BeginEvent(run);
        var before = NativePublicRunEvidence.Assets(player);
        Assert.Equal((56, 70, 99), (before.Hp, before.MaxHp, before.Gold));
        string rng = PublicJson.Serialize(new { Run = run.Rng.ToSerializable(), Player = player.PlayerRng.ToSerializable() });
        float rarity = player.Odds.CardRarity.CurrentValue, potion = player.Odds.PotionReward.CurrentValue;
        using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unexpected simple Neow acquisition RNG draw")))
            await RelicCmd.Obtain(relic == nameof(GoldenPearl) ? ModelDb.Relic<GoldenPearl>() : ModelDb.Relic<NutritiousOyster>(), player);
        Assert.Equal(rng, PublicJson.Serialize(new { Run = run.Rng.ToSerializable(), Player = player.PlayerRng.ToSerializable() }));
        Assert.Equal(rarity, player.Odds.CardRarity.CurrentValue); Assert.Equal(potion, player.Odds.PotionReward.CurrentValue);
        var after = NativePublicRunEvidence.Assets(player);
        var expected = JsonNode.Parse(PublicJson.Serialize(before))!;
        expected["hp"] = relic == nameof(GoldenPearl) ? 56 : 67;
        expected["maxHp"] = relic == nameof(GoldenPearl) ? 70 : 81;
        expected["gold"] = relic == nameof(GoldenPearl) ? 249 : 99;
        expected["relics"]!.AsArray().Add(JsonNode.Parse(PublicJson.Serialize(after.Relics[1])));
        Assert.Equal(expected.ToJsonString(), JsonNode.Parse(PublicJson.Serialize(after))!.ToJsonString());
        Assert.Equal(new[] { nameof(RingOfTheSnake), relic }, after.Relics.Select(r => r.Id));
        Assert.Equal(new Dictionary<string, int> { ["isMelted"] = 0, ["isUsedUp"] = 0, ["isWax"] = 0, ["stackCount"] = 1 },
            after.Relics[1].Details);
        Assert.Empty(after.Relics[1].Cards!); Assert.Null(after.Relics[1].SelectedModel);

        string[] hooks = [nameof(AbstractModel.ModifyGoldGained), nameof(AbstractModel.AfterGoldGained),
            nameof(AbstractModel.AfterCurrentHpChanged), nameof(AbstractModel.ModifyRewards),
            nameof(AbstractModel.BeforeCombatRewardOffered), nameof(AbstractModel.ShouldForcePotionReward),
            nameof(AbstractModel.ModifyCardRewardCreationOptions), nameof(AbstractModel.ModifyCardRewardCreationOptionsLate),
            nameof(AbstractModel.TryModifyCardRewardOptions), nameof(AbstractModel.TryModifyCardRewardOptionsLate),
            nameof(AbstractModel.TryModifyCardRewardOptionLate), nameof(AbstractModel.AfterModifyingCardRewardOptions),
            nameof(AbstractModel.TryModifyCardRewardAlternatives), nameof(AbstractModel.TryEnableCardRewardReroll)];
        foreach (var model in run.IterateHookListeners(null))
            foreach (var method in model.GetType().GetMethods().Where(m => hooks.Contains(m.Name)))
                Assert.Equal(typeof(AbstractModel), method.DeclaringType);
    }

    [Theory]
    [InlineData(24203UL)]
    [InlineData(24204UL)]
    public async Task MutatedOrMissingAcquisitionAndUnsupportedContextsRetainUniversalFallback(ulong seed)
    {
        var evidence = (await Root(Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1")))).PublicEvidence!;
        foreach (string change in new[] { "missing_assets", "noncompleted_end", "start_hp", "start_max_hp", "start_gold",
            "start_energy", "start_potion_slots", "start_potion", "start_orbs", "start_removals", "start_deck",
            "start_card_cost", "start_relic_state", "end_hp", "end_max_hp", "end_gold", "end_energy", "end_potion_slots",
            "end_potion", "end_orbs", "end_removals", "end_deck", "missing_relic", "extra_relic", "wrong_relic",
            "wax", "melted", "stacked", "used_up", "missing_status", "relic_cards", "selected_model", "later_modifier",
            "later_used_up", "extra_rewards", "wrong_neow", "wrong_act", "elite", "boss" })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray();
            var start = events[0]!["payload"]!["assets"]!; var end = events[4]!["payload"]!["assets"]!;
            var gained = end["relics"]![1]!;
            switch (change)
            {
                case "missing_assets": events[4]!["payload"]!["assets"] = null; break;
                case "noncompleted_end": events[4]!["payload"]!["outcome"] = "defeat"; break;
                case "start_hp": start["hp"] = 69; break;
                case "start_max_hp": start["maxHp"] = 71; break;
                case "start_gold": start["gold"] = 100; break;
                case "start_energy": start["maxEnergy"] = 4; break;
                case "start_potion_slots": start["potionSlots"] = 3; start["potions"]!.AsArray().Add((JsonNode?)null); break;
                case "start_potion": start["potions"]![0] = "LiquidMemories"; end["potions"]![0] = "LiquidMemories"; break;
                case "start_orbs": start["orbSlots"] = 1; break;
                case "start_removals": start["cardRemovalsUsed"] = 1; break;
                case "start_deck": start["deck"]![0]!["upgrade"] = 1; break;
                case "start_card_cost": start["deck"]![0]!["cost"] = 7; end["deck"]![0]!["cost"] = 7; break;
                case "start_relic_state": start["relics"]![0]!["details"]!["isUsedUp"] = 1;
                    end["relics"]![0]!["details"]!["isUsedUp"] = 1; break;
                case "end_hp": end["hp"] = end["hp"]!.GetValue<int>() - 1; break;
                case "end_max_hp": end["maxHp"] = end["maxHp"]!.GetValue<int>() + 1; break;
                case "end_gold": end["gold"] = end["gold"]!.GetValue<int>() + 1; break;
                case "end_energy": end["maxEnergy"] = 4; break;
                case "end_potion_slots": end["potionSlots"] = 3; end["potions"]!.AsArray().Add((JsonNode?)null); break;
                case "end_potion": end["potions"]![0] = "LiquidMemories"; break;
                case "end_orbs": end["orbSlots"] = 1; break;
                case "end_removals": end["cardRemovalsUsed"] = 1; break;
                case "end_deck": end["deck"]![0]!["upgrade"] = 1; break;
                case "missing_relic": end["relics"]!.AsArray().RemoveAt(1); break;
                case "extra_relic": end["relics"]!.AsArray().Add(gained.DeepClone()); break;
                case "wrong_relic": gained["id"] = "MawBank"; break;
                case "wax": gained["details"]!["isWax"] = 1; break;
                case "melted": gained["details"]!["isMelted"] = 1; break;
                case "stacked": gained["details"]!["stackCount"] = 2; break;
                case "used_up": gained["details"]!["isUsedUp"] = 1; break;
                case "missing_status": gained["details"]!.AsObject().Remove("isUsedUp"); break;
                case "relic_cards": gained["cards"]!.AsArray().Add(start["deck"]![0]!.DeepClone()); break;
                case "selected_model": gained["selectedModel"] = "Silent"; break;
                case "later_modifier":
                case "later_used_up":
                    var fact = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "combat_fact"
                        && e["payload"]!["assets"] is not null)!;
                    var later = fact["payload"]!["assets"]!["relics"]![1]!;
                    if (change == "later_modifier") later["id"] = "WhiteBeastStatue";
                    else later["details"]!["isUsedUp"] = 1;
                    break;
                case "extra_rewards":
                    var groups = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "offers")!["payload"]!["groups"]!.AsArray();
                    var extra = groups.First(g => g!["offers"]!.AsArray().Any(o => o!["offerKind"]!.GetValue<string>() == "card"))!.DeepClone();
                    extra["groupKind"] = "extra";
                    foreach (var offer in extra["offers"]!.AsArray()) offer!["key"] = "extra:0:" + offer["key"]!.GetValue<string>();
                    groups.Add(extra); break;
                case "wrong_neow":
                    string chosen = events[3]!["payload"]!["key"]!.GetValue<string>();
                    events[2]!["payload"]!["options"]!.AsArray().Single(o => o!["key"]!.GetValue<string>() == chosen)!["key"] = "NeowsTalisman";
                    events[3]!["payload"]!["key"] = "NeowsTalisman"; break;
                case "wrong_act": events[1]!["payload"]!["actIndex"] = 1; break;
                case "elite":
                case "boss":
                    var map = events[6]!["payload"]!; var coordinate = events[7]!["payload"]!["coordinate"]!.ToJsonString();
                    foreach (var node in map["nodes"]!.AsArray().Concat(map["currentMap"]!["nodes"]!.AsArray()))
                        if (node!["coordinate"]!.ToJsonString() == coordinate) node["nodeType"] = change;
                    if (change == "boss") map["currentMap"]!["bossNodes"]!.AsArray().Insert(0,
                        events[7]!["payload"]!["coordinate"]!.DeepClone());
                    break;
            }
            AssertUniversal(PublicRunEvidenceJson.Read(json.ToJsonString()), change);
        }
        foreach (bool gap in new[] { false, true })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray(); json["completeFromRunStart"] = false;
            if (gap) events[2]!["payload"] = new JsonObject { ["kind"] = "gap", ["reason"] = "observation_missing" };
            else events[1]!["payload"]!["completeFromOwnerStart"] = false;
            int through = gap ? 2 : 1; while (events.Count > through + 1) events.RemoveAt(events.Count - 1);
            var incomplete = PublicRunEvidenceJson.Read(json.ToJsonString());
            Assert.Throws<InvalidOperationException>(() => NativePublicRewardCondition.Create(incomplete));
        }
    }

    private static void AssertUniversal(PublicRunEvidence evidence, string change)
    {
        var cards = NativePublicRewardCondition.Create(evidence);
        Assert.NotEmpty(cards.Targets);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        foreach (var target in cards.Targets.Values)
        {
            Assert.True(target.PublicHistory is null, change);
            Assert.Equal(NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope, target.Certificate.Envelope);
            Assert.Null(resources.Targets[target.CombatIndex].Owner.PublicHistory);
        }
    }

    private static async Task<DecisionPacket> Root(NativeTapeRecipe recipe)
    {
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world); return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }

    private sealed class CheckOnDispose(Action check) : IDisposable
    { public void Dispose() => check(); }
}
