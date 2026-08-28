using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public enum TargetKind
{
    None,
    Self,
    Companion,
    Enemy,
    AllEnemies
}

public enum CardDestination
{
    Discard,
    Exhaust,
    Remove,
    DrawPileTop,
    Hand
}

public enum EvidenceLevel
{
    Unknown,
    HeuristicInferred,
    WikiText,
    OfficialPatchNote,
    LiveObserved,
    ILConfirmed
}

public enum ObservationView
{
    Public,
    Teacher
}

public enum SemanticSupportStatus
{
    Unknown,
    StateCapturedOnly,
    PartiallySupported,
    SimulatorSupported,
    NoCombatEffect,
    UnsupportedKnownEffect
}

public enum GeneratedCardDestination
{
    Hand,
    DrawPile,
    DiscardPile
}

public enum EffectKind
{
    Damage,
    DynamicDamage,
    LoseEnemyHp,
    RandomEnemyDamage,
    RandomEnemyStatus,
    Outbreak,
    RandomEnemyAttackByExhaustedCount,
    LoseHp,
    Block,
    DynamicBlock,
    Heal,
    GainEnergy,
    ModifyMaxHp,
    ScheduleCurrentBlock,
    Draw,
    DrawToHandSize,
    DrawUntilNonAttack,
    DiscardDrawnNonZeroCost,
    ApplyStatus,
    MultiplyStatus,
    RemoveStatus,
    DiscardCards,
    ExhaustCards,
    ExhaustStatusCards,
    RandomExhaustCards,
    RandomExhaustAttackAndGrow,
    DiscardHand,
    ExhaustHand,
    ExhaustNonAttacksAndBlock,
    GenerateCards,
    GenerateRandomCards,
    AutoPlayFromDrawPile,
    AutoPlayShivsFromExhaust,
    AutoPlaySelfFromPile,
    AutoPlayEtherealFromExhaust,
    CopyPlayedCard,
    CopyChosenHandCard,
    ModifySelectedHandCard,
    ChannelOrbs,
    EvokeOrbs,
    TriggerOrbPassives,
    ModifyOrbCapacity,
    ModifyHandCosts,
    CapHandCosts,
    ModifyPlayedCardCost,
    ModifyPlayedCardDamage,
    ModifyPlayedCardBlock,
    ClearEnemyBlockAndArtifact,
    ChooseDrawToHand,
    ChooseDrawToExhaust,
    ChooseDiscardToHand,
    ChooseDiscardToDrawTop,
    ChooseHandToDrawTop,
    MoveAllZeroCostDiscardToHand,
    MoveRandomRareDrawToHand,
    DiscardHandThenDrawSame,
    Reboot,
    DiscardHandAndGenerate,
    ReturnSelfToHandAfterSkills,
    PlayRestriction,
    KillAllDoomedEnemies,
    UpgradeCards,
    MoveKingsBladeToHand,
    RetainHand,
    TransformCards,
    Summon,
    CompanionDamage,
    KillCompanion,
    DelayedReturnSelfToHand,
    Forge
}

public sealed record StatusState(
    string Id,
    int Amount,
    int Duration = -1,
    bool IsDebuff = false,
    decimal FutureValuePerTurn = 0m,
    CardState? GeneratedCard = null,
    string? RandomSource = null);

public sealed record PowerState(
    string Id,
    string OwnerId,
    string? ApplierId,
    decimal Amount = 0m,
    ImmutableDictionary<string, string>? DynamicVars = null,
    ImmutableDictionary<string, decimal>? Counters = null,
    ImmutableArray<string> TriggerPhases = default,
    string? SourceId = null,
    SemanticSupportStatus Support = SemanticSupportStatus.Unknown,
    EvidenceLevel Evidence = EvidenceLevel.Unknown,
    string? SourceVersion = null)
{
    public ImmutableDictionary<string, string> SafeDynamicVars =>
        DynamicVars ?? ImmutableDictionary<string, string>.Empty;

    public ImmutableDictionary<string, decimal> SafeCounters =>
        Counters ?? ImmutableDictionary<string, decimal>.Empty;

    public ImmutableArray<string> SafeTriggerPhases =>
        TriggerPhases.IsDefault ? ImmutableArray<string>.Empty : TriggerPhases;
}

public sealed record SnapshotProvenance(
    string GameVersion,
    string? GameCommit,
    string? AssemblySha256,
    string CliProtocolVersion,
    string SimulatorVersion,
    int SchemaVersion = 1,
    ObservationView View = ObservationView.Public,
    string? CaptureSource = null,
    string? SemanticDatabaseVersion = null,
    string? ScorerVersion = null,
    string? FeatureSchemaVersion = null,
    string? ModelVersion = null);

/// <summary>
/// An occupied orb slot captured after live modifiers (including Focus) have
/// been applied. The shadow state keeps order because overflow evokes first.
/// </summary>
public sealed record OrbState(
    string Id,
    decimal PassiveValue,
    decimal EvokeValue,
    decimal FocusAdjustment = 0m)
{
    public decimal EffectivePassiveValue => Id.ToUpperInvariant() switch
    {
        "LIGHTNING" or "FROST" or "DARK" => Math.Max(0m, PassiveValue + FocusAdjustment),
        _ => PassiveValue
    };

    public decimal EffectiveEvokeValue => Id.ToUpperInvariant() switch
    {
        "LIGHTNING" or "FROST" => Math.Max(0m, EvokeValue + FocusAdjustment),
        _ => EvokeValue
    };
}

public sealed record EffectSpec(
    EffectKind Kind,
    decimal Amount = 0m,
    string? StatusId = null,
    int Duration = -1,
    bool IsDebuff = false,
    decimal FutureValuePerTurn = 0m,
    string? SourceId = null,
    string? UnsupportedReason = null,
    CardState? GeneratedCard = null,
    TargetKind? TargetOverride = null,
    string? Condition = null,
    GeneratedCardDestination GeneratedDestination = GeneratedCardDestination.Hand,
    int Repeat = 1,
    bool RepeatByEnergySpent = false,
    bool RepeatByOrbCount = false,
    int XBonus = 0,
    bool AmountByEnergySpent = false,
    bool AmountByAliveEnemyCount = false,
    bool AmountByDistinctOrbTypes = false,
    bool AmountByHandAttackCount = false,
    bool AmountByCardsDrawnThisTurn = false,
    bool RepeatByHistoryCounter = false,
    bool RepeatByKillCount = false,
    bool AmountByTargetVulnerableStacks = false,
    bool RepeatByExhaustedCount = false,
    string? RandomSource = null,
    ImmutableArray<CardState> GeneratedCardPool = default,
    bool RandomizeGeneratedPosition = false,
    bool RandomSelectionWithReplacement = false);

public sealed record CardState(
    string InstanceId,
    string ModelId,
    string Name,
    int EnergyCost,
    TargetKind Target,
    ImmutableArray<EffectSpec> Effects,
    CardDestination Destination = CardDestination.Discard,
    decimal PriorityHint = 0m,
    bool IsPlayable = true,
    string? RestrictionReason = null,
    ImmutableArray<ChoiceSpec> Choices = default,
    string? CardType = null,
    bool RetainAtTurnEnd = false,
    bool ExhaustAtTurnEnd = false,
    int EnergyChangeOnDraw = 0,
    decimal HpLossAtTurnEnd = 0m,
    bool CostsX = false,
    bool IsDupe = false,
    int ReplayCount = 0,
    bool IsColorless = false,
    int BaseEnergyCost = 0,
    int EnergyCostChangeOnDraw = 0,
    decimal DamageChangeOnDraw = 0m,
    decimal CombatDamageBonus = 0m,
    bool TemporaryRetainAtTurnEnd = false,
    ImmutableArray<EffectSpec> TurnEndInHandEffects = default,
    bool FreeUntilTurnEnd = false,
    bool FreeThisCombat = false,
    int? TemporaryEnergyCostBeforeCap = null,
    bool? TemporaryCostsXBeforeOverride = null,
    string? Rarity = null,
    decimal CombatBlockBonus = 0m,
    bool IsUpgraded = false)
{
    public ImmutableArray<ChoiceSpec> SafeChoices =>
        Choices.IsDefault ? ImmutableArray<ChoiceSpec>.Empty : Choices;

    public ImmutableArray<EffectSpec> SafeTurnEndInHandEffects =>
        TurnEndInHandEffects.IsDefault ? ImmutableArray<EffectSpec>.Empty : TurnEndInHandEffects;
}

public sealed record PotionState(
    string InstanceId,
    string ModelId,
    string Name,
    TargetKind Target,
    ImmutableArray<EffectSpec> Effects,
    decimal OpportunityCost,
    decimal PriorityHint = 0m,
    bool IsUsable = true,
    string? RestrictionReason = null);

public sealed record ChoiceSpec(
    string Id,
    string Label,
    ImmutableArray<EffectSpec> Effects,
    string? CardInstanceId = null,
    string? RestrictionReason = null,
    ImmutableArray<string> CardInstanceIds = default)
{
    public ImmutableArray<string> SelectedCardInstanceIds =>
        CardInstanceIds.IsDefaultOrEmpty
            ? CardInstanceId is { Length: > 0 } id ? [id] : []
            : CardInstanceIds;
}

public sealed record IntentState(
    string Type,
    decimal DamagePerHit = 0m,
    int Hits = 0,
    ImmutableArray<EffectSpec> Effects = default,
    string? RestrictionReason = null)
{
    public ImmutableArray<EffectSpec> SafeEffects =>
        Effects.IsDefault ? ImmutableArray<EffectSpec>.Empty : Effects;
}

public sealed record CreatureState(
    string Id,
    string Name,
    decimal Hp,
    decimal MaxHp,
    decimal Block,
    ImmutableDictionary<string, StatusState> Statuses,
    ImmutableArray<IntentState> Intents,
    decimal ThreatPerFutureTurn = 0m)
{
    public bool IsAlive => Hp > 0m;
}

public sealed record PlayerState(
    decimal Hp,
    decimal MaxHp,
    decimal Block,
    int Energy,
    int MaxEnergy,
    ImmutableDictionary<string, StatusState> Statuses,
    int CardsPerTurn = 5);

public sealed record CombatHistoryCounters(
    int AttacksPlayedThisTurn = 0,
    int SkillsPlayedThisTurn = 0,
    int CardsPlayedThisCombat = 0,
    int EtherealCardsPlayedThisCombat = 0,
    int CardsDrawnThisCombat = 0,
    int CardsDrawnThisTurn = 0,
    int CardsGeneratedThisCombat = 0,
    int StatusCardsDrawnThisTurn = 0,
    int CardsExhaustedThisTurn = 0,
    int BlockGainedThisTurn = 0,
    int CardsPlayedThisTurn = 0,
    int CardsDiscardedThisTurn = 0,
    int ShivsPlayedThisTurn = 0,
    int DamageReceivedEventsThisCombat = 0);

public sealed record CombatSnapshot(
    string Fingerprint,
    PlayerState Player,
    ImmutableArray<CreatureState> Enemies,
    ImmutableArray<CardState> Hand,
    ImmutableArray<CardState> DrawPile,
    ImmutableArray<CardState> DiscardPile,
    ImmutableArray<CardState> ExhaustPile,
    ImmutableArray<PotionState> Potions,
    ulong RngState,
    int Round,
    bool IsBoss,
    ImmutableArray<string> GlobalRestrictions,
    RngSnapshotSet? RngStreams = null,
    ImmutableArray<OrbState> Orbs = default,
    int OrbCapacity = 0,
    CombatHistoryCounters? HistoryCounters = null,
    ImmutableArray<RelicState> Relics = default,
    ImmutableArray<PowerState> Powers = default,
    SnapshotProvenance? Provenance = null,
    ObservationView View = ObservationView.Teacher)
{
    public ImmutableArray<RelicState> SafeRelics =>
        Relics.IsDefault ? ImmutableArray<RelicState>.Empty : Relics;

    public ImmutableArray<PowerState> SafePowers =>
        Powers.IsDefault ? ImmutableArray<PowerState>.Empty : Powers;

    public static CombatSnapshot Create(
        string fingerprint,
        PlayerState player,
        IEnumerable<CreatureState> enemies,
        IEnumerable<CardState> hand,
        IEnumerable<CardState>? drawPile = null,
        IEnumerable<CardState>? discardPile = null,
        IEnumerable<CardState>? exhaustPile = null,
        IEnumerable<PotionState>? potions = null,
        ulong rngState = 1,
        RngSnapshotSet? rngStreams = null,
        int round = 1,
        bool isBoss = false,
        IEnumerable<string>? globalRestrictions = null,
        IEnumerable<OrbState>? orbs = null,
        int orbCapacity = 0,
        CombatHistoryCounters? historyCounters = null,
        IEnumerable<RelicState>? relics = null,
        IEnumerable<PowerState>? powers = null,
        SnapshotProvenance? provenance = null,
        ObservationView view = ObservationView.Teacher) =>
        new(
            fingerprint,
            player,
            enemies.ToImmutableArray(),
            hand.ToImmutableArray(),
            (drawPile ?? []).ToImmutableArray(),
            (discardPile ?? []).ToImmutableArray(),
            (exhaustPile ?? []).ToImmutableArray(),
            (potions ?? []).ToImmutableArray(),
            rngState,
            round,
            isBoss,
            (globalRestrictions ?? []).ToImmutableArray(),
            rngStreams,
            (orbs ?? []).ToImmutableArray(),
            orbCapacity,
            historyCounters,
            (relics ?? []).ToImmutableArray(),
            (powers ?? []).ToImmutableArray(),
            provenance,
            view);
}
