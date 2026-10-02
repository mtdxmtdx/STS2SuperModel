using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// A label-only generative law. Native rules and raw-word conversions are retained;
/// distinct native generator states address independent hypothetical random words.
/// Equal states still share a word, including exact clones. This is deliberately not
/// the old common-run-seed law. The oracle has a reproducible SHA256 implementation.
/// </summary>
internal sealed record NativeTapePrior
{
    internal const string Version = "nosl.native-state-tape-prior.v1";
    internal const string RewardsVersion = "nosl.native-rewards-state-tape-prior.v1";
    public string SchemaVersion { get; init; } = Version;
    public NativeRunExecutionOptions Execution { get; init; } = new();
    public int EligibleCombats { get; init; } = 80;
    public int EligibleDecisionsPerCombat { get; init; } = 128;
    [JsonIgnore] internal bool UsesRewardsProvenance => SchemaVersion == RewardsVersion;
    public string PrimitiveLaw => UsesRewardsProvenance
        ? "independent-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1"
        : "ideal-independent-uint64-by-native-predraw-state-with-equal-state-aliases-v1";
    public string PrimitiveImplementation => "sha256-address-expansion-with-explicit-conditioned-overrides-v1";
    public string RootLaw => "independent-uniform-fixed-combat-and-local-decision-indices-v1";
    public string OutsideCombatScript => Execution.ResolvedOutsideCombatScript;

    internal NativeTapePrior Freeze()
    {
        if (SchemaVersion is not (Version or RewardsVersion) || Execution is null || Execution.MaxFloors is < 1 or > 100
            || Execution.SourceDecisionHorizon is < 1 or > 100000 || EligibleCombats is < 1 or > 10000
            || EligibleDecisionsPerCombat is < 1 or > 100000)
            throw new ArgumentException("Invalid declared native tape prior");
        _ = PublicContinuationPolicies.Create(Execution.SourcePolicyId);
        _ = Execution.ResolvedOutsideCombatScript;
        _ = Execution.EmitsPublicRunContext;
        _ = Execution.EmitsPublicEvidence;
        if (UsesRewardsProvenance && !Execution.EmitsPublicEvidence)
            throw new ArgumentException("The Rewards hybrid prior requires its explicit public run-evidence channel");
        return this with { Execution = Execution with { } };
    }

    internal string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(this)))).ToLowerInvariant();
    internal NativeTapeRecipe Draw(Rng random) => new(random.NextUnsignedLong(), random.NextUnsignedLong(),
        random.NextUnsignedLong(), (int)ConditionalShuffleProposal.UniformBelow((ulong)EligibleCombats, random.NextUnsignedLong),
        (int)ConditionalShuffleProposal.UniformBelow((ulong)EligibleDecisionsPerCombat, random.NextUnsignedLong));
}

internal sealed record NativeTapeRecipe(ulong RunSeed, ulong TapeSeed, ulong ProposalSeed, int CombatIndex, int DecisionIndex)
{
    internal string IndependentRunSeed => $"NOSL-NATIVE-TAPE-V1:{RunSeed:X16}";
    internal string SourceIdentity => $"{IndependentRunSeed}:{TapeSeed:X16}";
}

// Only a proved contradiction of public evidence may be retried as a nonmatch.
internal sealed class NativePublicConstraintMismatchException(string message) : Exception(message);
