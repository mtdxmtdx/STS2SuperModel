using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Sequential composition of public combat-start proposals. The caller owns the
/// independent proposal RNG and state-addressed force scope, including full-word
/// consumption, preexisting-cell consistency and unresolved alias protection.
/// Every completed combat's exact ratio/envelope is retained for correction.
/// </summary>
internal sealed class NativePublicCombatPrefixProposal(
    NativePublicCombatPrefixCondition condition, Func<ulong> nextWord,
    Func<IReadOnlyList<ulong>, string, IDisposable> forceStateWords)
{
    private RunState? _run;
    private int _combatIndex = -1;
    private NativePublicCombatPrefixInput? _active;
    private Rng? _initialShuffleRng;
    private bool _awaitingInitialShuffle, _conditioningInitialHp;
    private readonly HashSet<int> _entered = [], _shuffled = [];
    private readonly HashSet<string> _activeHpIds = new(StringComparer.Ordinal);
    private int _activeHpCount;
    private readonly List<ConditionalShufflePlan> _shuffles = [];
    private readonly List<NativeHpProposal> _hp = [];

    internal int ConditionedShuffleCount => _shuffles.Count;
    internal int ConditionedHpCount => _hp.Count;
    internal bool ConditionApplied => _shuffles.Count > 0;
    internal ShuffleRational NativeToProposalRatio => Product(_shuffles.Select(plan => plan.NativeToProposalRatio)
        .Concat(_hp.Select(plan => plan.NativeToProposalRatio)));
    internal ShuffleRational Envelope => Product(_shuffles.Select(plan => plan.Envelope)
        .Concat(_hp.Select(plan => plan.Envelope)));

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("A combat-prefix proposal already owns a native run");
        _run = run;
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        if (_run is null || !ReferenceEquals(initialShuffleRng, _run.Rng.Shuffle))
            throw new InvalidOperationException("Public combat conditioning escaped its owned native shuffle stream");
        if (combatIndex != _combatIndex + 1)
            throw new InvalidOperationException("Native combat owner indices must include every combat entry");
        ValidateActiveCompletion();
        _combatIndex = combatIndex; _active = null;
        _awaitingInitialShuffle = _conditioningInitialHp = false;
        _initialShuffleRng = null; _activeHpIds.Clear(); _activeHpCount = 0;
        if (!condition.Combats.TryGetValue(combatIndex, out var input)
            || (input.Shuffle is null && input.HpCount == 0)) return;
        if (input.EntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published combat-prefix entry differs at combat " + combatIndex);
        _active = input; _entered.Add(combatIndex);
        _initialShuffleRng = initialShuffleRng;
        _awaitingInitialShuffle = true; _conditioningInitialHp = input.HpCount > 0;
    }

    internal IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        if (!_awaitingInitialShuffle || !ReferenceEquals(rng, _initialShuffleRng)
            || items.Count == 0 || items.Any(item => item is not CardModel)) return null;
        var cards = items.Cast<CardModel>().ToArray();
        var owner = cards[0].Owner;
        if (!ReferenceEquals(owner, _run?.Players.Single()) || !ReferenceEquals(owner?.RunState, _run)
            || cards.Any(card => !ReferenceEquals(card.Owner, owner) || card.DeckVersion is null || card.Pile?.Type != PileType.Draw)
            || owner?.PlayerCombatState is null || !owner.PlayerCombatState.DrawPile.Cards.SequenceEqual(cards))
            throw new InvalidOperationException("Public combat prefix did not identify its owned native initial draw pile");
        if (_activeHpCount != _active!.HpCount)
            throw new NativePublicConstraintMismatchException("Native initial roster has fewer monsters than public combat " + _combatIndex);
        _awaitingInitialShuffle = _conditioningInitialHp = false;
        _shuffled.Add(_combatIndex);
        if (_active.Shuffle is not { } shuffle) return null;
        var keys = cards.Select(NativePublicDrawKey.From).ToArray();
        if (!shuffle.MatchesPublicInitialPool(keys))
            throw new InvalidOperationException("Certified public combat entry differs from its native initial shuffle pool");
        var plan = ConditionalShuffleProposal.Create(keys.Select(key => key.Signature).ToArray(),
            shuffle.DrawPrefixKeys.Select(key => key.Signature).ToArray(), nextWord)
            ?? throw new NativePublicConstraintMismatchException("Public combat draw prefix has no permutation support");
        _shuffles.Add(plan);
        return forceStateWords(plan.RawWords, "public combat " + _combatIndex + " shuffle");
    }

    internal IDisposable? BeginMonsterHp(LabelMonsterHpContext context)
    {
        if (!_conditioningInitialHp) return null;
        // The declared state-tape priors use the native sequential Niche stream.
        // A keyed or foreign HP generator needs its own certificate, never an
        // implicit reinterpretation of the same proposed word sequence.
        if (!ReferenceEquals(context.Rng, _run?.Rng.Niche)
            || !ReferenceEquals(context.Creature.CombatState?.RunState, _run)
            || !ReferenceEquals(context.Creature.CombatState, _run?.Players.Single().Creature.CombatState))
            throw new InvalidOperationException("Public combat HP conditioning escaped its owned native run");
        string id = context.Creature.Monster?.GetType().Name
            ?? throw new InvalidOperationException("Public combat HP conditioning requires a monster");
        var plan = _active!.SlugHp is { } slugs
            ? slugs.CreateProposal(context, _activeHpCount, nextWord)
            : _active.ToadpoleHp is { } toadpoles
                ? toadpoles.CreateProposal(context, _activeHpCount, nextWord)
            : _active.Hp!.CreateProposal(context, _activeHpIds, nextWord);
        _activeHpIds.Add(id); _activeHpCount++; _hp.Add(plan);
        return forceStateWords([plan.RawWord], "public combat " + _combatIndex + " HP");
    }

    internal void ValidateCompletion()
    {
        ValidateActiveCompletion();
        if (_entered.Count != condition.EligibleShuffleCount || _shuffled.Count != condition.EligibleShuffleCount
            || _shuffles.Count != condition.EligibleShuffleCount || _hp.Count != condition.EligibleHpCount)
            throw new InvalidOperationException("Public combat prefix was reached without every certified native startup");
    }

    private void ValidateActiveCompletion()
    {
        if (_active is not null && (_awaitingInitialShuffle || _conditioningInitialHp
            || _activeHpCount != _active.HpCount))
            throw new InvalidOperationException("A certified public combat startup was not completed");
    }

    internal bool AcceptCorrection(Func<ulong> correctionWord)
    {
        ArgumentNullException.ThrowIfNull(correctionWord);
        ValidateCompletion(); // Missing hooks cannot be hidden by random rejection.
        foreach (var plan in _hp) if (!plan.AcceptCorrection(correctionWord)) return false;
        foreach (var plan in _shuffles) if (!plan.AcceptCorrection(correctionWord)) return false;
        return true;
    }

    private static ShuffleRational Product(IEnumerable<ShuffleRational> factors)
    {
        var product = new ShuffleRational(1, 1);
        foreach (var factor in factors) product = product.Multiply(factor.Numerator, factor.Denominator);
        return product;
    }
}
