using System.Collections.ObjectModel;
using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeInitialHpTarget(string Id, int MinHp, int MaxHp, int TargetHp,
    ulong RootMaxBucketSize);

/// <summary>
/// An optional public-only initial-HP certificate. Ineligibility changes only acceleration,
/// never the underlying tape prior or content support. The existing shuffle certificate
/// supplies the source-pinned startup closure; unchanged lifetime MaxHp also permits later roots.
/// </summary>
internal sealed class NativeInitialHpCondition
{
    // Source-pinned A10 MinInitialHp/MaxInitialHp values in Models/Monsters/<Id>.cs.
    // Every getter below depends only on constants and ToughEnemies (active at A10),
    // never act, encounter, prior history or hidden monster state. The startup certificate
    // additionally checks these sealed types' move setup and all startup hooks.
    private static readonly IReadOnlyDictionary<string, (int Min, int Max)> A10Ranges =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["AssassinRubyRaider"] = (19, 24), ["AxeRubyRaider"] = (21, 23),
            ["BowlbugEgg"] = (23, 24), ["BowlbugNectar"] = (36, 39), ["BowlbugSilk"] = (41, 44),
            ["BruteRubyRaider"] = (31, 34), ["CalcifiedCultist"] = (39, 42),
            ["CeremonialBeast"] = (262, 262), ["CrossbowRubyRaider"] = (19, 22),
            ["DampCultist"] = (52, 54), ["DevotedSculptor"] = (172, 172),
            ["FakeMerchantMonster"] = (175, 175), ["FatGremlin"] = (14, 18), ["Fogmog"] = (78, 78),
            ["Fabricator"] = (155, 155), ["Flyconid"] = (51, 53),
            ["FuzzyWurmCrawler"] = (58, 59), ["Guardbot"] = (17, 21), ["HauntedShip"] = (67, 67),
            ["HunterKiller"] = (126, 126), ["KinPriest"] = (199, 199), ["KnowledgeDemon"] = (399, 399),
            ["LeafSlimeM"] = (33, 36), ["LeafSlimeS"] = (12, 16),
            ["LivingFog"] = (82, 82), ["MagiKnight"] = (89, 89), ["Nibbit"] = (44, 48),
            ["Mawler"] = (76, 76), ["Noisebot"] = (19, 24), ["Ovicopter"] = (126, 132),
            ["OwlMagistrate"] = (247, 247), ["Seapunk"] = (47, 49), ["ShrinkerBeetle"] = (40, 42),
            ["SlimedBerserker"] = (281, 281), ["SlitheringStrangler"] = (54, 56),
            ["SludgeSpinner"] = (41, 42), ["SnappingJaxfruit"] = (34, 36), ["SneakyGremlin"] = (11, 15),
            ["SoulFysh"] = (221, 221), ["SoulNexus"] = (254, 254), ["SpectralKnight"] = (97, 97),
            ["SpinyToad"] = (121, 124), ["Stabbot"] = (19, 24), ["TheInsatiable"] = (341, 341),
            ["TheObscura"] = (129, 129), ["TrackerRubyRaider"] = (22, 26), ["Tunneler"] = (92, 92),
            ["TurretOperator"] = (51, 51), ["TwigSlimeM"] = (27, 29), ["TwigSlimeS"] = (8, 12),
            ["VineShambler"] = (64, 64), ["Toadpole"] = (22, 26),
            ["TwoTailedRat"] = (18, 22), ["Wriggler"] = (18, 22),
        };

    internal IReadOnlyDictionary<string, NativeInitialHpTarget> Targets { get; }
    internal static IEnumerable<(string Id, int Min, int Max)> CertifiedRanges =>
        A10Ranges.Select(pair => (pair.Key, pair.Value.Min, pair.Value.Max));
    internal int EnemyCount => Targets.Count;
    internal ShuffleRational Envelope { get; }

    private NativeInitialHpCondition(PublicEnemy[] enemies)
    {
        var targets = new Dictionary<string, NativeInitialHpTarget>(StringComparer.Ordinal);
        var envelope = new ShuffleRational(1, 1);
        foreach (var enemy in enemies)
        {
            var range = A10Ranges[enemy.Id];
            ulong maximum = NativeHpProposal.RootEnvelopeBucket(range.Min, range.Max,
                enemies.Where(other => other.Id != enemy.Id).Select(other => other.MaxHp));
            targets.Add(enemy.Id, new(enemy.Id, range.Min, range.Max, enemy.MaxHp, maximum));
            envelope = envelope.Multiply(maximum, 1UL << ConditionalShuffleProposal.NativePrecisionBits);
        }
        Targets = new ReadOnlyDictionary<string, NativeInitialHpTarget>(targets);
        Envelope = envelope;
    }

    internal static bool TryCreate(DecisionPacket root, out NativeInitialHpCondition? condition,
        out string? reason) => TryCreate(root, false, out condition, out reason);

    internal static bool TryCreatePublicCombatV3(DecisionPacket root, out NativeInitialHpCondition? condition,
        out string? reason) => TryCreate(root, true, out condition, out reason);

    private static bool TryCreate(DecisionPacket root, bool publicCombatV3, out NativeInitialHpCondition? condition,
        out string? reason)
    {
        condition = null;
        // Source-pinned lifetime invariant: none of the 54 sealed types above changes its
        // MaxHp after the native initial roll. The only enemy writers are TestSubject,
        // ToughEgg, DecimillipedeSegment and WaterfallGiant, all outside this certificate.
        // Generic max-HP commands are otherwise called on players or typed Osty; FruitJuice
        // validates an AnyPlayer target and PaperCutsPower explicitly requires target.IsPlayer.
        // CombatState.CloneCreature copies MaxHp unchanged. Creature's model never changes,
        // and PublicKnowledge's lifetime slots persist across removal/summoning. Therefore
        // an original slot/type still present at a later stable or pending-choice root has
        // initial HP equal to its current MaxHp, regardless of damage, block or powers.
        bool startupCertified = publicCombatV3
            ? NativeInitialShuffleCondition.TryCreatePublicCombatV3(root, out _, out reason)
            : NativeInitialShuffleCondition.TryCreate(root, out _, out reason);
        if (!startupCertified) return false;
        var observation = root.Observation!;
        int index = 2;
        if (publicCombatV3)
            while (index < observation.History.Length && observation.History[index].Kind == "power_changed") index++;
        while (index < observation.History.Length && observation.History[index].Kind == "draw") index++;
        index++; // The shuffle certificate has proved this is player_turn 1.
        try
        {
            var intents = observation.History.Skip(index)
                .TakeWhile(item => item.Kind == "intent_published")
                .Select(item => PublicJson.Read<StartupIntent>(item.Detail)).ToArray();
            var enemies = observation.Enemies;
            if (enemies.Length != intents.Length
                || intents.Select(enemy => enemy.Id).Distinct(StringComparer.Ordinal).Count() != intents.Length
                || enemies.Select(enemy => enemy.Id).Distinct(StringComparer.Ordinal).Count() != enemies.Length
                || enemies.Select(enemy => enemy.Slot).Distinct().Count() != enemies.Length)
            { reason = "initial_hp_unique_startup_roster_required"; return false; }
            var originalBySlot = intents.ToDictionary(enemy => enemy.Slot, enemy => enemy.Id);
            if (enemies.Any(enemy => !originalBySlot.TryGetValue(enemy.Slot, out string? originalId)
                || originalId != enemy.Id))
            { reason = "initial_hp_unique_startup_roster_required"; return false; }
            foreach (var enemy in enemies)
                if (!A10Ranges.TryGetValue(enemy.Id, out var range)
                    || enemy.MaxHp < range.Min || enemy.MaxHp > range.Max)
                { reason = "initial_hp_certified_range_required"; return false; }
            condition = new(enemies);
            reason = null;
            return true;
        }
        catch (JsonException) { reason = "invalid_public_startup_intent"; return false; }
        catch (ArgumentException) { reason = "invalid_public_startup_intent"; return false; }
    }

    /// <summary>
    /// Called only for the target combat's native initial HP setup. Runtime checks protect
    /// the source-pinned certificate; unexpected context is an engine/certificate error,
    /// never an ordinary rejected proposal. Public roster/HP contradictions are nonmatches.
    /// </summary>
    internal NativeHpProposal CreateProposal(LabelMonsterHpContext context,
        IReadOnlyCollection<string> previouslyConditionedIds, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(previouslyConditionedIds);
        var creature = context.Creature;
        string id = creature.Monster?.GetType().Name
            ?? throw new InvalidOperationException("HP conditioning did not receive a native monster");
        if (!Targets.TryGetValue(id, out var target) || previouslyConditionedIds.Contains(id))
            throw new NativePublicConstraintMismatchException("Native initial monster roster differs from the public opening");
        if (context.MinHp != target.MinHp || context.MaxHp != target.MaxHp
            || creature.CombatState is null
            || !creature.CombatState.RunState.Ascension.HasLevel(AscensionLevel.DoubleBoss))
            throw new InvalidOperationException("Native HP range/context differs from its source-pinned A10 certificate");
        var earlier = creature.CombatState.Enemies;
        if (earlier.Count != previouslyConditionedIds.Count
            || previouslyConditionedIds.Distinct(StringComparer.Ordinal).Count() != previouslyConditionedIds.Count
            || earlier.Select(prior => prior.Monster?.GetType().Name).Distinct(StringComparer.Ordinal).Count() != earlier.Count)
            throw new InvalidOperationException("Native HP setup differs from the conditioned initial roster");
        foreach (var prior in earlier)
        {
            string? priorId = prior.Monster?.GetType().Name;
            if (priorId is null || !previouslyConditionedIds.Contains(priorId)
                || !Targets.TryGetValue(priorId, out var priorTarget)
                || prior.CurrentHp != priorTarget.TargetHp || prior.MaxHp != priorTarget.TargetHp)
                throw new InvalidOperationException("Native HP setup changed an earlier conditioned monster");
        }
        var expectedUsed = earlier.Where(prior => prior.MaxHp >= target.MinHp && prior.MaxHp <= target.MaxHp)
            .Select(prior => prior.MaxHp).Distinct().Order();
        if (!context.UsedHp.SequenceEqual(expectedUsed))
            throw new InvalidOperationException("Native unique HP context differs from its initial roster");
        return NativeHpProposal.Create(target.MinHp, target.MaxHp, target.TargetHp, context.UsedHp,
            target.RootMaxBucketSize, nextWord)
            ?? throw new NativePublicConstraintMismatchException("Public initial HP has no support in this native creation order");
    }

    private sealed record StartupIntent(int Slot, string Id);
}
