using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Entities.Players;

namespace Nosl.Worker;

/// <summary>Immutable public asset facts. Signatures have no private object identity or RNG.</summary>
internal sealed record CombatAssetSnapshot(int Gold, int MaxHp, string[] Deck, string[] Relics, int MaxEnergy, int PotionSlots, int OrbSlots, int CardRemovalsUsed)
{
    internal static CombatAssetSnapshot Capture(Player player) => new(player.Gold, player.Creature.MaxHp,
        player.Deck.Cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))).Order(StringComparer.Ordinal).ToArray(),
        player.Relics.Select(r => PublicJson.Serialize(new PublicRelic(r.GetType().Name,
            PublicRelicDetails.Details(r), PublicRelicDetails.Cards(r), PublicRelicDetails.SelectedModel(r)))).ToArray(),
        player.MaxEnergy,player.MaxPotionCount,player.BaseOrbSlotCount,player.CardRemovalsUsed);

    internal static PermanentChange[] Changes(CombatAssetSnapshot start, CombatAssetSnapshot end)
    {
        var result = new List<PermanentChange>();
        if (start.MaxHp != end.MaxHp) result.Add(new("max_hp", end.MaxHp - start.MaxHp, "settled_asset_snapshot"));
        if (start.Gold != end.Gold) result.Add(new("gold", end.Gold - start.Gold, "settled_asset_snapshot"));
        if (!start.Deck.SequenceEqual(end.Deck)) result.Add(new("permanent_deck_change", 1, "settled_asset_snapshot; preserve exact start/end signatures in audit"));
        if (!start.Relics.SequenceEqual(end.Relics)) result.Add(new("relic_state_change", 1, "settled_asset_snapshot; includes persistent counters and conservative transient differences"));
        if(start.MaxEnergy!=end.MaxEnergy) result.Add(new("max_energy",end.MaxEnergy-start.MaxEnergy,"settled_asset_snapshot"));
        if(start.PotionSlots!=end.PotionSlots) result.Add(new("potion_slots",end.PotionSlots-start.PotionSlots,"settled_asset_snapshot"));
        if(start.OrbSlots!=end.OrbSlots) result.Add(new("base_orb_slots",end.OrbSlots-start.OrbSlots,"settled_asset_snapshot"));
        if(start.CardRemovalsUsed!=end.CardRemovalsUsed) result.Add(new("card_removals_used",end.CardRemovalsUsed-start.CardRemovalsUsed,"settled_asset_snapshot"));
        return result.ToArray();
    }
}
