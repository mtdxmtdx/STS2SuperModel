using System.Buffers.Binary;
using System.Security.Cryptography;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Hypothetical tape only. No constructor accepts an actual source graph or its RNG.
/// SHA256 instantiates the ideal oracle reproducibly; correctness of conditional
/// proposal weights refers to the declared ideal law, not a finite SHA256-seed law.
/// </summary>
internal sealed class NativeLabelTape
{
    private readonly NativeTapeRecipe _recipe;
    private readonly NativeInitialShuffleCondition? _condition;
    private readonly string? _expectedEntryJson;
    private readonly Dictionary<LabelRandomState, ulong> _overrides;
    private readonly HashSet<LabelRandomState> _visited = [];
    private bool _awaitingInitialShuffle;
    private Rng? _initialShuffleRng;
    private ConditionalShufflePlan? _plan;
    internal bool ConditionApplied { get; private set; }
    internal int DistinctCells => _visited.Count;
    internal int ConditionedCells => _overrides.Count;

    internal NativeLabelTape(NativeTapeRecipe recipe, NativeInitialShuffleCondition? condition = null,
        IReadOnlyDictionary<LabelRandomState, ulong>? overrides = null, string? expectedEntryJson = null)
    {
        _recipe = recipe; _condition = condition; _expectedEntryJson = expectedEntryJson ?? condition?.EntryJson;
        _overrides = overrides is null ? [] : new(overrides);
    }

    internal IDisposable EnterScope() => LabelRandomScope.Enter(Word, BeginShuffle);
    internal NativeLabelTape ReplayCopy() => new(_recipe, _condition, _overrides, _expectedEntryJson);

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        if (combatIndex != _recipe.CombatIndex) return;
        if (_expectedEntryJson is not null && _expectedEntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published target combat entry differs");
        if (_condition is null) return;
        _awaitingInitialShuffle = true;
        _initialShuffleRng = initialShuffleRng;
    }

    private ulong Word(LabelRandomState state)
    {
        _visited.Add(state);
        if (_overrides.TryGetValue(state, out ulong forced)) return forced;
        Span<byte> bytes = stackalloc byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x4E4F534C54415031UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _recipe.TapeSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], state.State0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], state.State1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], state.State2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], state.State3);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        if (!_awaitingInitialShuffle || items.Count == 0 || items.Any(x => x is not CardModel)) return null;
        if (!ReferenceEquals(rng, _initialShuffleRng)) return null;
        _awaitingInitialShuffle = false;
        var cards = items.Cast<CardModel>().ToArray();
        var owner = cards[0].Owner;
        if (cards.Any(c => !ReferenceEquals(c.Owner, owner) || c.DeckVersion is null || c.Pile?.Type != PileType.Draw)
            || owner.PlayerCombatState is null || !owner.PlayerCombatState.DrawPile.Cards.SequenceEqual(cards))
            throw new InvalidOperationException("Conditional shuffle did not identify the native initial combat draw pile");
        var ids = cards.Select(c => c.GetType().Name).ToArray();
        if (!_condition!.MatchesInitialPool(ids))
            throw new InvalidOperationException("Certified entry does not match the native initial shuffle pool");
        var proposalRandom = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-shuffle-v1");
        _plan = ConditionalShuffleProposal.Create(ids, _condition.DrawPrefixIds, proposalRandom.NextUnsignedLong)
            ?? throw new NativePublicConstraintMismatchException("Published draw prefix has no permutation support");
        var forcedWords = _plan.RawWords;
        int ordinal = 0;
        ConditionApplied = true;
        return LabelRandomScope.Enter(state =>
        {
            if (ordinal >= forcedWords.Count) throw new InvalidOperationException("Native shuffle consumed an unexpected extra word");
            if (_visited.Contains(state))
                throw new InvalidOperationException("Conditional shuffle revisited an earlier tape cell; alias correction is unresolved");
            ulong value = forcedWords[ordinal++];
            if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != value)
                throw new InvalidOperationException("Owned hypothetical shuffle replay changed its conditioned tape");
            _overrides[state] = value; _visited.Add(state);
            return value;
        });
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        if (_condition is null) return true;
        if (!ConditionApplied || _plan is null)
            throw new InvalidOperationException("An accelerated target was reached without its certified initial shuffle");
        return _plan.AcceptCorrection(nextWord);
    }
}
