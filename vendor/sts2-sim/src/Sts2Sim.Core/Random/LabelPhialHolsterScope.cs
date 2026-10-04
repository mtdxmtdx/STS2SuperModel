using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Factories;
using Sts2Sim.Core.Models;

namespace Sts2Sim.Core.Random;

/// <summary>The actual PhialHolster pickup, after growing slots and before either native grant.</summary>
public sealed record LabelPhialHolsterContext(Player Player, Rng Rng, IReadOnlyList<PotionModel> Pool);

public interface IAbortableLabelPhialHolsterBoundary : IDisposable
{
    void Abort();
}

/// <summary>Optional label-only batch boundary; native rarity, filtering and procurement remain untouched.</summary>
public static class LabelPhialHolsterScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static IDisposable Enter(Func<LabelPhialHolsterContext, IDisposable?> begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        var scope = new Scope(Current.Value, begin); Current.Value = scope; return scope;
    }

    internal static IDisposable? BeginGrant(Player player, Rng rng) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Begin(new(player, rng, PotionFactory.GetOutOfCombatPool(player))))
        : null;

    private sealed class Scope(Scope? previous, Func<LabelPhialHolsterContext, IDisposable?> begin) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelPhialHolsterContext, IDisposable?> Begin => begin;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("PhialHolster scopes must be disposed in nesting order");
        }
    }
}
