using Sts2Sim.Core.Models;

namespace Sts2Sim.Core.Random;

/// <summary>Owned native default-transform selection, before its single index draw.</summary>
public sealed class LabelCardTransformContext(CardModel original, bool isInCombat, Rng rng,
    IReadOnlyList<CardModel> candidates)
{
    public CardModel Original { get; } = original;
    public bool IsInCombat { get; } = isInCombat;
    public Rng Rng { get; } = rng;
    public IReadOnlyList<CardModel> Candidates { get; } = Array.AsReadOnly(candidates.ToArray());
    public CardModel? CompletedCard { get; private set; }
    public Exception? NativeFailure { get; private set; }
    internal void Complete(CardModel card) => CompletedCard = card;
    internal void Abort(Exception error) => NativeFailure = error;
}

/// <summary>Label-only scope. Native transformation selection and cloning still run unchanged.</summary>
public static class LabelCardTransformScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    internal static bool IsActive => Current.Value is not null;

    public static IDisposable Enter(Func<LabelCardTransformContext, IDisposable?> begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        var scope = new Scope(Current.Value, begin); Current.Value = scope; return scope;
    }

    internal static IDisposable? Begin(LabelCardTransformContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Begin(context)) : null;

    private sealed class Scope(Scope? previous, Func<LabelCardTransformContext, IDisposable?> begin) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelCardTransformContext, IDisposable?> Begin => begin;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Card transform scopes must be disposed in nesting order");
        }
    }
}
