using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Player-visible carry-in facts, published before any combat setup effects.</summary>
internal sealed record NativeEntryAssets(string SchemaVersion, int Hp, int MaxHp, int Gold,
    PublicCard[] Deck, PublicRelic[] Relics, string?[] Potions,
    int MaxEnergy, int PotionSlots, int OrbSlots, int CardRemovalsUsed)
{
    internal const string EventKind = "native_entry_assets";
    internal static NativeEntryAssets Capture(CombatAssetSnapshot assets, int hp, string?[] potions) =>
        new("nosl.native-entry-assets.v1", hp, assets.MaxHp, assets.Gold,
            assets.Deck.Select(PublicJson.Read<PublicCard>).ToArray(),
            assets.Relics.Select(PublicJson.Read<PublicRelic>).ToArray(), potions.ToArray(),
            assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
}
