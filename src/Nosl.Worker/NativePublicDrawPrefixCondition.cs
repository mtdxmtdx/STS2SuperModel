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
    internal const string Version = "nosl.public-first-draw-cycle.v4";

    // Read the complete sealed OnPlay implementations, including both upgrade branches and
    // inherited result locations. Strike/Defend's GeneratedCardSpec has no GeneratedPowerEffect.
    // Effects are damage/block, Weak, hand-only discard (Survivor), and ordinary Draw (Backflip).
    // NeowsFury's bounded retrieval calls plain Add(Discard -> Hand, Bottom), which does
    // not dispatch draw/entry/discard hooks, select randomly, or autoplay Sly. Finisher's
    // hit count reads the completed-Attack counter; SuckerPunch only adds reviewed Weak.
    // Strangle adds the separately reviewed StranglePower below. Both upgrade branches
    // and the inherited result paths were checked for all four v2 additions.
    // Mirage's Poison lookup is read-only. These plays never generate, transform, insert into,
    // reorder, or select from Draw. CardModel's result path goes only to Discard/Exhaust.
    private static readonly HashSet<string> PlayedCards =
    [nameof(StrikeSilent), nameof(DefendSilent), nameof(Neutralize), nameof(Survivor),
        nameof(Backflip), nameof(Deflect), nameof(Mirage), nameof(NeowsFury), nameof(Strangle),
        nameof(Finisher), nameof(SuckerPunch), nameof(Slimed)];
    // All callbacks and branch selection reviewed, not merely the published numeric intent.
    // CorpseSlug: damage/Frail/stun; SludgeSpinner: damage/Weak/Strength; Toadpole:
    // fixed front/rear cyclic attacks and Thorns +/-2. None summons or touches piles.
    // Twig/Leaf attacks only damage; their other moves generate plain Slimed to Discard.
    // Their entire graphs and entry/generation callbacks are reviewed, not just intent IDs.
    private static readonly HashSet<string> EnemyTurns = [nameof(CorpseSlug), nameof(SludgeSpinner), nameof(Toadpole),
        nameof(TwigSlimeS), nameof(TwigSlimeM), nameof(LeafSlimeS), nameof(LeafSlimeM)];
    // Exact PowerModel implementations, including BeforeApplied/AfterApplied/AfterRemoved.
    // Weak/Frail only scale values/tick duration, Strength scales damage, Ravenous changes
    // CorpseSlug AI and applies Strength on a death. StranglePower only snapshots its
    // amount BeforeCardPlayed, removes that snapshot and deals direct damage AfterCardPlayed,
    // and removes itself at its owner's side end. Application/removal inherit no-ops;
    // its cloning/description methods never change live piles. All resulting damage,
    // power and death callbacks remain inside this same listener closure.
    // ThornsPower only retaliates through unpowered direct damage. Its powered-hit
    // guard prevents retaliation recursion; all application/removal hooks are base no-ops.
    // No closure member produces an unreviewed power.
    private static readonly HashSet<string> Powers =
        [nameof(WeakPower), nameof(FrailPower), nameof(StrengthPower), nameof(RavenousPower), nameof(StranglePower), nameof(ThornsPower)];
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
        var result = Scan(initial, initial.DrawPrefixIds.Length, ownerEvents, first, globalGap, false);
        audit = result.Audit;
        return result.InitialPrefix.Length == initial.DrawPrefixIds.Length ? initial
            : initial.ExtendDrawPrefix(result.InitialPrefix);
    }

    internal static IReadOnlyList<NativePublicReshuffleInput> FindReshuffles(
        NativeInitialShuffleCondition initial, int initialDrawCount, PublicRunEvidenceEvent[] ownerEvents,
        PublicRunEvidenceEvent first, long? globalGap, out NativePublicDrawPrefixAudit audit)
    {
        var result = Scan(initial, initialDrawCount, ownerEvents, first, globalGap, true);
        audit = result.Audit;
        return result.Reshuffles;
    }

    private sealed record ScanResult(string[] InitialPrefix, NativePublicDrawPrefixAudit Audit,
        IReadOnlyList<NativePublicReshuffleInput> Reshuffles);
    private sealed class Cycle(int ordinal, long shuffleEvent)
    {
        internal int Ordinal { get; } = ordinal;
        internal long ShuffleEvent { get; } = shuffleEvent;
        internal long? WitnessEvent { get; set; }
        internal string[]? Pool { get; set; }
        internal List<string> Prefix { get; } = [];
    }

    private static ScanResult Scan(NativeInitialShuffleCondition initial, int initialDrawCount,
        PublicRunEvidenceEvent[] ownerEvents, PublicRunEvidenceEvent first, long? globalGap, bool allowReshuffles)
    {
        var entry = PublicJson.Read<NativeEntryAssets>(initial.EntryJson);
        var prefix = initial.DrawPrefixIds.Take(initialDrawCount).ToList();
        var cycles = new List<Cycle>();
        Cycle? cycle = null;
        int initialCount = prefix.Count;
        long through = ownerEvents.Where(item => item.EventOrdinal < first.EventOrdinal
            && item.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardDrawn })
            .Take(initialCount).Last().EventOrdinal;
        var decision = (PublicCombatDecision)first.Payload;
        long decisionOrdinal = first.EventOrdinal;
        var roster = decision.Observation.Enemies.ToDictionary(enemy => enemy.Slot, enemy => enemy.Id);
        var inventory = entry.Deck.GroupBy(card => card.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int>? remaining = new(inventory, StringComparer.Ordinal);
        foreach (string id in prefix) remaining[id]--;
        string? stop = CheckSnapshot(decision, first.EventOrdinal);
        string? activeKind = null, activeModel = null, pendingCard = null;
        int pendingSlimed = 0;
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
        return new(prefix.ToArray(), new(Version, initialCount, prefix.Count, through, stop ?? "observed_prefix_complete"),
            cycles.Where(item => item.Pool is not null && item.WitnessEvent is not null && item.Prefix.Count > 0)
                .Select(item => new NativePublicReshuffleInput(item.Ordinal, item.ShuffleEvent,
                    item.WitnessEvent!.Value, item.Pool!, item.Prefix)).ToArray());

        string? CheckSnapshot(PublicCombatDecision snapshot, long eventOrdinal)
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
                || observation.UnknownDraw.Any(item => item.Count <= 0)
                || observation.DrawCount != observation.UnknownDraw.Sum(item => item.Count))
                return "draw_cycle_snapshot_pool_mismatch";
            var actual = observation.UnknownDraw.GroupBy(item => item.Card.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.Ordinal);
            if (remaining is not null && (observation.DrawCount != remaining.Values.Sum()
                || actual.Any(pair => !remaining.TryGetValue(pair.Key, out int count) || count != pair.Value)
                || remaining.Any(pair => pair.Value > 0 && !actual.ContainsKey(pair.Key))))
                return "draw_cycle_snapshot_pool_mismatch";
            if (observation.Hand.Concat(observation.Discard).Concat(observation.Exhaust)
                .Concat(observation.UnknownDraw.Select(item => item.Card)).Any(card => !SafeCardMetadata(card)))
                return "draw_cycle_card_modifier_not_certified";
            if (observation.Choice is { Source: not nameof(Survivor) }) return "draw_cycle_choice_not_certified";
            if (remaining is null)
            {
                // Joint public witness: closure permits only Draw between this native
                // reshuffle and the snapshot. No hidden order or physical membership is read.
                var pool = actual.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value))
                    .Concat(cycle!.Prefix).ToArray();
                // Entry plus explicitly witnessed generation is only an upper bound:
                // exhaust/hand membership can vary. This snapshot fixes the EXACT pool,
                // so the correction envelope never depends on a sampled latent pool.
                if (pool.Length == 0 || pool.GroupBy(id => id, StringComparer.Ordinal)
                    .Any(group => !inventory.TryGetValue(group.Key, out int count) || group.Count() > count))
                    return "reshuffle_snapshot_pool_mismatch";
                cycle.Pool = pool; cycle.WitnessEvent = eventOrdinal;
                remaining = new(actual, StringComparer.Ordinal);
            }
            return null;
        }

        string? Advance(PublicRunEvidenceEvent observed)
        {
            switch (observed.Payload)
            {
                case PublicEvidenceGap: return "owner_evidence_gap";
                case PublicOwnerEnded: return "combat_owner_ended";
                case PublicCombatDecision next:
                    if (CheckSnapshot(next, observed.EventOrdinal) is { } snapshotReason) return snapshotReason;
                    decision = next; decisionOrdinal = observed.EventOrdinal; decisionAvailable = true;
                    activeKind = activeModel = null;
                    pendingSlimed = 0;
                    if (next.Status != "card_choice") pendingCard = null;
                    return null;
                case PublicCombatActionTaken taken:
                    if (!decisionAvailable || taken.DecisionEventOrdinal != decisionOrdinal) return "draw_cycle_action_snapshot_missing";
                    decisionAvailable = false;
                    var action = taken.Action;
                    var observation = decision.Observation;
                    activeKind = action.Kind;
                    activeModel = null;
                    pendingSlimed = 0;
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
                            // Under the complete listener/turn closure, these are the ONLY
                            // possible generation sources. StatusIntent omits its count;
                            // exact sealed move implementations fix it at one or two.
                            pendingSlimed = observation.Enemies.Sum(SlimedGenerationLimit);
                            return null;
                        default: return "draw_cycle_action_not_certified";
                    }
                case PublicCombatFact fact:
                    if (fact.FactKind == PublicCombatFactKind.Shuffled)
                    {
                        if (!allowReshuffles) return "first_reshuffle";
                        if (activeKind != "end_turn" && !IsOrdinaryDrawSource(activeModel))
                            return "reshuffle_source_not_certified";
                        if (remaining is null) return "reshuffle_public_witness_missing";
                        if (remaining.Values.Sum() != 0) return "reshuffle_before_draw_pool_exhausted";
                        cycle = new(cycles.Count, observed.EventOrdinal); cycles.Add(cycle);
                        remaining = null;
                        pendingSlimed = 0;
                        return null;
                    }
                    if (fact.FactKind == PublicCombatFactKind.HiddenCardGenerated)
                        return "draw_cycle_generation_not_certified";
                    if (fact.FactKind == PublicCombatFactKind.CardGenerated)
                    {
                        // The public fact carries no destination. Only this closed native
                        // enemy-turn path proves Discard; visible generation alone cannot.
                        if (activeKind != "end_turn" || pendingSlimed == 0 || !IsPlainGeneratedSlimed(fact.Cards.Single()))
                            return "draw_cycle_generation_not_certified";
                        pendingSlimed--;
                        inventory[nameof(Slimed)] = inventory.GetValueOrDefault(nameof(Slimed)) + 1;
                        return null;
                    }
                    if (fact.FactKind == PublicCombatFactKind.PreSettlement) return "combat_pre_settlement";
                    if (activeKind is null) return "draw_cycle_effect_without_certified_action";
                    switch (fact.FactKind)
                    {
                        case PublicCombatFactKind.CardDrawn:
                            if (activeKind != "end_turn" && !IsOrdinaryDrawSource(activeModel))
                                return "draw_cycle_draw_source_not_certified";
                            pendingSlimed = 0;
                            var drawn = fact.Cards.Single();
                            if (!SafeCardMetadata(drawn)) return "draw_cycle_draw_pool_mismatch";
                            if (remaining is not null)
                            {
                                if (!remaining.TryGetValue(drawn.Id, out int count) || count == 0)
                                    return "draw_cycle_draw_pool_mismatch";
                                remaining[drawn.Id]--;
                            }
                            if (cycle is null) prefix.Add(drawn.Id); else cycle.Prefix.Add(drawn.Id);
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
                            pendingSlimed = 0;
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

    // Slimed's complete OnPlay is ordinary Draw(1, fromHandDraw:false), with inherited
    // Exhaust result. Its entry/draw/exhaust/turn-end and generation hooks are base no-ops.
    private static bool IsOrdinaryDrawSource(string? model) => model is nameof(Backflip) or nameof(SwiftPotion) or nameof(Slimed);

    private static int SlimedGenerationLimit(PublicEnemy enemy) => enemy.Hp > 0
        && enemy.Intents is [{ Kind: "StatusCard", Damage: null, Repeats: null }]
        ? enemy.Id switch { nameof(TwigSlimeM) or nameof(LeafSlimeS) => 1, nameof(LeafSlimeM) => 2, _ => 0 }
        : 0;

    internal static bool IsPlainGeneratedSlimed(PublicCard card) => card is
        { Id: nameof(Slimed), Upgrade: 0, Cost: 1, StarCost: -1, Type: "Status", Affliction: null }
        && card.Keywords.SequenceEqual(["Exhaust"])
        && (card.Enchantments?.Length ?? 0) == 0
        && card.Details is { CostsXEnergy: false, CostsXStar: false, LocalEnergyCost: 1, LocalStarCost: -1,
            RetainThisTurn: false, SlyThisTurn: false, BaseReplayCount: 0, ExhaustOnNextPlay: false,
            FreeThisTurn: false, FreeUntilPlayed: false, FreeThisCombat: false, StarCostThisTurn: null,
            EnergyModifiers.Length: 0 }
        && card.PublicState is { Count: 2 } state && state.TryGetValue("targetType", out string? target) && target == "None"
        && state.TryGetValue("tags", out string? tags) && tags == "";

    private static bool SafeCardMetadata(PublicCard card) => card.Affliction is null
        && (card.Enchantments ?? []).All(effect => effect.Id == nameof(Sharp));

    private static bool HasNoTurnEndEffect(string id) => CardTypes.TryGetValue(id, out Type? type)
        && type.GetMethod("OnTurnEndInHand", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType == typeof(CardModel)
        && type.GetProperty("HasTurnEndInHandEffect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!.DeclaringType == typeof(CardModel);
}
