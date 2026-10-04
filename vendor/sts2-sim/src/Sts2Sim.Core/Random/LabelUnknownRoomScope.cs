using Sts2Sim.Core.Map;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.Core.Random;

/// <summary>The owning map point immediately before its original native unknown-room roll.</summary>
public sealed class LabelUnknownRoomContext(RunState run, MapPoint point, Rng rng, bool excludeShop)
{
    public RunState Run { get; } = run;
    public MapPoint Point { get; } = point;
    public Rng Rng { get; } = rng;
    public bool ExcludeShop { get; } = excludeShop;
    /// <summary>Set only after the native roll and every odds update return successfully.</summary>
    public RoomType? CompletedRoomType { get; internal set; }
}

/// <summary>
/// Optional label boundary around RoomFactory's unknown case. It does not replace
/// the roll, blacklist, hook dispatch, RNG advancement, or native odds update.
/// </summary>
public static class LabelUnknownRoomScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static IDisposable Enter(Func<LabelUnknownRoomContext, IDisposable?> begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        var scope = new Scope(Current.Value, begin); Current.Value = scope; return scope;
    }

    internal static IDisposable? Begin(LabelUnknownRoomContext context) => Current.Value is { } scope
        ? LabelRandomScope.InvokeResourceCallback(() => scope.Begin(context)) : null;

    private sealed class Scope(Scope? previous, Func<LabelUnknownRoomContext, IDisposable?> begin) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelUnknownRoomContext, IDisposable?> Begin => begin;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this))
                    throw new InvalidOperationException("Unknown-room scopes must be disposed in nesting order");
        }
    }
}
