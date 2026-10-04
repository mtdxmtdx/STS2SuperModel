using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Exact optional proposal for the native weighted branch primitive. Source-pinned
/// sealed graphs prove pure constant weights and their repeat restrictions; the
/// callback supplies original effective evaluations. Every envelope maximizes over
/// all possible relevant histories, never just the sampled hidden state.
/// </summary>
internal sealed class NativePublicMonsterBranchIntentProposal(NativePublicMonsterBranchIntentCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private CombatState? _state;
    private int _combatIndex = -1;
    private NativePublicMonsterIntentInput? _active;
    private readonly Dictionary<Creature, int> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, int> _rollOrdinals = [];
    private readonly HashSet<(int Combat, int Slot, int Roll)> _completed = [];
    private readonly List<NativeFloatBranchPlan> _plans = [];
    private LabelMonsterMoveContext? _roll;
    private NativePublicMonsterRollTarget? _target;
    private bool _branchComplete;

    internal int ConditionedRollCount => _completed.Count;
    internal int ConditionedBranchCount => _plans.Count;
    internal ShuffleRational NativeToProposalRatio => Product(_plans.Select(plan => plan.NativeToProposalRatio));
    internal ShuffleRational Envelope => Product(_plans.Select(plan => plan.Envelope));

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Monster intent proposal already owns a native run");
        _run = run;
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        if (_run is null || !ReferenceEquals(initialShuffleRng, _run.Rng.Shuffle))
            throw new InvalidOperationException("Monster intent combat escaped its owned native run");
        if (combatIndex != _combatIndex + 1)
            throw new InvalidOperationException("Monster intent indices must include every combat entry");
        ValidateActiveCompletion();
        _combatIndex = combatIndex; _active = null; _state = null; _slots.Clear(); _rollOrdinals.Clear();
        if (!condition.Combats.TryGetValue(combatIndex, out var input)) return;
        if (input.EntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Public monster intent entry differs at combat " + combatIndex);
        _active = input;
    }

    internal IDisposable? BeginRoll(LabelMonsterMoveContext context)
    {
        if (_active is null || _active.Targets.All(target =>
            _completed.Contains((_combatIndex, target.Slot, target.RollOrdinal)))) return null;
        if (_roll is not null) throw new InvalidOperationException("Monster intent rolls cannot nest");
        var monster = context.Monster;
        if (monster.Creature?.CombatState is not CombatState state || _run is null
            || !ReferenceEquals(state.RunState, _run) || !ReferenceEquals(context.Rng, _run.Rng.MonsterAi)
            || _run.Rng.UsesSemanticKeys || !ReferenceEquals(monster.RunRng, _run.Rng)
            || _run.CurrentRoom is not CombatRoom room || !ReferenceEquals(room.Engine?.State, state)
            || !ReferenceEquals(_run.Players.Single().Creature.CombatState, state)
            || state.IsPlayerExtraTurn || monster.IsPerformingMove)
            throw new InvalidOperationException("Monster intent roll is unowned, out of phase, or on a foreign stream");
        if (_state is null)
        {
            if (!state.Enemies.Select(enemy => enemy.Monster!.GetType().Name).SequenceEqual(_active.InitialRoster)
                || state.SpawnedEnemies.Count != _active.InitialRoster.Count)
                throw new NativePublicConstraintMismatchException("Native initial intent roster differs from the public roster");
            _state = state;
            for (int slot = 0; slot < state.Enemies.Count; slot++) _slots.Add(state.Enemies[slot], slot);
        }
        if (!ReferenceEquals(_state, state) || !_slots.TryGetValue(monster.Creature, out int ownerSlot)
            || state.SpawnedEnemies.Count != _slots.Count)
            throw new InvalidOperationException("Monster intent lifetime slot changed its native owner");
        int ordinal = _rollOrdinals.GetValueOrDefault(ownerSlot) + 1;
        _rollOrdinals[ownerSlot] = ordinal;
        var target = _active.Targets.SingleOrDefault(item => item.Slot == ownerSlot && item.RollOrdinal == ordinal);
        if (target is null) return null;
        if (_run.Players.Single().PlayerCombatState!.TurnNumber + 1 != ordinal
            || target.Model != monster.GetType().Name)
            throw new InvalidOperationException("Public intent publication is not aligned to the native roll ordinal");
        ValidateGraph(monster, ordinal);
        _roll = context; _target = target; _branchComplete = false;
        int before = context.Rng.Counter;
        return new Completion(() =>
        {
            try
            {
                // On exceptional unwind keep this target incomplete. Do not hide the
                // original force/alias exception with a second missing-callback error.
                if (context.CompletedMove is null) return;
                bool needsBranch = target.Model == nameof(LeafSlimeS) || ordinal > 1;
                if (context.BranchCount != (needsBranch ? 1 : 0)
                    || needsBranch && !_branchComplete
                    || context.Rng.Counter - before != (needsBranch ? 1 : 0))
                    throw new InvalidOperationException("Native monster roll did not complete its exact branch draw");
                if (context.CompletedMove.Id != MoveIds(monster)[ShapeIndex(target)])
                    throw new NativePublicConstraintMismatchException("Native monster roll differs from the public intent shape");
                _completed.Add((_combatIndex, ownerSlot, ordinal));
            }
            finally { _roll = null; _target = null; }
        });
    }

    internal IDisposable? BeginBranch(LabelMonsterBranchContext context)
    {
        if (_active is null) return null;
        if (_roll is null)
        {
            if (context.Roll is null && _active.Targets.Any(target => !_completed.Contains((_combatIndex, target.Slot, target.RollOrdinal))))
                throw new InvalidOperationException("A public monster branch lacks its actual RollMove owner");
            return null;
        }
        if (!ReferenceEquals(context.Roll, _roll) || !ReferenceEquals(context.Owner, _roll.Monster.Creature)
            || !ReferenceEquals(context.Rng, _roll.Rng) || context.Roll.BranchCount != 1 || _branchComplete)
            throw new InvalidOperationException("Monster branch callback is repeated or has a different owner");
        var target = _target!;
        var monster = _roll.Monster;
        var ids = MoveIds(monster);
        if (!ReferenceEquals(monster.MoveStateMachine!.States["RAND"], context.Branch)
            || context.Weights.Count != ids.Length || context.Branch.States.Count != ids.Length)
            throw new InvalidOperationException("Monster branch differs from its reviewed native graph");
        var log = monster.MoveStateMachine.StateLog;
        string? last = log.LastOrDefault()?.Id;
        // Twig's attack can repeat twice. Even longer attack runs remain native
        // support: the first zero-weight branch wins the exact zero-word endpoint.
        float[] expected = monster is TwigSlimeM
            ? [log.Count >= 2 && log[^1].Id == ids[0] && log[^2].Id == ids[0] ? 0f : 1f,
                last == ids[1] ? 0f : 1f]
            : ids.Select(id => last == id ? 0f : 1f).ToArray();
        if (!context.Weights.SequenceEqual(expected) || context.TotalWeight != expected.Sum())
            throw new InvalidOperationException("Monster branch weights differ from the source-pinned repeat certificate");
        int selected = ShapeIndex(target);
        ulong envelope = monster is TwigSlimeM ? TwigEnvelopeSize(selected)
            : EnvelopeSize(ids.Length, selected, target.Model == nameof(LeafSlimeS) && target.RollOrdinal == 1);
        var plan = NativeFloatBranchProposal.Create(context.TotalWeight, context.Weights, selected, envelope, nextWord)
            ?? throw new NativePublicConstraintMismatchException("Public intent shape has no native branch support");
        int before = context.Rng.Counter;
        var inner = forcePrefixWords([plan.RawWord], context.Rng,
            "public combat " + _combatIndex + " monster slot " + target.Slot + " roll " + target.RollOrdinal);
        _plans.Add(plan);
        return new Completion(() =>
        {
            inner.Dispose();
            if (context.Rng.Counter - before > 1)
                throw new InvalidOperationException("Native monster branch consumed more than exactly one draw");
            _branchComplete = context.Rng.Counter - before == 1;
        });
    }

    internal void ValidateCompletion()
    {
        ValidateActiveCompletion();
        if (_completed.Count != condition.EligibleRollCount || _plans.Count != condition.EligibleBranchCount)
            throw new InvalidOperationException("Public monster intents were reached without every certified roll and branch");
    }
    private void ValidateActiveCompletion()
    {
        if (_roll is not null || _active is not null && _active.Targets.Any(target =>
            !_completed.Contains((_combatIndex, target.Slot, target.RollOrdinal))))
            throw new InvalidOperationException("A certified native monster roll did not complete");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    {
        ArgumentNullException.ThrowIfNull(correctionWord); ValidateCompletion();
        foreach (var plan in _plans) if (!plan.AcceptCorrection(correctionWord)) return false;
        return true;
    }

    internal static ulong EnvelopeSize(int count, int selected, bool initial, int precisionBits = 53)
    {
        if (count is not (2 or 3) || initial && count != 2) throw new ArgumentOutOfRangeException(nameof(count));
        if (initial) return NativeFloatBranchProposal.Bucket(count, Enumerable.Repeat(1f, count).ToArray(), selected, precisionBits).Size;
        // The public root fixes which calls are initial/later. Any compatible latent
        // history ends with exactly one move, making exactly one effective weight 0.
        return Enumerable.Range(0, count).Max(previous => NativeFloatBranchProposal.Bucket(count - 1,
            Enumerable.Range(0, count).Select(index => previous == index ? 0f : 1f).ToArray(), selected, precisionBits).Size);
    }
    internal static ulong TwigEnvelopeSize(int selected, int precisionBits = 53)
    {
        // Every nonempty log ends with sticky, one attack, or >=2 attacks. These
        // are the complete effective-weight contexts, including zero-word repeats.
        // Maximize over the whole root-fixed set, never over the sampled log only.
        float[][] contexts = [[1f, 0f], [1f, 1f], [0f, 1f]];
        return contexts.Max(weights => NativeFloatBranchProposal.Bucket(weights.Sum(), weights,
            selected, precisionBits).Size);
    }
    private static string[] MoveIds(MonsterModel monster) => monster switch
    {
        SludgeSpinner => ["OIL_SPRAY_MOVE", "SLAM_MOVE", "RAGE_MOVE"],
        LeafSlimeS => ["TACKLE_MOVE", "GOOP_MOVE"],
        TwigSlimeM => ["POKEY_POUNCE_MOVE", "STICKY_SHOT_MOVE"],
        _ => throw new InvalidOperationException("Monster weighted branch is not source-certified"),
    };
    private static int ShapeIndex(NativePublicMonsterRollTarget target) => (target.Model, target.Shape) switch
    {
        (nameof(SludgeSpinner), NativePublicMonsterIntentShape.AttackDebuff) => 0,
        (nameof(SludgeSpinner), NativePublicMonsterIntentShape.Attack) => 1,
        (nameof(SludgeSpinner), NativePublicMonsterIntentShape.AttackBuff) => 2,
        (nameof(LeafSlimeS), NativePublicMonsterIntentShape.Attack) => 0,
        (nameof(LeafSlimeS), NativePublicMonsterIntentShape.Status) => 1,
        (nameof(TwigSlimeM), NativePublicMonsterIntentShape.Attack) => 0,
        (nameof(TwigSlimeM), NativePublicMonsterIntentShape.Status) => 1,
        _ => throw new InvalidOperationException("Unsupported public intent shape"),
    };
    private static void ValidateGraph(MonsterModel monster, int ordinal)
    {
        var ids = MoveIds(monster);
        var machine = monster.MoveStateMachine ?? throw new InvalidOperationException("Monster has no state machine");
        string initial = monster switch { SludgeSpinner => ids[0], TwigSlimeM => ids[1], _ => "RAND" };
        int expectedLogs = ordinal == 1 && initial != "RAND" ? 1 : ordinal - 1;
        if (machine.States.Count != ids.Length + 1 || machine.States.GetValueOrDefault("RAND") is not RandomBranchState random
            || random.States.Count != ids.Length || machine.StateLog.Count != expectedLogs
            || machine.PerformedFirstMove != (ordinal > 1)
            || machine.StateLog.Any(state => !ids.Contains(state.Id) || !ReferenceEquals(state, machine.States[state.Id]))
            || initial != "RAND" && machine.StateLog[0].Id != initial
            || ordinal == 1 && machine.CurrentState.Id != initial
            || ordinal > 1 && !ReferenceEquals(machine.CurrentState, machine.StateLog[^1]))
            throw new InvalidOperationException("Native monster history differs from the source-pinned roll certificate");
        for (int i = 0; i < ids.Length; i++)
        {
            var branch = random.States[i];
            bool twice = monster is TwigSlimeM && i == 0;
            if (branch.StateId != ids[i]
                || branch.RepeatType != (twice ? MoveRepeatType.CanRepeatXTimes : MoveRepeatType.CannotRepeat)
                || branch.MaxTimes != (twice ? 2 : 0) || branch.Cooldown != 0
                || machine.States.GetValueOrDefault(ids[i]) is not MoveState move || !ReferenceEquals(move.FollowUpState, random)
                || move.MustPerformOnceBeforeTransitioning || move.FollowUpStateId is not null)
                throw new InvalidOperationException("Native monster graph differs from the pure weighted-branch certificate");
        }
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
