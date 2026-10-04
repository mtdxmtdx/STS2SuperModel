using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.CardPools;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.PotionPools;
using Sts2Sim.Core.Models.RelicPools;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// First early ordinary shop, with a closed public prefix proving no prior bucket
/// consumer. This deliberately narrow certificate is only an accelerator: all
/// unsupported histories retain ordinary rejection under the unchanged prior.
/// </summary>
internal sealed class NativePublicShopCondition
{
    internal long OwnerOrdinal { get; }
    internal long OfferEventOrdinal { get; }
    internal int Floor { get; }
    internal int Ascension { get; }
    internal float? CertifiedRarityOffset { get; }
    internal PublicEvidenceAssets BeforeAssets { get; }
    internal PublicOffersObserved Offers { get; }
    internal NativeShopStockCertificate Stock { get; }
    internal IReadOnlyList<RelicModel> RelicPool { get; }
    internal IReadOnlyDictionary<RelicRarity, string[]> BackDraws { get; }
    internal IReadOnlySet<string> EligibleRelics { get; }

    private NativePublicShopCondition(long owner, long offerOrdinal, int floor, int ascension,
        PublicEvidenceAssets assets, PublicOffersObserved offers, float? certifiedRarityOffset)
    {
        OwnerOrdinal = owner; OfferEventOrdinal = offerOrdinal; Floor = floor; Ascension = ascension;
        BeforeAssets = assets; Offers = offers; CertifiedRarityOffset = certifiedRarityOffset;
        Stock = new(offers, certifiedRarityOffset, ascension);
        RelicPool = SharedRelicPool.Instance.AllRelics.Concat(ModelDb.Character<Silent>().RelicPool.AllRelics).ToArray();
        Require(RelicPool.Select(r => r.Id).Distinct().Count() == RelicPool.Count, "shop_requires_unique_native_relic_pool");
        BackDraws = Stock.Relics.GroupBy(r => r.Rarity).ToDictionary(g => g.Key, g => g.Select(r => r.GetType().Name).ToArray());
        // Pinned overrides depend only on this certified early single-player floor.
        // Do not evaluate an arbitrary model against an invented or latent run.
        string[] early = ["BookOfFiveRings", "Girya", "MealTicket", "BowlerHat", "MoltenEgg", "FrozenEgg",
            "SilverCrucible", "Planisphere", "AmethystAubergine", "Shovel", "DragonFruit", "WingedBoots",
            "LuckyFysh", "ToxicEgg", "JuzuBracelet", "WhiteStar", "LastingCandy", "OldCoin", "WhiteBeastStatue"];
        Require(RelicPool.All(r => r.GetType().GetMethod(nameof(RelicModel.IsAllowed))!.DeclaringType == typeof(RelicModel)
            || early.Contains(r.GetType().Name) || r is MassiveScroll), "shop_relic_eligibility_catalog_changed");
        EligibleRelics = RelicPool.Where(r => r.IsAllowedInShops && r is not MassiveScroll)
            .Select(r => r.GetType().Name).ToHashSet();
        Require(Stock.Relics.All(r => EligibleRelics.Contains(r.GetType().Name))
            && Stock.Relics.Select(r => r.Id).Distinct().Count() == 3, "shop_relics_have_no_native_first_bag_support");
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicShopCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try
        {
            Require(prior.UsesRewardsProvenance, "shop_requires_explicit_hybrid_prior");
            Require(root.PublicEvidence is { CompleteFromRunStart: true }, "shop_requires_complete_public_evidence");
            var events = root.PublicEvidence!.Events;
            Require(events[0].Payload is PublicRunStarted { Character: nameof(Silent), Ascension: 10 }, "shop_requires_reviewed_native_start");
            NaturalSourceCollector.InitializeNativeModels();
            Require(NativeNeowCondition.TryCreate(root, prior, out var neow, out _)
                && neow!.TargetRelicId == nameof(PreciseScissors), "shop_requires_no_bag_consuming_neow");
            var entry = events.FirstOrDefault(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Shop });
            Require(entry is { OwnerOrdinal: { }, Payload: PublicOwnerStarted { ActIndex: 0, Floor: > 1 and < 38,
                ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } }, "shop_requires_first_early_ordinary_owner");
            int at = checked((int)entry!.EventOrdinal);
            Require(NativePublicShopLeaveBoundary.TryCreate(events, entry, out var boundary, out var boundaryReason), boundaryReason!);
            var assets = boundary!.BeforeAssets;
            Require(assets.Relics.Select(r => r.Id).SequenceEqual(new[] { nameof(RingOfTheSnake), nameof(PreciseScissors) }),
                "shop_public_hook_closure_not_certified");
            foreach (var old in events.Take(at))
            {
                // A '?' may resolve to a treasure room without an Event owner.
                // Certify each immediately observed room, rather than inferring
                // no bag pull from unchanged owned inventory or absent offers.
                if (old.Payload is PublicMapChosen && old.EventOrdinal != at - 2)
                    Require(ReviewedRoomAfterMapChoice(events, old), "shop_prefix_has_unreviewed_map_room");
                if (old.Payload is PublicOffersObserved previous)
                    Require(previous.Groups.All(g => g.Offers.All(o => o.Relic is null)), "shop_prefix_has_prior_relic_offer");
                if (old.Payload is not PublicOwnerStarted owner) continue;
                Require(owner.ActIndex == 0 && owner.CompleteFromOwnerStart
                    && owner.OwnerKind is not (PublicEvidenceOwnerKind.Shop or PublicEvidenceOwnerKind.Rest),
                    "shop_prefix_has_unreviewed_owner");
                if (owner.OwnerKind == PublicEvidenceOwnerKind.Event && old.OwnerOrdinal != 0)
                {
                    var choices = events.Take(at).Where(e => e.OwnerOrdinal == old.OwnerOrdinal).Select(e => e.Payload).OfType<PublicOptionChosen>().ToArray();
                    Require(choices.Length == 1 && choices[0].Key == "GORGE", "shop_prefix_has_unreviewed_event");
                    var options = (PublicOptionsObserved)events[(int)choices[0].OfferEventOrdinal].Payload;
                    Require(options.Options.Select(o => o.Key).SequenceEqual(new[] { "GORGE", "SEARCH" }), "shop_prefix_has_unreviewed_event");
                }
                if (owner.OwnerKind == PublicEvidenceOwnerKind.Combat)
                {
                    int index = checked((int)old.EventOrdinal);
                    Require(owner.ParentOwnerOrdinal is null && index >= 3
                        && events[index - 3].Payload is PublicMapObserved cm && events[index - 2].Payload is PublicMapChosen cc
                        && cc.OfferEventOrdinal == index - 3
                        && cm.Nodes.Single(n => n.Coordinate == cc.Coordinate).NodeType == PublicMapNodeType.Monster,
                        "shop_prefix_requires_ordinary_direct_map_combats");
                }
            }
            float? offset = NativePublicRewardHistoryCertificate.TryRarityOffsetBeforeOwner(root.PublicEvidence,
                entry.OwnerOrdinal!.Value, out float certified) ? certified : null;
            condition = new(entry.OwnerOrdinal!.Value, at + 1, boundary.Floor, 10, assets, boundary.Offers, offset);
            return true;
        }
        catch (NotCertified e) { reason = e.Message; return false; }
        catch (NativePublicConstraintMismatchException) { reason = "shop_public_offer_has_no_native_support"; return false; }
    }

    internal static bool ReviewedRoomAfterMapChoice(IReadOnlyList<PublicRunEvidenceEvent> events, PublicRunEvidenceEvent entry)
    {
        if (entry.Payload is not PublicMapChosen chosen || entry.OwnerOrdinal is not { } mapOwner) return false;
        int at = checked((int)entry.EventOrdinal);
        if (chosen.OfferEventOrdinal >= events.Count || at + 2 >= events.Count
            || events[(int)chosen.OfferEventOrdinal] is not { Payload: PublicMapObserved map } offer || offer.OwnerOrdinal != mapOwner
            || events.FirstOrDefault(e => e.OwnerOrdinal == mapOwner && e.Payload is PublicOwnerStarted)?.Payload
                is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Map, ActIndex: 0, ParentOwnerOrdinal: null,
                    CompleteFromOwnerStart: true } mapStart
            || events[at + 1] is not { Payload: PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed }, OwnerOrdinal: { } ended }
            || ended != mapOwner || events[at + 2].Payload is not PublicOwnerStarted
                { ActIndex: 0, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } next || next.Floor != mapStart.Floor + 1)
            return false;
        return map.Nodes.Single(n => n.Coordinate == chosen.Coordinate).NodeType switch
        {
            PublicMapNodeType.Monster => next.OwnerKind == PublicEvidenceOwnerKind.Combat,
            PublicMapNodeType.Unknown => next.OwnerKind == PublicEvidenceOwnerKind.Event,
            _ => false,
        };
    }

    internal static void Require(bool value, string reason) { if (!value) throw new NotCertified(reason); }
    private sealed class NotCertified(string reason) : Exception(reason);
}

internal sealed class NativeShopStockCertificate
{
    internal IReadOnlyList<CardModel> Cards { get; }
    internal IReadOnlyList<RelicModel> Relics { get; }
    internal IReadOnlyList<PotionModel> Potions { get; }
    internal IReadOnlyList<LabelRewardCardBranch>[] CardBranches { get; }
    internal IReadOnlyList<PotionModel>[] PotionPools { get; }
    internal IReadOnlyList<int> Prices { get; }
    internal IReadOnlyList<int> CardBasePrices { get; }
    internal IReadOnlyList<ShuffleRational> DiscountMasses { get; }
    internal ShuffleRational FixedMass { get; }
    internal ShuffleRational CardIdentityEnvelope { get; }
    internal ShuffleRational Envelope => FixedMass.Multiply(CardIdentityEnvelope.Numerator, CardIdentityEnvelope.Denominator);

    internal NativeShopStockCertificate(PublicOffersObserved observed, float? certifiedRarityOffset = null, int ascension = 10)
    {
        var group = observed.Groups.Single(); var offers = group.Offers;
        NativePublicShopCondition.Require(NativePublicShopLeaveBoundary.CompleteOrdinaryStock(observed),
            "shop_requires_all_fifteen_public_slots");
        Cards = offers.Take(7).Select(o => ModelDb.All<CardModel>().Single(c => c.GetType().Name == o.Card!.Id)).ToArray();
        Relics = offers.Skip(7).Take(3).Select(o => ModelDb.All<RelicModel>().Single(r => r.GetType().Name == o.Relic!.Id)).ToArray();
        Potions = offers.Skip(10).Take(3).Select(o => ModelDb.All<PotionModel>().Single(p => p.GetType().Name == o.Potion)).ToArray();
        NativePublicShopCondition.Require(Relics.Take(2).All(r => r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare)
            && Relics[2].Rarity == RelicRarity.Shop, "shop_requires_supported_relic_rarities");
        Prices = offers.Take(13).Select(o => o.Price!.Value).ToArray();
        CardBasePrices = Cards.Select(c => { int b = c.Rarity switch { CardRarity.Rare => 150, CardRarity.Uncommon => 75, _ => 50 };
            return c.IsColorless ? (int)Math.Round(b * 1.15f) : b; }).ToArray();
        var pool = ModelDb.Character<Silent>().CardPool.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false).ToArray();
        CardType[] types = [CardType.Attack, CardType.Attack, CardType.Skill, CardType.Skill, CardType.Power];
        var used = new HashSet<ModelId>(); CardBranches = new IReadOnlyList<LabelRewardCardBranch>[7];
        var identityBound = new ShuffleRational(1, 1);
        LabelCardRarityThresholds? fixedThresholds = certifiedRarityOffset is { } offset
            ? new CardRarityOdds(offset, new Rng(0), new AscensionManager(ascension))
                .GetLabelRollThresholds(CardRarityOddsType.Shop, changesFutureOdds: true) : null;
        for (int i = 0; i < 7; i++)
        {
            CardModel[] Candidates(CardRarity rolled)
            {
                var rarity = rolled;
                for (int k = 0; k < 3; k++)
                {
                    var result = pool.Where(c => !c.IsColorless && c.Type == types[i] && c.Rarity == rarity && !used.Contains(c.Id)).ToArray();
                    if (result.Length > 0) return result;
                    rarity = rarity switch { CardRarity.Common => CardRarity.Uncommon, CardRarity.Uncommon => CardRarity.Rare, _ => CardRarity.Common };
                }
                return [];
            }
            CardBranches[i] = i < 5
                ? new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common }.Select(r => new LabelRewardCardBranch(r, Candidates(r))).ToArray()
                : new[] { new LabelRewardCardBranch(null, ColorlessCardPool.Instance.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false)
                    .Where(c => c.IsColorless && c.Rarity == (i == 5 ? CardRarity.Uncommon : CardRarity.Rare) && !used.Contains(c.Id)).ToArray()) };
            var masses = CardBranches[i].Select(b => NativeNeowCardCondition.UniformMass(b.Candidates, Cards[i].GetType().Name)).ToArray();
            var maximum = masses.Aggregate((a, b) => a.Numerator * b.Denominator >= b.Numerator * a.Denominator ? a : b);
            if (maximum.Numerator.IsZero) throw new NativePublicConstraintMismatchException("Public shop card has no candidate support");
            if (i < 5 && fixedThresholds is { } thresholds)
                maximum = new(NativeRewardIdentityMath.Arms(CardBranches[i], thresholds, Cards[i].GetType().Name)
                    .Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass), BigInteger.One << 106);
            if (maximum.Numerator.IsZero) throw new NativePublicConstraintMismatchException("Public shop card has zero certified rarity mass");
            identityBound = identityBound.Multiply(maximum.Numerator, maximum.Denominator); used.Add(Cards[i].Id);
        }
        CardIdentityEnvelope = identityBound;
        var discounts = new List<ShuffleRational>();
        for (int d = 0; d < 5; d++)
        {
            var index = ConditionalShuffleProposal.Factor(5, d);
            var mass = new ShuffleRational(index.BucketSize, BigInteger.One << 53);
            for (int i = 0; i < 7; i++) { var price = NativeShopPriceMath.Mass(CardBasePrices[i], .95f, 1.05f, i == d, Prices[i]); mass = mass.Multiply(price.Numerator, price.Denominator); }
            discounts.Add(mass);
        }
        DiscountMasses = discounts;
        var fixedMass = discounts.Aggregate(new ShuffleRational(0, 1), (a, b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator));
        for (int i = 0; i < 3; i++)
        {
            var price = NativeShopPriceMath.Mass(Relics[i].MerchantCost, .85f, 1.15f, false, Prices[7 + i]);
            fixedMass = fixedMass.Multiply(price.Numerator, price.Denominator);
            if (i < 2) { var range = RelicRarityRange(Relics[i].Rarity); fixedMass = fixedMass.Multiply(range.Size, BigInteger.One << 53); }
        }
        var potionPool = ModelDb.Character<Silent>().PotionPool.GetUnlockedPotions(PlayerUnlockState.AllUnlocked())
            .Concat(SharedPotionPool.Instance.GetUnlockedPotions(PlayerUnlockState.AllUnlocked())).DistinctBy(p => p.Id).ToArray();
        PotionPools = new IReadOnlyList<PotionModel>[3]; var potionUsed = new HashSet<ModelId>();
        for (int i = 0; i < 3; i++)
        {
            PotionPools[i] = potionPool.Where(p => !potionUsed.Contains(p.Id)).ToArray();
            var identity = NativeRewardResourceMath.PotionMass(PotionPools[i], Potions[i].GetType().Name);
            var price = NativeShopPriceMath.Mass(PotionBasePrice(i), .95f, 1.05f, false, Prices[10 + i]);
            fixedMass = fixedMass.Multiply(identity.Numerator, identity.Denominator).Multiply(price.Numerator, price.Denominator);
            potionUsed.Add(Potions[i].Id);
        }
        if (fixedMass.Numerator.IsZero) throw new NativePublicConstraintMismatchException("Public shop has zero native mass");
        FixedMass = fixedMass;
    }
    internal int PotionBasePrice(int slot) => Potions[slot].Rarity switch
    { Sts2Sim.Core.Entities.Potions.PotionRarity.Rare => 100, Sts2Sim.Core.Entities.Potions.PotionRarity.Uncommon => 75, _ => 50 };
    internal static (ulong Start, ulong Size) RelicRarityRange(RelicRarity rarity)
    {
        ulong common = NativeRewardIdentityMath.FloatLowerBound(.5f), uncommon = NativeRewardIdentityMath.FloatLowerBound(.83f);
        return rarity switch { RelicRarity.Common => (0, common), RelicRarity.Uncommon => (common, uncommon - common),
            RelicRarity.Rare => (uncommon, (1UL << 53) - uncommon), _ => throw new ArgumentOutOfRangeException(nameof(rarity)) };
    }
}
