using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Eliminates the dead independent Map component after conditioning on its
/// complete public semantic value. Q(M) is root-constant, so no computable
/// per-world mass or rejection envelope is invented here.
/// </summary>
internal sealed class NativePublicMapReconstructionProposal(NativePublicMapReconstructionCondition condition)
{
    private RunState? _constructedRun;
    private StandardActMap? _constructedMap;
    private bool _attached;
    private bool _failed;
    internal int ReconstructedMapCount => _constructedMap is null ? 0 : 1;

    internal StandardActMap? Construct(LabelMapGenerationContext context)
    {
        if (_failed) throw new InvalidOperationException("Complete public map reconstruction previously failed");
        var run = context.Run;
        if (_constructedRun is not null)
        {
            if (!_attached || !ReferenceEquals(run, _constructedRun))
                throw new InvalidOperationException("Later map escaped its reconstructed hypothetical run");
            if (run.CurrentActIndex == 0)
            {
                _failed = true;
                throw new InvalidOperationException("The marginalized act-zero Map component cannot be regenerated");
            }
            // Other acts use distinct Map components and retain native construction.
            return null;
        }
        if (run.CurrentActIndex != 0 || run.TotalFloor != 0 || run.CurrentMapCoord is not null
            || run.Map is not null || run.Players.Count != 0 || run.CurrentRoomCount != 0
            || run.Acts.Count != 3 || !ReferenceEquals(run.Act, context.Act)
            || context.Act is not (Overgrowth or Underdocks) || context.Act.BaseNumberOfRooms != 15
            || run.Acts[1] is not Hive || run.Acts[2] is not Glory
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss) || context.Rng.Counter != 0)
            throw new InvalidOperationException("Complete map escaped its native construction boundary");
        ValidateMapRng(context);
        _constructedMap = condition.Reconstruct(context.Rng);
        _constructedRun = run;
        return _constructedMap;
    }

    private static void ValidateMapRng(LabelMapGenerationContext context)
    {
        var actual = context.Rng.ToSerializable();
        var expected = StandardActMap.CreateRng(context.Run.Rng.Seed, 0).ToSerializable();
        if (actual.LabelProvenance is not { Law: LabelRandomProvenance.MapLawId,
                Partition: LabelRandomProvenance.MapPartition, OriginFamily: LabelRandomProvenance.MapOrigin, ActIndex: 0, RawCursor: 0 }
            || actual != expected)
            throw new InvalidOperationException("Complete map requires the fresh owned independent Map RNG");
    }

    internal void AttachHypotheticalRun(RunState run)
    {
        if (_failed || !ReferenceEquals(run, _constructedRun) || !ReferenceEquals(run.Map, _constructedMap))
            throw new InvalidOperationException("Reconstructed map escaped its owned hypothetical run");
        _attached = true;
    }

    internal void ValidateCompletion()
    {
        if (_failed || !_attached || _constructedMap is null)
            throw new InvalidOperationException("Complete public map reconstruction did not finish");
    }
}
