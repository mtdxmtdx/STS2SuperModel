using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// One owned replay of the fixed public unknown-room prefix. Each replacement is
/// uniform on its complete 64-bit preimage. Root product mass equals its envelope;
/// every original roll, full odds update, and unforced future cell remains native.
/// </summary>
internal sealed class NativePublicUnknownRoomProposal
{
    private readonly NativePublicUnknownRoomCondition _condition;
    private readonly Func<ulong> _nextWord;
    private readonly Func<IReadOnlyList<ulong>, Rng, string, IDisposable> _forcePrefixWords;
    private readonly Action<Exception>? _captureFailure;
    private readonly NativePublicPrefixConstraint _prefix;
    private RunState? _run;
    private int _completed;
    private bool _active;
    private Exception? _failure;
    internal int ConditionedRoomCount => _completed;
    internal ShuffleRational Envelope => _condition.Envelope;
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);

    internal NativePublicUnknownRoomProposal(NativePublicUnknownRoomCondition condition, Func<ulong> nextWord,
        Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords, Action<Exception>? captureFailure = null)
    {
        _condition = condition; _nextWord = nextWord; _forcePrefixWords = forcePrefixWords;
        _captureFailure = captureFailure; _prefix = new(condition.BoundaryEvidence);
    }

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Unknown-room proposal already owns a native run");
        _run = run;
    }

    internal void ObservePublicEvidence(PublicRunEvidenceEvent evidence)
    {
        try { RequireHealthy(); _prefix.Observe(evidence); }
        catch (Exception error) { Capture(error); throw; }
    }

    internal IDisposable? BeginResolution(LabelUnknownRoomContext context)
    {
        try
        {
            RequireHealthy();
            if (_run is null || !ReferenceEquals(context.Run, _run))
                throw new InvalidOperationException("Unknown-room boundary escaped its owning hypothetical run");
            if (_active) throw new InvalidOperationException("Unknown-room boundary was nested or repeated");
            if (_completed == _condition.Targets.Count)
            {
                if (context.CompletedRoomType is not null || _run.CurrentActIndex == 0
                    && _run.TotalFloor <= _condition.Targets[^1].Floor)
                    throw new InvalidOperationException("A completed public unknown-room boundary was repeated");
                return null;
            }
            var target = _condition.Targets[_completed];
            var run = context.Run;
            if (context.CompletedRoomType is not null || run.Rng.UsesSemanticKeys
                || !ReferenceEquals(context.Rng, run.Rng.UnknownMapPoint) || context.Rng.Counter != target.Index
                || run.CurrentRoomCount != 0 || run.Players.Count != 1 || run.Players[0].Character is not Silent
                || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
                || context.Point.PointType != MapPointType.Unknown
                || !ReferenceEquals(context.Point, run.CurrentMapPoint)
                || !ReferenceEquals(context.Point, run.Map.GetPoint(context.Point.coord)))
                throw new InvalidOperationException("Unknown-room boundary differs from its fresh native map-roll certificate");
            if (run.CurrentActIndex != 0 || run.TotalFloor != target.Floor
                || run.VisitedMapCoords.Count != target.Floor
                || context.Point.coord.col != target.Coordinate.Col || context.Point.coord.row != target.Coordinate.Row
                || context.ExcludeShop != target.ExcludeShop)
                throw new NativePublicConstraintMismatchException("Native unknown-room location or blacklist differs from public history");
            if (_prefix.CheckedEvents != target.MapEndEventOrdinal + 1)
                throw new InvalidOperationException("Unknown-room roll is not immediately after its certified public map owner");
            // These are guard comparisons on the hypothetical object only. They
            // never determine the proposal, its mass, or the permitted raw words.
            if (NativeUnknownRoomOdds.Read(run.Odds.UnknownMapPoint) != target.Before
                || !NativePublicUnknownRoomCondition.UnmodifiedOdds(NativePublicRunEvidence.Assets(run.Players[0])))
                throw new InvalidOperationException("Native unknown-room odds or listener closure departed from its public certificate");
            var plan = NativeUnknownRoomMath.Create(target, _nextWord);
            int before = context.Rng.Counter;
            _active = true;
            var inner = _forcePrefixWords(plan.RawWords, context.Rng, "public unknown-room resolution");
            return new Completion(() =>
            {
                try
                {
                    inner.Dispose();
                    if (context.CompletedRoomType is null)
                    {
                        // Do not replace the original native exception on unwind.
                        // A native catch still cannot erase this unresolved boundary.
                        Capture(new InvalidOperationException("Native unknown-room roll did not finish its odds update"));
                        return;
                    }
                    if (context.Rng.Counter != before + 1 || context.CompletedRoomType != target.RoomType
                        || NativeUnknownRoomOdds.Read(run.Odds.UnknownMapPoint) != target.After)
                        throw new InvalidOperationException("Native unknown-room roll did not complete its certified result and update");
                    NativeToProposalRatio = NativeToProposalRatio.Multiply(
                        plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
                    _completed++; _active = false;
                }
                catch (Exception error) { Capture(error); throw; }
            });
        }
        catch (Exception error) { Capture(error); throw; }
    }

    internal void ValidateCompletion()
    {
        try
        {
            RequireHealthy();
            if (_active || _completed != _condition.Targets.Count || NativeToProposalRatio != Envelope)
                throw new InvalidOperationException("Public unknown-room proposal did not complete");
        }
        catch (Exception error) { Capture(error); throw; }
    }
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); ValidateCompletion(); return true; }

    private void RequireHealthy()
    {
        if (_failure is { } error)
            throw new InvalidOperationException("Public unknown-room proposal retained a failed boundary", error);
    }
    private void Capture(Exception error)
    {
        if (error is NativePublicConstraintMismatchException or OperationCanceledException) return;
        _failure ??= error; _captureFailure?.Invoke(error);
    }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
