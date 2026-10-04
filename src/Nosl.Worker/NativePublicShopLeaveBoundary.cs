using Nosl.Contracts;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

/// <summary>
/// Pure public boundary proof shared by shop conditioning and reward history.
/// It constructs no likelihood factor and never asks reward history for an offset.
/// </summary>
internal sealed record NativePublicShopLeaveBoundary(int Floor, long EndEventOrdinal,
    PublicEvidenceAssets BeforeAssets, PublicOffersObserved Offers)
{
    internal static bool TryCreate(IReadOnlyList<PublicRunEvidenceEvent> events, PublicRunEvidenceEvent entry,
        out NativePublicShopLeaveBoundary? boundary, out string? reason)
    {
        boundary = null;
        reason = "shop_requires_first_early_ordinary_owner";
        if (entry is not { OwnerOrdinal: { }, Payload: PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Shop, ActIndex: 0, Floor: > 1 and < 38,
                ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } start }) return false;
        int at = checked((int)entry.EventOrdinal);
        reason = "shop_requires_uninterrupted_complete_boundary";
        if (at < 5 || events.Count <= at + 3 || events.Take(at + 4).Any(e => e.Payload is PublicEvidenceGap)) return false;
        reason = "shop_requires_public_direct_map_before_assets";
        if (events[at - 4].Payload is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Map,
                ActIndex: 0, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } mapStart || mapStart.Floor != start.Floor - 1
            || events[at - 3].Payload is not PublicMapObserved map || events[at - 2].Payload is not PublicMapChosen chosen
            || chosen.OfferEventOrdinal != events[at - 3].EventOrdinal
            || map.Nodes.Single(n => n.Coordinate == chosen.Coordinate).NodeType != PublicMapNodeType.Shop
            || events[at - 1].Payload is not PublicOwnerEnded { Assets: not null, Outcome: PublicEvidenceOwnerOutcome.Completed }
            || !events.Skip(at - 4).Take(4).All(e => e.OwnerOrdinal == events[at - 4].OwnerOrdinal)) return false;
        reason = "shop_requires_complete_leave_boundary";
        if (events[at + 1].Payload is not PublicOffersObserved { Groups.Length: 1, ReplacesOfferEventOrdinal: null } offers
            || events[at + 1].OwnerOrdinal != entry.OwnerOrdinal
            || events[at + 2].Payload is not PublicOptionChosen { Key: "leave" } leave || leave.OfferEventOrdinal != at + 1
            || events[at + 2].OwnerOrdinal != entry.OwnerOrdinal
            || events[at + 3].Payload is not PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } ended
            || events[at + 3].OwnerOrdinal != entry.OwnerOrdinal) return false;
        var assets = ((PublicOwnerEnded)events[at - 1].Payload).Assets!;
        reason = "shop_leave_changed_public_assets";
        if (PublicJson.Serialize(assets) != PublicJson.Serialize(ended.Assets)) return false;
        reason = "shop_requires_all_fifteen_public_slots";
        if (!CompleteOrdinaryStock(offers)) return false;
        reason = "shop_public_hook_closure_not_certified";
        if (!NativeEventCardPoolCertificate.UnmodifiedInventory(assets) || !UnmodifiedMerchant(assets)) return false;
        boundary = new(start.Floor, at + 3, assets, offers); reason = null; return true;
    }

    internal static bool CompleteOrdinaryStock(PublicOffersObserved observed)
    {
        if (observed.Groups.Length != 1) return false;
        var group = observed.Groups[0]; var offers = group.Offers;
        return group.GroupKind == PublicOfferGroupKind.Primary && group.SelectionMode == PublicOfferSelectionMode.Independent
            && offers.Length == 15 && offers.Select(o => o.Key).SequenceEqual(Enumerable.Range(0, 7).Select(i => "card:" + i)
                .Concat(Enumerable.Range(0, 3).Select(i => "relic:" + i)).Concat(Enumerable.Range(0, 3).Select(i => "potion:" + i))
                .Concat(new[] { "remove", "leave" })) && offers.Take(13).All(o => o.Price.HasValue)
            && offers.Take(7).All(o => o.OfferKind == PublicOfferKind.Card && o.Card is { Upgrade: 0 })
            && offers.Skip(7).Take(3).All(o => o.OfferKind == PublicOfferKind.Relic && o.Relic is not null)
            && offers.Skip(10).Take(3).All(o => o.OfferKind == PublicOfferKind.Potion && o.Potion is not null)
            && offers[13].OfferKind == PublicOfferKind.Service
            && offers[14] is { OfferKind: PublicOfferKind.Continue, IsLocked: false };
    }

    private static bool UnmodifiedMerchant(PublicEvidenceAssets assets)
    {
        // UnmodifiedInventory already established catalog membership. Its room
        // and creation-hook checks also close the entry and exit dispatches.
        var types = assets.Deck.Select(c => ModelDb.All<CardModel>().Single(m => m.GetType().Name == c.Id).GetType())
            .Concat(assets.Relics.Select(r => ModelDb.All<RelicModel>().Single(m => m.GetType().Name == r.Id).GetType()))
            .Concat(assets.Potions.OfType<string>().Select(p => ModelDb.All<PotionModel>().Single(m => m.GetType().Name == p).GetType()));
        string[] hooks = [nameof(AbstractModel.ModifyMerchantCardCreationResults), nameof(AbstractModel.ModifyMerchantPrice)];
        return types.All(t => t.GetMethods().Where(m => hooks.Contains(m.Name)).All(m => m.DeclaringType == typeof(AbstractModel)));
    }
}
