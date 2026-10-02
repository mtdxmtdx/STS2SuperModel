using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// A reviewed native carry-in family. This is a certificate for the
/// ideal conditional-permutation / independent-future-RNG profile, NOT a posterior over
/// the actual finite run seed. No seed or unrevealed RNG draw is evidence for admission.
/// </summary>
internal sealed record NativeBeliefCertificate(string Encounter, string[] EntryRelics, bool HasGenerationPotionPrior)
{
    internal const string Profile = "native-public-entry-reviewed-memory-exchangeable-v2";
    internal string StableProfile => HasGenerationPotionPrior ? NativeGenerationPotionMemory.Profile : Profile;
    internal static readonly string[] Cards = ["StrikeSilent","DefendSilent","Neutralize","Survivor","AscendersBane","Acrobatics","Backflip","Prepared","ThinkingAhead","DeadlyPoison","Slimed","CloakAndDagger","DaggerThrow","Dash","LegSweep","Blur","DodgeAndRoll","BladeDance","PoisonedStab","Slice","NoxiousFumes","DaggerSpray","Footwork","Shiv",
        "SuckerPunch","Untouchable","Reflex","Tactician","Snakebite","Expose","LeadingStrike","Speedster","Skewer","Dazed"];
    private static readonly string[] Relics = ["RingOfTheSnake","BoomingConch","LavaRock","FishingRod","LeadPaperweight","WingedBoots","Pomander","BoneTea","RegalPillow","WarPaint"];
    private static readonly string[] Potions = ["FirePotion","BlockPotion","EnergyPotion","SwiftPotion","FruitJuice",
        "FlexPotion","VulnerablePotion","PowderedDemise","Clarity"];
    private static readonly string[] Powers = ["WeakPower","PoisonPower","StrengthPower","ThornsPower","DexterityPower","BlurPower","BlockNextTurnPower","NoxiousFumesPower",
        "VulnerablePower","ArtifactPower","ShrinkPower","SpeedsterPower","TangledPower",
        "FlexPotionPower","DemisePower","ClarityPower","HardenedShellPower"];

    internal static NativeBeliefCertificate Certify(NaturalSourceRoot root, NaturalSourceBoundary boundary)
    {
        void Require(bool condition, string reason)
        { if (!condition) throw new NotSupportedException("native_certificate:" + reason); }
        Require(boundary.State.RunState is RunState && !boundary.State.IsProjection, "not_actual_native_run");
        Require(boundary.IsStable && boundary.HistoryComplete && root.PublicRoot.Status == "player_decision", "stable_complete_history_required");
        Require(NativeMonsterMemory.Encounters.Contains(root.Encounter), "unreviewed_encounter");
        Require(boundary.Room.EncounterName == root.Encounter && boundary.Room.RoomType == NativeMonsterMemory.RoomTypeFor(root.Encounter)
            && root.RoomType == boundary.Room.RoomType.ToString(), "room_mismatch");
        Require(boundary.State.Players.Count == 1 && boundary.State.CurrentSide == CombatSide.Player && !boundary.State.IsPlayerExtraTurn,
            "single_normal_player_turn_required");
        var player = boundary.State.Players.Single();
        Require(ActualPriorMatches(player), "actual_silent_a10_required");
        Require(!boundary.State.RunState.Rng.UsesSemanticKeys && !player.PlayerRng.UsesSemanticKeys, "sequential_source_required");
        Require(player.PlayerCombatState!.Phase == PlayerTurnPhase.Play, "unstable_player_phase");
        Require(boundary.InitialAssets.MaxEnergy == 3 && boundary.InitialAssets.PotionSlots == 2 && boundary.InitialAssets.OrbSlots == 0
            && player.MaxEnergy == 3 && player.MaxPotionCount == 2 && player.BaseOrbSlotCount == 0 && player.CanUseOrRemovePotions,
            "unreviewed_permanent_capacities");
        Require(root.StartHp == boundary.StartHp && root.StartMaxHp == boundary.StartMaxHp && root.StartGold == boundary.StartGold,
            "start_asset_mismatch");
        var publicNow = PublicViews.Observe(boundary.State, boundary.Knowledge, boundary.StartHp, null, boundary.StartGold);
        Require(PublicJson.Serialize(publicNow) == PublicJson.Serialize(root.PublicRoot.Observation), "public_root_mismatch");
        Require(publicNow.History.Count(x => x.Kind == "combat_started") == 1 && publicNow.History.Any(x => x.Kind == "player_turn" && x.Detail == "1"),
            "entry_history_missing");
        var entryEvents = publicNow.History.Where(x => x.Kind == NativeEntryAssets.EventKind).ToArray();
        Require(entryEvents.Length == 1 && publicNow.History.Length > 1 && publicNow.History[1] == entryEvents[0]
            && entryEvents[0].Detail == PublicJson.Serialize(NativeEntryAssets.Capture(boundary.InitialAssets, boundary.StartHp, boundary.StartPotions)),
            "public_entry_assets_missing_or_mismatched");
        Require(boundary.Revision == publicNow.History.Count(x => x.Kind == "action"), "action_history_incomplete");
        Require(root.PermanentDeck.Select(PublicJson.Serialize).Order(StringComparer.Ordinal).SequenceEqual(boundary.InitialAssets.Deck),
            "entry_deck_mismatch");
        Require(boundary.InitialAssets.Deck.Select(PublicJson.Read<PublicCard>).All(c => AllowedCard(c) && c.Affliction is null), "unreviewed_entry_card");
        // Player.PopulateCombatState places Innate cards above the initial shuffle.
        // All must fit in the seven-card Ring opening hand; otherwise residual draw
        // positions need a stratified posterior rather than a uniform permutation.
        Require(root.PermanentDeck.Count(c => c.Keywords.Contains("Innate")) <= 7, "unrepresented_innate_order");
        Require(boundary.StartPotions.All(AllowedPotion), "unreviewed_entry_potion");
        Require(NativeGenerationPotionMemory.Untouched(publicNow), "generation_potion_already_used");
        bool generationPrior = boundary.StartPotions.Any(NativeGenerationPotionMemory.IsGenerationPotion);
        Require(generationPrior || !publicNow.Potions.Any(NativeGenerationPotionMemory.IsGenerationPotion), "generation_potion_entry_required");
        Require(!generationPrior || NativeGenerationPotionMemory.PoolsMatch(player), "generation_potion_pool_changed");
        var entryRelics = boundary.InitialAssets.Relics.Select(PublicJson.Read<PublicRelic>).ToArray();
        Require(entryRelics.All(AllowedRelic), "unreviewed_entry_relic");
        Require(entryRelics.Select(r => r.Id).SequenceEqual(publicNow.Relics), "entry_relic_mismatch");
        Require(AllCurrentPublicStateAllowed(publicNow), "unreviewed_current_state");
        Require(NativeMonsterMemory.Matches(boundary.State, boundary.Knowledge, root.Encounter, publicNow), "monster_history_mismatch");
        Require(NativePowerMemory.Matches(boundary.State, boundary.Knowledge, publicNow), "power_history_mismatch");
        return new(root.Encounter, publicNow.Relics.ToArray(), generationPrior);
    }

    internal bool AllowsCurrent(CombatSession session)
    {
        var packet = session.Observe();
        return packet.Status == "player_decision" && packet.Observation is { } o
            && ActualPriorMatches(session.State.Players.Single())
            && AllCurrentPublicStateAllowed(o) && o.Relics.SequenceEqual(EntryRelics)
            && NativeGenerationPotionMemory.Untouched(o)
            && (HasGenerationPotionPrior || !o.Potions.Any(NativeGenerationPotionMemory.IsGenerationPotion))
            && (!HasGenerationPotionPrior || NativeGenerationPotionMemory.PoolsMatch(session.State.Players.Single()))
            && session.State.CurrentSide == CombatSide.Player && !session.State.IsPlayerExtraTurn
            && NativeMonsterMemory.Matches(session.State, session.Knowledge, Encounter, o)
            && NativePowerMemory.Matches(session.State, session.Knowledge, o);
    }

    internal void PrepareSample(CombatSession world)
    {
        // FishingRod samples uniformly from upgradable permanent cards at settlement.
        // Public entry exposes a multiset, not its private backing-list order. A
        // canonical ordering makes the independent sampler coupling depend only on
        // public signatures while preserving every card and CloneOf reference.
        var deck = world.State.Players.Single().Deck;
        var canonical = deck.Cards.OrderBy(c => PublicJson.Serialize(PublicViews.Card(c)), StringComparer.Ordinal).ToArray();
        foreach (var card in deck.Cards.ToArray()) deck.RemoveInternal(card);
        foreach (var card in canonical) deck.AddInternal(card);
    }

    // PublicViews has a fixed A10 DTO; admission must inspect the actual run as well.
    private static bool ActualPriorMatches(Player player) => player.Character is Silent
        && player.RunState.Ascension.HasLevel((AscensionLevel)10)
        && !player.RunState.Ascension.HasLevel((AscensionLevel)11);
    private static bool AllowedPotion(string? id) => id is null || Potions.Contains(id)
        || NativeGenerationPotionMemory.IsGenerationPotion(id);

    private static bool AllowedCard(PublicCard c) => Cards.Contains(c.Id) && (c.Enchantments?.Length ?? 0) == 0
        && (c.Affliction is null || c.Affliction.Id == "Entangled");
    private static bool AllowedRelic(PublicRelic r) => Relics.Contains(r.Id)
        && r.Details["isWax"] == 0 && r.Details["isMelted"] == 0 && r.Details["stackCount"] == 1
        && (r.Cards?.Length ?? 0) == 0 && r.SelectedModel is null;
    private static bool AllCurrentPublicStateAllowed(PublicObservation o) =>
        o.UnidentifiedDrawCount == 0 && o.Turn >= 1 && o.Ascension == 10
        && o.Relics.Length > 0 && o.Relics[0] == "RingOfTheSnake" && o.Relics.Distinct().Count() == o.Relics.Length
        && o.Potions.All(AllowedPotion)
        && o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(x => x.Card)).Concat(o.KnownDraw.Select(x => x.Card)).All(AllowedCard)
        && o.Powers.Concat(o.Enemies.SelectMany(x => x.Powers)).All(x => Powers.Contains(x.Id))
        && (o.Pets?.Length ?? 0) == 0 && (o.Orbs?.Length ?? 0) == 0
        && o.RelicStates is { } relicStates && relicStates.Select(r => r.Id).SequenceEqual(o.Relics)
        && relicStates.All(AllowedRelic);
}
