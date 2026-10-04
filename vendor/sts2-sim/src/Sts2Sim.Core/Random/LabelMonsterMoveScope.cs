using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.MonsterMoves;

namespace Sts2Sim.Core.Random;

/// <summary>The actual hypothetical RollMove owner; completion is set only after native assignment.</summary>
public sealed class LabelMonsterMoveContext(MonsterModel monster, Rng rng)
{
    public MonsterModel Monster { get; } = monster;
    public Rng Rng { get; } = rng;
    public MoveState? CompletedMove { get; internal set; }
    public int BranchCount { get; internal set; }
}

/// <summary>Original ordered effective weights, evaluated once by the native Sum.</summary>
public sealed record LabelMonsterBranchContext(LabelMonsterMoveContext? Roll, RandomBranchState Branch,
    Creature Owner, Rng Rng, float TotalWeight, IReadOnlyList<float> Weights);

/// <summary>
/// Optional label-only resources. The ordinary algorithm still evaluates its Sum,
/// draws NextFloat, evaluates traversal weights, subtracts, and selects in order.
/// Proposal callbacks cannot recursively consume the native random tape.
/// </summary>
public static class LabelMonsterMoveScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly AsyncLocal<Roll?> CurrentRoll = new();
    private static readonly AsyncLocal<Branch?> CurrentBranch = new();

    public static IDisposable Enter(Func<LabelMonsterMoveContext, IDisposable?> beginRoll,
        Func<LabelMonsterBranchContext, IDisposable?> beginBranch)
    {
        ArgumentNullException.ThrowIfNull(beginRoll); ArgumentNullException.ThrowIfNull(beginBranch);
        var scope = new Scope(Current.Value, beginRoll, beginBranch); Current.Value = scope; return scope;
    }

    internal static IDisposable? BeginRoll(MonsterModel monster, Rng rng)
    {
        if (Current.Value is not { } scope) return null;
        var context = new LabelMonsterMoveContext(monster, rng);
        var resource = LabelRandomScope.InvokeResourceCallback(() => scope.BeginRoll(context));
        var roll = new Roll(CurrentRoll.Value, context, resource); CurrentRoll.Value = roll; return roll;
    }

    internal static void CompleteRoll(MoveState move)
    { if (CurrentRoll.Value is { } roll) roll.Context.CompletedMove = move; }

    internal static IDisposable? BeginBranch(RandomBranchState branch, Creature owner, Rng rng)
    {
        if (Current.Value is null) return null;
        var frame = new Branch(CurrentBranch.Value, branch, owner, rng, CurrentRoll.Value?.Context);
        CurrentBranch.Value = frame;
        if (frame.Roll is { } roll) roll.BranchCount++;
        return frame;
    }

    internal static float RecordWeight(float value)
    { CurrentBranch.Value?.Weights.Add(value); return value; }

    internal static IDisposable? ConditionBranch(float total)
    {
        if (Current.Value is not { } scope || CurrentBranch.Value is not { } branch) return null;
        var context = new LabelMonsterBranchContext(branch.Roll, branch.State, branch.Owner,
            branch.Rng, total, Array.AsReadOnly(branch.Weights.ToArray()));
        var resource = LabelRandomScope.InvokeResourceCallback(() => scope.BeginBranch(context));
        branch.Conditioned = resource is not null;
        return resource;
    }

    internal static float VerifyWeight(float value)
    {
        if (CurrentBranch.Value is { Conditioned: true } branch
            && (branch.Traversed >= branch.Weights.Count
                || BitConverter.SingleToInt32Bits(branch.Weights[branch.Traversed++]) != BitConverter.SingleToInt32Bits(value)))
            throw new InvalidOperationException("Conditioned native branch weights changed during traversal");
        return value;
    }

    private sealed class Branch(Branch? previous, RandomBranchState state, Creature owner, Rng rng,
        LabelMonsterMoveContext? roll) : IDisposable
    {
        internal RandomBranchState State { get; } = state;
        internal Creature Owner { get; } = owner;
        internal Rng Rng { get; } = rng;
        internal LabelMonsterMoveContext? Roll { get; } = roll;
        internal List<float> Weights { get; } = [];
        internal int Traversed;
        internal bool Conditioned;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (!ReferenceEquals(CurrentBranch.Value, this)) throw new InvalidOperationException("Branch scopes disposed out of order");
            CurrentBranch.Value = previous;
        }
    }
    private sealed class Roll(Roll? previous, LabelMonsterMoveContext context, IDisposable? resource) : IDisposable
    {
        internal LabelMonsterMoveContext Context { get; } = context;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try { resource?.Dispose(); }
            finally
            {
                if (!ReferenceEquals(CurrentRoll.Value, this)) throw new InvalidOperationException("Move scopes disposed out of order");
                CurrentRoll.Value = previous;
            }
        }
    }
    private sealed class Scope(Scope? previous, Func<LabelMonsterMoveContext, IDisposable?> beginRoll,
        Func<LabelMonsterBranchContext, IDisposable?> beginBranch) : IDisposable
    {
        internal Func<LabelMonsterMoveContext, IDisposable?> BeginRoll => beginRoll;
        internal Func<LabelMonsterBranchContext, IDisposable?> BeginBranch => beginBranch;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Monster move scopes disposed out of order");
        }
        private Scope? Previous => previous;
    }
}
