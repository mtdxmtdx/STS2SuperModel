using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

public enum ActionCandidateKind
{
    PlayCard,
    UsePotion,
    Choice,
    EndTurn
}

public sealed record ActionCandidate(
    ActionCandidateKind Kind,
    string ActionId,
    string? SourceModelId = null,
    string? SourceInstanceId = null,
    string? TargetId = null,
    string? ChoiceId = null,
    ImmutableArray<string> SelectedCardInstanceIds = default,
    int EffectiveEnergyCost = 0,
    bool Legal = true,
    string? Restriction = null)
{
    public ImmutableArray<string> SafeSelectedCardInstanceIds =>
        SelectedCardInstanceIds.IsDefault ? ImmutableArray<string>.Empty : SelectedCardInstanceIds;
}

public sealed record TraceActionRecord(
    string TraceId,
    int Step,
    string Decision,
    string PreStateHash,
    string? PostStateHash,
    ActionCandidate? Action,
    ImmutableDictionary<string, long>? RngBefore = null,
    ImmutableDictionary<string, long>? RngAfter = null,
    bool ProducedChanceBranch = false,
    string? Error = null);

public sealed record TrainingDecisionRecord(
    string RecordId,
    int SchemaVersion,
    string GameVersion,
    string GameCommit,
    string AssemblySha256,
    string CliProtocolVersion,
    string SimulatorVersion,
    string ScorerVersion,
    string GeneratorConfigHash,
    string EpisodeId,
    string Character,
    int Ascension,
    int Act,
    int Floor,
    string CombatId,
    int Round,
    string PublicStateHash,
    string TeacherStateHash,
    ImmutableArray<ActionCandidate> LegalActions,
    ImmutableArray<string> TeacherBestActions,
    ImmutableDictionary<string, decimal> ActionValues,
    PredictionConfidence Confidence,
    bool SearchComplete,
    string? RiskSummary = null,
    string SemanticDatabaseVersion = "unknown",
    string FeatureSchemaVersion = "unknown",
    string ModelVersion = "none")
{
    public ImmutableArray<ActionCandidate> SafeLegalActions =>
        LegalActions.IsDefault ? ImmutableArray<ActionCandidate>.Empty : LegalActions;

    public ImmutableArray<string> SafeTeacherBestActions =>
        TeacherBestActions.IsDefault ? ImmutableArray<string>.Empty : TeacherBestActions;

    public ImmutableDictionary<string, decimal> SafeActionValues =>
        ActionValues ?? ImmutableDictionary<string, decimal>.Empty;
}

public sealed record DatasetManifest(
    string DatasetId,
    int SchemaVersion,
    string GameVersion,
    string GameCommit,
    string AssemblySha256,
    string CliProtocolVersion,
    string SimulatorVersion,
    string ScorerVersion,
    string FeatureConfigHash,
    string SplitPolicy,
    long RowCount,
    long StateCount,
    long ActionCount,
    long ReliableCount,
    long EstimatedCount,
    long UncalculableCount,
    ImmutableArray<string> SourceHashes,
    DateTimeOffset CreatedAtUtc,
    string SemanticDatabaseVersion = "unknown",
    string FeatureSchemaVersion = "unknown",
    string ModelVersion = "none")
{
    public ImmutableArray<string> SafeSourceHashes =>
        SourceHashes.IsDefault ? ImmutableArray<string>.Empty : SourceHashes;
}
