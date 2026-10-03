using Sts2Sim.Core.Content;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.Core.Random;

/// <summary>The original whole act event pool, immediately before its native shuffle.</summary>
public sealed class LabelEventGenerationContext(RunState run, ActDefinition act, int actIndex,
    Rng rng, IReadOnlyList<Type> pool)
{
    public RunState Run { get; } = run;
    public ActDefinition Act { get; } = act;
    public int ActIndex { get; } = actIndex;
    public Rng Rng { get; } = rng;
    public IReadOnlyList<Type> Pool { get; } = Array.AsReadOnly(pool.ToArray());
    /// <summary>Set only after the original native shuffle returns successfully.</summary>
    public IReadOnlyList<Type>? CompletedPermutation { get; internal set; }
}

/// <summary>
/// Optional label-only resource boundary. The caller may supply hypothetical tape
/// words; native pool order, every shuffle draw, and subsequent selection stay intact.
/// </summary>
public static class LabelEventGenerationScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly AsyncLocal<Generation?> CurrentGeneration = new();

    public static IDisposable Enter(Func<LabelEventGenerationContext, IDisposable?> begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        var scope = new Scope(Current.Value, begin); Current.Value = scope; return scope;
    }

    internal static IDisposable? Begin(RunState run, ActDefinition act, int actIndex, Rng rng, IReadOnlyList<Type> pool)
    {
        if (Current.Value is not { } scope) return null;
        var context = new LabelEventGenerationContext(run, act, actIndex, rng, pool);
        var resource = LabelRandomScope.InvokeResourceCallback(() => scope.Begin(context));
        var generation = new Generation(CurrentGeneration.Value, context, resource);
        CurrentGeneration.Value = generation; return generation;
    }

    internal static void Complete(IReadOnlyList<Type> permutation)
    {
        if (CurrentGeneration.Value is { } generation)
            generation.Context.CompletedPermutation = Array.AsReadOnly(permutation.ToArray());
    }

    private sealed class Generation(Generation? previous, LabelEventGenerationContext context,
        IDisposable? resource) : IDisposable
    {
        internal LabelEventGenerationContext Context => context;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try { resource?.Dispose(); }
            finally
            {
                if (!ReferenceEquals(CurrentGeneration.Value, this))
                    throw new InvalidOperationException("Event generation boundaries disposed out of order");
                CurrentGeneration.Value = previous;
            }
        }
    }

    private sealed class Scope(Scope? previous, Func<LabelEventGenerationContext, IDisposable?> begin) : IDisposable
    {
        internal Func<LabelEventGenerationContext, IDisposable?> Begin => begin;
        private Scope? Previous => previous;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Event generation scopes disposed out of order");
        }
    }
}
