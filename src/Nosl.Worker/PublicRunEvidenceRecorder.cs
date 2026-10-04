using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>
/// Observer-owned history. Inputs are detached public DTOs; no native graph, recorder snapshot,
/// SourceTrace, private identifiers or inferred run-start completeness enter this API.
/// </summary>
public sealed class PublicRunEvidenceRecorder
{
    private PublicRunEvidence _evidence;
    private ImmutableArray<PublicRunEvidenceEvent> Events => _evidence.Events;
    private readonly Dictionary<long, List<string>> _historyHashes = [];
    private readonly Action<PublicRunEvidenceEvent>? _onAppended;
    private Exception? _observerFailure;
    private long _nextOwner;
    private readonly Dictionary<long, (long Decision, int HistoryIndex, string Action)> _pendingActionEchoes = [];

    public PublicRunEvidenceRecorder(PublicRunStarted? observedRunStart) : this(observedRunStart, null) { }

    // Only owned label replay supplies an observer. Ordinary source collection
    // retains the same public API and records exactly the same detached events.
    internal PublicRunEvidenceRecorder(PublicRunStarted? observedRunStart, Action<PublicRunEvidenceEvent>? onAppended,
        string schemaVersion = PublicRunEvidence.Version)
    {
        _onAppended = onAppended;
        _evidence = new(schemaVersion, observedRunStart is not null,
            [new(0, null, observedRunStart is null
                ? new PublicEvidenceGap(PublicEvidenceGapReason.RunStartNotObserved) : observedRunStart)]);
        NotifyAppended(Events[0]);
    }

    public PublicRunEvidence Capture() => _evidence;

    public long BeginOwner(PublicEvidenceOwnerKind kind, int actIndex, int floor,
        long? parentOwnerOrdinal = null, bool completeFromOwnerStart = true)
    {
        long owner = _nextOwner;
        Append(owner, new PublicOwnerStarted(kind, actIndex, floor, parentOwnerOrdinal, completeFromOwnerStart));
        _nextOwner++;
        if (!completeFromOwnerStart) RecordGap(owner, PublicEvidenceGapReason.OwnerStartNotObserved);
        return owner;
    }

    public long Record(long ownerOrdinal, PublicEvidencePayload observation)
    {
        if (observation is PublicRunStarted or PublicOwnerStarted)
            throw new ArgumentException("Recorder owns run and owner starts", nameof(observation));
        long ordinal = Append(ownerOrdinal, observation);
        if (observation is PublicCombatActionTaken action)
            _pendingActionEchoes[ownerOrdinal] = (action.DecisionEventOrdinal,
                _historyHashes.TryGetValue(ownerOrdinal, out var history) ? history.Count : 0,
                PublicJson.Serialize(action.Action));
        return ordinal;
    }

    public long RecordGap(long? ownerOrdinal, PublicEvidenceGapReason reason) => Append(ownerOrdinal, new PublicEvidenceGap(reason));

    /// <summary>
    /// Project a cumulative public combat history through the closed v1 vocabulary. Unknown,
    /// malformed, truncated or rewritten input becomes a gap; the raw Detail is never emitted.
    /// </summary>
    public void ObserveCombatHistory(long ownerOrdinal, IReadOnlyList<PublicEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        EnsureCombatOwner(ownerOrdinal);
        if (!_historyHashes.TryGetValue(ownerOrdinal, out var previous)) _historyHashes.Add(ownerOrdinal, previous = []);
        var hashes = history.Select(e => e is null ? "null" : Hash(PublicJson.Serialize(e))).ToArray();
        if (previous.Count > hashes.Length || !previous.SequenceEqual(hashes.Take(previous.Count)))
        {
            RecordGap(ownerOrdinal, PublicEvidenceGapReason.Interrupted);
            return; // Never replace or guess a divergent prefix.
        }
        for (int i = previous.Count; i < history.Count; i++)
        {
            try
            {
                if (history[i] is null || history[i].Kind is null || history[i].Detail is null)
                    throw new ArgumentException("Missing public event fields");
                if (_pendingActionEchoes.TryGetValue(ownerOrdinal, out var echo) && echo.HistoryIndex == i)
                {
                    _pendingActionEchoes.Remove(ownerOrdinal);
                    if (history[i].Kind != "action" || PublicJson.Serialize(Read<PublicAction>(history[i].Detail,
                        "revision", "kind", "slot", "target", "selection")) != echo.Action)
                        RecordGap(ownerOrdinal, PublicEvidenceGapReason.Interrupted);
                    else
                    {
                        previous.Add(hashes[i]);
                        continue; // One exact next-history occurrence reconciles the manual action.
                    }
                }
                if (history[i].Kind == "action") RecordProjectedAction(ownerOrdinal, Read<PublicAction>(history[i].Detail,
                    "revision", "kind", "slot", "target", "selection"));
                else Record(ownerOrdinal, Project(history[i]));
            }
            catch (Exception e) when (!ReferenceEquals(e, _observerFailure)
                && e is ArgumentException or JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
            {
                RecordGap(ownerOrdinal, PublicEvidenceGapReason.UnsupportedObservation);
            }
            previous.Add(hashes[i]);
        }
    }

    /// <summary>Records history before making the explicitly linked history-free decision snapshot.</summary>
    public long ObserveCombatDecision(long ownerOrdinal, DecisionPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Observation is null) throw new ArgumentException("Stable decision observation is required");
        // Validate the detached snapshot first so malformed input cannot partially append history.
        _ = new PublicCombatDecision(packet.Status, packet.Observation with { History = [] }, packet.Actions, 0, false);
        ObserveCombatHistory(ownerOrdinal, packet.Observation.History);
        var ownerEvents = Events.Where(e => e.OwnerOrdinal == ownerOrdinal).ToArray();
        var start = (PublicOwnerStarted)ownerEvents[0].Payload;
        bool complete = start.CompleteFromOwnerStart
            && ownerEvents.Any(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Started })
            && !Events.Any(e => e.EventOrdinal >= ownerEvents[0].EventOrdinal
                && (e.OwnerOrdinal == ownerOrdinal || e.OwnerOrdinal is null) && e.Payload is PublicEvidenceGap);
        if (!complete && !ownerEvents.Any(e => e.Payload is PublicEvidenceGap))
        {
            RecordGap(ownerOrdinal, PublicEvidenceGapReason.ObservationMissing);
            ownerEvents = Events.Where(e => e.OwnerOrdinal == ownerOrdinal).ToArray();
        }
        return Record(ownerOrdinal, new PublicCombatDecision(packet.Status,
            packet.Observation with { History = [] }, packet.Actions, ownerEvents[^1].EventOrdinal, complete));
    }

    private void RecordProjectedAction(long ownerOrdinal, PublicAction action)
    {
        var decision = Events.LastOrDefault(e => e.OwnerOrdinal == ownerOrdinal && e.Payload is PublicCombatDecision)
            ?? throw new ArgumentException("Public action has no preceding observed decision");
        // Append directly: this action already came from the cumulative history, so
        // it must not create a second reconciliation allowance for the same decision.
        Append(ownerOrdinal, new PublicCombatActionTaken(decision.EventOrdinal, action));
    }

    private void EnsureCombatOwner(long ownerOrdinal)
    {
        var events = Events.Where(e => e.OwnerOrdinal == ownerOrdinal).ToArray();
        if (events.Length == 0 || events[0].Payload is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }
            || events.Any(e => e.Payload is PublicOwnerEnded)) throw new ArgumentException("An open combat owner is required");
    }

    private long Append(long? owner, PublicEvidencePayload payload)
    {
        var entry = new PublicRunEvidenceEvent(Events.Length, owner, payload);
        // The validated immutable transition cannot retain a rejected event or alter a saved capture.
        _evidence = _evidence.Append(entry);
        NotifyAppended(entry);
        return entry.EventOrdinal;
    }

    private void NotifyAppended(PublicRunEvidenceEvent entry)
    {
        try { _onAppended?.Invoke(entry); }
        catch (Exception error)
        {
            // Observer control flow is not malformed public input. Preserve its
            // exact exception even when it uses a projection-error exception type.
            _observerFailure = error;
            throw;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly JsonSerializerOptions Strict = new(PublicJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false };
    private static T Read<T>(string json, params string[] required)
    {
        using var document = JsonDocument.Parse(json);
        PublicRunEvidenceJson.RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected public object");
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || required.Any(k => !keys.Contains(k, StringComparer.Ordinal)))
            throw new ArgumentException("Missing or duplicate public fields");
        return JsonSerializer.Deserialize<T>(json, Strict) ?? throw new ArgumentException("Empty public object");
    }

    private static PublicCombatFact Project(PublicEvent entry)
    {
        switch (entry.Kind)
        {
            case "combat_started":
                if (entry.Detail != "Silent:A10") throw new ArgumentException("Unsupported combat-start channel");
                return new(PublicCombatFactKind.Started);
            case "native_entry_assets":
                var assets = Read<EntryAssets>(entry.Detail, "schemaVersion", "hp", "maxHp", "gold", "deck", "relics", "potions", "maxEnergy", "potionSlots", "orbSlots", "cardRemovalsUsed");
                if (assets.SchemaVersion != "nosl.native-entry-assets.v1") throw new ArgumentException("Unknown entry-assets version");
                ArgumentNullException.ThrowIfNull(assets.Potions);
                return new(PublicCombatFactKind.EntryAssets, assets: new(assets.Hp, assets.MaxHp, assets.Gold,
                    assets.Deck, assets.Relics, assets.Potions.ToImmutableArray(), assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed));
            case "player_turn": return new(PublicCombatFactKind.PlayerTurnStarted, turn: int.Parse(entry.Detail, CultureInfo.InvariantCulture));
            case "player_turn_ended":
                if (entry.Detail.Length != 0) throw new ArgumentException("Unsupported turn-end payload");
                return new(PublicCombatFactKind.PlayerTurnEnded);
            case "intent_published":
                var intent = Read<Intent>(entry.Detail, "slot", "id", "intents");
                ArgumentNullException.ThrowIfNull(intent.Intents);
                return new(PublicCombatFactKind.IntentPublished, targetSlot: intent.Slot, model: intent.Id, intents: intent.Intents.ToImmutableArray());
            case "draw": return new(PublicCombatFactKind.CardDrawn, cards: [ReadCard(entry.Detail)]);
            case "card_started": return new(PublicCombatFactKind.CardStarted, cards: [ReadCard(entry.Detail)]);
            case "card_generated": return new(PublicCombatFactKind.CardGenerated, cards: [ReadCard(entry.Detail)]);
            case "card_played":
                var played = Read<CardPlayed>(entry.Detail, "card", "energySpent", "starsSpent", "resultPile");
                PublicCardPile? pile = played.ResultPile is null ? null : Enum.Parse<PublicCardPile>(played.ResultPile, ignoreCase: false);
                return new(PublicCombatFactKind.CardPlayed, cards: [played.Card], energySpent: played.EnergySpent, starsSpent: played.StarsSpent, resultPile: pile);
            case "hidden_card_generated":
                if (entry.Detail != "draw") throw new ArgumentException("Unsupported hidden-card payload");
                return new(PublicCombatFactKind.HiddenCardGenerated);
            case "potion_used": return new(PublicCombatFactKind.PotionUsed, model: entry.Detail);
            case "damage":
                var damage = Read<Damage>(entry.Detail, "target", "targetSlot", "sourceSlot", "blocked", "unblocked", "overkill", "hpAfter", "killed");
                if (string.IsNullOrWhiteSpace(damage.Target)) throw new ArgumentException("Missing visible target");
                return new(PublicCombatFactKind.Damage, targetSlot: damage.TargetSlot, sourceSlot: damage.SourceSlot,
                    damage: new(damage.Blocked, damage.Unblocked, damage.Overkill, damage.HpAfter, damage.Killed), targetModel: damage.Target);
            case "power_changed":
                var power = Read<Power>(entry.Detail, "target", "targetSlot", "sourceSlot", "id", "amount");
                if (string.IsNullOrWhiteSpace(power.Target)) throw new ArgumentException("Missing visible target");
                return new(PublicCombatFactKind.PowerChanged, targetSlot: power.TargetSlot, sourceSlot: power.SourceSlot, model: power.Id, amount: power.Amount, targetModel: power.Target);
            case "shuffle":
                if (entry.Detail != "known_positions_reset") throw new ArgumentException("Unsupported shuffle payload");
                return new(PublicCombatFactKind.Shuffled);
            case "choice": return new(PublicCombatFactKind.ChoiceOffered,
                choice: Read<PublicChoice>(entry.Detail, "source", "min", "max", "cancelable", "candidates", "candidateOrder", "bundles"));
            case "automatic_selection":
                var selected = Read<AutomaticSelection>(entry.Detail, "source", "cards", "unidentifiedCount");
                ArgumentNullException.ThrowIfNull(selected.Cards);
                return new(PublicCombatFactKind.AutomaticSelection, model: selected.Source, cards: selected.Cards, unidentifiedCount: selected.UnidentifiedCount);
            case "pre_settlement": return new(PublicCombatFactKind.PreSettlement,
                settlement: Read<PublicPreSettlementFact>(entry.Detail, "hp", "maxHp", "hand", "exhaust"));
            default: throw new NotSupportedException("Public event is outside the versioned evidence vocabulary");
        }
    }
    private static PublicCard ReadCard(string json) => Read<PublicCard>(json, "id", "upgrade", "cost", "starCost", "type", "keywords");
    private sealed record EntryAssets(string SchemaVersion, int Hp, int MaxHp, int Gold, PublicCard[] Deck,
        PublicRelic[] Relics, string?[] Potions, int MaxEnergy, int PotionSlots, int OrbSlots, int CardRemovalsUsed);
    private sealed record Intent(int Slot, string Id, PublicIntent[] Intents);
    private sealed record CardPlayed(PublicCard Card, int? EnergySpent, int? StarsSpent, string? ResultPile);
    private sealed record Damage(string Target, int TargetSlot, int? SourceSlot, decimal Blocked, decimal Unblocked, decimal Overkill, int HpAfter, bool Killed);
    private sealed record Power(string Target, int TargetSlot, int? SourceSlot, string Id, decimal Amount);
    private sealed record AutomaticSelection(string? Source, PublicCard[] Cards, int UnidentifiedCount);
}
