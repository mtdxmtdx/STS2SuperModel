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
/// One cell in the independently versioned Map partition. ActIndex is native act
/// lineage, so even effective seed collisions between acts cannot alias cells.
/// </summary>
public readonly record struct LabelMapRandomAddressV1(int ActIndex, ulong InitialSeed, ulong RawCursor);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? RawCursor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActIndex = null)
{
    public const string LawId = "native-rewards-provenance-tape-v1";
    public const string MapLawId = "native-map-rewards-provenance-tape-v1";
    public const string RewardsOrigin = "player-stream/0/Rewards";
    public const string MapOrigin = "standard-act-map";
    public const string FullStatePartition = "full_state";
    public const string RewardsPartition = "rewards_lineage";
    public const string MapPartition = "map_lineage";

    internal void Validate()
    {
        bool fullState = Partition == FullStatePartition && OriginFamily is null && InitialSeed is null && RawCursor is null && ActIndex is null;
        bool rewards = Partition == RewardsPartition && OriginFamily == RewardsOrigin && InitialSeed is not null && RawCursor is not null && ActIndex is null;
        bool map = Law == MapLawId && Partition == MapPartition && OriginFamily == MapOrigin
            && InitialSeed is not null && RawCursor is not null && ActIndex is >= 0;
        if (Law is not (LawId or MapLawId) || (!fullState && !rewards && !map))
            throw new InvalidOperationException("Missing or unsupported label RNG provenance.");
    }
}
