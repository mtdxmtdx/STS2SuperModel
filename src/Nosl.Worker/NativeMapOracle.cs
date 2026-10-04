using System.Buffers.Binary;
using System.Security.Cryptography;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Independent Map partition of the separately versioned three-partition label law.
/// The ideal law assigns independent words to each (act, effective seed, raw cursor).
/// SHA expansion is a reproducible implementation, not a finite-seed posterior proof.
/// </summary>
internal sealed class NativeMapOracle
{
    private readonly ulong _tapeSeed;
    private readonly Dictionary<LabelMapRandomAddressV1, ulong> _overrides;
    private readonly HashSet<LabelMapRandomAddressV1> _visited = [];

    internal NativeMapOracle(ulong tapeSeed, IReadOnlyDictionary<LabelMapRandomAddressV1, ulong>? overrides = null)
    { _tapeSeed = tapeSeed; _overrides = overrides is null ? [] : new(overrides); }

    internal int DistinctCells => _visited.Count;
    internal int ConditionedCells => _overrides.Count;
    internal NativeMapOracle ReplayCopy() => new(_tapeSeed, _overrides);
    internal bool WasVisited(LabelMapRandomAddressV1 address) => _visited.Contains(address);

    internal void ForceFresh(LabelMapRandomAddressV1 address, ulong word)
    {
        Validate(address);
        if (_visited.Contains(address))
            throw new InvalidOperationException("A Map proposal cannot overwrite an already read lineage cell");
        if (_overrides.TryGetValue(address, out ulong previous) && previous != word)
            throw new InvalidOperationException("A Map proposal changed its exact replay override");
        _overrides[address] = word;
    }

    private static void Validate(LabelMapRandomAddressV1 address)
    {
        if (address.ActIndex < 0)
            throw new InvalidOperationException("Unknown hypothetical Map act lineage");
    }

    internal ulong Word(LabelMapRandomAddressV1 address)
    {
        Validate(address);
        _visited.Add(address);
        if (_overrides.TryGetValue(address, out ulong forced)) return forced;
        Span<byte> bytes = stackalloc byte[40];
        // Distinct namespace; no bytes, aliases, or salts in the old partitions change.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x4E4F534C4D415031UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _tapeSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], (ulong)address.ActIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], address.InitialSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], address.RawCursor);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}
