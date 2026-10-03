using Sts2Sim.Core.Map;

namespace Sts2Sim.Core.Random;

/// <summary>
/// Explicit label-only constructor seam. Returning null retains ordinary native
/// generation. A replacement is built before native map hooks, at the same point
/// as ordinary construction; it must not run generation or mutate a live map.
/// The caller owns eligibility, independent-law proof, and run-boundary guards.
/// </summary>
public static class LabelMapConstructionScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static IDisposable Enter(Func<LabelMapGenerationContext, StandardActMap?> construct)
    {
        ArgumentNullException.ThrowIfNull(construct);
        var scope = new Scope(Current.Value, construct);
        Current.Value = scope;
        return scope;
    }

    internal static StandardActMap? TryConstruct(LabelMapGenerationContext context) => Current.Value?.Construct(context);

    private sealed class Scope(Scope? previous, Func<LabelMapGenerationContext, StandardActMap?> construct) : IDisposable
    {
        internal Scope? Previous { get; } = previous;
        internal StandardActMap? Construct(LabelMapGenerationContext context) => construct(context);
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = Previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this))
                    throw new InvalidOperationException("Label map construction scopes must be disposed in nesting order");
        }
    }
}
