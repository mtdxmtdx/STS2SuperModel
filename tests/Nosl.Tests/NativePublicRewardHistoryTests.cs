using System.Numerics;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicRewardHistoryTests
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
    [InlineData(24002UL)]
    [InlineData(24007UL)]
    public async Task CompletePublicWeakHistoryFixesEveryCardAndResourceMass(ulong sourceSeed)
    {
        // Two already inspected development sources. These are native lifecycle
        // regressions, not fresh posterior draws or acceptance-rate evidence.
        var recipe = Prior.Draw(new Rng(sourceSeed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        var cards = NativePublicRewardCondition.Create(root.PublicEvidence!);
        var resources = NativePublicRewardResourceCondition.Create(root.PublicEvidence!, cards);
        Assert.Equal(new[] { 0, 1 }, cards.Targets.Keys);
        Assert.All(cards.Targets.Values, t => Assert.NotNull(t.PublicHistory));
        Assert.All(resources.Targets.Values, t => Assert.Equal(new[] { (7, 15) }, t.GoldCertificate.Ranges));
        var oldCards = cards.Targets.Values.Aggregate(new ShuffleRational(1, 1), (mass, t) =>
        {
            var old = NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), t.BaseCardIds).Envelope;
            return mass.Multiply(old.Numerator, old.Denominator);
        });
        Assert.True(cards.Envelope.Numerator * oldCards.Denominator * 50 < cards.Envelope.Denominator * oldCards.Numerator);
        var proposalRecipe = recipe with { ProposalSeed = recipe.ProposalSeed ^ 713582UL };
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, proposalRecipe, publicRewardCondition: cards,
            publicResourceCondition: resources, expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, proposalRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(cards.Envelope, tape.PublicRewardRatio);
        Assert.Equal(resources.Envelope, tape.PublicResourceRatio);

        // An unreviewed intervening event keeps the already proved first target,
        // then retains the previous universal envelope for the unknown suffix.
        var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(root.PublicEvidence!))!;
        var entries = json["events"]!.AsArray();
        var eventStart = entries.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "owner_started"
            && e["payload"]!["ownerKind"]!.GetValue<string>() == "event" && e["payload"]!["floor"]!.GetValue<int>() == 3)!;
        long owner = eventStart["ownerOrdinal"]!.GetValue<long>();
        foreach (var entry in entries.Where(e => e!["ownerOrdinal"]?.GetValue<long>() == owner))
        {
            var p = entry!["payload"]!;
            if (p["kind"]!.GetValue<string>() == "options")
                p["options"]![0]!["key"] = "UNREVIEWED";
            if (p["kind"]!.GetValue<string>() == "option_chosen") p["key"] = "UNREVIEWED";
        }
        var changed = PublicRunEvidenceJson.Read(json.ToJsonString());
        var fallback = NativePublicRewardCondition.Create(changed);
        Assert.NotNull(fallback.Targets[0].PublicHistory); Assert.Null(fallback.Targets[1].PublicHistory);
        Assert.Equal(NativeRewardPoolCertificate.Create(ModelDb.Character<Silent>(), fallback.Targets[1].BaseCardIds).Envelope,
            fallback.Targets[1].Certificate.Envelope);
    }

    [Fact]
    public async Task PublicOffsetBeforeShopIncludesGorgeAndStopsAtTheShop()
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        var evidence = root.PublicEvidence!;
        var cards = NativePublicRewardCondition.Create(evidence);
        Assert.NotNull(cards.Targets[0].PublicHistory);
        Assert.Null(cards.Targets[1].PublicHistory); // Shop remains a conservative suffix boundary.
        Assert.True(NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(evidence, 9, out float offset));
        var first = cards.Targets[0];
        float expected = -0.05f;
        var pool = ModelDb.Character<Silent>().CardPool.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false);
        foreach (string id in first.BaseCardIds)
            expected = NativePublicRewardHistoryCertificate.AdvanceRarity(expected,
                pool.Single(c => c.GetType().Name == id).Rarity, 0.005f);
        Assert.Equal(expected, offset);
        Assert.False(NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(evidence, 11, out _));
        Assert.False(NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(evidence, 4, out _));
        Assert.False(NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(evidence, 7, out _));

        foreach (bool extra in new[] { false, true })
        {
            var json = JsonNode.Parse(PublicRunEvidenceJson.Serialize(evidence))!;
            var entries = json["events"]!.AsArray();
            if (extra)
            {
                // TheHunt's native pending rewards become an Extra group in the
                // first snapshot. A card without a reward hook must not evade it.
                var payload = entries[(int)first.OfferEventOrdinal]!["payload"]!;
                var card = payload["groups"]!.AsArray().SelectMany(g => g!["offers"]!.AsArray())
                    .First(o => o!["offerKind"]!.GetValue<string>() == "card")!.DeepClone();
                card["key"] = "extra:0:card:0";
                payload["groups"]!.AsArray().Add(new JsonObject
                {
                    ["groupKind"] = "extra", ["selectionMode"] = "choose_one", ["alternativeToGroupIndex"] = null,
                    ["offers"] = new JsonArray(card),
                });
            }
            else
            {
                var refresh = entries.Skip((int)first.OfferEventOrdinal + 1).First(e =>
                    e!["payload"]!["kind"]!.GetValue<string>() == "offers")!;
                var payload = refresh["payload"]!;
                payload["replacesOfferEventOrdinal"] = first.OfferEventOrdinal;
                payload["groups"]!.AsArray().First(g => g!["offers"]!.AsArray()
                    .Any(o => o!["offerKind"]!.GetValue<string>() == "card"))!["groupKind"] = "reroll";
            }
            var changed = PublicRunEvidenceJson.Read(json.ToJsonString());
            var fallback = NativePublicRewardCondition.Create(changed);
            if (extra) Assert.Null(fallback.Targets[0].PublicHistory);
            else Assert.NotNull(fallback.Targets[0].PublicHistory); // Earlier first-offer proof survives.
            Assert.False(NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(changed, 9, out _));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void FinitePublicRarityMassAndTransitionsMatchNativeRolls(int bits)
    {
        NaturalSourceCollector.InitializeNativeModels();
        CardModel[] pool = [ModelDb.Card<Backflip>(), ModelDb.Card<DaggerSpray>(), ModelDb.Card<Blur>(), ModelDb.Card<Adrenaline>()];
        string[] ids = [nameof(Blur), nameof(Backflip), nameof(DaggerSpray)];
        float offset = -0.05f;
        var thresholds = new List<LabelCardRarityThresholds>();
        for (int slot = 0; slot < ids.Length; slot++)
        {
            var odds = new CardRarityOdds(offset, new Rng(13), new AscensionManager(10));
            thresholds.Add(odds.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
            offset = NativePublicRewardHistoryCertificate.AdvanceRarity(offset,
                pool.Single(c => c.GetType().Name == ids[slot]).Rarity, odds.RarityGrowth);
        }
        var certificate = NativeRewardPoolCertificate.FromPublicHistory(pool, ids, thresholds, bits);
        int domain = 1 << bits;
        CardModel[] remaining = pool;
        float current = -0.05f;
        var total = new ShuffleRational(1, 1);
        for (int slot = 0; slot < ids.Length; slot++)
        {
            int matches = 0;
            for (int rarityWord = 0; rarityWord < domain; rarityWord++)
            for (int pickWord = 0; pickWord < domain; pickWord++)
            {
                var native = new CardRarityOdds(current, new Rng(11), new AscensionManager(10));
                var words = new Queue<ulong>([(ulong)rarityWord << (64 - bits), (ulong)pickWord << (64 - bits)]);
                using var scope = LabelRandomScope.Enter(_ => words.Dequeue());
                var rarity = native.Roll(CardRarityOddsType.RegularEncounter);
                Assert.Equal(NativePublicRewardHistoryCertificate.AdvanceRarity(current, rarity, native.RarityGrowth), native.CurrentValue);
                var candidates = remaining.Where(c => c.Rarity == rarity).ToArray();
                var picked = new Rng(19).NextItem(candidates);
                if (picked?.GetType().Name == ids[slot]) matches++;
            }
            var mass = new ShuffleRational(matches, domain * domain);
            Assert.Equal(mass, certificate.SlotBounds[slot]);
            total = total.Multiply(mass.Numerator, mass.Denominator);
            var selected = remaining.Single(c => c.GetType().Name == ids[slot]);
            current = NativePublicRewardHistoryCertificate.AdvanceRarity(current, selected.Rarity, 0.005f);
            remaining = remaining.Where(c => !ReferenceEquals(c, selected)).ToArray();
        }
        Assert.Equal(total, certificate.Envelope);
        // Rare resets and repeated native float additions through the cap.
        foreach (float initial in new[] { -0.05f, 0.39999998f, 0.4f })
        foreach (ulong word in new[] { 0UL, ulong.MaxValue })
        {
            var native = new CardRarityOdds(initial, new Rng(4), new AscensionManager(10));
            using var scope = LabelRandomScope.Enter(_ => word);
            var rarity = native.Roll(CardRarityOddsType.RegularEncounter);
            Assert.Equal(NativePublicRewardHistoryCertificate.AdvanceRarity(initial, rarity, native.RarityGrowth), native.CurrentValue);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void FinitePublicPotionMassKeepsSuccessAbsenceAndForcedBranches(int bits)
    {
        int domain = 1 << bits;
        foreach (float threshold in new[] { 0.4f, 0.5f, 0.3f, 0.625f })
        foreach (bool forced in new[] { false, true })
        foreach (bool displayed in new[] { false, true })
        {
            // A nonempty-rarity fixed pool has identity mass1 for the sole
            // displayed identity and mass0 for absence. Every raw presence word
            // is enumerated against native PotionRewardOdds.Roll.
            int matches = 0;
            for (int high = 0; high < domain; high++)
            {
                using var scope = LabelRandomScope.Enter(_ => (ulong)high << (64 - bits));
                var native = new PotionRewardOdds(threshold, new Rng(12), new PotionHooks(forced));
                bool actual = native.Roll(RoomType.Monster);
                if (actual == displayed) matches++;
                Assert.Equal(forced ? threshold : NativePublicRewardHistoryCertificate.AdvancePotion(threshold, actual), native.CurrentValue);
            }
            Assert.Equal(new ShuffleRational(matches, domain), NativeRewardResourceMath.DisplayMass(forced, threshold,
                displayed, new(displayed ? 1 : 0, 1), bits));
        }
    }

    private sealed class PotionHooks(bool forced) : IOddsHooks
    {
        public bool ShouldForcePotionReward(RoomType roomType) => forced;
        public float ModifyOddsIncreaseForUnrolledRoomType(RoomType roomType, float increase) => increase;
        public IReadOnlySet<RoomType> ModifyUnknownMapPointRoomTypes(IReadOnlySet<RoomType> roomTypes) => roomTypes;
    }
}
