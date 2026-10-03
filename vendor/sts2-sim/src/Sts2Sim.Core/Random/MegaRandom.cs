using System.Numerics;
using Sts2Sim.Core.Saves;

namespace Sts2Sim.Core.Random;

/// <summary>
/// Xoshiro256** (xor, shift, rotate) pseudo-random number generator (PRNG).
/// Vendored verbatim from v0.109 game source (MegaCrit.Sts2.Core.Random.MegaRandom),
/// with deviations registered in Plan 02.5's deviation list:
///  - #26: NextBytes(Span&lt;byte&gt;) omitted (unsafe pointer path; no caller in the simulator).
///  - #21: Clone() added (not present in the game; needed for O(1) parallel env snapshots).
/// </summary>
public sealed class MegaRandom
{
    private const double _incrDouble = 1.1102230246251565E-16;

    private const float _incrFloat = 5.9604645E-08f;

    private ulong _s0;

    private ulong _s1;

    private ulong _s2;

    private ulong _s3;

    // Label-only metadata. Untagged generators keep their existing full-state law.
    private ulong? _labelInitialSeed;
    private ulong _labelRawCursor;
    private bool _labelRewardsOrigin;
    private bool _labelHybrid;
    private bool _labelMapLaw;
    private int? _labelMapActIndex;

    public LabelRandomAddressV1? LabelRewardAddress => _labelRewardsOrigin
        ? new LabelRandomAddressV1(LabelRandomProvenance.RewardsOrigin, _labelInitialSeed!.Value, _labelRawCursor)
        : null;

    public LabelMapRandomAddressV1? LabelMapAddress => _labelMapActIndex is { } actIndex
        ? new LabelMapRandomAddressV1(actIndex, _labelInitialSeed!.Value, _labelRawCursor)
        : null;

    private string? LabelLaw => !_labelHybrid ? null
        : _labelMapLaw ? LabelRandomProvenance.MapLawId : LabelRandomProvenance.LawId;

    private void RequireCompatibleLabelLaw(string? sourceLaw)
    {
        // Neither a nested scope nor a loaded source can erase the object's law.
        // Check both constraints independently, before any state/lineage mutation.
        if ((LabelLaw is { } existingLaw && sourceLaw != existingLaw)
            || (LabelRandomScope.ProvenanceLaw is { } activeLaw && sourceLaw != activeLaw))
            throw new InvalidOperationException("A hybrid label RNG restore or reseed cannot change its declared primitive law.");
    }

    public MegaRandom(ulong seed)
    {
        Reinitialise(seed);
    }

    public MegaRandom(SerializableRng serializable)
    {
        Reinitialise(serializable);
    }

    private MegaRandom(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    /// <summary>
    /// Splitmix64 PRNG.
    /// </summary>
    public static ulong Splitmix64(ref ulong x)
    {
        ulong num = (x += 11400714819323198485uL);
        num = (num ^ (num >> 30)) * 13787848793156543929uL;
        num = (num ^ (num >> 27)) * 10723151780598845931uL;
        return num ^ (num >> 31);
    }

    public void Reinitialise(ulong seed)
    {
        RequireCompatibleLabelLaw(LabelLaw ?? LabelRandomScope.ProvenanceLaw);
        _labelHybrid = _labelHybrid || LabelRandomScope.UsesRewardProvenance;
        _labelMapLaw = _labelMapLaw || LabelRandomScope.UsesMapProvenance;
        _labelInitialSeed = _labelRewardsOrigin || _labelMapActIndex is not null || LabelRandomScope.UsesRewardProvenance ? seed : null;
        _labelRawCursor = 0;
        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    public void Reinitialise(SerializableRng serializable)
    {
        LabelRandomProvenance? provenance = serializable.LabelProvenance;
        provenance?.Validate();
        if ((_labelHybrid || LabelRandomScope.UsesRewardProvenance) && provenance is null)
            throw new InvalidOperationException("A hybrid label RNG restore requires an explicit source partition.");
        RequireCompatibleLabelLaw(provenance?.Law);
        _labelHybrid = provenance is not null;
        _labelMapLaw = provenance?.Law == LabelRandomProvenance.MapLawId;
        _labelRewardsOrigin = provenance?.Partition == LabelRandomProvenance.RewardsPartition;
        _labelMapActIndex = provenance?.ActIndex;
        _labelInitialSeed = provenance?.InitialSeed;
        _labelRawCursor = provenance?.RawCursor ?? 0;
        _s0 = serializable.state0;
        _s1 = serializable.state1;
        _s2 = serializable.state2;
        _s3 = serializable.state3;
    }

    private ulong NextULongInner()
    {
        LabelRandomAddressV1? labelAddress = LabelRewardAddress;
        LabelMapRandomAddressV1? mapAddress = LabelMapAddress;
        if (_labelInitialSeed is not null)
            _labelRawCursor = checked(_labelRawCursor + 1);
        ulong s = _s0;
        ulong s2 = _s1;
        ulong s3 = _s2;
        ulong s4 = _s3;
        var labelState = new LabelRandomState(s, s2, s3, s4);
        ulong result = BitOperations.RotateLeft(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s;
        s4 ^= s2;
        s2 ^= s3;
        s ^= s4;
        s3 ^= num;
        s4 = BitOperations.RotateLeft(s4, 45);
        _s0 = s;
        _s1 = s2;
        _s2 = s3;
        _s3 = s4;
        // Explicit label scopes replace only the word, after native state advancement.
        return LabelRandomScope.NextWordOrOriginal(labelState, labelAddress, mapAddress, result);
    }

    public int Next(int maxValue)
    {
        if (maxValue < 1)
        {
            throw new ArgumentOutOfRangeException("maxValue", maxValue, "maxValue must be > 0");
        }
        return NextInner(maxValue);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException("maxValue", maxValue, "maxValue must be > minValue");
        }
        long num = (long)maxValue - (long)minValue;
        if (num <= int.MaxValue)
        {
            return NextInner((int)num) + minValue;
        }
        return (int)(NextInner(num) + minValue);
    }

    public double NextDouble()
    {
        return (double)(NextULongInner() >> 11) * 1.1102230246251565E-16;
    }

    public int NextInt()
    {
        return (int)(NextULongInner() >> 33);
    }

    public uint NextUInt()
    {
        return (uint)NextULongInner();
    }

    public ulong NextULong()
    {
        return NextULongInner();
    }

    public bool NextBool()
    {
        return (NextULongInner() & 0x8000000000000000uL) != 0;
    }

    public float NextFloat()
    {
        return (float)(NextULongInner() >> 40) * 5.9604645E-08f;
    }

    private int NextInner(int maxValue)
    {
        return (int)(NextDouble() * (double)maxValue);
    }

    private long NextInner(long maxValue)
    {
        return (long)(NextDouble() * (double)maxValue);
    }

    public void FillSerializableState(SerializableRng rng)
    {
        rng.state0 = _s0;
        rng.state1 = _s1;
        rng.state2 = _s2;
        rng.state3 = _s3;
        string law = _labelMapLaw ? LabelRandomProvenance.MapLawId : LabelRandomProvenance.LawId;
        rng.LabelProvenance = _labelMapActIndex is { } actIndex
            ? new LabelRandomProvenance(law, LabelRandomProvenance.MapPartition,
                LabelRandomProvenance.MapOrigin, _labelInitialSeed!.Value, _labelRawCursor, actIndex)
            : _labelRewardsOrigin
            ? new LabelRandomProvenance(law, LabelRandomProvenance.RewardsPartition,
                LabelRandomProvenance.RewardsOrigin,
                _labelInitialSeed!.Value, _labelRawCursor)
            : _labelHybrid ? new LabelRandomProvenance(law,
                LabelRandomProvenance.FullStatePartition) : null;
    }

    /// <summary>
    /// Explicit label-only binding for the native player Rewards construction role.
    /// Has no effect without the hybrid scope, and cannot infer lineage from loaded state.
    /// </summary>
    public MegaRandom WithLabelRewardsProvenance()
    {
        if (!LabelRandomScope.UsesRewardProvenance) return this;
        if (_labelRewardsOrigin) return this;
        if (_labelInitialSeed is null || _labelRawCursor != 0 || _labelMapActIndex is not null)
            throw new InvalidOperationException("Rewards provenance must be bound at native seed construction.");
        _labelRewardsOrigin = true;
        _labelHybrid = true;
        return this;
    }

    internal void WithLabelMapProvenance(int actIndex)
    {
        if (!LabelRandomScope.UsesMapProvenance) return;
        if (actIndex < 0) throw new ArgumentOutOfRangeException(nameof(actIndex));
        if (_labelInitialSeed is null || _labelRawCursor != 0 || _labelRewardsOrigin || _labelMapActIndex is not null)
            throw new InvalidOperationException("Map provenance must be bound at native seed construction.");
        _labelMapActIndex = actIndex;
        _labelHybrid = true;
        _labelMapLaw = true;
    }

    internal void PreserveLabelOriginForReseed(MegaRandom source, ulong effectiveSeed)
    {
        RequireCompatibleLabelLaw(source.LabelLaw ?? LabelLaw);
        _labelHybrid = source._labelHybrid || _labelHybrid;
        _labelMapLaw = source._labelMapLaw || _labelMapLaw;
        if (!source._labelRewardsOrigin && source._labelMapActIndex is null) return;
        _labelRewardsOrigin = source._labelRewardsOrigin;
        _labelMapActIndex = source._labelMapActIndex;
        _labelHybrid = true;
        _labelInitialSeed = effectiveSeed;
        _labelRawCursor = 0;
    }

    /// <summary>
    /// Deviation #21: exact, independent snapshot of the generator's internal state.
    /// Not present in the game; used for O(1) parallel environment resets.
    /// </summary>
    public MegaRandom Clone()
    {
        return new MegaRandom(_s0, _s1, _s2, _s3)
        {
            _labelInitialSeed = _labelInitialSeed,
            _labelRawCursor = _labelRawCursor,
            _labelRewardsOrigin = _labelRewardsOrigin,
            _labelHybrid = _labelHybrid,
            _labelMapLaw = _labelMapLaw,
            _labelMapActIndex = _labelMapActIndex
        };
    }
}
