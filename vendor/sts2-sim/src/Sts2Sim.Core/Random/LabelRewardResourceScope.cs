using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rooms;

namespace Sts2Sim.Core.Random;

/// <summary>
/// Optional label boundary notified before disposal when native reward generation
/// fails. Implementations abandon dependent validation while preserving cleanup.
/// Ordinary execution and label scopes without this contract are unchanged.
/// </summary>
public interface IAbortableLabelRewardBoundary : IDisposable
{
    void Abort();
}

/// <summary>Actual native presence branch, after the force hook and before any draw or pity update.</summary>
public sealed record LabelPotionPresenceContext(Rng Rng, RoomType RoomType, bool Forced, float Threshold);

/// <summary>Actual native range; omitted zero-proportion rewards consume no word.</summary>
public sealed record LabelGoldRewardContext(Player Player, Rng Rng, int Min, int Max, bool Omitted);

/// <summary>Unfiltered character-plus-shared pool before the standard native rarity/index draws.</summary>
public sealed record LabelPotionSelectionContext(Player Player, Rng Rng, IReadOnlyList<PotionModel> Pool);

/// <summary>
/// Optional label-only callbacks. No ordinary game execution enters this scope. Callbacks
/// share LabelRandomScope's interception guard so proposal randomness stays off the tape.
/// Native branches, draws, pity updates, and mutations remain in their original methods.
/// </summary>
public static class LabelRewardResourceScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static IDisposable Enter(Func<LabelPotionPresenceContext, IDisposable?> presence,
        Func<LabelGoldRewardContext, IDisposable?> gold, Func<LabelPotionSelectionContext, IDisposable?> potion)
    {
        var scope = new Scope(Current.Value, presence, gold, potion);
        Current.Value = scope;
        return scope;
    }

    internal static IDisposable? BeginPresence(LabelPotionPresenceContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Presence(context)) : null;
    internal static IDisposable? BeginGold(LabelGoldRewardContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Gold(context)) : null;
    internal static IDisposable? BeginPotion(LabelPotionSelectionContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Potion(context)) : null;

    private sealed class Scope(Scope? previous, Func<LabelPotionPresenceContext, IDisposable?> presence,
        Func<LabelGoldRewardContext, IDisposable?> gold, Func<LabelPotionSelectionContext, IDisposable?> potion) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelPotionPresenceContext, IDisposable?> Presence => presence;
        internal Func<LabelGoldRewardContext, IDisposable?> Gold => gold;
        internal Func<LabelPotionSelectionContext, IDisposable?> Potion => potion;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Resource scopes must be disposed in nesting order");
        }
    }
}
