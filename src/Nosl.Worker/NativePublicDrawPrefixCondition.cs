using System.Reflection;
using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;

namespace Nosl.Worker;

internal sealed record NativePublicDrawPrefixAudit(string CertificateVersion, int InitialDrawCount,
    int DrawPrefixCount, long ThroughEventOrdinal, string StopReason);

/// <summary>
/// A source-pinned transition closure, not an inference from an apparently quiet event log.
/// CardMoved omits arbitrary pile moves. Every crossed action must therefore prove that its
/// entire native continuation preserves the undrawn initial pile except for ordinary Draw.
/// Failure stops acceleration at the already proved prefix; no public evidence is removed.
/// </summary>
internal static class NativePublicDrawPrefixCondition
{
    internal const string Version = "nosl.public-first-draw-cycle.v1";

    // Read the complete sealed OnPlay implementations, including both upgrade branches and
    // inherited result locations. Strike/Defend's GeneratedCardSpec has no GeneratedPowerEffect.
    // Effects are damage/block, Weak, hand-only discard (Survivor), and ordinary Draw (Backflip).
    // Mirage's Poison lookup is read-only. These plays never generate, transform, insert into,
    // reorder, or select from Draw. CardModel's result path goes only to Discard/Exhaust.
    private static readonly HashSet<string> PlayedCards =
    [nameof(StrikeSilent), nameof(DefendSilent), nameof(Neutralize), nameof(Survivor),
        nameof(Backflip), nameof(Deflect), nameof(Mirage)];
    // All callbacks and branch selection reviewed, not merely the published numeric intent.
    // CorpseSlug: damage/Frail/stun; SludgeSpinner: damage/Weak/Strength. Neither summons.
    private static readonly HashSet<string> EnemyTurns = [nameof(CorpseSlug), nameof(SludgeSpinner)];
    // Exact PowerModel implementations, including BeforeApplied/AfterApplied/AfterRemoved.
    // Weak/Frail only scale values/tick duration, Strength scales damage, Ravenous changes
    // CorpseSlug AI and applies Strength on a death. No closure member produces an unreviewed power.
    private static readonly HashSet<string> Powers =
        [nameof(WeakPower), nameof(FrailPower), nameof(StrengthPower), nameof(RavenousPower)];
    // OnUse is reviewed separately from AbstractModel hooks; PotionUsed occurs AFTER effects.
    private static readonly HashSet<string> UsedPotions =
        [nameof(FirePotion), nameof(BlockPotion), nameof(EnergyPotion), nameof(StrengthPotion), nameof(SwiftPotion)];
    private static readonly Dictionary<string, Type> CardTypes = ContentRegistry.AllTypes
        .Where(type => type.IsSealed && typeof(CardModel).IsAssignableFrom(type))
        .ToDictionary(type => type.Name, StringComparer.Ordinal);

    internal static NativeInitialShuffleCondition Extend(NativeInitialShuffleCondition initial,
        PublicRunEvidenceEvent[] ownerEvents, PublicRunEvidenceEvent first, long? globalGap,
        out NativePublicDrawPrefixAudit audit)
    {
        var entry = PublicJson.Read<NativeEntryAssets>(initial.EntryJson);
        var prefix = initial.DrawPrefixIds.ToList();
        int initialCount = prefix.Count;
        long through = ownerEvents.Where(item => item.EventOrdinal < first.EventOrdinal
            && item.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardDrawn })
            .Take(initialCount).Last().EventOrdinal;
        var decision = (PublicCombatDecision)first.Payload;
        long decisionOrdinal = first.EventOrdinal;
        var roster = decision.Observation.Enemies.ToDictionary(enemy => enemy.Slot, enemy => enemy.Id);
        var remaining = entry.Deck.GroupBy(card => card.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (string id in prefix) remaining[id]--;
        string? stop = CheckSnapshot(decision);
        string? activeKind = null, activeModel = null, pendingCard = null;
        bool decisionAvailable = true;
        if (stop is null)
        {
            through = first.EventOrdinal;
            foreach (var observed in ownerEvents.Where(item => item.EventOrdinal > first.EventOrdinal))
            {
                if (globalGap is { } gap && observed.EventOrdinal >= gap)
                { stop = "global_evidence_gap"; break; }
                stop = Advance(observed);
                if (stop is not null) break;
                through = observed.EventOrdinal;
            }
        }
        audit = new(Version, initialCount, prefix.Count, through, stop ?? "observed_prefix_complete");
        return prefix.Count == initialCount ? initial : initial.ExtendDrawPrefix(prefix);

        string? CheckSnapshot(PublicCombatDecision snapshot)
        {
            var observation = snapshot.Observation;
            if (!snapshot.HistoryCompleteFromCombatStart) return "complete_combat_history_required";
            if (!observation.Relics.Order(StringComparer.Ordinal).SequenceEqual(entry.Relics.Select(relic => relic.Id).Order(StringComparer.Ordinal))
                || observation.OrbCapacity != 0 || (observation.Orbs?.Length ?? 0) != 0 || (observation.Pets?.Length ?? 0) != 0)
                return "draw_cycle_listener_set_changed";
            if (observation.Enemies.Any(enemy => !roster.TryGetValue(enemy.Slot, out string? id) || id != enemy.Id))
                return "draw_cycle_monster_roster_changed";
            if (observation.Powers.Concat(observation.Enemies.SelectMany(enemy => enemy.Powers)).Any(power => !Powers.Contains(power.Id)))
                return "draw_cycle_power_not_certified";
            if (observation.UnidentifiedDrawCount != 0 || observation.KnownDraw.Length != 0
                || observation.DrawCount != remaining.Values.Sum()) return "draw_cycle_snapshot_pool_mismatch";
            var actual = observation.UnknownDraw.GroupBy(item => item.Card.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.Ordinal);
            if (actual.Any(pair => !remaining.TryGetValue(pair.Key, out int count) || count != pair.Value)
                || remaining.Any(pair => pair.Value > 0 && !actual.ContainsKey(pair.Key)))
                return "draw_cycle_snapshot_pool_mismatch";
            if (observation.Hand.Concat(observation.Discard).Concat(observation.Exhaust)
                .Concat(observation.UnknownDraw.Select(item => item.Card)).Any(card => !SafeCardMetadata(card)))
                return "draw_cycle_card_modifier_not_certified";
            if (observation.Choice is { Source: not nameof(Survivor) }) return "draw_cycle_choice_not_certified";
            return null;
        }

        string? Advance(PublicRunEvidenceEvent observed)
        {
            switch (observed.Payload)
            {
                case PublicEvidenceGap: return "owner_evidence_gap";
                case PublicOwnerEnded: return "combat_owner_ended";
                case PublicCombatDecision next:
                    if (CheckSnapshot(next) is { } snapshotReason) return snapshotReason;
                    decision = next; decisionOrdinal = observed.EventOrdinal; decisionAvailable = true;
                    activeKind = activeModel = null;
                    if (next.Status != "card_choice") pendingCard = null;
                    return null;
                case PublicCombatActionTaken taken:
                    if (!decisionAvailable || taken.DecisionEventOrdinal != decisionOrdinal) return "draw_cycle_action_snapshot_missing";
                    decisionAvailable = false;
                    var action = taken.Action;
                    var observation = decision.Observation;
                    activeKind = action.Kind;
                    activeModel = null;
                    switch (action.Kind)
                    {
                        case "play":
                            var card = observation.Hand[action.Slot];
                            if (!PlayedCards.Contains(card.Id) || !SafeCardMetadata(card)) return "draw_cycle_play_not_certified:" + card.Id;
                            // CardCmd.Discard can autoplay a selected Sly card without a new
                            // public action. Stop before Survivor, including automatic selection.
                            if (card.Id == nameof(Survivor) && observation.Hand.Any(IsSly))
                                return "draw_cycle_sly_discard_not_certified";
                            activeModel = pendingCard = card.Id;
                            return null;
                        case "potion":
                            activeModel = observation.Potions[action.Slot];
                            return activeModel is not null && UsedPotions.Contains(activeModel) ? null
                                : "draw_cycle_potion_not_certified:" + activeModel;
                        case "discard_potion": return null;
                        case "choose":
                            if (pendingCard != nameof(Survivor) || observation.Choice?.Source != nameof(Survivor)
                                || observation.Hand.Any(IsSly) || observation.Choice.Candidates.Any(IsSly))
                                return "draw_cycle_choice_not_certified";
                            activeModel = pendingCard;
                            return null;
                        case "end_turn":
                            if (observation.Enemies.Any(enemy => !EnemyTurns.Contains(enemy.Id)))
                                return "draw_cycle_enemy_turn_not_certified";
                            // The protected turn-end surface is outside AbstractModel reflection.
                            // Base false/no-op proves ordinary flush or Ethereal exhaust affects
                            // only the currently public hand, including unseen card IDs elsewhere.
                            if (observation.Hand.Any(card => !HasNoTurnEndEffect(card.Id)))
                                return "draw_cycle_hand_end_effect_not_certified";
                            return null;
                        default: return "draw_cycle_action_not_certified";
                    }
                case PublicCombatFact fact:
                    if (fact.FactKind == PublicCombatFactKind.Shuffled) return "first_reshuffle";
                    if (fact.FactKind is PublicCombatFactKind.CardGenerated or PublicCombatFactKind.HiddenCardGenerated)
                        return "draw_cycle_generation_not_certified";
                    if (fact.FactKind == PublicCombatFactKind.PreSettlement) return "combat_pre_settlement";
                    if (activeKind is null) return "draw_cycle_effect_without_certified_action";
                    switch (fact.FactKind)
                    {
                        case PublicCombatFactKind.CardDrawn:
                            if (activeKind != "end_turn" && activeModel is not (nameof(Backflip) or nameof(SwiftPotion)))
                                return "draw_cycle_draw_source_not_certified";
                            var drawn = fact.Cards.Single();
                            if (!SafeCardMetadata(drawn) || !remaining.TryGetValue(drawn.Id, out int count) || count == 0)
                                return "draw_cycle_draw_pool_mismatch";
                            prefix.Add(drawn.Id); remaining[drawn.Id]--;
                            return null;
                        case PublicCombatFactKind.CardStarted:
                        case PublicCombatFactKind.CardPlayed:
                            var played = fact.Cards.Single();
                            if (played.Id != activeModel || !PlayedCards.Contains(played.Id) || !SafeCardMetadata(played)
                                || fact.ResultPile is PublicCardPile.Draw or PublicCardPile.Hand or PublicCardPile.Play)
                                return "draw_cycle_card_effect_not_certified";
                            return null;
                        case PublicCombatFactKind.PowerChanged:
                            return Powers.Contains(fact.Model!) ? null : "draw_cycle_power_not_certified:" + fact.Model;
                        case PublicCombatFactKind.ChoiceOffered:
                            return activeModel == nameof(Survivor) && fact.Choice?.Source == nameof(Survivor)
                                ? null : "draw_cycle_choice_not_certified";
                        case PublicCombatFactKind.AutomaticSelection:
                            return activeModel == nameof(Survivor) && fact.UnidentifiedCount == 0
                                ? null : "draw_cycle_selection_not_certified";
                        case PublicCombatFactKind.PotionUsed:
                            return activeKind == "potion" && fact.Model == activeModel ? null : "draw_cycle_potion_not_certified";
                        case PublicCombatFactKind.Damage: return null;
                        case PublicCombatFactKind.PlayerTurnStarted:
                        case PublicCombatFactKind.PlayerTurnEnded:
                            return activeKind == "end_turn" ? null : "draw_cycle_turn_not_certified";
                        case PublicCombatFactKind.IntentPublished:
                            return roster.TryGetValue(fact.TargetSlot!.Value, out string? id) && id == fact.Model
                                ? null : "draw_cycle_monster_roster_changed";
                        default: return "draw_cycle_fact_not_certified";
                    }
                default: return "draw_cycle_event_not_certified";
            }
        }
    }

    private static bool IsSly(PublicCard card) => card.Keywords.Contains("Sly", StringComparer.Ordinal)
        || card.Details?.SlyThisTurn == true;

    private static bool SafeCardMetadata(PublicCard card) => card.Affliction is null
        && (card.Enchantments ?? []).All(effect => effect.Id == nameof(Sharp));

    private static bool HasNoTurnEndEffect(string id) => CardTypes.TryGetValue(id, out Type? type)
        && type.GetMethod("OnTurnEndInHand", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType == typeof(CardModel)
        && type.GetProperty("HasTurnEndInHandEffect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!.DeclaringType == typeof(CardModel);
}
