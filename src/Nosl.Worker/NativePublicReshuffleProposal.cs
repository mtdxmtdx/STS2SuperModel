using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Exact physical-permutation proposals for publicly witnessed later pools. Each
/// pool's ID multiplicities and prefix are root-fixed, hence so is its envelope.
/// Runtime variants remain distinct physical indices, including native sort ties.
/// </summary>
internal sealed class NativePublicReshuffleProposal(NativePublicReshuffleCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private int _combatIndex = -1, _ordinal;
    private NativePublicReshuffleCombat? _active;
    private readonly HashSet<(int Combat, int Ordinal)> _completed = [];
    private readonly List<ConditionalShufflePlan> _plans = [];
    private bool _forcing;
    internal int ConditionedShuffleCount => _plans.Count;
    internal ShuffleRational NativeToProposalRatio => Product(_plans.Select(plan => plan.NativeToProposalRatio));
    internal ShuffleRational Envelope => Product(_plans.Select(plan => plan.Envelope));
    internal IDisposable EnterScope() => LabelCombatReshuffleScope.Enter();

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Reshuffle proposal already owns a run");
        _run = run;
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng shuffleRng)
    {
        if (_run is null || !ReferenceEquals(shuffleRng, _run.Rng.Shuffle)
            || combatIndex != _combatIndex + 1)
            throw new InvalidOperationException("Reshuffle proposal escaped its owned combat entry sequence");
        ValidateActiveCompletion();
        _combatIndex = combatIndex; _ordinal = 0; _active = null;
        if (!condition.Combats.TryGetValue(combatIndex, out var combat)) return;
        if (combat.EntryJson != Nosl.Contracts.PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Public reshuffle entry differs at combat " + combatIndex);
        _active = combat;
    }

    internal IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        if (LabelCombatReshuffleScope.Current is not { } context) return null;
        if (_run is null || _combatIndex < 0 || _forcing || !ReferenceEquals(context.Rng, rng)
            || !ReferenceEquals(rng, _run.Rng.Shuffle) || !ReferenceEquals(context.Combat.RunState, _run)
            || !ReferenceEquals(context.Player, _run.Players.Single())
            || !ReferenceEquals(context.Player.Creature.CombatState, context.Combat)
            || context.Combat is not CombatState { IsProjection: false }
            || context.Player.PlayerCombatState is not { } state
            || items.Count != context.Cards.Count || !items.SequenceEqual(context.Cards.Cast<object?>()))
            throw new InvalidOperationException("Reshuffle callback lacks its exact owned native call-site boundary");
        int ordinal = _ordinal++;
        var target = _active?.Targets.SingleOrDefault(item => item.ReshuffleOrdinal == ordinal);
        if (target is null) return null;
        var cards = items.OfType<CardModel>().ToArray();
        var actualPool = state.DiscardPile.Cards.Concat(state.DrawPile.Cards).ToArray();
        // Closed slime generation creates a new physical Slimed without a deck origin.
        // It is still a distinct owned index in the exact native pile union. The public
        // witness fixes the ID counts below; no sampled-pool envelope is substituted.
        if (cards.Length != items.Count || cards.Distinct(ReferenceEqualityComparer.Instance).Count() != cards.Length
            || cards.Length != actualPool.Length || cards.Any(card => !ReferenceEquals(card.Owner, context.Player)
                || (card.DeckVersion is null && !NativePublicDrawPrefixCondition.IsPlainGeneratedSlimed(PublicViews.Card(card)))
                || card.Pile?.Type is not (PileType.Draw or PileType.Discard))
            || actualPool.Any(card => !cards.Contains(card, ReferenceEqualityComparer.Instance)))
            throw new InvalidOperationException("Reshuffle boundary does not contain its owned physical pile union");
        string[] ids = cards.Select(card => card.GetType().Name).ToArray();
        if (!ids.Order(StringComparer.Ordinal).SequenceEqual(target.PoolIds.Order(StringComparer.Ordinal)))
            throw new NativePublicConstraintMismatchException("Witnessed public reshuffle pool differs at combat " + _combatIndex);
        var plan = ConditionalShuffleProposal.Create(ids, target.DrawPrefixIds, nextWord)
            ?? throw new NativePublicConstraintMismatchException("Witnessed public reshuffle prefix has no native support");
        int counter = rng.Counter;
        var inner = forcePrefixWords(plan.RawWords, rng, "public combat " + _combatIndex + " reshuffle " + ordinal);
        _forcing = true;
        return new Completion(() =>
        {
            inner.Dispose();
            // The forcing scope preserves an exception from its word callback while
            // unwinding. Leave an incomplete target unresolved instead of masking it.
            long consumed = (long)rng.Counter - counter;
            if (consumed < plan.RawWords.Count) return;
            if (consumed > plan.RawWords.Count)
                throw new InvalidOperationException("Native reshuffle consumed an unexpected number of primitive words");
            // Counter advances before the native raw callback. In particular a
            // final-word error must not count as successful completion merely
            // because every counter increment occurred. The exact call-site
            // acknowledges success only after StableShuffle returns normally.
            context.AfterSuccessfulShuffle(() =>
            {
                if (!_completed.Add((_combatIndex, ordinal)))
                    throw new InvalidOperationException("A public reshuffle target completed twice");
                _plans.Add(plan); _forcing = false;
            });
        });
    }

    private void ValidateActiveCompletion()
    {
        if (_forcing || (_active is not null && _active.Targets.Any(target => !_completed.Contains((_combatIndex, target.ReshuffleOrdinal)))))
            throw new InvalidOperationException("A witnessed public reshuffle did not complete its native boundary");
    }
    internal void ValidateCompletion()
    {
        ValidateActiveCompletion();
        if (_plans.Count != condition.EligibleShuffleCount)
            throw new InvalidOperationException("Public root was reached without every witnessed reshuffle");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    {
        ArgumentNullException.ThrowIfNull(correctionWord);
        ValidateCompletion();
        foreach (var plan in _plans) if (!plan.AcceptCorrection(correctionWord)) return false;
        return true;
    }
    private static ShuffleRational Product(IEnumerable<ShuffleRational> factors)
    {
        var result = new ShuffleRational(1, 1);
        foreach (var factor in factors) result = result.Multiply(factor.Numerator, factor.Denominator);
        return result;
    }
    private sealed class Completion(Action complete) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; complete(); }
    }
}
