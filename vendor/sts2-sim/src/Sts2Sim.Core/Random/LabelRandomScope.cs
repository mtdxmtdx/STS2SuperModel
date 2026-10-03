using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Events;

namespace Sts2Sim.Core.Random;

/// <summary>The native generator state immediately before one primitive draw.</summary>
public readonly record struct LabelRandomState(ulong State0, ulong State1, ulong State2, ulong State3);

/// <summary>Native HP draw context; UsedHp is a sorted, distinct snapshot within the native range.</summary>
public sealed record LabelMonsterHpContext(Creature Creature, Rng Rng, int MinHp, int MaxHp,
    IReadOnlyList<int> UsedHp);

/// <summary>The native combat reward boundary before potion, gold, and card generation.</summary>
public sealed record LabelCombatRewardContext(Player Player, Rng Rng, RoomType RoomType,
    EncounterDefinition? Encounter, int? FixedGoldAmount, float GoldProportion);

/// <summary>The fresh act-zero normal encounter generation boundary.</summary>
public sealed record LabelNormalEncounterContext(RunState Run, ActDefinition Act, Rng Rng);

/// <summary>The native map boundary before randomized point counts are generated.</summary>
public sealed record LabelMapGenerationContext(RunState Run, ActDefinition Act, Rng Rng);

/// <summary>The native Neow opening boundary before its curse and extra-pair coin draws.</summary>
public sealed record LabelNeowInitialOptionsContext(Neow Neow, Rng Rng, IReadOnlyList<Type> AllowedCurses);

/// <summary>Actual post-modifier ScrollBoxes pools, before its native bundle draws.</summary>
public sealed record LabelScrollBoxesContext(Player Player, Rng Rng,
    IReadOnlyList<CardModel> Commons, IReadOnlyList<CardModel> Uncommons);

/// <summary>Actual strict float comparison thresholds used by native rarity selection.</summary>
public readonly record struct LabelCardRarityThresholds(float RareUpperExclusive, float UncommonUpperExclusive);

public enum LabelRewardCardSelectionKind { LegacyCombat, CreationOptions }

/// <summary>One native hidden rolled-rarity arm; candidates retain native order and duplicate entries.</summary>
public sealed record LabelRewardCardBranch(CardRarity? RolledRarity, IReadOnlyList<CardModel> Candidates);

/// <summary>
/// Label-only snapshot immediately before native rarity/index draws, after creation modifiers.
/// Null thresholds mean the native uniform branch has no rarity draw. Branches are computed
/// by CardFactory using that overload's own fallback and exclusion policy; hooks run once.
/// </summary>
public sealed record LabelRewardCardSelectionContext(Player Player, Rng Rng,
    LabelRewardCardSelectionKind Kind, int SelectionIndex, int OptionCount,
    CardCreationSource Source, CardCreationFlags Flags, CardRarityOddsType OddsType,
    LabelCardRarityThresholds? Thresholds, bool ChangesFutureOdds,
    IReadOnlyList<CardModel> RemainingCards, IReadOnlyList<LabelRewardCardBranch> Branches);

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

    // Legacy nested forcing scopes keep their enclosing provenance construction mode.
    internal static bool UsesRewardProvenance => Current.Value?.UsesProvenance == true && !InsideCallback.Value;
    internal static bool UsesMapProvenance => Current.Value?.UsesMapProvenance == true && !InsideCallback.Value;
    internal static string? ProvenanceLaw => !UsesRewardProvenance ? null
        : UsesMapProvenance ? LabelRandomProvenance.MapLawId : LabelRandomProvenance.LawId;

    private static void RequireNestedHybridLaw(bool usesMap)
    {
        // Generic Enter is a forcing override and inherits its enclosing law.
        // The explicit hybrid entry points may nest only within the same law.
        if (Current.Value is { UsesProvenance: true } previous && previous.UsesMapProvenance != usesMap)
            throw new InvalidOperationException("Nested hybrid RNG scopes must use the same declared primitive law.");
    }

    internal static IDisposable? InvokeResourceCallback(Func<IDisposable?> callback)
    {
        if (InsideCallback.Value) return null;
        InsideCallback.Value = true;
        try { return callback(); }
        finally { InsideCallback.Value = false; }
    }

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
    /// <param name="beginNormalEncounter">
    /// Optional label-only scope before the first act's native normal encounter loop.
    /// The callback runs without interception; native bag selection remains unchanged.
    /// </param>
    /// <param name="beginMapGeneration">
    /// Optional label-only scope around native map generation, starting before
    /// randomized point counts. Initial construction runs before players are added.
    /// </param>
    /// <param name="beginNeowInitialOptions">
    /// Optional label-only scope around Neow's native curse and extra-pair draws.
    /// Receives the actual allowed curse order and RNG; native rules are unchanged.
    /// </param>
    public static IDisposable Enter(
        Func<LabelRandomState, ulong> nextWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle = null,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp = null,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward = null,
        Func<LabelNormalEncounterContext, IDisposable?>? beginNormalEncounter = null,
        Func<LabelMapGenerationContext, IDisposable?>? beginMapGeneration = null,
        Func<LabelRewardCardSelectionContext, IDisposable?>? beginRewardCardSelection = null,
        Func<LabelNeowInitialOptionsContext, IDisposable?>? beginNeowInitialOptions = null,
        Func<LabelScrollBoxesContext, IDisposable?>? beginScrollBoxes = null)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var scope = new Scope(Current.Value, nextWord, null, beginShuffle, beginMonsterHp, beginCombatReward, beginNormalEncounter, beginMapGeneration, beginRewardCardSelection, beginNeowInitialOptions, beginScrollBoxes);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Enter the hybrid law: tagged player Rewards use origin/seed/raw-cursor cells;
    /// all untagged RNGs retain the full-state law, independent of the Rewards partition.
    /// Construct owned native worlds inside this scope. Missing source-partition markers
    /// at restore boundaries fail closed. Callers own oracle consistency and the
    /// independence of both partitions. Native conversion and advancement are unchanged.
    /// </summary>
    public static IDisposable EnterRewardProvenance(
        Func<LabelRandomAddressV1, ulong> nextRewardWord,
        Func<LabelRandomState, ulong> nextStateWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle = null,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp = null,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward = null,
        Func<LabelNormalEncounterContext, IDisposable?>? beginNormalEncounter = null,
        Func<LabelMapGenerationContext, IDisposable?>? beginMapGeneration = null,
        Func<LabelRewardCardSelectionContext, IDisposable?>? beginRewardCardSelection = null,
        Func<LabelNeowInitialOptionsContext, IDisposable?>? beginNeowInitialOptions = null,
        Func<LabelScrollBoxesContext, IDisposable?>? beginScrollBoxes = null)
    {
        ArgumentNullException.ThrowIfNull(nextRewardWord);
        ArgumentNullException.ThrowIfNull(nextStateWord);
        RequireNestedHybridLaw(usesMap: false);
        var scope = new Scope(Current.Value, nextStateWord, nextRewardWord, beginShuffle, beginMonsterHp, beginCombatReward,
            beginNormalEncounter, beginMapGeneration, beginRewardCardSelection, beginNeowInitialOptions, beginScrollBoxes);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Enter the separately versioned three-partition law. Only StandardActMap's
    /// native source receives Map lineage; incidental draws made during generation
    /// retain their own partition. The Rewards and full-state partitions are unchanged.
    /// </summary>
    public static IDisposable EnterMapRewardProvenance(
        Func<LabelMapRandomAddressV1, ulong> nextMapWord,
        Func<LabelRandomAddressV1, ulong> nextRewardWord,
        Func<LabelRandomState, ulong> nextStateWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle = null,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp = null,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward = null,
        Func<LabelNormalEncounterContext, IDisposable?>? beginNormalEncounter = null,
        Func<LabelMapGenerationContext, IDisposable?>? beginMapGeneration = null,
        Func<LabelRewardCardSelectionContext, IDisposable?>? beginRewardCardSelection = null,
        Func<LabelNeowInitialOptionsContext, IDisposable?>? beginNeowInitialOptions = null,
        Func<LabelScrollBoxesContext, IDisposable?>? beginScrollBoxes = null)
    {
        ArgumentNullException.ThrowIfNull(nextMapWord);
        ArgumentNullException.ThrowIfNull(nextRewardWord);
        ArgumentNullException.ThrowIfNull(nextStateWord);
        RequireNestedHybridLaw(usesMap: true);
        var scope = new Scope(Current.Value, nextStateWord, nextRewardWord, beginShuffle, beginMonsterHp, beginCombatReward,
            beginNormalEncounter, beginMapGeneration, beginRewardCardSelection, beginNeowInitialOptions,
            beginScrollBoxes, nextMapWord);
        Current.Value = scope;
        return scope;
    }

    internal static ulong NextWordOrOriginal(LabelRandomState state, LabelRandomAddressV1? address,
        LabelMapRandomAddressV1? mapAddress, ulong original)
    {
        Scope? scope = Current.Value;
        if (scope is null || InsideCallback.Value) return original;

        InsideCallback.Value = true;
        try
        {
            if (scope.NextMapWord is not null && mapAddress is { } sourceMapAddress)
                return scope.NextMapWord(sourceMapAddress);
            if (scope.NextProvenanceWord is not null && address is { } rewardAddress)
                return scope.NextProvenanceWord(rewardAddress);
            return scope.NextWord!(state);
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

    internal static IDisposable? BeginNormalEncounter(RunState run, ActDefinition act)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginNormalEncounter is null || InsideCallback.Value) return null;
        // Resolve the existing native stream only for an active label callback.
        var context = new LabelNormalEncounterContext(run, act, run.Rng.UpFront);
        InsideCallback.Value = true;
        try { return scope.BeginNormalEncounter(context); }
        finally { InsideCallback.Value = false; }
    }

    internal static IDisposable? BeginMapGeneration(RunState run, ActDefinition act, Rng rng)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginMapGeneration is null || InsideCallback.Value) return null;
        var context = new LabelMapGenerationContext(run, act, rng);
        InsideCallback.Value = true;
        try { return scope.BeginMapGeneration(context); }
        finally { InsideCallback.Value = false; }
    }

    internal static IDisposable? BeginNeowInitialOptions(Neow neow, Rng rng, IReadOnlyList<Type> allowedCurses)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginNeowInitialOptions is null || InsideCallback.Value) return null;
        var context = new LabelNeowInitialOptionsContext(neow, rng, Array.AsReadOnly(allowedCurses.ToArray()));
        InsideCallback.Value = true;
        try { return scope.BeginNeowInitialOptions(context); }
        finally { InsideCallback.Value = false; }
    }

    internal static IDisposable? BeginScrollBoxes(Player player, Rng rng,
        IReadOnlyList<CardModel> commons, IReadOnlyList<CardModel> uncommons)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginScrollBoxes is null || InsideCallback.Value) return null;
        var context = new LabelScrollBoxesContext(player, rng,
            Array.AsReadOnly(commons.ToArray()), Array.AsReadOnly(uncommons.ToArray()));
        InsideCallback.Value = true;
        try { return scope.BeginScrollBoxes(context); }
        finally { InsideCallback.Value = false; }
    }

    internal static bool HasRewardCardSelection => Current.Value?.BeginRewardCardSelection is not null && !InsideCallback.Value;

    internal static IDisposable? BeginRewardCardSelection(LabelRewardCardSelectionContext context)
    {
        Scope? scope = Current.Value;
        if (scope?.BeginRewardCardSelection is null || InsideCallback.Value) return null;
        InsideCallback.Value = true;
        try { return scope.BeginRewardCardSelection(context); }
        finally { InsideCallback.Value = false; }
    }

    private sealed class Scope(
        Scope? previous,
        Func<LabelRandomState, ulong>? nextWord,
        Func<LabelRandomAddressV1, ulong>? nextProvenanceWord,
        Func<Rng, IReadOnlyList<object?>, IDisposable?>? beginShuffle,
        Func<LabelMonsterHpContext, IDisposable?>? beginMonsterHp,
        Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward,
        Func<LabelNormalEncounterContext, IDisposable?>? beginNormalEncounter,
        Func<LabelMapGenerationContext, IDisposable?>? beginMapGeneration,
        Func<LabelRewardCardSelectionContext, IDisposable?>? beginRewardCardSelection,
        Func<LabelNeowInitialOptionsContext, IDisposable?>? beginNeowInitialOptions,
        Func<LabelScrollBoxesContext, IDisposable?>? beginScrollBoxes,
        Func<LabelMapRandomAddressV1, ulong>? nextMapWord = null) : IDisposable
    {
        public Scope? Previous { get; } = previous;
        public Func<LabelRandomState, ulong>? NextWord { get; } = nextWord;
        public Func<LabelRandomAddressV1, ulong>? NextProvenanceWord { get; } = nextProvenanceWord;
        public Func<LabelMapRandomAddressV1, ulong>? NextMapWord { get; } = nextMapWord;
        public bool UsesProvenance { get; } = nextProvenanceWord is not null || previous?.UsesProvenance == true;
        public bool UsesMapProvenance { get; } = nextMapWord is not null || previous?.UsesMapProvenance == true;
        public Func<Rng, IReadOnlyList<object?>, IDisposable?>? BeginShuffle { get; } = beginShuffle;
        public Func<LabelMonsterHpContext, IDisposable?>? BeginMonsterHp { get; } = beginMonsterHp;
        public Func<LabelCombatRewardContext, IDisposable?>? BeginCombatReward { get; } = beginCombatReward;
        public Func<LabelNormalEncounterContext, IDisposable?>? BeginNormalEncounter { get; } = beginNormalEncounter;
        public Func<LabelMapGenerationContext, IDisposable?>? BeginMapGeneration { get; } = beginMapGeneration;

        public Func<LabelRewardCardSelectionContext, IDisposable?>? BeginRewardCardSelection { get; } = beginRewardCardSelection;

        public Func<LabelNeowInitialOptionsContext, IDisposable?>? BeginNeowInitialOptions { get; } = beginNeowInitialOptions;

        public Func<LabelScrollBoxesContext, IDisposable?>? BeginScrollBoxes { get; } = beginScrollBoxes;

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
