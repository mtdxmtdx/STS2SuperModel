using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicNeutralBoundaryHistoryTests
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
    [InlineData(24004UL, "ScrollBoxes", 1)]
    [InlineData(24101UL, "Pomander", 1)]
    [InlineData(24103UL, "ArcaneScroll", 2)]
    [InlineData(24008UL, "PreciseScissors", 2)]
    public async Task ReviewedBoundariesMatchNativePityAndCompletedRootFixedMasses(ulong seed, string neow, int count)
    {
        // Fixed roots already inspected in map-public-v4/v5. This is native
        // lifecycle verification, not a new source search or posterior probe.
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var root = await Root(recipe);
        var evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(root.PublicEvidence!));
        Assert.Equal(neow, Assert.IsType<PublicOptionChosen>(evidence.Events[3].Payload).Key);
        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Equal(count, cards.Targets.Count);
        Assert.All(cards.Targets.Values, t => Assert.NotNull(t.PublicHistory));
        Assert.All(resources.Targets.Values, t => Assert.Equal(new[] { (7, 15) }, t.GoldCertificate.Ranges));

        Player? player = null;
        int presences = 0, golds = 0, shops = 0;
        using (LabelMerchantScope.Enter(context => { player = context.Player; return null; }, context =>
        {
            Assert.Same(player, context.Player); shops++;
            float rarity = player!.Odds.CardRarity.CurrentValue, potion = player.Odds.PotionReward.CurrentValue;
            return new CheckOnDispose(() =>
            {
                Assert.NotNull(context.CompletedInventory);
                Assert.Equal(rarity, player.Odds.CardRarity.CurrentValue);
                Assert.Equal(potion, player.Odds.PotionReward.CurrentValue);
            });
        }))
        using (LabelRewardResourceScope.Enter(context =>
        {
            Assert.NotNull(player);
            var target = cards.Targets[presences++];
            Assert.Equal(target.Floor, player.RunState.TotalFloor);
            Assert.False(context.Forced);
            Assert.Same(player.PlayerRng.Rewards, context.Rng);
            Assert.Equal(target.PublicHistory!.PotionThreshold, context.Threshold);
            Assert.Equal(target.PublicHistory.Thresholds[0],
                player.Odds.CardRarity.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
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
            Assert.Equal(count, presences); Assert.Equal(count, golds); Assert.Equal(seed == 24008 ? 1 : 0, shops);
        }

        foreach (ulong salt in new[] { 713582UL, 38477UL })
        {
            // Only the detached public evidence enters these certificates.
            // Different owned hypothetical proposal words cannot alter their factors.
            var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ salt };
            var tape = NativeLabelTape.ForDeclaredPrior(Prior, hypothetical,
                publicRewardCondition: cards, publicResourceCondition: resources, expectedPublicEvidence: evidence);
            await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, hypothetical, tape);
            Assert.NotNull(world); tape.ValidateProposalCompletion();
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            Assert.Equal(3 * count, tape.ConditionedPublicRewardCards);
            Assert.Equal(count, tape.ConditionedResourcePresence); Assert.Equal(count, tape.ConditionedResourceGold);
            Assert.Equal(cards.Envelope, tape.PublicRewardRatio);
            Assert.Equal(resources.Envelope, tape.PublicResourceRatio);
            var detached = NativePublicRewardCondition.Create(PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence)));
            Assert.Equal(cards.Envelope, detached.Envelope);
            Assert.Equal(resources.Envelope, NativePublicRewardResourceCondition.Create(evidence, detached).Envelope);
        }
    }

    [Theory]
    [InlineData(24004UL)]
    [InlineData(24101UL)]
    [InlineData(24103UL)]
    public async Task MissingChangedOrModifiedNeowBoundariesKeepUniversalEnvelopes(ulong seed)
    {
        var evidence = (await Root(Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1")))).PublicEvidence!;
        int end = seed == 24103 ? 4 : 8;
        var changes = new List<string> { "missing_assets", "missing_grant", "changed_grant", "foreign_relic", "wax", "melted", "stacked", "missing_status" };
        if (seed != 24103) changes.AddRange(["wrong_source", "foreign_child", "wrong_selection", "partial_candidates"]);
        if (seed == 24004) changes.AddRange(["partial_bundle", "wrong_rarity", "upgraded_bundle"]);
        foreach (string change in changes)
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray();
            var assets = events[end]!["payload"]!["assets"]!;
            var relic = assets["relics"]![1]!;
            var deck = assets["deck"]!.AsArray();
            var choice = seed == 24103 ? null : events[5]!["payload"]!["choice"]!;
            switch (change)
            {
                case "missing_assets": events[end]!["payload"]!["assets"] = null; break;
                case "missing_grant": deck.RemoveAt(seed == 24101 ? deck.ToList().FindIndex(c => c!["upgrade"]!.GetValue<int>() == 1) : deck.Count - 1); break;
                case "changed_grant": deck[0]!["upgrade"] = 1; break;
                case "foreign_relic": relic["id"] = "WhiteBeastStatue"; break;
                case "wax": relic["details"]!["isWax"] = 1; break;
                case "melted": relic["details"]!["isMelted"] = 1; break;
                case "stacked": relic["details"]!["stackCount"] = 2; break;
                case "missing_status": relic["details"]!.AsObject().Remove("stackCount"); break;
                case "wrong_source": choice!["source"] = "NewLeaf"; break;
                case "foreign_child": events[4]!["payload"]!["parentOwnerOrdinal"] = null; break;
                case "wrong_selection": events[6]!["payload"]!["selection"]![0] = seed == 24004 ? 1 : 5; break;
                case "partial_candidates":
                    choice!["candidates"]!.AsArray().RemoveAt(0);
                    if (seed == 24004) choice["bundles"]!.AsArray().RemoveAt(0);
                    break;
                case "partial_bundle": choice!["bundles"]![1]!.AsArray().RemoveAt(2); break;
                case "wrong_rarity": choice!["bundles"]![1]![2]!["id"] = "Adrenaline"; break;
                case "upgraded_bundle": choice!["bundles"]![1]![2]!["upgrade"] = 1; break;
            }
            AssertUniversal(PublicRunEvidenceJson.Read(json.ToJsonString()));
        }
    }

    [Fact]
    public async Task PurchasesIncompleteStockChangedAssetsAndMerchantModifiersStopOnlyTheShopSuffix()
    {
        var evidence = (await Root(Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1")))).PublicEvidence!;
        var entry = evidence.Events.Single(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Shop });
        int at = (int)entry.EventOrdinal;
        Assert.True(NativePublicShopLeaveBoundary.TryCreate(evidence.Events, entry, out _, out _));
        foreach (string change in new[] { "purchase", "missing_before_assets", "missing_after_assets", "changed_gold", "changed_deck",
            "partial_stock", "upgraded_stock", "foreign_parent", "merchant_modifier", "room_modifier", "refresh" })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray();
            var before = events[at - 1]!["payload"]!["assets"]!;
            var after = events[at + 3]!["payload"]!["assets"]!;
            var stock = events[at + 1]!["payload"]!["groups"]![0]!["offers"]!.AsArray();
            switch (change)
            {
                case "purchase": events[at + 2]!["payload"]!["key"] = "card:0"; break;
                case "missing_before_assets": events[at - 1]!["payload"]!["assets"] = null; break;
                case "missing_after_assets": events[at + 3]!["payload"]!["assets"] = null; break;
                case "changed_gold": after["gold"] = after["gold"]!.GetValue<int>() - 1; break;
                case "changed_deck": after["deck"]![0]!["upgrade"] = 1; break;
                case "partial_stock": stock.RemoveAt(6); break;
                case "upgraded_stock": stock[0]!["card"]!["upgrade"] = 1; break;
                case "foreign_parent": events[at]!["payload"]!["parentOwnerOrdinal"] = 8; break;
                case "merchant_modifier":
                case "room_modifier":
                    string modifier = change == "merchant_modifier" ? "MoltenEgg" : "MealTicket";
                    before["relics"]![1]!["id"] = modifier;
                    after["relics"]![1]!["id"] = modifier;
                    break;
                case "refresh":
                    var refresh = events[at + 1]!.DeepClone();
                    refresh["payload"]!["replacesOfferEventOrdinal"] = (long)at + 1;
                    events.Insert(at + 2, refresh);
                    for (int i = at + 2; i < events.Count; i++)
                    {
                        events[i]!["eventOrdinal"] = i;
                        var payload = events[i]!["payload"]!;
                        foreach (string key in new[] { "offerEventOrdinal", "replacesOfferEventOrdinal",
                            "historyThroughEventOrdinal", "decisionEventOrdinal" })
                            if (payload[key] is { } ordinal && ordinal.GetValue<long>() >= at + 2)
                                payload[key] = ordinal.GetValue<long>() + 1;
                    }
                    events[at + 3]!["payload"]!["offerEventOrdinal"] = at + 2;
                    break;
            }
            if (change == "foreign_parent")
            {
                // A Shop cannot become a child of its already closed map owner.
                // The public DTO rejects that malformed ancestry before history.
                Assert.Throws<ArgumentException>(() => PublicRunEvidenceJson.Read(json.ToJsonString()));
                continue;
            }
            var changed = PublicRunEvidenceJson.Read(json.ToJsonString());
            Assert.False(NativePublicShopLeaveBoundary.TryCreate(changed.Events, changed.Events[at], out _, out _));
            AssertUniversal(changed, preserveFirst: true);
        }
    }

    [Theory]
    [InlineData(24004UL)]
    [InlineData(24101UL)]
    [InlineData(24103UL)]
    [InlineData(24008UL)]
    public async Task GapsAndIncompleteNeutralOwnersCannotAdmitPublicRewardHistory(ulong seed)
    {
        var evidence = (await Root(Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1")))).PublicEvidence!;
        int at = seed == 24008 ? (int)evidence.Events.Single(e =>
            e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Shop }).EventOrdinal : seed == 24103 ? 1 : 4;
        foreach (bool gap in new[] { false, true })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var events = json["events"]!.AsArray();
            json["completeFromRunStart"] = false;
            int through = gap ? at + 1 : at;
            if (gap) events[through]!["payload"] = new JsonObject { ["kind"] = "gap", ["reason"] = "observation_missing" };
            else events[at]!["payload"]!["completeFromOwnerStart"] = false;
            while (events.Count > through + 1) events.RemoveAt(events.Count - 1);
            var incomplete = PublicRunEvidenceJson.Read(json.ToJsonString());
            Assert.Throws<InvalidOperationException>(() => NativePublicRewardCondition.Create(incomplete));
        }
    }

    private static void AssertUniversal(PublicRunEvidence evidence, bool preserveFirst = false)
    {
        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.NotEmpty(cards.Targets);
        foreach (var target in cards.Targets.Values)
        {
            if (preserveFirst && target.CombatIndex == 0) { Assert.NotNull(target.PublicHistory); continue; }
            Assert.Null(target.PublicHistory);
            Assert.Equal(NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope, target.Certificate.Envelope);
            Assert.True(resources.Targets[target.CombatIndex].GoldCertificate.Ranges.Count > 1);
        }
    }

    private static async Task<DecisionPacket> Root(NativeTapeRecipe recipe)
    {
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }
    private sealed class CheckOnDispose(Action check) : IDisposable { public void Dispose() => check(); }
}
