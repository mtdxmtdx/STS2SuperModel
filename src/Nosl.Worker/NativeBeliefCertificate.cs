using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// A deliberately small, reviewed native carry-in family. This is a certificate for the
/// ideal conditional-permutation / independent-future-RNG profile, NOT a posterior over
/// the actual finite run seed. No seed or unrevealed RNG draw is evidence for admission.
/// </summary>
internal sealed record NativeBeliefCertificate(string Encounter)
{
    internal const string Profile = "native-ring-conch-toadpole-seapunk-exchangeable-v1";
    internal static readonly string[] Cards = ["StrikeSilent","DefendSilent","Neutralize","Survivor","AscendersBane","Acrobatics","Backflip","Prepared","ThinkingAhead","DeadlyPoison","Slimed","CloakAndDagger","DaggerThrow","Dash","LegSweep","Blur","DodgeAndRoll","BladeDance","PoisonedStab","Slice","NoxiousFumes","DaggerSpray","Footwork","Shiv"];
    private static readonly string[] Potions = ["FirePotion","BlockPotion","EnergyPotion","SwiftPotion","FruitJuice"];
    private static readonly string[] Powers = ["WeakPower","PoisonPower","StrengthPower","ThornsPower","DexterityPower","BlurPower","BlockNextTurnPower","NoxiousFumesPower"];

    internal static NativeBeliefCertificate Certify(NaturalSourceRoot root, NaturalSourceBoundary boundary)
    {
        void Require(bool condition, string reason)
        { if (!condition) throw new NotSupportedException("native_certificate:" + reason); }
        Require(boundary.State.RunState is RunState && !boundary.State.IsProjection, "not_actual_native_run");
        Require(boundary.IsStable && boundary.HistoryComplete && root.PublicRoot.Status == "player_decision", "stable_complete_history_required");
        Require(root.Encounter is "ToadpolesWeak" or "SeapunkWeak", "unreviewed_encounter");
        Require(boundary.Room.EncounterName == root.Encounter && boundary.Room.RoomType == RoomType.Monster, "room_mismatch");
        Require(boundary.State.Players.Count == 1 && boundary.State.CurrentSide == CombatSide.Player && !boundary.State.IsPlayerExtraTurn,
            "single_normal_player_turn_required");
        var player = boundary.State.Players.Single();
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
        Require(boundary.Revision == publicNow.History.Count(x => x.Kind == "action"), "action_history_incomplete");
        Require(root.PermanentDeck.Select(PublicJson.Serialize).Order(StringComparer.Ordinal).SequenceEqual(boundary.InitialAssets.Deck),
            "entry_deck_mismatch");
        Require(boundary.InitialAssets.Deck.Select(PublicJson.Read<PublicCard>).All(AllowedCard), "unreviewed_entry_card");
        Require(boundary.StartPotions.All(x => x is null || Potions.Contains(x)), "unreviewed_entry_potion");
        Require(boundary.InitialAssets.Relics.Select(PublicJson.Read<PublicRelic>).Select(x => x.Id).SequenceEqual(new[] { "RingOfTheSnake", "BoomingConch" }),
            "unreviewed_entry_relic");
        Require(AllCurrentPublicStateAllowed(publicNow), "unreviewed_current_state");
        Require(MonsterStateMatchesPublicHistory(boundary.State, boundary.Knowledge, root.Encounter, publicNow), "monster_history_mismatch");
        return new(root.Encounter);
    }

    internal bool AllowsCurrent(CombatSession session)
    {
        var packet = session.Observe();
        return packet.Status == "player_decision" && packet.Observation is { } o
            && AllCurrentPublicStateAllowed(o) && session.State.CurrentSide == CombatSide.Player && !session.State.IsPlayerExtraTurn
            && MonsterStateMatchesPublicHistory(session.State, session.Knowledge, Encounter, o);
    }

    private static bool AllowedCard(PublicCard c) => Cards.Contains(c.Id) && (c.Enchantments?.Length ?? 0) == 0 && c.Affliction is null;
    private static bool AllCurrentPublicStateAllowed(PublicObservation o) =>
        o.UnidentifiedDrawCount == 0 && o.Turn >= 1 && o.Ascension == 10
        && o.Relics.SequenceEqual(new[] { "RingOfTheSnake", "BoomingConch" })
        && o.Potions.All(x => x is null || Potions.Contains(x))
        && o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(x => x.Card)).Concat(o.KnownDraw.Select(x => x.Card)).All(AllowedCard)
        && o.Powers.Concat(o.Enemies.SelectMany(x => x.Powers)).All(x => Powers.Contains(x.Id))
        && (o.Pets?.Length ?? 0) == 0 && (o.Orbs?.Length ?? 0) == 0
        && (o.RelicStates ?? []).All(x => x.Details["isWax"] == 0 && x.Details["isMelted"] == 0 && x.Details["isUsedUp"] == 0 && x.Details["stackCount"] == 1);

    private static bool MonsterStateMatchesPublicHistory(CombatState state, PublicKnowledge knowledge, string encounter, PublicObservation o)
    {
        // The only Toadpole-specific field (IsFront) is determined by the fixed native
        // encounter and initial published intents: front Buff, rear Attack. All later
        // moves follow the deterministic cycle. Seapunk has no private model fields.
        var initial = o.History.TakeWhile(x => x.Kind != "player_turn" || x.Detail != "2")
            .Where(x => x.Kind == "intent_published").Select(x => JsonDocument.Parse(x.Detail)).ToArray();
        try
        {
            int expected = encounter == "ToadpolesWeak" ? 2 : 1;
            if (initial.Length != expected || state.Enemies.Count == 0 || state.Enemies.Count > expected) return false;
            if (state.Enemies.Select(knowledge.Slot).Distinct().Count() != state.Enemies.Count) return false;
            foreach (var e in state.Enemies)
            {
                // Enemy deaths can shrink the live list; public lifetime slots do not renumber.
                int i = knowledge.Slot(e);
                if (i < 0 || i >= expected) return false;
                var first = initial[i].RootElement;
                if (first.GetProperty("slot").GetInt32() != i) return false;
                string next;
                if (encounter == "ToadpolesWeak")
                {
                    if (e.Monster is not Toadpole toad || toad.IsFront != (i == 0) || first.GetProperty("id").GetString() != "Toadpole") return false;
                    var firstIntents = first.GetProperty("intents");
                    if (firstIntents.GetArrayLength() != 1 || firstIntents[0].GetProperty("kind").GetString() != (i == 0 ? "Buff" : "Attack")) return false;
                    string[] cycle = i == 0 ? ["SPIKEN_MOVE", "SPIKE_SPIT_MOVE", "WHIRL_MOVE"] : ["WHIRL_MOVE", "SPIKEN_MOVE", "SPIKE_SPIT_MOVE"];
                    next = cycle[(o.Turn - 1) % 3];
                }
                else
                {
                    if (e.Monster is not Seapunk || first.GetProperty("id").GetString() != "Seapunk") return false;
                    next = new[] { "SEA_KICK_MOVE", "SPINNING_KICK_MOVE", "BUBBLE_BURP_MOVE" }[(o.Turn - 1) % 3];
                }
                // Dead monsters no longer advance; their state never affects future rules.
                if (e.IsAlive && e.Monster!.NextMove?.Id != next) return false;
            }
            return true;
        }
        finally { foreach (var document in initial) document.Dispose(); }
    }
}
