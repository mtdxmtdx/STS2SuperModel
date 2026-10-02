using Sts2Sim.Core.Content;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.Core.Random;

/// <summary>Actual hypothetical factory references, before the three native formation draws.</summary>
public sealed record LabelSlimesWeakFormationContext(RunState? Run, CombatRoom? Room,
    EncounterDefinition? Encounter, Rng? FactoryRng, Rng Rng);

/// <summary>
/// Optional label-only observation of Overgrowth's weak-slime factory. Its native
/// calls, including the one-item draw, remain unchanged. Event preparation outside
/// CombatRoom.Prepare carries no run/room certificate.
/// </summary>
public static class LabelSlimesWeakScope
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly AsyncLocal<Factory?> CurrentFactory = new();
    private static readonly AsyncLocal<Encounter?> CurrentEncounter = new();

    public static IDisposable Enter(Func<LabelSlimesWeakFormationContext, IDisposable?> begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        var scope = new Scope(Current.Value, begin);
        Current.Value = scope;
        return scope;
    }

    internal static IDisposable? EnterFactory(RunState run, CombatRoom room)
    {
        if (Current.Value is null) return null;
        var previous = CurrentFactory.Value;
        CurrentFactory.Value = new(run, room);
        return new Restore(() => CurrentFactory.Value = previous);
    }

    internal static IDisposable? EnterEncounter(EncounterDefinition definition, Rng? rng)
    {
        if (Current.Value is null) return null;
        var previous = CurrentEncounter.Value;
        CurrentEncounter.Value = new(definition, rng);
        return new Restore(() => CurrentEncounter.Value = previous);
    }

    internal static IDisposable? BeginFormation(Rng rng)
    {
        if (Current.Value is not { } scope) return null;
        return LabelRandomScope.InvokeResourceCallback(() =>
        {
            var factory = CurrentFactory.Value;
            var encounter = CurrentEncounter.Value;
            return scope.Begin(new(factory?.Run, factory?.Room, encounter?.Definition, encounter?.Rng, rng));
        });
    }

    private sealed record Factory(RunState Run, CombatRoom Room);
    private sealed record Encounter(EncounterDefinition Definition, Rng? Rng);
    private sealed class Restore(Action restore) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; restore(); }
    }
    private sealed class Scope(Scope? previous,
        Func<LabelSlimesWeakFormationContext, IDisposable?> begin) : IDisposable
    {
        internal Scope? Previous => previous;
        internal Func<LabelSlimesWeakFormationContext, IDisposable?> Begin => begin;
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) { Current.Value = previous; return; }
            for (var scope = Current.Value; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope, this)) throw new InvalidOperationException("Slime formation scopes must be disposed in nesting order");
        }
    }
}
