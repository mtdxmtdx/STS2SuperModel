using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicContextualNeowHistoryTests
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
    [InlineData(24108UL, nameof(LavaRock))]
    [InlineData(24109UL, nameof(NeowsTorment))]
    public async Task FixedInspectedRootsMatchNativePityGoldAndCompletedRootFixedRatios(ulong seed, string relic)
    {
        // The exact v6 inspected-twelve roots already measured by the parent.
        // Fixed-recipe lifecycle tests are not new source or posterior searches.
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var root = await Root(recipe);
        var evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(root.PublicEvidence!));
        Assert.Equal(relic, Assert.IsType<PublicOptionChosen>(evidence.Events[3].Payload).Key);
        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        var target = Assert.Single(cards.Targets).Value;
        Assert.NotNull(target.PublicHistory);
        Assert.Equal(new[] { (7, 15) }, Assert.Single(resources.Targets).Value.GoldCertificate.Ranges);
        Assert.True(cards.Envelope.Numerator * NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope.Denominator
            < cards.Envelope.Denominator * NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope.Numerator);

        Player? player = null; int presences = 0, golds = 0;
        using (LabelMerchantScope.Enter(context => { player = context.Player; return null; }, _ => null))
        using (LabelRewardResourceScope.Enter(context =>
        {
            Assert.NotNull(player); presences++;
            Assert.Equal(target.Floor, player.RunState.TotalFloor);
            Assert.Equal(RoomType.Monster, context.RoomType); Assert.False(context.Forced);
            Assert.Equal(target.PublicHistory!.PotionThreshold, context.Threshold);
            Assert.Equal(target.PublicHistory.Thresholds[0],
                player.Odds.CardRarity.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
            if (relic == nameof(LavaRock)) Assert.False(Assert.Single(player.Relics.OfType<LavaRock>()).HasTriggered);
            return null;
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
            Assert.Equal(1, presences); Assert.Equal(1, golds);
        }
        foreach (ulong salt in new[] { 713582UL, 38477UL })
        {
            var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ salt };
            var tape = NativeLabelTape.ForDeclaredPrior(Prior, hypothetical,
                publicRewardCondition: cards, publicResourceCondition: resources, expectedPublicEvidence: evidence);
            await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, hypothetical, tape);
            Assert.NotNull(world); tape.ValidateProposalCompletion();
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            Assert.Equal(3, tape.ConditionedPublicRewardCards);
            Assert.Equal(1, tape.ConditionedResourcePresence); Assert.Equal(1, tape.ConditionedResourceGold);
            Assert.Equal(cards.Envelope, tape.PublicRewardRatio); Assert.Equal(resources.Envelope, tape.PublicResourceRatio);
            var detached = NativePublicRewardCondition.Create(PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence)));
            Assert.Equal(cards.Envelope, detached.Envelope);
            Assert.Equal(resources.Envelope, NativePublicRewardResourceCondition.Create(evidence, detached).Envelope);
        }
    }

    [Theory]
    [InlineData(nameof(LavaRock))]
    [InlineData(nameof(NeowsTorment))]
    public async Task InitialAcquisitionUsesNoRandomDrawAndNeowsFuryHasNoRewardOrAcquisitionHook(string relic)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("contextual-neow-acquisition", new Overgrowth());
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var before = player.Deck.Cards.Select(PublicCardDetailsBuilder.Card).ToArray();
        string rng = PublicJson.Serialize(new { Run = run.Rng.ToSerializable(), Player = player.PlayerRng.ToSerializable() });
        float rarity = player.Odds.CardRarity.CurrentValue, potion = player.Odds.PotionReward.CurrentValue;
        using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unexpected Neow acquisition RNG draw")))
            await RelicCmd.Obtain(relic == nameof(LavaRock) ? ModelDb.Relic<LavaRock>() : ModelDb.Relic<NeowsTorment>(), player);
        Assert.Equal(rng, PublicJson.Serialize(new { Run = run.Rng.ToSerializable(), Player = player.PlayerRng.ToSerializable() }));
        Assert.Equal(rarity, player.Odds.CardRarity.CurrentValue); Assert.Equal(potion, player.Odds.PotionReward.CurrentValue);
        Assert.Equal(before.Length + (relic == nameof(NeowsTorment) ? 1 : 0), player.Deck.Cards.Count);
        if (relic == nameof(NeowsTorment))
            Assert.Equal(PublicJson.Serialize(PublicCardDetailsBuilder.Card((NeowsFury)ModelDb.Card<NeowsFury>().MutableClone())),
                PublicJson.Serialize(PublicCardDetailsBuilder.Card(Assert.Single(player.Deck.Cards.OfType<NeowsFury>()))));

        string[] hooks = [nameof(AbstractModel.ModifyRewards), nameof(AbstractModel.BeforeCombatRewardOffered),
            nameof(AbstractModel.ShouldForcePotionReward), nameof(AbstractModel.ModifyCardRewardCreationOptions),
            nameof(AbstractModel.ModifyCardRewardCreationOptionsLate), nameof(AbstractModel.TryModifyCardRewardOptions),
            nameof(AbstractModel.TryModifyCardRewardOptionsLate), nameof(AbstractModel.TryModifyCardRewardOptionLate),
            nameof(AbstractModel.AfterModifyingCardRewardOptions), nameof(AbstractModel.TryModifyCardRewardAlternatives),
            nameof(AbstractModel.TryEnableCardRewardReroll), nameof(AbstractModel.TryModifyCardBeingAddedToDeck),
            nameof(AbstractModel.AfterCardChangedPiles)];
        foreach (var model in player.Relics.Cast<AbstractModel>().Concat(player.Deck.Cards))
            foreach (var method in model.GetType().GetMethods().Where(m => hooks.Contains(m.Name)))
                Assert.Equal(model is LavaRock && method.Name == nameof(AbstractModel.ModifyRewards)
                    ? typeof(LavaRock) : typeof(AbstractModel), method.DeclaringType);
    }

    [Fact]
    public async Task LavaRockNativeOverrideIsNeutralForMonsterAndActiveForActZeroBoss()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("contextual-lava-rock", new Overgrowth());
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        await RelicCmd.Obtain(ModelDb.Relic<LavaRock>(), player);
        var relic = Assert.Single(player.Relics.OfType<LavaRock>());
        // Even a native Boss current room cannot activate the hook when the
        // supplied reward context is Monster, which every accepted target proves.
        run.PushRoom(new CombatRoom((Func<MonsterModel>)(() => throw new InvalidOperationException()), RoomType.Boss));
        var rewards = new List<Reward>(); int before = player.PlayerRng.Rewards.Counter;
        using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unexpected inactive LavaRock draw")))
            Hook.ModifyRewards(run, player, rewards, RoomType.Monster);
        Assert.Empty(rewards); Assert.False(relic.HasTriggered); Assert.Equal(before, player.PlayerRng.Rewards.Counter);
        Hook.ModifyRewards(run, player, rewards, RoomType.Boss);
        Assert.Equal(2, rewards.Count); Assert.All(rewards, r => Assert.IsType<RelicReward>(r));
        Assert.True(relic.HasTriggered); Assert.Equal(before + 2, player.PlayerRng.Rewards.Counter);
    }

    [Theory]
    [InlineData(24108UL)]
    [InlineData(24109UL)]
    public async Task IncompleteAcquisitionModifiersAndNonWeakContextsRetainUniversalFallback(ulong seed)
    {
        var evidence = (await Root(Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1")))).PublicEvidence!;
        var changes = new List<string> { "missing_assets", "changed_deck", "wrong_neow", "extra_relic", "wax", "melted", "stacked",
            "missing_status", "boss", "elite", "wrong_act", "extra_rewards", "modifier" };
        if (seed == 24108) changes.AddRange(["triggered", "used_up", "missing_triggered"]);
        else changes.AddRange(["missing_fury", "upgraded_fury", "wrong_fury"]);
        foreach (string change in changes)
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray(); var assets = events[4]!["payload"]!["assets"]!;
            var relic = assets["relics"]![1]!; var deck = assets["deck"]!.AsArray();
            var fury = deck.FirstOrDefault(c => c!["id"]!.GetValue<string>() == nameof(NeowsFury));
            switch (change)
            {
                case "missing_assets": events[4]!["payload"]!["assets"] = null; break;
                case "changed_deck": deck[0]!["upgrade"] = 1; break;
                case "wrong_neow":
                    events[2]!["payload"]!["options"]![0]!["key"] = "NeowsTalisman";
                    events[3]!["payload"]!["key"] = "NeowsTalisman"; break;
                case "extra_relic": assets["relics"]!.AsArray().Add(relic.DeepClone()); break;
                case "wax": relic["details"]!["isWax"] = 1; break;
                case "melted": relic["details"]!["isMelted"] = 1; break;
                case "stacked": relic["details"]!["stackCount"] = 2; break;
                case "missing_status": relic["details"]!.AsObject().Remove("stackCount"); break;
                case "triggered": relic["details"]!["hasTriggered"] = 1; break;
                case "used_up": relic["details"]!["isUsedUp"] = 1; break;
                case "missing_triggered": relic["details"]!.AsObject().Remove("hasTriggered"); break;
                case "missing_fury": deck.Remove(fury); break;
                case "upgraded_fury": fury!["upgrade"] = 1; break;
                case "wrong_fury": fury!["id"] = "Adrenaline"; break;
                case "boss":
                case "elite":
                    var map = events[6]!["payload"]!; var chosen = events[7]!["payload"]!["coordinate"]!.ToJsonString();
                    foreach (var node in map["nodes"]!.AsArray().Concat(map["currentMap"]!["nodes"]!.AsArray()))
                        if (node!["coordinate"]!.ToJsonString() == chosen) node["nodeType"] = change;
                    if (change == "boss") map["currentMap"]!["bossNodes"]!.AsArray()
                        .Insert(0, events[7]!["payload"]!["coordinate"]!.DeepClone());
                    break;
                case "wrong_act":
                    foreach (var entry in events.Skip(5).Where(e => e!["payload"]!["kind"]!.GetValue<string>() == "owner_started"))
                        entry!["payload"]!["actIndex"] = 1;
                    break;
                case "extra_rewards":
                    var groups = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "offers")!["payload"]!["groups"]!.AsArray();
                    var extra = groups.First(g => g!["offers"]!.AsArray().Any(o => o!["offerKind"]!.GetValue<string>() == "card"))!.DeepClone();
                    extra["groupKind"] = "extra";
                    foreach (var offer in extra["offers"]!.AsArray()) offer!["key"] = "extra:0:" + offer["key"]!.GetValue<string>();
                    groups.Add(extra); break;
                case "modifier":
                    var snapshot = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "combat_fact"
                        && e["payload"]!["assets"] is not null)!;
                    snapshot["payload"]!["assets"]!["relics"]![1]!["id"] = "WhiteBeastStatue"; break;
            }
            AssertUniversal(PublicRunEvidenceJson.Read(json.ToJsonString()));
        }
        foreach (bool gap in new[] { false, true })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!; var events = json["events"]!.AsArray();
            json["completeFromRunStart"] = false;
            if (gap) events[2]!["payload"] = new JsonObject { ["kind"] = "gap", ["reason"] = "observation_missing" };
            else events[1]!["payload"]!["completeFromOwnerStart"] = false;
            int through = gap ? 2 : 1; while (events.Count > through + 1) events.RemoveAt(events.Count - 1);
            var incomplete = PublicRunEvidenceJson.Read(json.ToJsonString());
            Assert.Throws<InvalidOperationException>(() => NativePublicRewardCondition.Create(incomplete));
        }
    }

    private static void AssertUniversal(PublicRunEvidence evidence)
    {
        var cards = NativePublicRewardCondition.Create(evidence); Assert.Single(cards.Targets);
        var target = cards.Targets[0]; Assert.Null(target.PublicHistory);
        Assert.Equal(NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope, target.Certificate.Envelope);
        Assert.Null(NativePublicRewardResourceCondition.Create(evidence, cards).Targets[0].Owner.PublicHistory);
    }
    private static async Task<DecisionPacket> Root(NativeTapeRecipe recipe)
    {
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world); return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }
}
