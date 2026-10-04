using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.PotionPools;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicRewardHistoryTarget(IReadOnlyList<LabelCardRarityThresholds> Thresholds,
    float PotionThreshold, NativeGoldEnvelopeCertificate GoldCertificate);

/// <summary>
/// Optional source-closed PUBLIC prefix, never a sampled-world normalizer. The thirteen
/// reviewed Neow positives do not update either pity state. NewLeaf's default
/// transformation uses one Niche index, with no reward rarity or potion roll.
/// ScrollBoxes uses fixed-rarity indices, ArcaneScroll uses Uniform/Other, and
/// Pomander upgrades one public starter. A complete unchanged shop leave is neutral.
/// NeowsTorment adds one fixed NeowsFury; LavaRock's reward override is inactive
/// only under the separately proved ordinary weak-combat prefix.
/// GoldenPearl and NutritiousOyster change only their exact native gold/HP assets
/// after Neow's A10 entry heal, with no random draw or pity update.
/// WoodCarvings and SunkenStatue
/// have no random reward generation; Gorge uses Source.Other/Uniform. The first three
/// native weak encounters have no escape, summon, or extra-reward behavior. Every base
/// card, including unpicked options, advances rarity pity; every standard potion roll
/// advances potion pity. Unsupported history leaves the original universal bound intact.
/// </summary>
internal static class NativePublicRewardHistoryCertificate
{
    private static readonly HashSet<string> Neow = ["FishingRod", "LostCoffer", "LeadPaperweight", "WingedBoots", "PreciseScissors", "NewLeaf",
        "ScrollBoxes", "ArcaneScroll", "Pomander", "LavaRock", "NeowsTorment", "GoldenPearl", "NutritiousOyster"];
    private static readonly HashSet<string> Relics = ["RingOfTheSnake", "FishingRod", "LostCoffer",
        "LeadPaperweight", "WingedBoots", "PreciseScissors", "NewLeaf", "ScrollBoxes", "ArcaneScroll", "Pomander",
        "LavaRock", "NeowsTorment", "GoldenPearl", "NutritiousOyster", "SwordOfStone"];
    private static readonly string[] RewardHooks = [nameof(AbstractModel.ModifyRewards),
        nameof(AbstractModel.BeforeCombatRewardOffered), nameof(AbstractModel.ShouldForcePotionReward),
        nameof(AbstractModel.ModifyCardRewardCreationOptions), nameof(AbstractModel.ModifyCardRewardCreationOptionsLate),
        nameof(AbstractModel.TryModifyCardRewardOptions), nameof(AbstractModel.TryModifyCardRewardOptionsLate),
        nameof(AbstractModel.TryModifyCardRewardOptionLate), nameof(AbstractModel.AfterModifyingCardRewardOptions),
        nameof(AbstractModel.TryModifyCardRewardAlternatives), nameof(AbstractModel.TryEnableCardRewardReroll)];

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
            || !CompleteNeutralNeowBoundary(events, start.Assets, choice.Key, character)
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
            var prefix = events.Take(checked((int)through + 1)).ToArray();
            // LavaRock.ModifyRewards returns before any draw unless BOTH the
            // supplied room type and native current room are Act0 Boss. Every
            // combat in this prefix must instead have the public direct-map
            // first-three weak proof; the runtime gold boundary independently
            // validates Monster/current room/encounter/act before conditioning.
            // This is an explicit context exception, never a registry-wide waiver.
            bool lavaRockInactive = choice.Key == nameof(LavaRock) && prefix
                .Where(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat })
                .All(e => e.Payload is PublicOwnerStarted { ActIndex: 0, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true }
                    && weakOwners.ContainsKey(e.OwnerOrdinal!.Value));
            foreach (var e in prefix)
            {
                if (e.Payload is PublicEvidenceGap) return false;
                PublicEvidenceAssets? assets = e.Payload switch { PublicRunStarted r => r.Assets,
                    PublicOwnerEnded end => end.Assets, PublicCombatFact f => f.Assets, _ => null };
                if (assets is not null && !NeutralInventory(assets, lavaRockInactive)) return false;
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
                        if (!(owner.ParentOwnerOrdinal == 0 && choice.Key is "LeadPaperweight" or "PreciseScissors" or "NewLeaf"
                                or "ScrollBoxes" or "Pomander")
                            && !(owner.ParentOwnerOrdinal is { } parent && NeutralEvent(parent)
                                && events.Any(x => x.OwnerOrdinal == e.OwnerOrdinal && x.Payload is PublicCardsObserved
                                    { Choice.Source: "RoomFullOfCheese" }))) return false;
                        break;
                    case PublicEvidenceOwnerKind.Shop:
                        if (!NativePublicShopLeaveBoundary.TryCreate(events, e, out var shop, out _)
                            || shop!.EndEventOrdinal > through) return false;
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

    private static bool CompleteNeutralNeowBoundary(IReadOnlyList<PublicRunEvidenceEvent> events,
        PublicEvidenceAssets before, string relic, CharacterModel character)
    {
        if (relic is "GoldenPearl" or "NutritiousOyster")
            return CompleteResourceNeowBoundary(events, before, relic, character);
        if (relic is not ("ScrollBoxes" or "ArcaneScroll" or "Pomander" or "LavaRock" or "NeowsTorment")) return true;
        // These additions require the native starter inventory and the complete
        // acquisition boundary. An arbitrary child or a partial public grant is
        // not evidence that the initial pity states survived unchanged.
        string[] starter = ["AscendersBane", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent",
            "Neutralize", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "Survivor"];
        if (!before.Relics.Select(r => r.Id).SequenceEqual(new[] { "RingOfTheSnake" })
            || !before.Deck.Select(c => c.Id).Order(StringComparer.Ordinal).SequenceEqual(starter.Order(StringComparer.Ordinal))
            || before.Deck.Any(c => !PlainCard(c))) return false;
        int end = relic is "ArcaneScroll" or "LavaRock" or "NeowsTorment" ? 4 : 8;
        if (events.Count <= end || events[end] is not { OwnerOrdinal: 0, Payload: PublicOwnerEnded
                { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } ended }) return false;
        var after = ended.Assets!;
        if (!after.Relics.Select(r => r.Id).SequenceEqual(new[] { "RingOfTheSnake", relic })
            || PublicJson.Serialize(before.Relics[0]) != PublicJson.Serialize(after.Relics[0])
            || before.MaxHp != after.MaxHp || before.Gold != after.Gold || before.MaxEnergy != after.MaxEnergy
            || before.PotionSlots != after.PotionSlots || !before.Potions.SequenceEqual(after.Potions)
            || before.OrbSlots != after.OrbSlots || before.CardRemovalsUsed != after.CardRemovalsUsed) return false;
        var pool = character.CardPool.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false).ToArray();
        if (relic == "LavaRock") return SameCards(before.Deck, after.Deck);
        if (relic == "NeowsTorment")
        {
            var fury = (CardModel)ModelDb.Card<Sts2Sim.Core.Models.Cards.NeowsFury>().MutableClone();
            return SameCards(before.Deck.Append(PublicCardDetailsBuilder.Card(fury)), after.Deck);
        }
        if (relic == "ArcaneScroll")
        {
            var additions = after.Deck.ToList();
            return RemoveCards(additions, before.Deck) && additions.Count == 1 && PlainCard(additions[0])
                && pool.Any(c => c.GetType().Name == additions[0].Id && c.Rarity == CardRarity.Rare);
        }
        if (events[4] is not { OwnerOrdinal: 1, Payload: PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.OutsideChoice, ActIndex: 0, Floor: 1,
                    ParentOwnerOrdinal: 0, CompleteFromOwnerStart: true } }
            || events[5] is not { OwnerOrdinal: 1, Payload: PublicCardsObserved observed }
            || events[6] is not { OwnerOrdinal: 1, Payload: PublicCardsChosen
                { OfferEventOrdinal: 5, Cancelled: false, Selection.Length: 1 } selected }
            || events[7] is not { OwnerOrdinal: 1, Payload: PublicOwnerEnded
                { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: null } }) return false;
        var choice = observed.Choice;
        if (choice.Source != relic || choice is not { Min: 1, Max: 1, Cancelable: false }
            || selected.Selection[0] >= choice.Candidates.Length) return false;
        var expected = before.Deck.ToList();
        if (relic == "ScrollBoxes")
        {
            if (choice is not { CandidateOrder: "public", Candidates.Length: 2, Bundles.Length: 2 }
                || choice.Bundles.Any(b => b.Length != 3)
                || choice.Bundles.SelectMany(b => b).Any(c => !PlainCard(c))
                || choice.Bundles.SelectMany(b => b).Select(c => c.Id).Distinct().Count() != 6
                || !choice.Candidates.Select(PublicJson.Serialize).SequenceEqual(choice.Bundles.Select(b => PublicJson.Serialize(b[0])))
                || choice.Bundles.Any(b => b.Where((c, i) => !pool.Any(m => m.GetType().Name == c.Id
                    && m.Rarity == (i == 2 ? CardRarity.Uncommon : CardRarity.Common))).Any())) return false;
            expected.AddRange(choice.Bundles[selected.Selection[0]]);
        }
        else
        {
            if (choice is not { CandidateOrder: "canonical_unordered_reveal", Bundles: null }
                || !SameCards(choice.Candidates, before.Deck.Where(c => c.Id != "AscendersBane"))) return false;
            var original = choice.Candidates[selected.Selection[0]];
            if (original.Id is not ("DefendSilent" or "StrikeSilent" or "Neutralize" or "Survivor")
                || !RemoveCards(expected, [original])) return false;
            // Only these reviewed starter upgrades are admitted. Their native
            // upgrade has no random draw; the projection is a public catalog fact.
            var upgraded = (CardModel)ModelDb.All<CardModel>().Single(c => c.GetType().Name == original.Id).MutableClone();
            upgraded.Upgrade(); expected.Add(PublicCardDetailsBuilder.Card(upgraded));
        }
        return SameCards(expected, after.Deck);
    }

    private static bool CompleteResourceNeowBoundary(IReadOnlyList<PublicRunEvidenceEvent> events,
        PublicEvidenceAssets before, string relic, CharacterModel character)
    {
        // This proof is deliberately limited to the pinned Silent A10 start.
        // AncientEventModel.CalculateVars resets HP to zero and heals 80% of
        // 70 before either option is chosen. GoldenPearl then gains 150 gold;
        // NutritiousOyster gains 11 max HP and heals that actual gained amount.
        // RelicCmd.Obtain removes the fixed relic ID from bags without a draw.
        // The complete starter inventory has no gold-gain/acquisition modifier.
        var starter = character.StartingDeck.Select(type =>
            PublicCardDetailsBuilder.Card((CardModel)ModelDb.Get(type).MutableClone()))
            .Append(PublicCardDetailsBuilder.Card((CardModel)ModelDb.Card<Sts2Sim.Core.Models.Cards.AscendersBane>().MutableClone()));
        if (before is not { Hp: 70, MaxHp: 70, Gold: 99, MaxEnergy: 3, PotionSlots: 2,
                OrbSlots: 0, CardRemovalsUsed: 0 }
            || before.Potions.Any(p => p is not null) || !SameCards(starter, before.Deck)
            || before.Relics.Length != 1 || !SameRelic(before.Relics[0], ModelDb.Relic<RingOfTheSnake>())
            || events.Count <= 4 || events[4] is not { OwnerOrdinal: 0, Payload: PublicOwnerEnded
                { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } ended }) return false;
        var after = ended.Assets!;
        bool pearl = relic == "GoldenPearl";
        return after.Hp == (pearl ? 56 : 67) && after.MaxHp == (pearl ? 70 : 81)
            && after.Gold == (pearl ? 249 : 99) && after.MaxEnergy == before.MaxEnergy
            && after.PotionSlots == before.PotionSlots && after.Potions.SequenceEqual(before.Potions)
            && after.OrbSlots == before.OrbSlots && after.CardRemovalsUsed == before.CardRemovalsUsed
            && SameCards(before.Deck, after.Deck) && after.Relics.Length == 2
            && SameRelic(after.Relics[0], ModelDb.Relic<RingOfTheSnake>())
            && SameRelic(after.Relics[1], pearl ? ModelDb.Relic<GoldenPearl>() : ModelDb.Relic<NutritiousOyster>());
    }

    private static bool SameRelic(PublicRelic observed, RelicModel model) =>
        PublicJson.Serialize(observed) == PublicJson.Serialize(new PublicRelic(model.GetType().Name,
            PublicRelicDetails.Details(model), PublicRelicDetails.Cards(model), PublicRelicDetails.SelectedModel(model)));

    private static bool PlainCard(PublicCard card) => card.Upgrade == 0
        && (card.Enchantments?.Length ?? 0) == 0 && card.Affliction is null;
    private static bool SameCards(IEnumerable<PublicCard> first, IEnumerable<PublicCard> second) =>
        first.Select(PublicJson.Serialize).Order(StringComparer.Ordinal)
            .SequenceEqual(second.Select(PublicJson.Serialize).Order(StringComparer.Ordinal));
    private static bool RemoveCards(List<PublicCard> cards, IEnumerable<PublicCard> removed)
    {
        foreach (var card in removed)
        {
            int at = cards.FindIndex(c => PublicJson.Serialize(c) == PublicJson.Serialize(card));
            if (at < 0) return false;
            cards.RemoveAt(at);
        }
        return true;
    }

    private static bool NeutralInventory(PublicEvidenceAssets assets, bool lavaRockInactive)
    {
        if (assets.Relics.Any(r => !Relics.Contains(r.Id) || r.Details is null
            || !r.Details.TryGetValue("isWax", out int wax) || wax != 0
            || !r.Details.TryGetValue("isMelted", out int melted) || melted != 0
            || !r.Details.TryGetValue("stackCount", out int count) || count != 1)) return false;
        // A used or missing LavaRock state is outside the reviewed pre-boss
        // history, even though the native used-up branch is also an early return.
        if (assets.Relics.Any(r => r.Id == nameof(LavaRock) && (!lavaRockInactive
            || !r.Details.TryGetValue("hasTriggered", out int triggered) || triggered != 0
            || !r.Details.TryGetValue("isUsedUp", out int used) || used != 0))) return false;
        if (assets.Relics.Any(r => r.Id == nameof(GoldenPearl) && !SameRelic(r, ModelDb.Relic<GoldenPearl>())
            || r.Id == nameof(NutritiousOyster) && !SameRelic(r, ModelDb.Relic<NutritiousOyster>()))) return false;
        var models = new List<AbstractModel>();
        foreach (string id in assets.Deck.Select(c => c.Id).Distinct())
        { var model = ModelDb.All<CardModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        foreach (string id in assets.Relics.Select(r => r.Id))
        { var model = ModelDb.All<RelicModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        foreach (string id in assets.Potions.OfType<string>())
        { var model = ModelDb.All<PotionModel>().SingleOrDefault(c => c.GetType().Name == id); if (model is null) return false; models.Add(model); }
        return models.All(m => m.GetType().GetMethods().Where(method => RewardHooks.Contains(method.Name))
            .All(method => method.DeclaringType == typeof(AbstractModel)
                || lavaRockInactive && m is LavaRock && method.Name == nameof(AbstractModel.ModifyRewards)
                    && method.DeclaringType == typeof(LavaRock)));
    }
}
