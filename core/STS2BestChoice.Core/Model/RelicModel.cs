using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public enum RelicEffectSupportStatus
{
    NoCombatEffect,
    StateCapturedOnly,
    SimulatorSupported,
    PartiallySupported,
    UnsupportedKnownEffect,
    Unknown
}

public enum RelicEvidenceLevel
{
    ILConfirmed,
    LiveObserved,
    OfficialPatchNote,
    WikiText,
    HeuristicInferred,
    Unknown
}

public enum RelicCombatRelevance
{
    NonCombat,
    CombatPassive,
    CombatStart,
    TurnStart,
    TurnEnd,
    CardPlay,
    DamageTrigger,
    HpTrigger,
    EnergyTrigger,
    PotionTrigger,
    OrbTrigger,
    CombatEnd
}

public sealed record RelicState(
    string Id,
    int? Counter = null,
    ImmutableDictionary<string, decimal>? DynamicVars = null,
    bool IsEnabled = true,
    bool IsUsedUp = false,
    int UsesThisTurn = 0,
    int UsesThisCombat = 0,
    RelicEffectSupportStatus SupportStatus = RelicEffectSupportStatus.Unknown,
    RelicEvidenceLevel EvidenceLevel = RelicEvidenceLevel.Unknown,
    bool UnknownStatePresent = false,
    string? Name = null,
    string? Description = null)
{
    public ImmutableDictionary<string, decimal> SafeDynamicVars =>
        DynamicVars ?? ImmutableDictionary<string, decimal>.Empty;
}

public sealed record RelicCatalogEntry(
    string RelicId,
    string CanonicalName,
    string LocalizedNameZh,
    string Rarity,
    string Description,
    string DynamicDescription,
    ImmutableArray<string> Keywords,
    ImmutableArray<string> Aliases,
    string WikiUrl,
    string ModelType,
    string RuntimeType,
    ImmutableArray<string> DynamicVarNames,
    ImmutableDictionary<string, decimal> DynamicVars,
    bool HasCounter,
    int? DefaultCounter,
    RelicCombatRelevance CombatRelevance,
    RelicEffectSupportStatus SupportStatus,
    RelicEvidenceLevel EvidenceLevel,
    string EvidenceSource,
    string EvidenceReference,
    string VerifiedGameVersion,
    string Notes);

public sealed record RelicCatalogDocument(
    int SchemaVersion,
    string GameVersion,
    string GameCommit,
    string AssemblySha256,
    string CliProtocolVersion,
    string GeneratedAtUtc,
    ImmutableArray<RelicCatalogEntry> Relics);
