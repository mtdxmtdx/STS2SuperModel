using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.PotionPools;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicRewardHistoryTarget(IReadOnlyList<LabelCardRarityThresholds> Thresholds,
    float PotionThreshold, NativeGoldEnvelopeCertificate GoldCertificate);

/// <summary>
/// Optional source-closed PUBLIC prefix, never a sampled-world normalizer. The six
/// reviewed Neow positives do not update either pity state. NewLeaf's default
/// transformation uses one Niche index, with no reward rarity or potion roll.
/// WoodCarvings and SunkenStatue
/// have no random reward generation; Gorge uses Source.Other/Uniform. The first three
/// native weak encounters have no escape, summon, or extra-reward behavior. Every base
/// card, including unpicked options, advances rarity pity; every standard potion roll
/// advances potion pity. Unsupported history leaves the original universal bound intact.
/// </summary>
internal static class NativePublicRewardHistoryCertificate
{
    private static readonly HashSet<string> Neow = ["FishingRod", "LostCoffer", "LeadPaperweight", "WingedBoots", "PreciseScissors", "NewLeaf"];
    private static readonly HashSet<string> Relics = ["RingOfTheSnake", "FishingRod", "LostCoffer",
        "LeadPaperweight", "WingedBoots", "PreciseScissors", "NewLeaf", "SwordOfStone"];
    private static readonly string[] RewardHooks = [nameof(AbstractModel.ModifyRewards),
        nameof(AbstractModel.BeforeCombatRewardOffered), nameof(AbstractModel.ShouldForcePotionReward),
        nameof(AbstractModel.ModifyCardRewardCreationOptions), nameof(AbstractModel.ModifyCardRewardCreationOptionsLate),
        nameof(AbstractModel.TryModifyCardRewardOptions), nameof(AbstractModel.TryModifyCardRewardOptionsLate),
        nameof(AbstractModel.TryModifyCardRewardOptionLate), nameof(AbstractModel.TryEnableCardRewardReroll)];

    internal static bool TryRarityOffsetBeforeOwner(PublicRunEvidence evidence, long ownerOrdinal, out float offset)
    {
        offset = 0;
        var boundary = evidence.Events.FirstOrDefault(e => e.OwnerOrdinal == ownerOrdinal && e.Payload is PublicOwnerStarted);
        // Shop generation reads but does not advance rarity pity. A Reward owner
        // starts AFTER generation, so its owner-start ordinal is not this seam.
        if (boundary?.Payload is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Shop,
                ParentOwnerOrdinal: null, ActIndex: 0, CompleteFromOwnerStart: true }
            || !evidence.CompleteFromRunStart) return false;
        var cards = NativePublicRewardCondition.Create(evidence);
        var character = ModelDb.AllCharacters.Single(c => c.GetType().Name == cards.Character);
        float? value = Tighten(evidence, cards.Targets.ToDictionary(), character, boundary.EventOrdinal);
        if (value is null) return false;
        offset = value.Value; return true;
    }

    internal static float? Tighten(PublicRunEvidence evidence, Dictionary<int, NativePublicRewardTarget> targets,
        CharacterModel character, long? beforeEventOrdinal = null)
    {
        var events = evidence.Events;
        if (events.Length < 4 || events[0].Payload is not PublicRunStarted { Character: "Silent", Ascension: 10 } start
            || events[1] is not { OwnerOrdinal: 0, Payload: PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Event,
                ActIndex: 0, Floor: 1, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } }
            || events[2] is not { OwnerOrdinal: 0, Payload: PublicOptionsObserved initial }
            || events[3] is not { OwnerOrdinal: 0, Payload: PublicOptionChosen { OfferEventOrdinal: 2 } choice }
            || !Neow.Contains(choice.Key) || initial.Options.All(o => o.Key != choice.Key || o.IsLocked)
            || !NativePublicWeakEncounterSequenceCondition.TryCreate(evidence, out var weak, out _)) return null;
        // All native potion rarity buckets must contain a model. Otherwise an absent
        // display also permits a successful roll into an empty pool and pity is latent.
        var unlock = PlayerUnlockState.AllUnlocked();
        var potionPool = character.PotionPool.GetUnlockedPotions(unlock)
            .Concat(SharedPotionPool.Instance.GetUnlockedPotions(unlock)).DistinctBy(p => p.Id).ToArray();
        if (NativeRewardResourceMath.PotionMass(potionPool, null).Numerator != 0) return null;
        var pool = character.CardPool.GetUnlockedCards(unlock, false).ToArray();
        if (pool.Select(c => c.GetType().Name).Distinct().Count() != pool.Length) return null;
        var starts = events.Where(e => e.Payload is PublicOwnerStarted).ToDictionary(e => e.OwnerOrdinal!.Value);
        var combats = events.Where(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToArray();
        var rewardOwners = targets.Values.ToDictionary(t => events[(int)t.OfferEventOrdinal].OwnerOrdinal!.Value);
        var weakOwners = weak!.Targets.ToDictionary(t => t.CombatOwnerOrdinal);
        float rarityOffset = -0.05f, potionOdds = 0.4f;
        int expectedCombat = 0, completed = 0;
        var requested = targets.Values.Where(t => beforeEventOrdinal is null || t.OfferEventOrdinal < beforeEventOrdinal)
            .OrderBy(t => t.CombatIndex).ToArray();
        foreach (var original in requested)
        {
            if (original.CombatIndex != expectedCombat++ || !weakOwners.TryGetValue(combats[original.CombatIndex].OwnerOrdinal!.Value, out var weakTarget)
                || !ClosedPrefix(original.OfferEventOrdinal)) break;
            var displayed = (PublicOffersObserved)events[(int)original.OfferEventOrdinal].Payload;
            var cards = displayed.Groups.SelectMany(g => g.Offers).Where(o => o.OfferKind == PublicOfferKind.Card).ToArray();
            if (cards.Length != 3 || !cards.Select(c => c.Card!.Id).SequenceEqual(original.BaseCardIds)
                || original.BaseCardIds.Any(id => pool.All(c => c.GetType().Name != id))) break;
            var thresholds = new List<LabelCardRarityThresholds>();
            foreach (string id in original.BaseCardIds)
            {
                var odds = new CardRarityOdds(rarityOffset, new Rng(0), new AscensionManager(start.Ascension));
                thresholds.Add(odds.GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, true));
                rarityOffset = AdvanceRarity(rarityOffset, pool.Single(c => c.GetType().Name == id).Rarity, odds.RarityGrowth);
            }
            string[] names = NativeOpeningEncounterCatalog.Entries.Where(e => e.ActType == weak.TargetActType
                && weakTarget.EncounterIndices.Contains(e.Index)).Select(e => e.EncounterId).ToArray();
            var gold = displayed.Groups.SelectMany(g => g.Offers).SingleOrDefault(o => o.OfferKind == PublicOfferKind.Gold)?.Gold;
            if (gold is null) break;
            var history = new NativePublicRewardHistoryTarget(thresholds.AsReadOnly(), potionOdds,
                NativeGoldEnvelopeCertificate.ExactWeak(names, start.Ascension, original.ActIndex, gold.Value));
            targets[original.CombatIndex] = original with { Certificate = NativeRewardPoolCertificate.FromPublicHistory(
                pool, original.BaseCardIds, history.Thresholds), PublicHistory = history };
            bool potion = displayed.Groups.SelectMany(g => g.Offers).Any(o => o.OfferKind == PublicOfferKind.Potion);
            potionOdds = AdvancePotion(potionOdds, potion);
            completed++;
        }
        return beforeEventOrdinal is { } before && completed == requested.Length && ClosedPrefix(before - 1)
            ? rarityOffset : null;

        bool ClosedPrefix(long through)
        {
            foreach (var e in events.Take(checked((int)through + 1)))
            {
                if (e.Payload is PublicEvidenceGap) return false;
                PublicEvidenceAssets? assets = e.Payload switch { PublicRunStarted r => r.Assets,
                    PublicOwnerEnded end => end.Assets, PublicCombatFact f => f.Assets, _ => null };
                if (assets is not null && !NeutralInventory(assets)) return false;
                if (e.Payload is PublicOffersObserved offer && (offer.ReplacesOfferEventOrdinal is not null
                    || offer.Groups.Any(g => g.GroupKind != PublicOfferGroupKind.Primary))) return false;
                if (e.Payload is PublicOptionChosen selected && selected.Key == "card:reroll") return false;
                if (e.Payload is not PublicOwnerStarted owner) continue;
                if (!owner.CompleteFromOwnerStart || owner.ActIndex != 0) return false;
                switch (owner.OwnerKind)
                {
                    case PublicEvidenceOwnerKind.Map:
                        if (owner.ParentOwnerOrdinal is not null) return false;
                        break;
                    case PublicEvidenceOwnerKind.Combat:
                        if (owner.ParentOwnerOrdinal is not null || !weakOwners.ContainsKey(e.OwnerOrdinal!.Value)) return false;
                        break;
                    case PublicEvidenceOwnerKind.Event:
                        if (e.OwnerOrdinal != 0 && !NeutralEvent(e.OwnerOrdinal!.Value)) return false;
                        break;
                    case PublicEvidenceOwnerKind.Reward:
                        if (!(rewardOwners.ContainsKey(e.OwnerOrdinal!.Value) && owner.ParentOwnerOrdinal is null)
                            && !(owner.ParentOwnerOrdinal == 0 && choice.Key == "LostCoffer")) return false;
                        break;
                    case PublicEvidenceOwnerKind.OutsideChoice:
                        if (!(owner.ParentOwnerOrdinal == 0 && choice.Key is "LeadPaperweight" or "PreciseScissors" or "NewLeaf")
                            && !(owner.ParentOwnerOrdinal is { } parent && NeutralEvent(parent)
                                && events.Any(x => x.OwnerOrdinal == e.OwnerOrdinal && x.Payload is PublicCardsObserved
                                    { Choice.Source: "RoomFullOfCheese" }))) return false;
                        break;
                    default: return false;
                }
            }
            return true;
        }

        bool NeutralEvent(long owner)
        {
            if (starts[owner].Payload is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Event,
                    ParentOwnerOrdinal: null, ActIndex: 0, Floor: >= 2 }) return false;
            var own = events.Where(e => e.OwnerOrdinal == owner).ToArray();
            return own.Length >= 4 && own[1].Payload is PublicOptionsObserved options
                && NativeEventSelectionCertificate.Identify(options) is "WoodCarvings" or "SunkenStatue" or "RoomFullOfCheese"
                && own[2].Payload is PublicOptionChosen selected && selected.OfferEventOrdinal == own[1].EventOrdinal
                && (NativeEventSelectionCertificate.Identify(options) != "RoomFullOfCheese" || selected.Key == "GORGE")
                && own[^1].Payload is PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed };
        }
    }

    internal static float AdvanceRarity(float offset, CardRarity rarity, float growth) =>
        rarity == CardRarity.Rare ? -0.05f : Math.Min(offset + growth, 0.4f);
    internal static float AdvancePotion(float odds, bool present) => present ? odds - 0.1f : odds + 0.1f;

    private static bool NeutralInventory(PublicEvidenceAssets assets)
    {
        if (assets.Relics.Any(r => !Relics.Contains(r.Id) || r.Details is null
            || !r.Details.TryGetValue("isWax", out int wax) || wax != 0
            || !r.Details.TryGetValue("isMelted", out int melted) || melted != 0
            || !r.Details.TryGetValue("stackCount", out int count) || count != 1)) return false;
        var models = new List<AbstractModel>();
        foreach (string id in assets.Deck.Select(c => c.Id).Distinct())
        { var model = ModelDb.All<CardModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        foreach (string id in assets.Relics.Select(r => r.Id))
        { var model = ModelDb.All<RelicModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        foreach (string id in assets.Potions.OfType<string>())
        { var model = ModelDb.All<PotionModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        return models.All(m => m.GetType().GetMethods().Where(method => RewardHooks.Contains(method.Name))
            .All(method => method.DeclaringType == typeof(AbstractModel)));
    }
}
