using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace Sts2Sim.Core.Random;

/// <summary>Owned native call-site context; Cards follows the actual list through its native sort.</summary>
public sealed record LabelCombatReshuffleContext(ICombatState Combat, Player Player, Rng Rng,
    IReadOnlyList<CardModel> Cards)
{
    private Action? _afterSuccess;
    private bool _completed;
    /// <summary>Runs only after the exact native StableShuffle returns normally, including its forcing scope.</summary>
    public void AfterSuccessfulShuffle(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_completed || _afterSuccess is not null || !ReferenceEquals(LabelCombatReshuffleScope.Current, this))
            throw new InvalidOperationException("Native reshuffle success callback is repeated or outside its exact boundary");
        _afterSuccess = callback;
    }
    internal void Complete()
    {
        if (_completed) throw new InvalidOperationException("Native reshuffle completed twice");
        _completed = true;
        _afterSuccess?.Invoke();
    }
}

/// <summary>
/// Optional label-only marker around CardPileCmd.Shuffle's original StableShuffle.
/// It distinguishes that call from other card-list shuffles using the same RNG.
/// It can acknowledge normal native completion; it performs no random draw,
/// sorting, or pile mutation.
/// </summary>
public static class LabelCombatReshuffleScope
{
    private static readonly AsyncLocal<Scope?> Active = new();
    private static readonly AsyncLocal<LabelCombatReshuffleContext?> Boundary = new();
    public static LabelCombatReshuffleContext? Current => Active.Value is null ? null : Boundary.Value;

    public static IDisposable Enter()
    {
        var scope = new Scope(Active.Value); Active.Value = scope; return scope;
    }

    internal static BoundaryScope? Mark(ICombatState combat, Player player, Rng rng, List<CardModel> cards)
    {
        if (Active.Value is null) return null;
        var previous = Boundary.Value;
        var context = new LabelCombatReshuffleContext(combat, player, rng, cards.AsReadOnly());
        Boundary.Value = context;
        return new BoundaryScope(context, () => Boundary.Value = previous);
    }

    internal sealed class BoundaryScope(LabelCombatReshuffleContext context, Action restore) : IDisposable
    {
        private bool _disposed;
        internal void Complete() => context.Complete();
        public void Dispose() { if (_disposed) return; _disposed = true; restore(); }
    }
    private sealed class Scope(Scope? previous) : IDisposable
    {
        internal Scope? Previous => previous;
        public void Dispose()
        {
            if (ReferenceEquals(Active.Value, this)) { Active.Value = previous; return; }
            for (var current = Active.Value; current is not null; current = current.Previous)
                if (ReferenceEquals(current, this)) throw new InvalidOperationException("Reshuffle scopes must be disposed in nesting order");
        }
    }
}
