using System.Collections.Immutable;

namespace STS2BestChoice.Core.Model;

/// <summary>
/// Serializable xoshiro256** state used by StS2 v0.110.  Keeping every stream
/// separate prevents a simulated card-generation roll from perturbing shuffle,
/// targeting, or energy-cost prediction.
/// </summary>
public sealed record RngStreamSnapshot(
    string Name,
    ulong State0,
    ulong State1,
    ulong State2,
    ulong State3,
    int Counter = 0,
    bool IsKnown = true)
{
    public RngStreamSnapshot Copy() => this with { };

    public int NextInt(int exclusiveMax, out RngStreamSnapshot next)
    {
        if (!IsKnown || exclusiveMax <= 0)
        {
            next = this;
            return 0;
        }

        var value = NextUnsignedLong(out next);
        // MegaRandom maps a raw 64-bit value into a bounded range with the high
        // half of a 128-bit product (rather than modulo), avoiding modulo bias.
        return (int)(((UInt128)value * (uint)exclusiveMax) >> 64);
    }

    public ulong NextUnsignedLong(out RngStreamSnapshot next)
    {
        if (!IsKnown)
        {
            next = this;
            return 0;
        }

        var result = RotateLeft(State1 * 5, 7) * 9;
        var t = State1 << 17;
        var s2 = State2 ^ State0;
        var s3 = State3 ^ State1;
        var s1 = State1 ^ s2;
        var s0 = State0 ^ s3;
        s2 ^= t;
        s3 = RotateLeft(s3, 45);
        next = this with
        {
            State0 = s0,
            State1 = s1,
            State2 = s2,
            State3 = s3,
            Counter = Counter + 1
        };
        return result;
    }

    private static ulong RotateLeft(ulong value, int count) =>
        (value << count) | (value >> (64 - count));
}

public sealed record RngSnapshotSet(ImmutableDictionary<string, RngStreamSnapshot> Streams)
{
    public const string Shuffle = "Shuffle";
    public const string CombatCardGeneration = "CombatCardGeneration";
    public const string CombatPotionGeneration = "CombatPotionGeneration";
    public const string CombatCardSelection = "CombatCardSelection";
    public const string CombatEnergyCosts = "CombatEnergyCosts";
    public const string CombatTargets = "CombatTargets";
    public const string CombatOrbGeneration = "CombatOrbGeneration";

    public static RngSnapshotSet Empty { get; } = new(ImmutableDictionary<string, RngStreamSnapshot>.Empty);

    public bool IsComplete => Streams.Count > 0 && Streams.Values.All(static stream => stream.IsKnown);

    public RngStreamSnapshot? Get(string name) => Streams.TryGetValue(name, out var stream) ? stream : null;

    public RngSnapshotSet With(RngStreamSnapshot stream) => this with { Streams = Streams.SetItem(stream.Name, stream) };

    public RngSnapshotSet Copy() => new(Streams.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value.Copy()));
}

public enum OutcomeKind
{
    Deterministic,
    Stochastic
}

public sealed record ChanceBranch<T>(
    string Label,
    decimal Probability,
    T State,
    OutcomeKind Kind = OutcomeKind.Stochastic,
    bool ProbabilityKnown = true,
    string? RngSource = null);

public sealed record PolicyBranch(
    string Label,
    decimal Probability,
    ImmutableArray<ActionStep> Actions,
    PredictionConfidence Confidence,
    ImmutableArray<RiskEvent> Risks,
    ImmutableArray<PolicyBranch> Continuations = default)
{
    public ImmutableArray<PolicyBranch> SafeContinuations =>
        Continuations.IsDefault ? ImmutableArray<PolicyBranch>.Empty : Continuations;
}

public sealed record DistributionSummary(
    decimal Expected,
    decimal Minimum,
    decimal Maximum,
    decimal DeathProbability,
    int SampleCount,
    bool IsExact,
    decimal? ConfidenceIntervalLow = null,
    decimal? ConfidenceIntervalHigh = null,
    decimal ProbabilityMass = 1m);
