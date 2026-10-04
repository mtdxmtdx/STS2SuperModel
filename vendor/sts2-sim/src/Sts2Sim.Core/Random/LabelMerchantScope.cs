using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace Sts2Sim.Core.Random;

/// <summary>Before the player's native relic-bag shuffles; the shared bag has already been created.</summary>
public sealed record LabelPlayerRelicBagContext(Player Player, Rng Rng, IReadOnlyList<RelicModel> Pool);

/// <summary>Before initial stock generation, with completion populated only after native generation succeeds.</summary>
public sealed class LabelMerchantInventoryContext(Player player)
{
    public Player Player { get; } = player;
    public MerchantInventory? CompletedInventory { get; internal set; }
}

/// <summary>Label-only boundaries. Native selection, draws, prices and bag removals remain in their original methods.</summary>
public static class LabelMerchantScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    public static IDisposable Enter(Func<LabelPlayerRelicBagContext, IDisposable?> bag,
        Func<LabelMerchantInventoryContext, IDisposable?> inventory)
    {
        var scope = new Scope(Current.Value, bag, inventory); Current.Value = scope; return scope;
    }
    internal static IDisposable? BeginBag(LabelPlayerRelicBagContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Bag(context)) : null;
    internal static IDisposable? BeginInventory(LabelMerchantInventoryContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Inventory(context)) : null;
    private sealed class Scope(Scope? previous, Func<LabelPlayerRelicBagContext, IDisposable?> bag,
        Func<LabelMerchantInventoryContext, IDisposable?> inventory) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelPlayerRelicBagContext, IDisposable?> Bag => bag;
        internal Func<LabelMerchantInventoryContext, IDisposable?> Inventory => inventory;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Merchant scopes must be disposed in nesting order");
        }
    }
}
