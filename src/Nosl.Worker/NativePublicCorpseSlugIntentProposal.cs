using Nosl.Contracts;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Independent optional composition with the existing combat HP/shuffle proposal.
/// The tape supplies its state-addressed force scope with exact stream, previous
/// cell, replay-value and full-consumption checks. No observed root is excluded.
/// </summary>
internal sealed class NativePublicCorpseSlugIntentProposal(NativePublicCorpseSlugIntentCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private int _combatIndex = -1;
    private NativeCorpseSlugIntentInput? _active;
    private bool _complete;
    private readonly Dictionary<int, NativeCorpseSlugIntentPlan> _plans = [];
    internal int ConditionedCombatCount => _plans.Count;
    internal ShuffleRational NativeToProposalRatio => Product(_plans.Values.Select(plan => plan.NativeToProposalRatio));
    internal ShuffleRational Envelope => Product(_plans.Values.Select(plan => plan.Envelope));

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Slug intent proposal already owns a native run");
        _run = run;
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        if (_run is null || !ReferenceEquals(initialShuffleRng, _run.Rng.Shuffle))
            throw new InvalidOperationException("Slug intent owner escaped its hypothetical run");
        if (combatIndex != _combatIndex + 1)
            throw new InvalidOperationException("Slug intent owner indices must include every combat entry");
        ValidateActiveCompletion();
        _combatIndex = combatIndex; _active = null; _complete = false;
        if (!condition.Combats.TryGetValue(combatIndex, out var input)) return;
        if (input.EntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published slug startup entry differs at combat " + combatIndex);
        _active = input;
    }

    internal IDisposable? BeginInitialIntents(LabelCorpseSlugInitialIntentsContext context)
    {
        if (_active is null) return null;
        // ConfigureCombatObserver -> CombatEntering precedes PushRoom -> Prepare
        // for the reviewed map factories. EventRoom's early Prepare is excluded
        // publicly and cannot masquerade as the next normal owner here.
        if (_run is null || !ReferenceEquals(context.Run, _run)
            || context.Room is not { RoomType: RoomType.Monster, Engine: null }
            || !ReferenceEquals(context.Room, _run.CurrentRoom)
            || !ReferenceEquals(context.FactoryRng, context.Rng) || _plans.ContainsKey(_combatIndex))
            throw new InvalidOperationException("Slug initializer is unowned, out of phase, or repeated");
        var plan = _active.CreateProposal(context, nextWord);
        var inner = forcePrefixWords([plan.RawWord], context.Rng, "public combat " + _combatIndex + " slug intents");
        _plans.Add(_combatIndex, plan);
        return new Completion(() =>
        {
            inner.Dispose();
            if (context.Rng.Counter > 1)
                throw new InvalidOperationException("Native slug initializer did not consume exactly one draw");
            // A foreign-stream callback may already be unwinding; do not mask
            // that original error with a second missing-draw exception.
            _complete = context.Rng.Counter == 1;
        });
    }

    internal void ValidateCompletion()
    {
        ValidateActiveCompletion();
        if (_plans.Count != condition.EligibleCombatCount)
            throw new InvalidOperationException("Slug intents were reached without every certified native initializer");
    }
    private void ValidateActiveCompletion()
    {
        if (_active is not null && !_complete)
            throw new InvalidOperationException("A certified slug initial-intent draw did not complete");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    {
        ArgumentNullException.ThrowIfNull(correctionWord);
        ValidateCompletion();
        foreach (var plan in _plans.Values) if (!plan.AcceptCorrection(correctionWord)) return false;
        return true;
    }
    private static ShuffleRational Product(IEnumerable<ShuffleRational> factors)
    {
        var result = new ShuffleRational(1, 1);
        foreach (var factor in factors) result = result.Multiply(factor.Numerator, factor.Denominator);
        return result;
    }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
