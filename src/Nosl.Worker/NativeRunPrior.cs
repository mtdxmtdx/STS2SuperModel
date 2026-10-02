using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>Declared source eligibility, not a hidden-state transplant recipe.</summary>
internal sealed record NativeRunExecutionOptions(int MaxFloors = 60, int SourceDecisionHorizon = 10000,
    string SourcePolicyId = PublicContinuationPolicies.ReviewedId);

/// <summary>
/// Draw one independent seed and one slot from a fixed range. A run with several
/// matching roots contributes once for each matching slot, never just its first match.
/// The same law generates source roots and independent posterior proposals.
/// </summary>
internal sealed record NativeRunPrior
{
    public const string Version = "nosl.native-run-slot-prior.v1";
    public string SchemaVersion { get; init; } = Version;
    public NativeRunExecutionOptions Execution { get; init; } = new();
    public int EligibleSlots { get; init; } = 256;
    public ulong[]? FiniteSeedSupport { get; init; }
    public string SeedLaw => FiniteSeedSupport is null ? "uniform-uint64-hex-v1" : "uniform-declared-finite-seeds-v1";
    public string SlotLaw => "uniform-fixed-combat-decision-slot-including-choices-v1";
    public string OutsideCombatScript => NaturalSourceCollector.ScriptVersion;

    internal NativeRunPrior Freeze()
    {
        Validate();
        return this with { Execution = Execution with { }, FiniteSeedSupport = FiniteSeedSupport?.ToArray() };
    }

    internal void Validate()
    {
        if (SchemaVersion != Version || Execution is null || Execution.MaxFloors is < 1 or > 100
            || Execution.SourceDecisionHorizon is < 1 or > 100000 || EligibleSlots is < 1 or > 100000)
            throw new ArgumentException("Invalid declared native run prior or source horizon");
        _ = PublicContinuationPolicies.Create(Execution.SourcePolicyId);
        if (FiniteSeedSupport is { } support && (support.Length is < 1 or > 4096 || support.Distinct().Count() != support.Length))
            throw new ArgumentException("A finite seed prior must declare unique support before collecting roots");
    }

    internal string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(this)))).ToLowerInvariant();
    internal NativeRunRecipe Draw(Rng random)
    {
        ulong seed = FiniteSeedSupport is { } support ? support[random.NextInt(support.Length)] : random.NextUnsignedLong();
        return new(seed, random.NextInt(EligibleSlots));
    }
    internal IEnumerable<NativeRunRecipe> EnumerateFiniteRecipes()
    {
        if (FiniteSeedSupport is null) throw new InvalidOperationException("Exact enumeration requires a declared finite prior");
        foreach (ulong seed in FiniteSeedSupport)
            for (int slot = 0; slot < EligibleSlots; slot++) yield return new(seed, slot);
    }
}

internal sealed record NativeRunRecipe(ulong Seed, int Slot)
{
    internal string IndependentRunSeed => $"NOSL-OWNED-RUN-V1:{Seed:X16}";
}
