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

public sealed class NativePublicNewLeafRewardHistoryTests
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
    [InlineData(24104UL, "Blur", 2)]
    [InlineData(24105UL, "Tracking", 1)]
    public async Task NewLeafPublicHistoryMatchesNativePityBeforeEveryWitnessedReward(
        ulong seed, string replacement, int rewardCount)
    {
        // These exact development roots were already inspected in the retained
        // map-public-v4/fresh-twelve-response report. No fresh source search.
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var root = await Root(recipe);
        var evidence = root.PublicEvidence!;
        Assert.Equal("NewLeaf", Assert.IsType<PublicOptionChosen>(evidence.Events[3].Payload).Key);
        Assert.Equal("NewLeaf", Assert.IsType<PublicCardsObserved>(evidence.Events[5].Payload).Choice.Source);
        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Equal(rewardCount, cards.Targets.Count);
        Assert.All(cards.Targets.Values, target => Assert.NotNull(target.PublicHistory));
        Assert.All(resources.Targets.Values, target => Assert.Equal(new[] { (7, 15) }, target.GoldCertificate.Ranges));
        Assert.Equal(seed == 24105 ? "RadiantTincture" : null, resources.Targets[0].Potion);
        if (rewardCount == 2) Assert.Null(resources.Targets[1].Potion);

        // Observe a separate native hypothetical lifecycle. Only the detached
        // public condition supplies expected thresholds; no source odds enter it.
        Player? player = null;
        int transforms = 0, witnessed = 0;
        using (LabelCardTransformScope.Enter(context =>
        {
            Assert.Null(player); player = context.Original.Owner; transforms++;
            Assert.Same(player.RunState.Rng.Niche, context.Rng);
            Assert.False(context.IsInCombat);
            Assert.Equal(-0.05f, player.Odds.CardRarity.CurrentValue);
            Assert.Equal(0.4f, player.Odds.PotionReward.CurrentValue);
            var rewardsBefore = PublicJson.Serialize(player.PlayerRng.Rewards.ToSerializable());
            int nicheBefore = context.Rng.Counter;
            return new CheckOnDispose(() =>
            {
                Assert.Null(context.NativeFailure);
                Assert.Equal(replacement, context.CompletedCard!.GetType().Name);
                Assert.Equal(nicheBefore + 1, context.Rng.Counter);
                Assert.Equal(rewardsBefore, PublicJson.Serialize(player.PlayerRng.Rewards.ToSerializable()));
                Assert.Equal(-0.05f, player.Odds.CardRarity.CurrentValue);
                Assert.Equal(0.4f, player.Odds.PotionReward.CurrentValue);
            });
        }))
        using (LabelRewardResourceScope.Enter(context =>
        {
            Assert.NotNull(player);
            var target = cards.Targets[witnessed++];
            Assert.Equal(target.Floor, player.RunState.TotalFloor);
            Assert.Same(player.PlayerRng.Rewards, context.Rng);
            Assert.False(context.Forced);
            Assert.Equal(target.PublicHistory!.PotionThreshold, context.Threshold);
            Assert.Equal(target.PublicHistory.Thresholds[0],
                player.Odds.CardRarity.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
            return null;
        }, _ => null, _ => null))
        await using (var observed = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(observed);
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(observed.Observe()));
            Assert.Equal(1, transforms); Assert.Equal(rewardCount, witnessed);
        }

        // Existing owning proposals validate all three native per-card thresholds
        // and potion/gold seams, using independent proposal words on this fixed
        // hypothetical recipe. This is lifecycle evidence, not throughput data.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 713582UL };
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, hypothetical,
            publicRewardCondition: cards, publicResourceCondition: resources, expectedPublicEvidence: evidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, hypothetical, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(3 * rewardCount, tape.ConditionedPublicRewardCards);
        Assert.Equal(rewardCount, tape.ConditionedResourcePresence);
        Assert.Equal(rewardCount, tape.ConditionedResourceGold);
        Assert.Equal(seed == 24105 ? 1 : 0, tape.ConditionedResourcePotions);
        Assert.Equal(cards.Envelope, tape.PublicRewardRatio);
        Assert.Equal(resources.Envelope, tape.PublicResourceRatio);
    }

    [Fact]
    public async Task NewLeafStillDeclinesGapsModifiersAndUnreviewedOwnerPaths()
    {
        var root = await Root(Prior.Draw(new Rng(24104, "nosl-native-tape-source-draw-v1")));
        var evidence = root.PublicEvidence!;
        var original = NativePublicRewardCondition.Create(evidence);
        Assert.Equal(2, original.Targets.Count);
        foreach (string variant in new[] { "initial_gap", "later_gap", "foreign_child", "incomplete_child",
            "unreviewed_neow", "potion_modifier", "card_modifier", "later_modifier", "extra_rewards",
            "wax", "melted", "stacked", "missing_status" })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var entries = json["events"]!.AsArray();
            var initialRelic = entries[8]!["payload"]!["assets"]!["relics"]![1]!;
            switch (variant)
            {
                case "initial_gap":
                case "later_gap":
                    long combat = variant == "initial_gap" ? 3 : 6;
                    var fact = entries.First(e => e!["ownerOrdinal"]?.GetValue<long>() == combat
                        && e["payload"]!["kind"]!.GetValue<string>() == "combat_fact")!;
                    fact["payload"] = new JsonObject { ["kind"] = "gap", ["reason"] = "observation_missing" };
                    break;
                case "foreign_child": entries[4]!["payload"]!["parentOwnerOrdinal"] = null; break;
                case "incomplete_child": entries[4]!["payload"]!["completeFromOwnerStart"] = false; break;
                case "unreviewed_neow":
                    entries[2]!["payload"]!["options"]!.AsArray()
                        .Single(o => o!["key"]!.GetValue<string>() == "NewLeaf")!["key"] = "ArcaneScroll";
                    entries[3]!["payload"]!["key"] = "ArcaneScroll";
                    break;
                case "potion_modifier": initialRelic["id"] = "WhiteBeastStatue"; break;
                case "card_modifier": initialRelic["id"] = "PrismaticGem"; break;
                case "later_modifier":
                    var end = entries.First(e => e!["ownerOrdinal"]?.GetValue<long>() == 4
                        && e["payload"]!["kind"]!.GetValue<string>() == "owner_ended")!;
                    end["payload"]!["assets"]!["relics"]![1]!["id"] = "WhiteBeastStatue";
                    break;
                case "wax": initialRelic["details"]!["isWax"] = 1; break;
                case "melted": initialRelic["details"]!["isMelted"] = 1; break;
                case "stacked": initialRelic["details"]!["stackCount"] = 2; break;
                case "missing_status": initialRelic["details"]!.AsObject().Remove("stackCount"); break;
                case "extra_rewards":
                    var groups = entries[(int)original.Targets[0].OfferEventOrdinal]!["payload"]!["groups"]!.AsArray();
                    var extra = groups.Single(g => g!["offers"]!.AsArray()
                        .Any(o => o!["offerKind"]!.GetValue<string>() == "card"))!.DeepClone();
                    extra["groupKind"] = "extra";
                    foreach (var offer in extra["offers"]!.AsArray())
                        offer!["key"] = "extra:0:" + offer["key"]!.GetValue<string>();
                    groups.Add(extra);
                    break;
            }
            bool incomplete = variant is "initial_gap" or "later_gap" or "incomplete_child";
            if (incomplete)
            {
                json["completeFromRunStart"] = false;
                // End at the missing boundary instead of retaining later
                // decision snapshots which claim their old complete history.
                int through = variant == "incomplete_child" ? 4 : entries.ToList().FindIndex(e =>
                    e!["payload"]!["kind"]!.GetValue<string>() == "gap");
                while (entries.Count > through + 1) entries.RemoveAt(entries.Count - 1);
            }
            var changed = PublicRunEvidenceJson.Read(json.ToJsonString());
            if (incomplete)
            {
                // The enclosing public proposal rejects incomplete evidence
                // before the optional history certificate can tighten it.
                Assert.Throws<InvalidOperationException>(() => NativePublicRewardCondition.Create(changed));
                continue;
            }
            var fallback = NativePublicRewardCondition.Create(changed);
            var resources = NativePublicRewardResourceCondition.Create(changed, fallback);
            Assert.Equal(2, fallback.Targets.Count);
            foreach (var target in fallback.Targets.Values)
            {
                if (variant == "later_modifier" && target.CombatIndex == 0)
                { Assert.NotNull(target.PublicHistory); continue; }
                Assert.Null(target.PublicHistory);
                Assert.Equal(NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), target.BaseCardIds).Envelope,
                    target.Certificate.Envelope);
                Assert.True(resources.Targets[target.CombatIndex].GoldCertificate.Ranges.Count > 1);
            }
        }
    }

    private static async Task<DecisionPacket> Root(NativeTapeRecipe recipe)
    {
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }

    private sealed class CheckOnDispose(Action check) : IDisposable
    { public void Dispose() => check(); }
}
