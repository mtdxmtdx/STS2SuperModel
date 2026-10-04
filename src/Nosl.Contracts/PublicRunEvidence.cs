using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nosl.Contracts;

/// <summary>An opt-in channel of recorded public observations, not all human-visible information.</summary>
public sealed class PublicRunEvidence
{
    public const string Version = "nosl.public-run-evidence.v1";
    public const string CompleteMapVersion = "nosl.public-run-evidence.v2";
    public const string StudentSchema = "nosl.student.public.v4";
    public const string CompleteMapStudentSchema = "nosl.student.public.v5";

    public static bool IsRequested(string? profile) => profile switch
    {
        null => false,
        Version or CompleteMapVersion => true,
        _ => throw new ArgumentException("Unknown public run-evidence profile", nameof(profile)),
    };

    public static bool ValidateChannel(string? profile, string? contextProfile)
    {
        bool requested = IsRequested(profile);
        if (requested && !PublicRunContext.IsRequested(contextProfile))
            throw new ArgumentException("Public run evidence requires the v3 public run-context channel");
        return requested;
    }
    public string SchemaVersion { get; }
    public bool CompleteFromRunStart { get; }
    public ImmutableArray<PublicRunEvidenceEvent> Events { get; }
    private readonly PublicRunEvidenceValidation _validation;

    [JsonConstructor]
    public PublicRunEvidence(string schemaVersion, bool completeFromRunStart, ImmutableArray<PublicRunEvidenceEvent> events)
    {
        if (schemaVersion is not (Version or CompleteMapVersion)) throw new ArgumentException("Unknown public run-evidence version");
        SchemaVersion = schemaVersion;
        Events = EvidenceGuard.Array(events, nameof(events));
        foreach (var entry in Events) ValidateMapVersion(entry);
        _validation = PublicRunEvidenceValidation.Validate(Events, completeFromRunStart);
        CompleteFromRunStart = completeFromRunStart;
    }

    private PublicRunEvidence(string schemaVersion, ImmutableArray<PublicRunEvidenceEvent> events, PublicRunEvidenceValidation validation)
    {
        SchemaVersion = schemaVersion;
        Events = events;
        _validation = validation;
        CompleteFromRunStart = validation.CompleteFromRunStart;
    }

    /// <summary>Validate one new event and return an independent immutable prefix. Rejection leaves this prefix unchanged.</summary>
    public PublicRunEvidence Append(PublicRunEvidenceEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateMapVersion(entry);
        var validation = _validation.Append(Events, entry, Events.Length);
        return new(SchemaVersion, Events.Add(entry), validation);
    }

    private void ValidateMapVersion(PublicRunEvidenceEvent entry)
    {
        if (entry.Payload is PublicMapObserved map
            && (SchemaVersion == CompleteMapVersion) != (map.CurrentMap is not null))
            throw new ArgumentException("The evidence version differs from its current map capture field");
    }
}

public sealed class PublicRunEvidenceEvent
{
    public long EventOrdinal { get; }
    public long? OwnerOrdinal { get; }
    public PublicEvidencePayload Payload { get; }
    [JsonConstructor]
    public PublicRunEvidenceEvent(long eventOrdinal, long? ownerOrdinal, PublicEvidencePayload payload)
    {
        if (eventOrdinal < 0 || ownerOrdinal < 0) throw new ArgumentException("Invalid observer ordinal");
        EventOrdinal = eventOrdinal; OwnerOrdinal = ownerOrdinal;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PublicRunStarted), "run_started")]
[JsonDerivedType(typeof(PublicEvidenceGap), "gap")]
[JsonDerivedType(typeof(PublicOwnerStarted), "owner_started")]
[JsonDerivedType(typeof(PublicOwnerEnded), "owner_ended")]
[JsonDerivedType(typeof(PublicOffersObserved), "offers")]
[JsonDerivedType(typeof(PublicOptionsObserved), "options")]
[JsonDerivedType(typeof(PublicOptionChosen), "option_chosen")]
[JsonDerivedType(typeof(PublicCardsObserved), "card_choice")]
[JsonDerivedType(typeof(PublicCardsChosen), "cards_chosen")]
[JsonDerivedType(typeof(PublicMapObserved), "map")]
[JsonDerivedType(typeof(PublicMapChosen), "map_chosen")]
[JsonDerivedType(typeof(PublicCombatFact), "combat_fact")]
[JsonDerivedType(typeof(PublicCombatDecision), "combat_decision")]
[JsonDerivedType(typeof(PublicCombatActionTaken), "combat_action")]
public abstract class PublicEvidencePayload
{
    internal PublicEvidencePayload() { }
}

public enum PublicEvidenceOwnerKind { Combat, Event, Reward, Rest, Shop, Map, OutsideChoice }
public enum PublicEvidenceGapReason { RunStartNotObserved, OwnerStartNotObserved, ObservationMissing, AmbiguousVisibility, UnsupportedObservation, Interrupted }
public enum PublicEvidenceOwnerOutcome { Completed, Victory, Defeat, Escaped, Interrupted }
public enum PublicOfferKind { Card, Relic, Potion, Gold, Service, Skip, Continue, Reroll }
public enum PublicOfferGroupKind { Primary, Extra, Alternative, Reroll }
public enum PublicOfferSelectionMode { Independent, ChooseOne }
public enum PublicMapNodeType { Start, Unknown, Monster, Elite, Boss, Rest, Shop, Treasure, Event, Ancient }
public enum PublicCombatFactKind { Started, EntryAssets, PlayerTurnStarted, PlayerTurnEnded, IntentPublished, CardDrawn, CardStarted, CardPlayed, CardGenerated, HiddenCardGenerated, PotionUsed, Damage, PowerChanged, Shuffled, ChoiceOffered, AutomaticSelection, PreSettlement }
public enum PublicCardPile { Hand, Draw, Discard, Exhaust, Play, None }

public sealed class PublicRunStarted : PublicEvidencePayload
{
    public string Character { get; }
    public int Ascension { get; }
    public PublicEvidenceAssets Assets { get; }
    [JsonConstructor]
    public PublicRunStarted(string character, int ascension, PublicEvidenceAssets assets)
    {
        Character = EvidenceGuard.Key(character); Ascension = EvidenceGuard.Nonnegative(ascension);
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }
}

public sealed class PublicEvidenceGap : PublicEvidencePayload
{
    public PublicEvidenceGapReason Reason { get; }
    [JsonConstructor]
    public PublicEvidenceGap(PublicEvidenceGapReason reason) => Reason = EvidenceGuard.Enum(reason);
}

public sealed class PublicOwnerStarted : PublicEvidencePayload
{
    public PublicEvidenceOwnerKind OwnerKind { get; }
    public int ActIndex { get; }
    public int Floor { get; }
    public long? ParentOwnerOrdinal { get; }
    public bool CompleteFromOwnerStart { get; }
    [JsonConstructor]
    public PublicOwnerStarted(PublicEvidenceOwnerKind ownerKind, int actIndex, int floor,
        long? parentOwnerOrdinal, bool completeFromOwnerStart)
    {
        OwnerKind = EvidenceGuard.Enum(ownerKind); ActIndex = EvidenceGuard.Nonnegative(actIndex);
        Floor = EvidenceGuard.Nonnegative(floor);
        if (parentOwnerOrdinal < 0) throw new ArgumentException("Invalid parent owner ordinal");
        ParentOwnerOrdinal = parentOwnerOrdinal; CompleteFromOwnerStart = completeFromOwnerStart;
    }
}

public sealed class PublicOwnerEnded : PublicEvidencePayload
{
    public PublicEvidenceOwnerOutcome Outcome { get; }
    public PublicEvidenceAssets? Assets { get; }
    [JsonConstructor]
    public PublicOwnerEnded(PublicEvidenceOwnerOutcome outcome, PublicEvidenceAssets? assets = null)
    { Outcome = EvidenceGuard.Enum(outcome); Assets = assets; }
}

/// <summary>Only explicitly detached visible inventory; legacy mutable DTOs are copied on input and access.</summary>
public sealed class PublicEvidenceAssets
{
    private readonly PublicCard[] _deck;
    private readonly PublicRelic[] _relics;
    public int Hp { get; }
    public int MaxHp { get; }
    public int Gold { get; }
    public PublicCard[] Deck => EvidenceGuard.Copy(_deck);
    public PublicRelic[] Relics => EvidenceGuard.Copy(_relics);
    public ImmutableArray<string?> Potions { get; }
    public int MaxEnergy { get; }
    public int PotionSlots { get; }
    public int OrbSlots { get; }
    public int CardRemovalsUsed { get; }
    [JsonConstructor]
    public PublicEvidenceAssets(int hp, int maxHp, int gold, PublicCard[] deck, PublicRelic[] relics,
        ImmutableArray<string?> potions, int maxEnergy, int potionSlots, int orbSlots, int cardRemovalsUsed)
    {
        Hp = EvidenceGuard.Nonnegative(hp); MaxHp = EvidenceGuard.Nonnegative(maxHp);
        if (hp > maxHp) throw new ArgumentException("HP exceeds maximum");
        Gold = EvidenceGuard.Nonnegative(gold);
        _deck = EvidenceGuard.Cards(deck); _relics = EvidenceGuard.Relics(relics);
        Potions = EvidenceGuard.Array(potions, nameof(potions), allowNull: true);
        foreach (var potion in Potions) if (potion is not null) EvidenceGuard.Key(potion);
        MaxEnergy = EvidenceGuard.Nonnegative(maxEnergy); PotionSlots = EvidenceGuard.Nonnegative(potionSlots);
        if (Potions.Length != potionSlots) throw new ArgumentException("Potion slots do not match inventory");
        OrbSlots = EvidenceGuard.Nonnegative(orbSlots); CardRemovalsUsed = EvidenceGuard.Nonnegative(cardRemovalsUsed);
    }
}

public sealed class PublicOffer
{
    private readonly PublicCard? _card;
    private readonly PublicRelic? _relic;
    public string Key { get; }
    public PublicOfferKind OfferKind { get; }
    public bool IsLocked { get; }
    public int? Price { get; }
    public PublicCard? Card => _card is null ? null : EvidenceGuard.Copy(_card);
    public PublicRelic? Relic => _relic is null ? null : EvidenceGuard.Copy(_relic);
    public string? Potion { get; }
    public int? Gold { get; }
    public string? ServiceKey { get; }
    [JsonConstructor]
    public PublicOffer(string key, PublicOfferKind offerKind, bool isLocked = false, int? price = null,
        PublicCard? card = null, PublicRelic? relic = null, string? potion = null, int? gold = null, string? serviceKey = null)
    {
        Key = EvidenceGuard.Key(key); OfferKind = EvidenceGuard.Enum(offerKind); IsLocked = isLocked;
        if (price < 0 || gold < 0) throw new ArgumentException("Negative public amount");
        Price = price;
        int count = (card is null ? 0 : 1) + (relic is null ? 0 : 1) + (potion is null ? 0 : 1)
            + (gold is null ? 0 : 1) + (serviceKey is null ? 0 : 1);
        bool valid = offerKind switch { PublicOfferKind.Card => card is not null,
            PublicOfferKind.Relic => relic is not null, PublicOfferKind.Potion => potion is not null,
            PublicOfferKind.Gold => gold is not null, PublicOfferKind.Service => serviceKey is not null,
            _ => count == 0 };
        if (!valid || count > 1) throw new ArgumentException("Offer payload does not match its kind");
        _card = card is null ? null : EvidenceGuard.Cards([card])[0];
        _relic = relic is null ? null : EvidenceGuard.Relics([relic])[0];
        Potion = potion is null ? null : EvidenceGuard.Key(potion); Gold = gold;
        ServiceKey = serviceKey is null ? null : EvidenceGuard.Key(serviceKey);
    }
}

public sealed class PublicOfferGroup
{
    public PublicOfferGroupKind GroupKind { get; }
    public PublicOfferSelectionMode SelectionMode { get; }
    public int? AlternativeToGroupIndex { get; }
    public ImmutableArray<PublicOffer> Offers { get; }
    [JsonConstructor]
    public PublicOfferGroup(PublicOfferGroupKind groupKind, PublicOfferSelectionMode selectionMode,
        ImmutableArray<PublicOffer> offers, int? alternativeToGroupIndex = null)
    {
        GroupKind = EvidenceGuard.Enum(groupKind); SelectionMode = EvidenceGuard.Enum(selectionMode);
        Offers = EvidenceGuard.Array(offers, nameof(offers));
        if (alternativeToGroupIndex < 0 || (groupKind == PublicOfferGroupKind.Alternative) != alternativeToGroupIndex.HasValue)
            throw new ArgumentException("Alternative groups must identify a displayed group");
        AlternativeToGroupIndex = alternativeToGroupIndex;
    }
}

public sealed class PublicOffersObserved : PublicEvidencePayload
{
    public ImmutableArray<PublicOfferGroup> Groups { get; }
    public long? ReplacesOfferEventOrdinal { get; }
    [JsonConstructor]
    public PublicOffersObserved(ImmutableArray<PublicOfferGroup> groups, long? replacesOfferEventOrdinal = null)
    {
        Groups = EvidenceGuard.Array(groups, nameof(groups));
        EvidenceGuard.Unique(Groups.SelectMany(g => g.Offers).Select(o => o.Key));
        for (int i = 0; i < Groups.Length; i++)
            if (Groups[i].AlternativeToGroupIndex is int other && (other >= Groups.Length || other == i))
                throw new ArgumentException("Invalid alternative group reference");
        if (replacesOfferEventOrdinal < 0 || (Groups.Any(g => g.GroupKind == PublicOfferGroupKind.Reroll) && replacesOfferEventOrdinal is null))
            throw new ArgumentException("A displayed reroll requires the prior offer event");
        ReplacesOfferEventOrdinal = replacesOfferEventOrdinal;
    }
}

public sealed class PublicVisibleOption
{
    public string Key { get; }
    public bool IsLocked { get; }
    public int? Price { get; }
    [JsonConstructor]
    public PublicVisibleOption(string key, bool isLocked, int? price = null)
    {
        Key = EvidenceGuard.Key(key); IsLocked = isLocked;
        if (price < 0) throw new ArgumentException("Negative public price"); Price = price;
    }
}

/// <summary>Visible event/rest/service keys, including locked choices. Never synthesize unavailable choices.</summary>
public sealed class PublicOptionsObserved : PublicEvidencePayload
{
    public ImmutableArray<PublicVisibleOption> Options { get; }
    [JsonConstructor]
    public PublicOptionsObserved(ImmutableArray<PublicVisibleOption> options)
    { Options = EvidenceGuard.Array(options, nameof(options)); EvidenceGuard.Unique(Options.Select(o => o.Key)); }
}

public sealed class PublicOptionChosen : PublicEvidencePayload
{
    public long OfferEventOrdinal { get; }
    public string Key { get; }
    [JsonConstructor]
    public PublicOptionChosen(long offerEventOrdinal, string key)
    { OfferEventOrdinal = EvidenceGuard.Ordinal(offerEventOrdinal); Key = EvidenceGuard.Key(key); }
}

public sealed class PublicCardsObserved : PublicEvidencePayload
{
    private readonly PublicChoice _choice;
    public PublicChoice Choice => EvidenceGuard.Copy(_choice);
    [JsonConstructor]
    public PublicCardsObserved(PublicChoice choice) => _choice = EvidenceGuard.Choice(choice);
}

public sealed class PublicCardsChosen : PublicEvidencePayload
{
    public long OfferEventOrdinal { get; }
    public ImmutableArray<int> Selection { get; }
    public bool Cancelled { get; }
    [JsonConstructor]
    public PublicCardsChosen(long offerEventOrdinal, ImmutableArray<int> selection, bool cancelled)
    {
        OfferEventOrdinal = EvidenceGuard.Ordinal(offerEventOrdinal);
        Selection = EvidenceGuard.Array(selection, nameof(selection)); Cancelled = cancelled;
        if (Selection.Any(i => i < 0) || Selection.Distinct().Count() != Selection.Length || (cancelled && Selection.Length != 0))
            throw new ArgumentException("Invalid card selection indices");
    }
}

public sealed record PublicMapCoordinate
{
    public int Col { get; }
    public int Row { get; }
    [JsonConstructor]
    public PublicMapCoordinate(int col, int row) { Col = col; Row = row; }
}
public sealed class PublicMapNode
{
    public PublicMapCoordinate Coordinate { get; }
    public PublicMapNodeType NodeType { get; }
    [JsonConstructor]
    public PublicMapNode(PublicMapCoordinate coordinate, PublicMapNodeType nodeType)
    { Coordinate = coordinate ?? throw new ArgumentNullException(nameof(coordinate)); NodeType = EvidenceGuard.Enum(nodeType); }
}
public sealed class PublicMapEdge
{
    public PublicMapCoordinate From { get; }
    public PublicMapCoordinate To { get; }
    [JsonConstructor]
    public PublicMapEdge(PublicMapCoordinate from, PublicMapCoordinate to)
    {
        From = from ?? throw new ArgumentNullException(nameof(from)); To = to ?? throw new ArgumentNullException(nameof(to));
        if (from == to) throw new ArgumentException("Map edge cannot loop to itself");
    }
}
public sealed class PublicMapOption
{
    public PublicMapCoordinate Coordinate { get; }
    public bool IsOrdinaryConnection { get; }
    [JsonConstructor]
    public PublicMapOption(PublicMapCoordinate coordinate, bool isOrdinaryConnection)
    { Coordinate = coordinate ?? throw new ArgumentNullException(nameof(coordinate)); IsOrdinaryConnection = isOrdinaryConnection; }
}
public sealed class PublicMapObserved : PublicEvidencePayload
{
    public PublicMapCoordinate? Current { get; }
    public ImmutableArray<PublicMapNode> Nodes { get; }
    public ImmutableArray<PublicMapEdge> Edges { get; }
    public ImmutableArray<PublicMapOption> Options { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicCurrentMapCapture? CurrentMap { get; }
    [JsonConstructor]
    public PublicMapObserved(PublicMapCoordinate? current, ImmutableArray<PublicMapNode> nodes,
        ImmutableArray<PublicMapEdge> edges, ImmutableArray<PublicMapOption> options,
        PublicCurrentMapCapture? currentMap = null)
    {
        Current = current; Nodes = EvidenceGuard.Array(nodes, nameof(nodes)); Edges = EvidenceGuard.Array(edges, nameof(edges));
        Options = EvidenceGuard.Array(options, nameof(options));
        var coordinates = Nodes.Select(n => n.Coordinate).ToHashSet();
        if (coordinates.Count != Nodes.Length || (current is not null && !coordinates.Contains(current))
            || Edges.Any(e => !coordinates.Contains(e.From) || !coordinates.Contains(e.To))
            || Edges.Select(e => (e.From, e.To)).Distinct().Count() != Edges.Length
            || Options.Any(o => !coordinates.Contains(o.Coordinate))
            || Options.Select(o => o.Coordinate).Distinct().Count() != Options.Length)
            throw new ArgumentException("Invalid public map slice");
        if (current is null || Options.Any(o => o.IsOrdinaryConnection != Edges.Any(e => e.From == current && e.To == o.Coordinate)))
            throw new ArgumentException("Map options must state the observed ordinary connections from the current node");
        CurrentMap = currentMap;
        CurrentMap?.ValidateSlice(this);
    }
}
public sealed class PublicMapChosen : PublicEvidencePayload
{
    public long OfferEventOrdinal { get; }
    public PublicMapCoordinate Coordinate { get; }
    [JsonConstructor]
    public PublicMapChosen(long offerEventOrdinal, PublicMapCoordinate coordinate)
    { OfferEventOrdinal = EvidenceGuard.Ordinal(offerEventOrdinal); Coordinate = coordinate ?? throw new ArgumentNullException(nameof(coordinate)); }
}

/// <summary>Public combat effects have a closed typed vocabulary, never a free-form Detail JSON string.</summary>
public sealed class PublicCombatFact : PublicEvidencePayload
{
    private readonly PublicCard[] _cards;
    private readonly PublicChoice? _choice;
    public PublicCombatFactKind FactKind { get; }
    public int? Turn { get; }
    public int? TargetSlot { get; }
    public int? SourceSlot { get; }
    public string? Model { get; }
    public string? TargetModel { get; }
    public PublicCard[] Cards => EvidenceGuard.Copy(_cards);
    public ImmutableArray<PublicIntent> Intents { get; }
    public PublicChoice? Choice => _choice is null ? null : EvidenceGuard.Copy(_choice);
    public PublicEvidenceAssets? Assets { get; }
    public PublicDamageFact? Damage { get; }
    public PublicPreSettlementFact? Settlement { get; }
    public decimal? Amount { get; }
    public int? EnergySpent { get; }
    public int? StarsSpent { get; }
    public PublicCardPile? ResultPile { get; }
    public int? UnidentifiedCount { get; }
    [JsonConstructor]
    public PublicCombatFact(PublicCombatFactKind factKind, int? turn = null, int? targetSlot = null,
        int? sourceSlot = null, string? model = null, PublicCard[]? cards = null,
        ImmutableArray<PublicIntent> intents = default, PublicChoice? choice = null,
        PublicEvidenceAssets? assets = null, PublicDamageFact? damage = null, decimal? amount = null,
        int? energySpent = null, int? starsSpent = null, PublicCardPile? resultPile = null, int? unidentifiedCount = null, string? targetModel = null, PublicPreSettlementFact? settlement = null)
    {
        FactKind = EvidenceGuard.Enum(factKind); Turn = turn; TargetSlot = targetSlot; SourceSlot = sourceSlot;
        TargetModel = targetModel is null ? null : EvidenceGuard.Key(targetModel);
        Model = model is null ? null : EvidenceGuard.Key(model); _cards = EvidenceGuard.Cards(cards ?? []);
        Intents = intents.IsDefault ? [] : EvidenceGuard.Array(intents, nameof(intents));
        foreach (var intent in Intents) EvidenceGuard.Intent(intent);
        _choice = choice is null ? null : EvidenceGuard.Choice(choice); Assets = assets; Damage = damage;
        Amount = amount; EnergySpent = energySpent; StarsSpent = starsSpent; ResultPile = resultPile;
        UnidentifiedCount = unidentifiedCount; Settlement = settlement;
        if (turn < 0 || targetSlot < -2 || sourceSlot < -2 || targetSlot == -1 || sourceSlot == -1
            || energySpent < 0 || starsSpent < 0 || unidentifiedCount < 0) throw new ArgumentException("Invalid public combat values");
        if (resultPile is not null) EvidenceGuard.Enum(resultPile.Value);
        // Presence mask prevents irrelevant fields carrying unvalidated/private snapshots.
        int fields = (turn.HasValue ? 1 : 0) | (targetSlot.HasValue ? 2 : 0) | (sourceSlot.HasValue ? 4 : 0)
            | (model is not null ? 8 : 0) | (_cards.Length > 0 ? 16 : 0) | (Intents.Length > 0 ? 32 : 0)
            | (choice is not null ? 64 : 0) | (assets is not null ? 128 : 0) | (damage is not null ? 256 : 0)
            | (amount.HasValue ? 512 : 0) | (energySpent.HasValue ? 1024 : 0) | (starsSpent.HasValue ? 2048 : 0)
            | (resultPile.HasValue ? 4096 : 0) | (unidentifiedCount.HasValue ? 8192 : 0) | (targetModel is not null ? 16384 : 0) | (settlement is not null ? 32768 : 0);
        int allowed = factKind switch {
            PublicCombatFactKind.Started or PublicCombatFactKind.PlayerTurnEnded or PublicCombatFactKind.Shuffled or PublicCombatFactKind.HiddenCardGenerated => 0,
            PublicCombatFactKind.EntryAssets => 128,
            PublicCombatFactKind.PreSettlement => 32768,
            PublicCombatFactKind.PlayerTurnStarted => 1,
            PublicCombatFactKind.IntentPublished => 2 | 8 | 32,
            PublicCombatFactKind.CardDrawn or PublicCombatFactKind.CardStarted or PublicCombatFactKind.CardGenerated => 16,
            PublicCombatFactKind.CardPlayed => 16 | 1024 | 2048 | 4096,
            PublicCombatFactKind.PotionUsed => 8,
            PublicCombatFactKind.Damage => 2 | 4 | 256 | 16384,
            PublicCombatFactKind.PowerChanged => 2 | 4 | 8 | 512 | 16384,
            PublicCombatFactKind.ChoiceOffered => 64,
            PublicCombatFactKind.AutomaticSelection => 8 | 16 | 8192,
            _ => throw new ArgumentException("Unsupported combat fact") };
        bool required = factKind switch {
            PublicCombatFactKind.EntryAssets => assets is not null,
            PublicCombatFactKind.PreSettlement => settlement is not null,
            PublicCombatFactKind.PlayerTurnStarted => turn.HasValue,
            PublicCombatFactKind.IntentPublished => targetSlot.HasValue && model is not null,
            PublicCombatFactKind.CardDrawn or PublicCombatFactKind.CardStarted or PublicCombatFactKind.CardGenerated or PublicCombatFactKind.CardPlayed => _cards.Length == 1,
            PublicCombatFactKind.PotionUsed => model is not null,
            PublicCombatFactKind.Damage => targetSlot.HasValue && damage is not null,
            PublicCombatFactKind.PowerChanged => targetSlot.HasValue && model is not null && amount.HasValue,
            PublicCombatFactKind.ChoiceOffered => choice is not null,
            PublicCombatFactKind.AutomaticSelection => unidentifiedCount.HasValue,
            _ => true };
        if ((fields & ~allowed) != 0 || !required) throw new ArgumentException("Combat payload does not match fact kind");
    }
}

public sealed class PublicPreSettlementFact
{
    private readonly PublicCard[] _hand, _exhaust;
    public int Hp { get; }
    public int MaxHp { get; }
    public PublicCard[] Hand => EvidenceGuard.Copy(_hand);
    public PublicCard[] Exhaust => EvidenceGuard.Copy(_exhaust);
    [JsonConstructor]
    public PublicPreSettlementFact(int hp, int maxHp, PublicCard[] hand, PublicCard[] exhaust)
    {
        Hp = EvidenceGuard.Nonnegative(hp); MaxHp = EvidenceGuard.Nonnegative(maxHp);
        if (hp > maxHp) throw new ArgumentException("HP exceeds maximum");
        _hand = EvidenceGuard.Cards(hand); _exhaust = EvidenceGuard.Cards(exhaust);
    }
}

public sealed class PublicDamageFact
{
    public decimal Blocked { get; }
    public decimal Unblocked { get; }
    public decimal Overkill { get; }
    public int HpAfter { get; }
    public bool Killed { get; }
    [JsonConstructor]
    public PublicDamageFact(decimal blocked, decimal unblocked, decimal overkill, int hpAfter, bool killed)
    {
        if (blocked < 0 || unblocked < 0 || overkill < 0 || hpAfter < 0) throw new ArgumentException("Invalid public damage");
        Blocked = blocked; Unblocked = unblocked; Overkill = overkill; HpAfter = hpAfter; Killed = killed;
    }
}

/// <summary>A stable public packet without recursive run evidence or untyped history; facts are separate events.</summary>
public sealed class PublicCombatDecision : PublicEvidencePayload
{
    private readonly PublicObservation _observation;
    private readonly PublicAction[] _actions;
    public string Status { get; }
    public long HistoryThroughEventOrdinal { get; }
    public bool HistoryCompleteFromCombatStart { get; }
    public PublicObservation Observation => EvidenceGuard.Copy(_observation);
    public PublicAction[] Actions => EvidenceGuard.Copy(_actions);
    [JsonConstructor]
    public PublicCombatDecision(string status, PublicObservation observation, PublicAction[] actions,
        long historyThroughEventOrdinal, bool historyCompleteFromCombatStart)
    {
        if (status is not ("player_decision" or "card_choice")) throw new ArgumentException("Not a stable public decision");
        Status = status; HistoryThroughEventOrdinal = EvidenceGuard.Ordinal(historyThroughEventOrdinal);
        HistoryCompleteFromCombatStart = historyCompleteFromCombatStart;
        _observation = EvidenceGuard.Observation(observation);
        _actions = EvidenceGuard.Actions(actions);
        if (_actions.Length == 0 || _actions.Select(a => a.Revision).Distinct().Count() != 1)
            throw new ArgumentException("A decision requires actions at a single revision");
        if ((status == "card_choice") != (observation.Choice is not null)) throw new ArgumentException("Choice status mismatch");
        foreach (var action in _actions)
        {
            bool valid = action.Kind switch {
                "choose" => observation.Choice is { } c && action.Selection is { } selection && action.Slot == -1 && action.Target == -1
                    && selection.All(i => i < c.Candidates.Length)
                    && ((c.Cancelable && selection.Length == 0) || (selection.Length >= c.Min && selection.Length <= c.Max)),
                "play" => observation.Choice is null && action.Slot >= 0 && action.Slot < observation.Hand.Length,
                "potion" or "discard_potion" => observation.Choice is null && action.Slot >= 0 && action.Slot < observation.Potions.Length
                    && observation.Potions[action.Slot] is not null,
                "end_turn" => observation.Choice is null && action.Slot == -1 && action.Target == -1,
                _ => false };
            if (action.Target >= 0 && !observation.Enemies.Any(e => e.Slot == action.Target)
                && !(observation.Pets?.Any(p => p.Slot == action.Target) ?? false)) valid = false;
            if (!valid) throw new ArgumentException("Action indices disagree with the public decision snapshot");
        }
    }
}
public sealed class PublicCombatActionTaken : PublicEvidencePayload
{
    private readonly PublicAction _action;
    public long DecisionEventOrdinal { get; }
    public PublicAction Action => EvidenceGuard.Copy(_action);
    [JsonConstructor]
    public PublicCombatActionTaken(long decisionEventOrdinal, PublicAction action)
    { DecisionEventOrdinal = EvidenceGuard.Ordinal(decisionEventOrdinal); _action = EvidenceGuard.Actions([action])[0]; }
}

/// <summary>Use this strict codec for the new channel; legacy PublicJson and packet bytes are untouched.</summary>
public static class PublicRunEvidenceJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(PublicJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
    public static string Serialize(PublicRunEvidence evidence) => JsonSerializer.Serialize(evidence, Options);
    public static PublicRunEvidence Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement);
        // A null optional CLR property must not silently widen the frozen v1
        // wire grammar: even an explicit currentMap:null is a v2-only field.
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.String
            && version.GetString() == PublicRunEvidence.Version
            && root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            foreach (var entry in events.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("payload", out var payload)
                    && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("currentMap", out _))
                    throw new JsonException("The currentMap field requires v2 public run evidence");
        return JsonSerializer.Deserialize<PublicRunEvidence>(json, Options)
            ?? throw new ArgumentException("Empty public run evidence");
    }

    public static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate public JSON property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

// Property-scoped: enabling the channel does not change any legacy PublicJson encoding.
public sealed class PublicRunEvidenceJsonConverter : JsonConverter<PublicRunEvidence>
{
    public override PublicRunEvidence? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return PublicRunEvidenceJson.Read(document.RootElement.GetRawText());
    }
    public override void Write(Utf8JsonWriter writer, PublicRunEvidence value, JsonSerializerOptions options) =>
        writer.WriteRawValue(PublicRunEvidenceJson.Serialize(value));
}

internal static class EvidenceGuard
{
    internal static T Copy<T>(T value) => PublicJson.Read<T>(PublicJson.Serialize(value));
    internal static int Nonnegative(int value) => value >= 0 ? value : throw new ArgumentException("Negative public count");
    internal static long Ordinal(long value) => value >= 0 ? value : throw new ArgumentException("Negative observer ordinal");
    internal static T Enum<T>(T value) where T : struct, Enum => System.Enum.IsDefined(value) ? value : throw new ArgumentException("Unknown public enum value");
    internal static string Key(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl)
        ? value : throw new ArgumentException("Invalid public key");
    internal static ImmutableArray<T> Array<T>(ImmutableArray<T> values, string name, bool allowNull = false)
    {
        if (values.IsDefault || (!allowNull && values.Any(x => x is null))) throw new ArgumentException("Missing public collection", name);
        // Do not retain arrays manufactured via ImmutableCollectionsMarshal.AsImmutableArray.
        return ImmutableArray.CreateRange(values.AsSpan().ToArray());
    }
    internal static void Unique(IEnumerable<string> keys)
    { var array = keys.ToArray(); if (array.Distinct(StringComparer.Ordinal).Count() != array.Length) throw new ArgumentException("Duplicate public option keys"); }
    internal static PublicCard[] Cards(PublicCard[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var card in values)
        {
            ArgumentNullException.ThrowIfNull(card); Key(card.Id); Key(card.Type); Nonnegative(card.Upgrade);
            if (card.Keywords is null || card.Keywords.Any(k => string.IsNullOrWhiteSpace(k))) throw new ArgumentException("Invalid card keywords");
            if (card.Details is { } d && (d.EnergyModifiers is null || d.EnergyModifiers.Any(x => x is null || string.IsNullOrWhiteSpace(x.Kind))))
                throw new ArgumentException("Invalid card modifiers");
            if (card.Enchantments is { } e && e.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id))) throw new ArgumentException("Invalid card enchantments");
            if (card.Affliction is { } a) Key(a.Id);
            if (card.PublicState is { } s) foreach (var pair in s) { Key(pair.Key); ArgumentNullException.ThrowIfNull(pair.Value); }
        }
        return Copy(values);
    }
    internal static PublicRelic[] Relics(PublicRelic[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var relic in values) { ArgumentNullException.ThrowIfNull(relic); Key(relic.Id); ArgumentNullException.ThrowIfNull(relic.Details);
            foreach (var key in relic.Details.Keys) Key(key); if (relic.Cards is not null) Cards(relic.Cards); if (relic.SelectedModel is not null) Key(relic.SelectedModel); }
        return Copy(values);
    }
    internal static PublicChoice Choice(PublicChoice value)
    {
        ArgumentNullException.ThrowIfNull(value); Key(value.Source); Cards(value.Candidates);
        if (value.Min < 0 || value.Max < value.Min
            || value.CandidateOrder is not ("public" or "canonical_unordered_reveal")) throw new ArgumentException("Invalid visible card choice");
        if (value.Bundles is not null) { if (value.Bundles.Length != value.Candidates.Length) throw new ArgumentException("Candidate/bundle mismatch"); foreach (var bundle in value.Bundles) Cards(bundle); }
        return Copy(value);
    }
    internal static void Intent(PublicIntent intent)
    { ArgumentNullException.ThrowIfNull(intent); Key(intent.Kind); if (intent.Damage < 0 || intent.Repeats < 0) throw new ArgumentException("Invalid public intent"); }
    private static void Powers(PublicPower[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var power in values) { ArgumentNullException.ThrowIfNull(power); Key(power.Id);
            if (power.SelectedCard is not null) Key(power.SelectedCard);
            if (power.SelectedUpgrade < 0 || power.ApplierSlot < -2 || power.ApplierSlot == -1) throw new ArgumentException("Invalid public power details"); }
    }
    internal static PublicAction[] Actions(PublicAction[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var action in values)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (action.Revision < 0 || action.Kind is not ("play" or "end_turn" or "potion" or "discard_potion" or "choose")
                || action.Slot < -1 || action.Target < -2 || (action.Kind == "choose") != (action.Selection is not null)
                || (action.Selection is { } s && (s.Any(i => i < 0) || s.Distinct().Count() != s.Length))) throw new ArgumentException("Invalid public action");
        }
        if (values.Select(PublicJson.Serialize).Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Duplicate public actions");
        return Copy(values);
    }
    internal static PublicObservation Observation(PublicObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Schema is not ("nosl.public.v2" or "nosl.public.v3") || value.History is null || value.History.Length != 0)
            throw new ArgumentException("Evidence decisions require a known schema and empty untyped history; record typed facts separately");
        if ((value.Schema == PublicRunContext.ObservationSchema) != (value.RunContext is not null)) throw new ArgumentException("Public context schema mismatch");
        value.RunContext?.Validate();
        Cards(value.Hand); Cards(value.Discard); Cards(value.Exhaust);
        ArgumentNullException.ThrowIfNull(value.UnknownDraw); ArgumentNullException.ThrowIfNull(value.KnownDraw);
        foreach (var count in value.UnknownDraw) { ArgumentNullException.ThrowIfNull(count); Cards([count.Card]); if (count.Count <= 0) throw new ArgumentException("Invalid draw count"); }
        foreach (var known in value.KnownDraw) { ArgumentNullException.ThrowIfNull(known); Cards([known.Card]); Nonnegative(known.Position); }
        if (value.KnownDraw.Select(k => k.Position).Distinct().Count() != value.KnownDraw.Length
            || value.KnownDraw.Any(k => k.Position >= value.DrawCount)
            || value.UnknownDraw.Sum(x => (long)x.Count) + value.KnownDraw.Length + value.UnidentifiedDrawCount != value.DrawCount)
            throw new ArgumentException("Draw visibility counts disagree");
        if (value.Choice is not null) Choice(value.Choice);
        if (value.RelicStates is not null) Relics(value.RelicStates);
        ArgumentNullException.ThrowIfNull(value.Potions); foreach (var potion in value.Potions) if (potion is not null) Key(potion);
        ArgumentNullException.ThrowIfNull(value.Relics); foreach (var relic in value.Relics) Key(relic);
        Powers(value.Powers); ArgumentNullException.ThrowIfNull(value.Enemies);
        foreach (var enemy in value.Enemies) { ArgumentNullException.ThrowIfNull(enemy); Key(enemy.Id); Nonnegative(enemy.Slot);
            Nonnegative(enemy.Hp); Nonnegative(enemy.MaxHp); if (enemy.Hp > enemy.MaxHp) throw new ArgumentException("Enemy HP exceeds maximum");
            Powers(enemy.Powers); ArgumentNullException.ThrowIfNull(enemy.Intents); foreach (var intent in enemy.Intents) Intent(intent); }
        if (value.Enemies.Select(e => e.Slot).Distinct().Count() != value.Enemies.Length) throw new ArgumentException("Duplicate public enemy slot");
        if (value.Orbs is not null) foreach (var orb in value.Orbs) { ArgumentNullException.ThrowIfNull(orb); Key(orb.Id); }
        if (value.Pets is not null) foreach (var pet in value.Pets) { ArgumentNullException.ThrowIfNull(pet); Key(pet.Id); Nonnegative(pet.Slot);
            Nonnegative(pet.Hp); Nonnegative(pet.MaxHp); if (pet.Hp > pet.MaxHp) throw new ArgumentException("Pet HP exceeds maximum"); Powers(pet.Powers); }
        foreach (var count in new[] { value.Hp, value.MaxHp, value.StartHp, value.Ascension, value.Turn, value.Gold, value.StartGold,
            value.DrawCount, value.UnidentifiedDrawCount, value.OrbCapacity }) Nonnegative(count);
        if (value.Hp > value.MaxHp) throw new ArgumentException("HP exceeds maximum");
        return Copy(value);
    }
}

internal sealed class PublicRunEvidenceValidation
{
    private sealed record Owner(PublicOwnerStarted Start, bool Ended = false, bool HasGap = false,
        bool CombatStarted = false, long LastEvent = 0, long? PendingOffer = null);
    private readonly ImmutableDictionary<long, Owner> _owners;
    private readonly bool _began, _gap;
    internal bool CompleteFromRunStart => _began && !_gap;

    private PublicRunEvidenceValidation(ImmutableDictionary<long, Owner> owners, bool began, bool gap)
    { _owners = owners; _began = began; _gap = gap; }

    internal static PublicRunEvidenceValidation Validate(ImmutableArray<PublicRunEvidenceEvent> events, bool complete)
    {
        if (events.Length == 0) throw new ArgumentException("Evidence must explicitly state run start or its absence");
        var state = new PublicRunEvidenceValidation(ImmutableDictionary<long, Owner>.Empty, false, false);
        for (int i = 0; i < events.Length; i++) state = state.Append(events, events[i], i);
        if (complete != state.CompleteFromRunStart) throw new ArgumentException("Run completeness contradicts recorded boundaries or gaps");
        return state;
    }

    // This same transition checks both external full envelopes and incremental appends.
    // All state is immutable so failed transitions and branches cannot affect an earlier prefix.
    internal PublicRunEvidenceValidation Append(ImmutableArray<PublicRunEvidenceEvent> events,
        PublicRunEvidenceEvent entry, int i)
    {
        bool began = i == 0 ? entry.Payload is PublicRunStarted : _began;
        if (i == 0 && !began && entry.Payload is not PublicEvidenceGap { Reason: PublicEvidenceGapReason.RunStartNotObserved })
            throw new ArgumentException("Missing explicit run-start gap");
        var owners = _owners;
        bool gap = _gap;
        if (entry.EventOrdinal != i) throw new ArgumentException("Event ordinals must be contiguous observer ordinals");
        Owner? owner = null;
        if (entry.Payload is PublicOwnerStarted start)
        {
            if (entry.OwnerOrdinal != owners.Count) throw new ArgumentException("Owner ordinals must be contiguous observer ordinals");
            if (start.ParentOwnerOrdinal is long parent && (!owners.TryGetValue(parent, out var parentOwner) || parentOwner.Ended))
                throw new ArgumentException("Parent owner must precede its child and remain open");
            owner = new(start, LastEvent: i);
            if (!start.CompleteFromOwnerStart) gap = true;
        }
        else if (entry.OwnerOrdinal is long id)
        {
            if (!owners.TryGetValue(id, out owner) || owner.Ended) throw new ArgumentException("Unknown or ended public owner");
        }
        else if (entry.Payload is not (PublicRunStarted or PublicEvidenceGap)) throw new ArgumentException("Observation requires an owner");
        PublicEvidencePayload Referenced(long ordinal)
        {
            if (ordinal >= i || ordinal < 0 || events[(int)ordinal].OwnerOrdinal != entry.OwnerOrdinal)
                throw new ArgumentException("Choice must refer to an earlier observation for the same owner");
            return events[(int)ordinal].Payload;
        }
        void Choose(long ordinal)
        {
            if (owner!.PendingOffer != ordinal) throw new ArgumentException("Choice must consume the latest unconsumed offer");
            owner = owner with { PendingOffer = null };
        }
        switch (entry.Payload)
        {
            case PublicRunStarted:
                if (i != 0 || entry.OwnerOrdinal is not null) throw new ArgumentException("Run start can only occur once at ordinal zero");
                break;
            case PublicEvidenceGap g:
                if (g.Reason == PublicEvidenceGapReason.RunStartNotObserved && (i != 0 || entry.OwnerOrdinal is not null))
                    throw new ArgumentException("Run-start gap is only valid at ordinal zero");
                gap = true; if (owner is not null) owner = owner with { HasGap = true };
                else foreach (var active in owners.Where(p => !p.Value.Ended))
                    owners = owners.SetItem(active.Key, active.Value with { HasGap = true });
                break;
            case PublicOwnerEnded end:
                if (owners.Any(p => p.Value.Start.ParentOwnerOrdinal == entry.OwnerOrdinal && !p.Value.Ended))
                    throw new ArgumentException("Child owners must end before their parent");
                if (end.Outcome == PublicEvidenceOwnerOutcome.Interrupted && !owner!.HasGap)
                    throw new ArgumentException("Interrupted owner requires an explicit gap");
                if (owner!.Start.OwnerKind == PublicEvidenceOwnerKind.Combat && !owner.CombatStarted && !owner.HasGap)
                    throw new ArgumentException("Completed combat requires its public start or an explicit history gap");
                owner = owner with { Ended = true };
                break;
            case PublicOffersObserved offers:
                if (owner!.Start.OwnerKind is not (PublicEvidenceOwnerKind.Reward or PublicEvidenceOwnerKind.Shop or PublicEvidenceOwnerKind.Event))
                    throw new ArgumentException("Offers require a reward, shop or event owner");
                if (offers.ReplacesOfferEventOrdinal is long prior && Referenced(prior) is not PublicOffersObserved)
                    throw new ArgumentException("Replacement must refer to displayed offers");
                owner = owner with { PendingOffer = i };
                break;
            case PublicOptionsObserved:
                if (owner!.Start.OwnerKind is not (PublicEvidenceOwnerKind.Event or PublicEvidenceOwnerKind.Rest or PublicEvidenceOwnerKind.Shop))
                    throw new ArgumentException("Options require an event, rest or shop owner");
                owner = owner with { PendingOffer = i };
                break;
            case PublicOptionChosen chosen:
                bool enabled = Referenced(chosen.OfferEventOrdinal) switch {
                    PublicOffersObserved o => o.Groups.SelectMany(g => g.Offers).Any(x => x.Key == chosen.Key && !x.IsLocked),
                    PublicOptionsObserved o => o.Options.Any(x => x.Key == chosen.Key && !x.IsLocked), _ => false };
                if (!enabled) throw new ArgumentException("Selected option was not visibly enabled");
                Choose(chosen.OfferEventOrdinal); break;
            case PublicCardsObserved:
                owner = owner! with { PendingOffer = i }; break;
            case PublicCardsChosen chosen:
                if (Referenced(chosen.OfferEventOrdinal) is not PublicCardsObserved cards) throw new ArgumentException("Missing public card candidates");
                var choice = cards.Choice;
                if (chosen.Cancelled ? !choice.Cancelable : chosen.Selection.Length < choice.Min || chosen.Selection.Length > choice.Max
                    || chosen.Selection.Any(x => x >= choice.Candidates.Length)) throw new ArgumentException("Selection is outside the visible card choice");
                Choose(chosen.OfferEventOrdinal); break;
            case PublicMapObserved:
                if (owner!.Start.OwnerKind != PublicEvidenceOwnerKind.Map) throw new ArgumentException("Map snapshot requires map owner");
                owner = owner with { PendingOffer = i }; break;
            case PublicMapChosen chosen:
                if (Referenced(chosen.OfferEventOrdinal) is not PublicMapObserved map || !map.Options.Any(o => o.Coordinate == chosen.Coordinate))
                    throw new ArgumentException("Chosen map node was not offered");
                Choose(chosen.OfferEventOrdinal); break;
            case PublicCombatFact fact:
                if (owner!.Start.OwnerKind != PublicEvidenceOwnerKind.Combat) throw new ArgumentException("Combat fact requires combat owner");
                if (fact.FactKind == PublicCombatFactKind.Started)
                {
                    if (owner.CombatStarted) throw new ArgumentException("Duplicate combat start");
                    owner = owner with { CombatStarted = true };
                }
                else if (!owner.CombatStarted && owner.Start.CompleteFromOwnerStart && !owner.HasGap)
                    throw new ArgumentException("Missing combat-start observation");
                break;
            case PublicCombatDecision decision:
                if (owner!.Start.OwnerKind != PublicEvidenceOwnerKind.Combat || decision.HistoryThroughEventOrdinal != owner.LastEvent)
                    throw new ArgumentException("Decision must link the entire preceding owner history");
                if (owner.Start.CompleteFromOwnerStart && !owner.CombatStarted && !owner.HasGap)
                    throw new ArgumentException("Stable combat decision requires its observed start or an explicit gap");
                bool historyComplete = owner.Start.CompleteFromOwnerStart && owner.CombatStarted && !owner.HasGap;
                if (decision.HistoryCompleteFromCombatStart != historyComplete) throw new ArgumentException("Decision history completeness contradicts its prefix");
                owner = owner with { PendingOffer = i }; break;
            case PublicCombatActionTaken taken:
                if (Referenced(taken.DecisionEventOrdinal) is not PublicCombatDecision referencedDecision
                    || !referencedDecision.Actions.Any(a => PublicJson.Serialize(a) == PublicJson.Serialize(taken.Action)))
                    throw new ArgumentException("Action was not offered by the linked stable decision");
                Choose(taken.DecisionEventOrdinal); break;
        }
        if (owner is not null) owners = owners.SetItem(entry.OwnerOrdinal!.Value, owner with { LastEvent = i });
        return new(owners, began, gap);
    }
}
