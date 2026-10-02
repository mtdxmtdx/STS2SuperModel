using System.Buffers.Binary;
using System.Security.Cryptography;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Independent Rewards partition of the explicit hybrid label prior. The native
/// seed identifies an owned hypothetical lineage; no source graph is accepted.
/// SHA expansion instantiates the ideal oracle, not a finite-seed posterior proof.
/// </summary>
internal sealed class NativeRewardsOracle
{
    private readonly ulong _tapeSeed;
    private readonly Dictionary<LabelRandomAddressV1, ulong> _overrides;
    private readonly HashSet<LabelRandomAddressV1> _visited = [];
    internal NativeRewardsOracle(ulong tapeSeed, IReadOnlyDictionary<LabelRandomAddressV1, ulong>? overrides = null)
    { _tapeSeed = tapeSeed; _overrides = overrides is null ? [] : new(overrides); }
    internal int DistinctCells => _visited.Count;
    internal int ConditionedCells => _overrides.Count;
    internal NativeRewardsOracle ReplayCopy() => new(_tapeSeed, _overrides);
    internal bool WasVisited(LabelRandomAddressV1 address) => _visited.Contains(address);
    internal void ForceFresh(LabelRandomAddressV1 address, ulong word)
    {
        Validate(address);
        if (_visited.Contains(address))
            throw new InvalidOperationException("A Rewards proposal cannot overwrite an already read lineage cell");
        if (_overrides.TryGetValue(address, out ulong previous) && previous != word)
            throw new InvalidOperationException("A Rewards proposal changed its exact replay override");
        _overrides[address] = word;
    }

    private static void Validate(LabelRandomAddressV1 address)
    {
        if (address.OriginFamily != LabelRandomProvenance.RewardsOrigin)
            throw new InvalidOperationException("Unknown hypothetical Rewards lineage");
    }

    internal ulong Word(LabelRandomAddressV1 address)
    {
        Validate(address);
        _visited.Add(address);
        if (_overrides.TryGetValue(address, out ulong forced)) return forced;
        Span<byte> bytes = stackalloc byte[32];
        // Separate namespace from the unchanged full-state oracle.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x4E4F534C52574431UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _tapeSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], address.InitialSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], address.RawCursor);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}
