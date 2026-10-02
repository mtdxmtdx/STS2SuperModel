using System.Buffers.Binary;
using System.Security.Cryptography;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

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
    private readonly NativeInitialHpCondition? _hpCondition;
    private readonly NativeNeowCondition? _neowCondition;
    private readonly NativeFirstRewardCondition? _firstRewardCondition;
    private readonly NativeFirstEncounterCondition? _firstEncounterCondition;
    private NativeFirstEncounterProposal? _firstEncounterPlan;
    private readonly NativeInitialPrefixProposal? _initialPrefixPlan;
    private RunState? _constructingRun;
    private int _prefixWordsReplayed;
    private bool _prefixComplete, _prefixFailed;
    private NativeFirstRewardProposal? _firstRewardPlan;
    private int _currentCombatIndex = -1;
    private readonly string? _expectedEntryJson;
    private readonly Dictionary<LabelRandomState, ulong> _overrides;
    private readonly HashSet<LabelRandomState> _visited = [];
    private bool _awaitingInitialShuffle;
    private Rng? _initialShuffleRng;
    private ConditionalShufflePlan? _plan;
    private readonly HashSet<string> _conditionedHpIds = new(StringComparer.Ordinal);
    private readonly List<NativeHpProposal> _hpPlans = [];
    private bool _conditioningInitialHp;
    private NativeNeowProposal? _neowPlan;
    private RunState? _run;
    internal bool ConditionApplied { get; private set; }
    internal int DistinctCells => _visited.Count;
    internal int ConditionedCells => _overrides.Count;
    internal int ConditionedHpCount => _hpPlans.Count;
    internal bool NeowConditionApplied => _neowPlan is not null;
    internal bool FirstRewardConditionApplied => _firstRewardPlan is not null;
    internal bool FirstEncounterConditionApplied => _firstEncounterPlan is not null;
    internal int? ProposedFirstEncounterIndex => _firstEncounterPlan?.FirstEncounterIndex;

    internal NativeLabelTape(NativeTapeRecipe recipe, NativeInitialShuffleCondition? condition = null,
        IReadOnlyDictionary<LabelRandomState, ulong>? overrides = null, string? expectedEntryJson = null,
        NativeInitialHpCondition? hpCondition = null, NativeNeowCondition? neowCondition = null,
        NativeFirstRewardCondition? firstRewardCondition = null, NativeFirstEncounterCondition? firstEncounterCondition = null, NativeInitialPrefixProposal? initialPrefixPlan = null)
    {
        _recipe = recipe; _condition = condition; _expectedEntryJson = expectedEntryJson ?? condition?.EntryJson;
        _hpCondition = hpCondition;
        _neowCondition = neowCondition;
        _firstRewardCondition = firstRewardCondition;
        _firstEncounterCondition = firstEncounterCondition;
        _initialPrefixPlan = initialPrefixPlan;
        if (initialPrefixPlan is not null && initialPrefixPlan.SelectedRecipe != recipe)
            throw new ArgumentException("Initial prefix and native replay recipe differ", nameof(initialPrefixPlan));
        _overrides = overrides is null ? [] : new(overrides);
        if (initialPrefixPlan is not null)
            foreach (var word in initialPrefixPlan.Trace)
            {
                if (_overrides.TryGetValue(word.State, out var previous) && previous != word.Word)
                    throw new InvalidOperationException("Prepared initial prefix conflicts with a replayed oracle cell");
                _overrides[word.State] = word.Word;
            }
    }

    internal IDisposable EnterScope() => LabelRandomScope.Enter(Word, BeginShuffle, BeginMonsterHp, BeginCombatRewardGeneration, BeginNormalEncounterGeneration, BeginMapGeneration);
    internal NativeLabelTape ReplayCopy() => new(_recipe, _condition, _overrides, _expectedEntryJson, _hpCondition, _neowCondition, _firstRewardCondition, _firstEncounterCondition, _initialPrefixPlan);
    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("A hypothetical tape already owns a native run");
        if (_initialPrefixPlan is not null && (!_prefixComplete || !ReferenceEquals(_constructingRun, run)))
            throw new InvalidOperationException("Initial prefix did not finish in this native construction");
        _run = run;
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        _currentCombatIndex = combatIndex;
        if (combatIndex != _recipe.CombatIndex) return;
        if (_expectedEntryJson is not null && _expectedEntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published target combat entry differs");
        if (_condition is null && _hpCondition is null) return;
        _awaitingInitialShuffle = true;
        _initialShuffleRng = initialShuffleRng;
        _conditioningInitialHp = _hpCondition is not null;
    }

    private ulong Word(LabelRandomState state)
    {
        if (_initialPrefixPlan is not null && !_prefixComplete)
        {
            try
            {
                if (_prefixWordsReplayed >= _initialPrefixPlan.Trace.Count
                    || _initialPrefixPlan.Trace[_prefixWordsReplayed].State != state)
                    throw new InvalidOperationException("Native initial prefix changed its complete ordered RNG trace");
                var expected = _initialPrefixPlan.Trace[_prefixWordsReplayed++];
                if (!_overrides.TryGetValue(state, out var value) || value != expected.Word)
                    throw new InvalidOperationException("Native initial prefix changed an aliased oracle word");
                // Internal act/map aliases were jointly sampled. Reuse is required.
                _visited.Add(state); return value;
            }
            catch { _prefixFailed = true; throw; }
        }
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
        if (_neowCondition is not null && _run?.CurrentRoom is EventRoom { Event: Neow neow }
            && ReferenceEquals(rng, neow.Rng) && items.Count > 0 && items.All(item => item is Type))
        {
            if (_neowPlan is not null) throw new InvalidOperationException("Native Neow repeated its certified initial positive shuffle");
            var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-neow-v1");
            _neowPlan = _neowCondition.CreateProposal(items.Cast<Type>().ToArray(), random.NextUnsignedLong);
            return ForceWords(_neowPlan.Plan.RawWords, "Neow");
        }
        if (!_awaitingInitialShuffle || items.Count == 0 || items.Any(x => x is not CardModel)) return null;
        if (!ReferenceEquals(rng, _initialShuffleRng)) return null;
        _awaitingInitialShuffle = false;
        _conditioningInitialHp = false;
        if (_hpCondition is not null && _conditionedHpIds.Count != _hpCondition.EnemyCount)
            throw new NativePublicConstraintMismatchException("Native initial roster has fewer monsters than the public opening");
        var cards = items.Cast<CardModel>().ToArray();
        var owner = cards[0].Owner;
        if (cards.Any(c => !ReferenceEquals(c.Owner, owner) || c.DeckVersion is null || c.Pile?.Type != PileType.Draw)
            || owner.PlayerCombatState is null || !owner.PlayerCombatState.DrawPile.Cards.SequenceEqual(cards))
            throw new InvalidOperationException("Conditional shuffle did not identify the native initial combat draw pile");
        if (_condition is null) return null;
        var ids = cards.Select(c => c.GetType().Name).ToArray();
        if (!_condition!.MatchesInitialPool(ids))
            throw new InvalidOperationException("Certified entry does not match the native initial shuffle pool");
        var proposalRandom = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-shuffle-v1");
        _plan = ConditionalShuffleProposal.Create(ids, _condition.DrawPrefixIds, proposalRandom.NextUnsignedLong)
            ?? throw new NativePublicConstraintMismatchException("Published draw prefix has no permutation support");
        ConditionApplied = true;
        return ForceWords(_plan.RawWords, "shuffle");
    }

    internal void ValidateProposalCompletion()
    {
        if (_initialPrefixPlan is not null && !_prefixComplete)
            throw new InvalidOperationException("Native initial prefix is incomplete");
        if (_firstEncounterCondition is not null && _firstEncounterPlan is null)
            throw new InvalidOperationException("An encounter-conditioned root was reached without its native normal encounter generation");
        if (_firstRewardCondition is not null && _firstRewardPlan is null)
            throw new InvalidOperationException("A first-reward-conditioned root was reached without its native first reward generation");
        if (_neowCondition is not null && _neowPlan is null)
            throw new InvalidOperationException("A Neow-conditioned root was reached without its native positive shuffle");
        if (_hpCondition is not null && (_conditioningInitialHp || _hpPlans.Count != _hpCondition.EnemyCount))
            throw new InvalidOperationException("An HP-conditioned root was reached without its complete native initial roster");
        if (_condition is not null && (!ConditionApplied || _plan is null))
            throw new InvalidOperationException("An accelerated target was reached without its certified initial shuffle");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        // No random rejection may hide a missing required hook or a partial plan.
        ValidateProposalCompletion();
        if (_initialPrefixPlan is not null && !_initialPrefixPlan.AcceptCorrection(nextWord)) return false;
        if (_firstEncounterPlan is not null && !_firstEncounterPlan.AcceptCorrection(nextWord)) return false;
        if (_neowPlan is not null && !_neowPlan.AcceptCorrection(nextWord)) return false;
        if (_firstRewardPlan is not null && !_firstRewardPlan.AcceptCorrection(nextWord)) return false;
        foreach (var hp in _hpPlans) if (!hp.AcceptCorrection(nextWord)) return false;
        return _plan?.AcceptCorrection(nextWord) ?? true;
    }

    private IDisposable? BeginMonsterHp(LabelMonsterHpContext context)
    {
        if (!_conditioningInitialHp) return null;
        string id = context.Creature.Monster?.GetType().Name
            ?? throw new InvalidOperationException("Initial HP conditioning requires a monster");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-hp-v1:" + id);
        var plan = _hpCondition!.CreateProposal(context, _conditionedHpIds, random.NextUnsignedLong);
        _conditionedHpIds.Add(id); _hpPlans.Add(plan);
        return ForceWords([plan.RawWord], "HP");
    }

    private IDisposable? BeginCombatRewardGeneration(LabelCombatRewardContext context)
    {
        if (_firstRewardCondition is null || _currentCombatIndex != 0) return null;
        if (_firstRewardPlan is not null)
            throw new InvalidOperationException("Native first-combat reward generation occurred twice");
        if (_run is null || !ReferenceEquals(context.Player.RunState, _run))
            throw new InvalidOperationException("Native reward conditioning escaped its owned hypothetical run");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-first-reward-v1");
        _firstRewardPlan = _firstRewardCondition.CreateProposal(context, random.NextUnsignedLong);
        return ForcePrefixWords(_firstRewardPlan.RawWords, context.Rng, "first reward");
    }

    private IDisposable? BeginMapGeneration(LabelMapGenerationContext context)
    {
        if (_initialPrefixPlan is null) return null;
        if (_constructingRun is not null || _run is not null || _prefixComplete)
            throw new InvalidOperationException("Initial map prefix was entered again outside native construction");
        _initialPrefixPlan.ValidateMapBoundary(context, _prefixWordsReplayed);
        _constructingRun = context.Run;
        return new PrefixCompletionScope(() =>
        {
            if (_prefixFailed) return;
            if (_prefixWordsReplayed != _initialPrefixPlan.Trace.Count)
                throw new InvalidOperationException("Native map did not replay its complete selected prefix");
            _prefixComplete = true;
        });
    }

    private sealed class PrefixCompletionScope(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }

    private IDisposable? BeginNormalEncounterGeneration(LabelNormalEncounterContext context)
    {
        if (_firstEncounterCondition is null) return null;
        if (_firstEncounterPlan is not null)
            throw new InvalidOperationException("Native first-act normal encounter generation occurred twice");
        if (_run is null || !ReferenceEquals(context.Run, _run))
            throw new InvalidOperationException("Encounter conditioning escaped its owned hypothetical run");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-first-encounters-v1");
        _firstEncounterPlan = _firstEncounterCondition.CreateProposal(context, random.NextUnsignedLong);
        return ForcePrefixWords(_firstEncounterPlan.RawWords, context.Rng, "first encounters");
    }

    private IDisposable ForcePrefixWords(IReadOnlyList<ulong> words, Rng nativeRng, string purpose)
    {
        // This callback runs with label interception suppressed. Advancing this
        // exact private clone records expected addresses without reading source
        // data, consuming the native stream, or changing hypothetical tape cells.
        var snapshot = nativeRng.ToSerializable();
        // Use the primitive copy so hypothetical address enumeration does not
        // emit native Rng diagnostics or copy its per-draw observer.
        var clone = new MegaRandom(snapshot);
        var expected = new LabelRandomState[words.Count];
        for (int i = 0; i < expected.Length; i++)
        {
            clone.FillSerializableState(snapshot);
            expected[i] = new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3);
            clone.NextULong();
        }
        int ordinal = 0; bool callbackFailed = false;
        var inner = LabelRandomScope.Enter(state =>
        {
            if (ordinal == words.Count) return Word(state);
            try
            {
                if (state != expected[ordinal])
                    throw new InvalidOperationException($"Native {purpose} prefix used an unexpected RNG state or stream");
                if (_visited.Contains(state))
                    throw new InvalidOperationException($"Conditional {purpose} revisited an earlier tape cell; alias correction is unresolved");
                ulong value = words[ordinal++];
                if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != value)
                    throw new InvalidOperationException($"Owned hypothetical {purpose} replay changed its conditioned tape");
                _overrides[state] = value; _visited.Add(state);
                return value;
            }
            catch { callbackFailed = true; throw; }
        });
        return new CompleteWordScope(inner, () => callbackFailed || ordinal == words.Count, $"{purpose} prefix");
    }

    private IDisposable ForceWords(IReadOnlyList<ulong> words, string purpose)
    {
        int ordinal = 0; bool callbackFailed = false;
        var inner = LabelRandomScope.Enter(state =>
        {
            try
            {
                if (ordinal >= words.Count) throw new InvalidOperationException($"Native {purpose} consumed an unexpected extra word");
                if (_visited.Contains(state))
                    throw new InvalidOperationException($"Conditional {purpose} revisited an earlier tape cell; alias correction is unresolved");
                ulong value = words[ordinal++];
                if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != value)
                    throw new InvalidOperationException($"Owned hypothetical {purpose} replay changed its conditioned tape");
                _overrides[state] = value; _visited.Add(state);
                return value;
            }
            catch { callbackFailed = true; throw; }
        });
        return new CompleteWordScope(inner, () => callbackFailed || ordinal == words.Count, purpose);
    }

    private sealed class CompleteWordScope(IDisposable inner, Func<bool> complete, string purpose) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            if (!complete()) throw new InvalidOperationException($"Native {purpose} did not consume its complete conditioned word sequence");
        }
    }
}
