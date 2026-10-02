using System.Text.Json.Serialization;

namespace Nosl.Objectives;

[JsonConverter(typeof(JsonStringEnumConverter<TerminalKind>))]
public enum TerminalKind { Win, Loss, ComputeTruncated, EngineError, PolicyNonterminating }

public sealed record InventoryQuantity(string ResourceId, int Count);
// Provenance is diagnostic. Never add these movements to the start-minus-end inventory cost again.
public sealed record ResourceEvent(string Kind, string ResourceId, int Quantity, string PublicSource);
// Future value only: any immediate HP change is already in HpAfterSettlement.
public sealed record PermanentChange(string Kind, double Amount, string PublicSource);

/// <summary>Outcome facts only. Private seeds/digests belong in a separate audit record.</summary>
public sealed record RolloutOutcome
{
    public TerminalKind TerminalKind { get; init; }
    public bool? PlayerAlive { get; init; }
    public int HpAtCombatStart { get; init; }
    public int? HpAfterSettlement { get; init; }
    public int MaxHpStart { get; init; }
    public int? MaxHpAfterSettlement { get; init; }
    public double? CumulativeHpDamage { get; init; }
    public double? HealingReceived { get; init; }
    // Signed committed HP changes outside damage/healing (direct sets and max-HP caps).
    // Diagnostic only; legacy incomplete records intentionally leave this unknown.
    public double? OtherHpAdjustment { get; init; }
    public bool HpEventDiagnosticsComplete { get; init; }
    public bool InventorySnapshotsComplete { get; init; }
    public InventoryQuantity[] InventoryStart { get; init; } = [];
    public InventoryQuantity[] InventoryEnd { get; init; } = [];
    public ResourceEvent[] ResourceEvents { get; init; } = [];
    public bool ResourceProvenanceComplete { get; init; }
    public PermanentChange[] PermanentChanges { get; init; } = [];
    public string? PersistentAssetsAtStartJson { get; init; }
    public string? PersistentAssetsAfterSettlementJson { get; init; }
    public bool PermanentChangesComplete { get; init; }
    public bool? SpecifiedFinishSuccess { get; init; }
    public bool? EarnedBonus { get; init; }
    public bool? DeadlineMet { get; init; }
    public int PlayerTurnsElapsed { get; init; }
    public long AtomicActionsExecuted { get; init; }
    public bool SettlementComplete { get; init; }
    public string SettlementProfileId { get; init; } = "";
    public string ContinuationPolicyId { get; init; } = "";
    public string? Detail { get; init; }
    public bool IsTrueTerminal => TerminalKind is TerminalKind.Win or TerminalKind.Loss;
}

public sealed record ResourceValue(double HpEquivalent, string Evidence, bool Calibrated = false);

public sealed record ObjectiveProfile
{
    public string Id { get; init; } = "nosl_silent_a10_terminal_v4_candidate";
    public double DefeatCost { get; init; } = 1000;
    public double DownsideCoefficient { get; init; } = .2;
    public bool Calibrated { get; init; }
    public string CalibrationEvidence { get; init; } = "";
    public string ValueTableVersion { get; init; } = "unresolved-v1";
    public double MaximumAbsoluteUnitValue { get; init; } = 100;
    // Explicit caps prevent an arbitrarily large resource bonus overwhelming the finite death penalty.
    public double MaximumAbsoluteInventoryAdjustment { get; init; } = 100;
    public double MaximumAbsolutePermanentAdjustment { get; init; } = 100;
    public IReadOnlyDictionary<string, ResourceValue> InventoryValues { get; init; } = new Dictionary<string, ResourceValue>();
    public IReadOnlyDictionary<string, ResourceValue> PermanentFutureValues { get; init; } = new Dictionary<string, ResourceValue>();
    public static ObjectiveProfile Candidate => new();

    public void Validate()
    {
        if (InventoryValues is null || PermanentFutureValues is null
            || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ValueTableVersion)
            || !double.IsFinite(DefeatCost) || DefeatCost <= 0
            || !double.IsFinite(DownsideCoefficient) || DownsideCoefficient < 0
            || !double.IsFinite(MaximumAbsoluteUnitValue) || MaximumAbsoluteUnitValue <= 0
            || !double.IsFinite(MaximumAbsoluteInventoryAdjustment) || MaximumAbsoluteInventoryAdjustment < 0
            || !double.IsFinite(MaximumAbsolutePermanentAdjustment) || MaximumAbsolutePermanentAdjustment < 0)
            throw new ArgumentException("Invalid objective profile");
        foreach (var pair in InventoryValues.Concat(PermanentFutureValues))
            if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value.HpEquivalent)
                || Math.Abs(pair.Value.HpEquivalent) > MaximumAbsoluteUnitValue || string.IsNullOrWhiteSpace(pair.Value.Evidence))
                throw new ArgumentException("Resource values require a finite bound and evidence");
    }

    public void AssertFormalLabelsAllowed()
    {
        Validate();
        if (!Calibrated || string.IsNullOrWhiteSpace(CalibrationEvidence)
            || InventoryValues.Concat(PermanentFutureValues).Any(x => !x.Value.Calibrated))
            throw new InvalidOperationException("Uncalibrated objective/resource profile cannot generate formal labels");
    }
}

public enum EvaluationStatus { Scored, Incomplete, ObjectiveValueUnresolved, InvalidOutcome }
public sealed record OutcomeEvaluation(EvaluationStatus Status, double? Cost, double? NetHpLoss,
    double? InventoryAdjustment, double? PermanentFutureValue, string[] Reasons);
public sealed record BatchEvaluation(int AssignedWorlds, int Wins, int Losses, int ComputeTruncated,
    int EngineErrors, int PolicyNonterminating, int ValueUnresolved, int InvalidOutcomes,
    double CompletionRate, double? ExpectedCost, double? ExpectedNetHpLoss,
    EstimateInterval WinProbabilityBounds, EstimateInterval LossProbabilityBounds, EstimateInterval DeathProbabilityBounds,
    bool FormalLabelsAllowed, string[] Reasons);
