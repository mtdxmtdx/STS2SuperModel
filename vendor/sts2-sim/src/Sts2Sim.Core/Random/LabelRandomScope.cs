using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Rooms;

namespace Sts2Sim.Core.Random;

/// <summary>The native generator state immediately before one primitive draw.</summary>
public readonly record struct LabelRandomState(ulong State0, ulong State1, ulong State2, ulong State3);

/// <summary>Native HP draw context; UsedHp is a sorted, distinct snapshot within the native range.</summary>
public sealed record LabelMonsterHpContext(Creature Creature, Rng Rng, int MinHp, int MaxHp,
    IReadOnlyList<int> UsedHp);

/// <summary>The native combat reward boundary before potion, gold, and card generation.</summary>
public sealed record LabelCombatRewardContext(Player Player, Rng Rng, RoomType RoomType,
    EncounterDefinition? Encounter, int? FixedGoldAmount, float GoldProportion);

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
    /// <param name="beginMonsterHp">
    /// Optional label-only callback immediately before the native unique monster HP draw.
    /// The callback runs without interception and may supply a nested word scope; native
    /// range selection, conversion, generator advancement and HP assignment stay unchanged.
    /// </param>
    /// <param name="beginCombatReward">
    /// Optional label-only scope around native combat reward generation. The callback
    /// runs without interception; native generation and draw order remain unchanged.
    /// </param>
    public static IDisposable Enter(
        Func<LabelRandomState, ulong> nextWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle = null,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp = null,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward = null)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var scope = new Scope(Current.Value, nextWord, beginShuffle, beginMonsterHp, beginCombatReward);
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

    internal static IDisposable? BeginMonsterHp(Creature creature, Rng rng, int min, int max,
        IReadOnlyCollection<int> usedHp)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginMonsterHp is null || InsideCallback.Value) return null;
        var context = new LabelMonsterHpContext(creature, rng, min, max,
            Array.AsReadOnly(usedHp.ToArray()));
        InsideCallback.Value = true;
        try { return scope.BeginMonsterHp(context); }
        finally { InsideCallback.Value = false; }
    }

    internal static IDisposable? BeginCombatReward(Player player, Rng rng, RoomType roomType,
        EncounterDefinition? encounter, int? fixedGoldAmount, float goldProportion)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginCombatReward is null || InsideCallback.Value) return null;
        var context = new LabelCombatRewardContext(player, rng, roomType, encounter, fixedGoldAmount, goldProportion);
        InsideCallback.Value = true;
        try { return scope.BeginCombatReward(context); }
        finally { InsideCallback.Value = false; }
    }

    private sealed class Scope(
        Scope? previous,
        Func<LabelRandomState, ulong> nextWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward) : IDisposable
    {
        public Scope? Previous { get; } = previous;
        public Func<LabelRandomState, ulong> NextWord { get; } = nextWord;
        public Func<Rng, IReadOnlyList<object?>, IDisposable?>? BeginShuffle { get; } = beginShuffle;
        public Func<LabelMonsterHpContext, IDisposable?>? BeginMonsterHp { get; } = beginMonsterHp;
        public Func<LabelCombatRewardContext, IDisposable?>? BeginCombatReward { get; } = beginCombatReward;

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
