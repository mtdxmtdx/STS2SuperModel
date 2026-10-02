using System.Text.Json.Serialization;

namespace Sts2Sim.Core.Random;

/// <summary>
/// One Rewards cell of the separately versioned hypothetical hybrid tape. Distinct
/// tuples have independent uniform 64-bit words, independent of the full-state tape
/// used by untagged RNGs. Clones and same-seed Rewards reconstructions share cells.
/// This changes Rewards/full-state collisions and shifted-state collisions between
/// different Rewards seeds; all aliases within the untagged partition remain intact.
/// </summary>
public readonly record struct LabelRandomAddressV1(string OriginFamily, ulong InitialSeed, ulong RawCursor);

/// <summary>
/// Opt-in snapshot marker and source lineage. The seed is the effective native seed of
/// the owned hypothetical generator, never a source-run observation. Numeric generator
/// state and the wrapper Counter cannot reconstruct this metadata.
/// </summary>
public sealed record LabelRandomProvenance(
    string Law,
    string Partition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginFamily = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? InitialSeed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? RawCursor = null)
{
    public const string LawId = "native-rewards-provenance-tape-v1";
    public const string RewardsOrigin = "player-stream/0/Rewards";
    public const string FullStatePartition = "full_state";
    public const string RewardsPartition = "rewards_lineage";

    internal void Validate()
    {
        bool fullState = Partition == FullStatePartition && OriginFamily is null && InitialSeed is null && RawCursor is null;
        bool rewards = Partition == RewardsPartition && OriginFamily == RewardsOrigin && InitialSeed is not null && RawCursor is not null;
        if (Law != LawId || (!fullState && !rewards))
            throw new InvalidOperationException("Missing or unsupported label RNG provenance.");
    }
}
