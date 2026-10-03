using System.Collections.ObjectModel;
using System.Globalization;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Detached, observer-indexed inputs. Ineligibility disables acceleration only.</summary>
internal sealed record NativePublicCombatPrefixInput(int CombatIndex, long OwnerOrdinal,
    long? DecisionEventOrdinal, string? EntryJson, NativeInitialShuffleCondition? Shuffle,
    NativeInitialHpCondition? Hp, string? ShuffleReason, string? HpReason,
    NativeCorpseSlugHpCondition? SlugHp = null, NativePublicDrawPrefixAudit? DrawPrefix = null,
    NativeToadpoleHpCondition? ToadpoleHp = null)
{
    internal int HpCount => Hp?.EnemyCount ?? SlugHp?.EnemyCount ?? ToadpoleHp?.EnemyCount ?? 0;
}

/// <summary>
/// Reuses the existing source-pinned startup certificates for each observed combat.
/// Only typed public values are inputs; neither native objects nor source audit data
/// participate. Combat indices count every combat owner start, including forced fights
/// that never offered a decision. The first stable snapshot fixes startup HP;
/// certified later transitions can extend the draw prefix.
/// </summary>
internal sealed class NativePublicCombatPrefixCondition
{
    internal IReadOnlyDictionary<int, NativePublicCombatPrefixInput> Combats { get; }
    internal int EligibleShuffleCount => Combats.Values.Count(input => input.Shuffle is not null);
    internal int EligibleHpCombatCount => Combats.Values.Count(input => input.HpCount > 0);
    internal int EligibleHpCount => Combats.Values.Sum(input => input.HpCount);
    internal string? Reason { get; }

    private NativePublicCombatPrefixCondition(Dictionary<int, NativePublicCombatPrefixInput> combats,
        string? reason = null)
    { Combats = new ReadOnlyDictionary<int, NativePublicCombatPrefixInput>(combats); Reason = reason; }

    internal static NativePublicCombatPrefixCondition Create(DecisionPacket publicRoot)
    {
        ArgumentNullException.ThrowIfNull(publicRoot);
        return publicRoot.PublicEvidence is { } evidence ? Create(evidence)
            : new([], "public_run_evidence_required");
    }

    /// <summary>
    /// One locally complete combat under the separately declared constructed
    /// lifecycle. The run-start gap remains in the original packet. Combat zero
    /// follows from that lifecycle's one-combat contract, never from guessing the
    /// number of unobserved natural combats or rewriting a PublicRunStarted event.
    /// </summary>
    internal static NativePublicCombatPrefixCondition CreateConstructed(DecisionPacket publicRoot,
        NativeConstructedTapePrior prior)
    {
        ArgumentNullException.ThrowIfNull(publicRoot);
        prior = prior.Freeze();
        var evidence = publicRoot.PublicEvidence;
        if (evidence is null || evidence.CompleteFromRunStart
            || publicRoot.Observation?.RunContext is not { CompleteFromRunStart: false, CombatEntryIndex: null })
            return new([], "declared_constructed_history_required");
        var owners = evidence.Events.Where(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToArray();
        if (owners.Length != 1 || owners[0].OwnerOrdinal is not long owner
            || owners[0].Payload is not PublicOwnerStarted { CompleteFromOwnerStart: true, ParentOwnerOrdinal: null } start
            || start.ActIndex != EncounterCoverage.Find(prior.Setup.Encounter).ActIndex)
            return new([], "one_complete_constructed_combat_owner_required");
        var globalGaps = evidence.Events.Where(e => e.OwnerOrdinal is null && e.Payload is PublicEvidenceGap).ToArray();
        if (evidence.Events[0] is not { EventOrdinal: 0, OwnerOrdinal: null,
                Payload: PublicEvidenceGap { Reason: PublicEvidenceGapReason.RunStartNotObserved } }
            || globalGaps.Length != 1 || evidence.Events.Any(e => e.Payload is PublicRunStarted))
            return new([], "only_declared_constructed_initial_gap_required");
        var events = evidence.Events.Where(e => e.OwnerOrdinal == owner).ToArray();
        var first = events.FirstOrDefault(e => e.Payload is PublicCombatDecision);
        if (first?.Payload is not PublicCombatDecision { HistoryCompleteFromCombatStart: true })
            return new([], "complete_constructed_startup_decision_required");
        var packet = StartupPacket("Silent", 10, events, first, out string? reason);
        NativeInitialShuffleCondition? shuffle = null;
        NativeInitialHpCondition? hp = null;
        NativeCorpseSlugHpCondition? slugHp = null;
        NativeToadpoleHpCondition? toadpoleHp = null;
        string? shuffleReason = reason, hpReason = reason;
        if (packet is not null)
        {
            NativeInitialShuffleCondition.TryCreatePublicCombatV5(packet, out shuffle, out shuffleReason);
            if (shuffle is not null) NativeInitialHpCondition.TryCreatePublicCombatV4(packet, out hp, out hpReason);
            else hpReason = shuffleReason;
            if (hp is null && shuffle is not null && packet.Observation!.Enemies.Any(e => e.Id == "CorpseSlug"))
                NativeCorpseSlugHpCondition.TryCreate(packet, out slugHp, out hpReason);
            if (hp is null && slugHp is null && shuffle is not null && packet.Observation!.Enemies.Any(e => e.Id == "Toadpole"))
                NativeToadpoleHpCondition.TryCreate(packet, out toadpoleHp, out hpReason);
        }
        NativePublicDrawPrefixAudit? drawPrefix = null;
        if (shuffle is not null)
            shuffle = NativePublicDrawPrefixCondition.Extend(shuffle, events, first, null, out drawPrefix);
        return new(new() { [0] = new(0, owner, first.EventOrdinal,
            packet?.Observation!.History[1].Detail, shuffle, hp, shuffleReason, hpReason, slugHp, drawPrefix, toadpoleHp) });
    }

    internal static NativePublicCombatPrefixCondition Create(PublicRunEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var inputs = new Dictionary<int, NativePublicCombatPrefixInput>();
        var runStart = evidence.Events.FirstOrDefault()?.Payload as PublicRunStarted;
        // An owner-scoped gap need not invalidate another owner's local startup.
        // A missing run start or global gap can hide owner starts, so later absolute
        // combat indices are not certified. Never guess an offset from floors.
        long? globalGap = evidence.Events.FirstOrDefault(entry => entry.OwnerOrdinal is null
            && entry.Payload is PublicEvidenceGap)?.EventOrdinal;
        var ownerEvents = evidence.Events.Where(entry => entry.OwnerOrdinal is not null)
            .GroupBy(entry => entry.OwnerOrdinal!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var owner in evidence.Events.Where(entry => entry.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat }))
        {
            int index = inputs.Count;
            var events = ownerEvents[owner.OwnerOrdinal!.Value];
            var first = events.FirstOrDefault(entry => entry.Payload is PublicCombatDecision);
            string? reason = runStart is null || globalGap < owner.EventOrdinal
                ? "public_combat_index_not_certified"
                : first is null ? "no_observed_stable_decision"
                : first.Payload is PublicCombatDecision { HistoryCompleteFromCombatStart: false }
                    ? "complete_combat_history_required" : null;
            DecisionPacket? packet = null;
            if (reason is null)
            {
                var decision = (PublicCombatDecision)first!.Payload;
                if (decision.Observation.RunContext is { CombatEntryIndex: int publishedIndex }
                    && publishedIndex != index)
                    reason = "public_combat_index_disagrees";
                else packet = StartupPacket(runStart!, events, first, out reason);
            }
            NativeInitialShuffleCondition? shuffle = null;
            NativeInitialHpCondition? hp = null;
            NativeCorpseSlugHpCondition? slugHp = null;
            NativeToadpoleHpCondition? toadpoleHp = null;
            string? shuffleReason = reason, hpReason = reason;
            string? entryJson = packet?.Observation!.History[1].Detail;
            if (packet is not null)
            {
                NativeInitialShuffleCondition.TryCreatePublicCombatV5(packet, out shuffle, out shuffleReason);
                // HP and shuffle share one owned startup lifecycle. An impossible public
                // upgrade witness cannot leave an HP-only plan with no eligible shuffle.
                if (shuffle is not null) NativeInitialHpCondition.TryCreatePublicCombatV4(packet, out hp, out hpReason);
                else hpReason = shuffleReason;
                if (hp is null && shuffle is not null && packet.Observation!.Enemies.Any(enemy => enemy.Id == "CorpseSlug"))
                    NativeCorpseSlugHpCondition.TryCreate(packet, out slugHp, out hpReason);
                if (hp is null && slugHp is null && shuffle is not null
                    && packet.Observation!.Enemies.Any(enemy => enemy.Id == "Toadpole"))
                    NativeToadpoleHpCondition.TryCreate(packet, out toadpoleHp, out hpReason);
            }
            NativePublicDrawPrefixAudit? drawPrefix = null;
            if (shuffle is not null)
                shuffle = NativePublicDrawPrefixCondition.Extend(shuffle, events, first!, globalGap, out drawPrefix);
            inputs.Add(index, new(index, owner.OwnerOrdinal.Value, first?.EventOrdinal,
                entryJson, shuffle, hp, shuffleReason, hpReason, slugHp, drawPrefix, toadpoleHp));
        }
        return new(inputs, inputs.Count == 0 ? "no_observed_combat_owner" : null);
    }

    private static DecisionPacket? StartupPacket(PublicRunStarted runStart,
        PublicRunEvidenceEvent[] ownerEvents, PublicRunEvidenceEvent first, out string? reason)
        => StartupPacket(runStart.Character, runStart.Ascension, ownerEvents, first, out reason);

    private static DecisionPacket? StartupPacket(string character, int ascension,
        PublicRunEvidenceEvent[] ownerEvents, PublicRunEvidenceEvent first, out string? reason)
    {
        reason = null;
        var decision = (PublicCombatDecision)first.Payload;
        var prefix = ownerEvents.Skip(1).TakeWhile(entry => entry.EventOrdinal < first.EventOrdinal).ToArray();
        if (prefix.Length < 4
            || prefix[0].Payload is not PublicCombatFact { FactKind: PublicCombatFactKind.Started }
            || prefix[1].Payload is not PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets, Assets: { } assets }
            || prefix.Count(entry => entry.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets }) != 1)
        { reason = "typed_entry_history_required"; return null; }
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", assets.Hp, assets.MaxHp, assets.Gold,
            assets.Deck, assets.Relics, assets.Potions.ToArray(), assets.MaxEnergy, assets.PotionSlots,
            assets.OrbSlots, assets.CardRemovalsUsed);
        // Started's v1 projection represents the observed character/ascension from
        // run startup. The schema tag is the public recorder's fixed entry codec.
        var history = new List<PublicEvent>
        {
            new("combat_started", character + ":A" + ascension.ToString(CultureInfo.InvariantCulture)),
            new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
        };
        int cursor = 2;
        // Retain every typed power fact in the certificate input. The explicit v2
        // source closure validates their model, amount, owner and startup roster;
        // full typed replay equality still checks these original facts unchanged.
        while (cursor < prefix.Length && prefix[cursor].Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PowerChanged } power)
        {
            history.Add(new("power_changed", PublicJson.Serialize(new
            {
                target = power.TargetModel, targetSlot = power.TargetSlot, sourceSlot = power.SourceSlot,
                id = power.Model, amount = power.Amount,
            })));
            cursor++;
        }
        int drawStart = cursor;
        while (cursor < prefix.Length && prefix[cursor].Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.CardDrawn } draw)
        { history.Add(new("draw", PublicJson.Serialize(draw.Cards.Single()))); cursor++; }
        if (cursor == drawStart || cursor >= prefix.Length || prefix[cursor].Payload is not PublicCombatFact
            { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 })
        { reason = "uninterrupted_initial_draw_history_required"; return null; }
        history.Add(new("player_turn", "1")); cursor++;
        int intentStart = cursor;
        while (cursor < prefix.Length && prefix[cursor].Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.IntentPublished } intent)
        {
            history.Add(new("intent_published", PublicJson.Serialize(new
                { slot = intent.TargetSlot!.Value, id = intent.Model!, intents = intent.Intents.ToArray() })));
            cursor++;
        }
        if (cursor == intentStart) { reason = "startup_intent_history_required"; return null; }
        // This packet proves startup only. The separate action-linked transition closure
        // can extend its draw prefix using the original typed evidence. Full history
        // and stable snapshots remain unchanged for final native replay equality.
        return new(decision.Status, decision.Observation with { History = history.ToArray() }, decision.Actions);
    }
}
