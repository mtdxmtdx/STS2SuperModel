namespace Sts2Sim.Core.Random;

/// <summary>The native generator state immediately before one primitive draw.</summary>
public readonly record struct LabelRandomState(ulong State0, ulong State1, ulong State2, ulong State3);

/// <summary>
/// Explicit label-only distribution departure: substitutes hypothetical random-tape words
/// while preserving native draw conversion, generator advancement, counters, and game order.
/// Ordinary sequential execution never enters this scope. Tape ownership, state-addressed
/// lookup, and replay belong to the caller; no hypothetical trace is stored globally here.
/// </summary>
public static class LabelRandomScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly AsyncLocal<bool> InsideCallback = new();

    /// <summary>
    /// Enter a temporary scope flowing across awaits. Dispose in nesting order within the
    /// entering execution context. Concurrent worlds must enter their own scopes and own
    /// their callback state; inherited child work shares the enclosing hypothetical tape.
    /// </summary>
    /// <param name="nextWord">
    /// Supplies a word for the full native pre-draw state. Equal states, including exact
    /// clones and recreated seeds, should address the same tape cell. The callback runs
    /// without interception so its own sampling RNG does not recursively consume the tape.
    /// </param>
    /// <param name="beginShuffle">
    /// Optional callback receiving the algorithm RNG and a pre-shuffle snapshot of the
    /// original object references. It may return a nested scope supplying forced words;
    /// the native Fisher-Yates loop still performs every draw and swap. This callback also
    /// runs without interception. Its returned scope is disposed after the native loop.
    /// </param>
    public static IDisposable Enter(
        Func<LabelRandomState, ulong> nextWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle = null)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var scope = new Scope(Current.Value, nextWord, beginShuffle);
        Current.Value = scope;
        return scope;
    }

    internal static ulong NextWordOrOriginal(LabelRandomState state, ulong original)
    {
        Scope? scope = Current.Value;
        if (scope is null || InsideCallback.Value) return original;

        InsideCallback.Value = true;
        try
        {
            return scope.NextWord(state);
        }
        finally
        {
            InsideCallback.Value = false;
        }
    }

    internal static IDisposable? BeginShuffle<T>(Rng rng, IList<T> list)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginShuffle is null || InsideCallback.Value) return null;

        var originalItems = new object?[list.Count];
        for (int i = 0; i < originalItems.Length; i++) originalItems[i] = list[i];
        InsideCallback.Value = true;
        try
        {
            return scope.BeginShuffle(rng, originalItems);
        }
        finally
        {
            InsideCallback.Value = false;
        }
    }

    private sealed class Scope(
        Scope? previous,
        Func<LabelRandomState, ulong> nextWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle) : IDisposable
    {
        public Scope? Previous { get; } = previous;
        public Func<LabelRandomState, ulong> NextWord { get; } = nextWord;
        public Func<Rng, IReadOnlyList<object?>, IDisposable?>? BeginShuffle { get; } = beginShuffle;

        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this))
            {
                Current.Value = Previous;
                return;
            }

            // Keep restoration local to this execution context, even when a scope has
            // flowed to a child task. Repeated disposal after removal is harmless.
            for (Scope? scope = Current.Value; scope is not null; scope = scope.Previous)
            {
                if (ReferenceEquals(scope, this))
                    throw new InvalidOperationException("Label random scopes must be disposed in nesting order.");
            }
        }
    }
}
