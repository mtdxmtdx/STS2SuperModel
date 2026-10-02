using Nosl.Contracts;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>One certified native opening factory, using the owning tape's exact-state force scope.</summary>
internal sealed class NativePublicWeakSlimeFormationProposal(NativePublicWeakSlimeFormationCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private int _combatIndex = -1;
    private NativeWeakSlimeFormationPlan? _plan;
    private bool _active, _complete;
    internal int ConditionedCombatCount => _plan is null ? 0 : 1;
    internal ShuffleRational NativeToProposalRatio => _plan?.NativeToProposalRatio ?? new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Weak-slime formation already owns a native run");
        _run = run;
    }
    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        if (_run is null || !ReferenceEquals(initialShuffleRng, _run.Rng.Shuffle))
            throw new InvalidOperationException("Weak-slime formation owner escaped its hypothetical run");
        if (combatIndex != _combatIndex + 1)
            throw new InvalidOperationException("Weak-slime formation owner indices must include every combat entry");
        if (_active && !_complete) throw new InvalidOperationException("A certified weak-slime formation did not complete");
        _combatIndex = combatIndex; _active = combatIndex == 0;
        if (_active && condition.EntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published weak-slime opening entry differs");
    }
    internal IDisposable? BeginFormation(LabelSlimesWeakFormationContext context)
    {
        if (!_active) return null;
        if (_run is null || !ReferenceEquals(context.Run, _run)
            || context.Room is not { RoomType: RoomType.Monster, Engine: null }
            || !ReferenceEquals(context.Room, _run.CurrentRoom) || _plan is not null)
            throw new InvalidOperationException("Weak-slime formation is unowned, out of phase, or repeated");
        var plan = condition.CreateProposal(context, nextWord);
        var inner = forcePrefixWords(plan.RawWords, context.Rng, "public opening weak-slime formation");
        _plan = plan;
        return new Completion(() =>
        {
            inner.Dispose();
            if (context.Rng.Counter > 3)
                throw new InvalidOperationException("Native weak-slime formation did not consume exactly three draws");
            // Retain an original tape error while unwinding; missing callbacks
            // remain unresolved on the required completion check.
            _complete = context.Rng.Counter == 3;
        });
    }
    internal void ValidateCompletion()
    {
        if (_plan is null || !_complete) throw new InvalidOperationException("Certified weak-slime formation did not complete");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    { ValidateCompletion(); return _plan!.AcceptCorrection(correctionWord); }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
