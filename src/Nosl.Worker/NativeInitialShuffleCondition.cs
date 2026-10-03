using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Models.Potions;

namespace Nosl.Worker;

/// <summary>
/// Public-only acceleration certificate for the separately declared ideal primitive-tape prior.
/// Failure means use its ordinary native replay proposal, never exclude this root or its content.
/// This does not certify a conditional distribution under the game's finite run-seed prior.
/// </summary>
internal sealed class NativeInitialShuffleCondition
{
    private readonly string[] _deckIds;
    private readonly string[] _drawPrefixIds;
    private readonly Dictionary<string, int> _deckCounts;
    private readonly NativePublicDrawKey[] _deckKeys, _drawPrefixKeys;

    internal string EntryJson { get; }
    internal string[] DeckIds => _deckIds.ToArray();
    internal string[] DrawPrefixIds => _drawPrefixIds.ToArray();
    internal NativePublicDrawKey[] DeckKeys => _deckKeys.ToArray();
    internal NativePublicDrawKey[] DrawPrefixKeys => _drawPrefixKeys.ToArray();
    internal int DeckCount => _deckIds.Length;
    internal double PrefixProbability { get; }

    // Fixed-source startup closure, reviewed in Models/Monsters/<Type>.cs:
    // The first 47 types construct graphs with an ordinary MoveState as initial state. The final
    // seven use only sealed ConditionalBranchState/RandomBranchState before a MoveState: predicates
    // read spawn flags/slots or enemy counts, and weights are constants/read-only CanSummon queries.
    // Their OnEnterState/OnExitState are no-ops. Selection changes only AI state/log/RNG; it never
    // invokes a move callback. Constructors/registration likewise never execute callbacks.
    // Numeric intent values are constants or read-only ascension calculations. None of these
    // types overrides AfterAddedToRoom or any AbstractModel hook (also guarded at runtime below).
    // Native pre-first-turn startup preserves monster identity/roster: the other startup hooks
    // apply powers/HP/block/internal state, and even ToughEgg.Hatch retains its model identity.
    // Thus an unreviewed original monster cannot disappear into this published startup roster.
    // This is source-pinned acceleration scope, not proof about arbitrary future game content.
    private static readonly HashSet<Type> ReviewedStartupMonsters =
    [
        typeof(AssassinRubyRaider), typeof(AxeRubyRaider), typeof(BowlbugEgg),
        typeof(BowlbugNectar), typeof(BowlbugSilk), typeof(BruteRubyRaider),
        typeof(CalcifiedCultist), typeof(CeremonialBeast), typeof(CrossbowRubyRaider),
        typeof(DampCultist), typeof(DevotedSculptor), typeof(FakeMerchantMonster),
        typeof(FatGremlin), typeof(Fogmog), typeof(FuzzyWurmCrawler),
        typeof(Guardbot), typeof(HauntedShip), typeof(HunterKiller),
        typeof(KinPriest), typeof(KnowledgeDemon), typeof(LeafSlimeM),
        typeof(LivingFog), typeof(MagiKnight), typeof(Mawler),
        typeof(Noisebot), typeof(Ovicopter), typeof(OwlMagistrate),
        typeof(Seapunk), typeof(ShrinkerBeetle), typeof(SlimedBerserker),
        typeof(SlitheringStrangler), typeof(SludgeSpinner), typeof(SnappingJaxfruit),
        typeof(SneakyGremlin), typeof(SoulFysh), typeof(SoulNexus),
        typeof(SpectralKnight), typeof(SpinyToad), typeof(Stabbot),
        typeof(TheInsatiable), typeof(TheObscura), typeof(TrackerRubyRaider),
        typeof(Tunneler), typeof(TurretOperator), typeof(TwigSlimeM),
        typeof(TwigSlimeS), typeof(VineShambler),
        // Pure initial branch closures: see their GenerateMoveStateMachine methods plus
        // ConditionalBranchState.GetNextState and RandomBranchState.GetStateWeight/GetNextState.
        typeof(Fabricator), typeof(Flyconid), typeof(LeafSlimeS), typeof(Nibbit),
        typeof(Toadpole), typeof(TwoTailedRat), typeof(Wriggler),
    ];

    // Complete sealed implementations reviewed in Models/Relics/<Type>.cs. These hooks only
    // change a requested hand-draw count, energy, or their own counters, never physical card order.
    private static readonly Dictionary<Type, HashSet<string>> ReviewedEntryHooks = new()
    {
        [typeof(RingOfTheSnake)] = [nameof(AbstractModel.ModifyHandDraw)],
        [typeof(WingedBoots)] = [nameof(AbstractModel.AfterRoomEntered), nameof(AbstractModel.ShouldAllowFreeTravel)],
        [typeof(BoomingConch)] = [nameof(AbstractModel.ModifyHandDraw), nameof(AbstractModel.AfterSideTurnStart)],
        [typeof(BagOfPreparation)] = [nameof(AbstractModel.ModifyHandDraw)],
        [typeof(PaelsBlood)] = [nameof(AbstractModel.ModifyHandDraw)],
        [typeof(BigMushroom)] = [nameof(AbstractModel.ModifyHandDraw), nameof(AbstractModel.AfterRoomEntered)],
        [typeof(Fiddle)] = [nameof(AbstractModel.ModifyHandDraw), nameof(AbstractModel.ShouldDraw)],
        [typeof(PollinousCore)] = [nameof(AbstractModel.ModifyHandDraw), nameof(AbstractModel.BeforeHandDraw),
            nameof(AbstractModel.AfterModifyingHandDraw)],
        [typeof(Pocketwatch)] = [nameof(AbstractModel.ModifyHandDraw), nameof(AbstractModel.BeforeSideTurnStart),
            nameof(AbstractModel.AfterSideTurnStart), nameof(AbstractModel.AfterCardPlayed)],
    };

    private static readonly Dictionary<string, Type[]> Models = ContentRegistry.AllTypes
        .GroupBy(type => type.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private static readonly MethodInfo[] AbstractHooks = typeof(AbstractModel)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.IsVirtual && !method.IsSpecialName
            && method.Name is not (nameof(AbstractModel.CompareTo) or nameof(AbstractModel.ToString)))
        .ToArray();

    private NativeInitialShuffleCondition(string entryJson, NativePublicDrawKey[] deckKeys, NativePublicDrawKey[] prefixKeys)
    {
        EntryJson = entryJson;
        _deckKeys = deckKeys.ToArray(); _drawPrefixKeys = prefixKeys.ToArray();
        _deckIds = deckKeys.Select(card => card.Id).ToArray();
        _drawPrefixIds = prefixKeys.Select(card => card.Id).ToArray();
        _deckCounts = Counts(_deckIds);
        var remaining = new Dictionary<string, int>(_deckCounts, StringComparer.Ordinal);
        double probability = 1;
        for (int i = 0; i < _drawPrefixIds.Length; i++)
            probability *= remaining[_drawPrefixIds[i]]-- / (double)(_deckIds.Length - i);
        PrefixProbability = probability;
    }

    /// <summary>Extend only after a public transition certificate proves these are successive initial-pile draws.</summary>
    internal NativeInitialShuffleCondition ExtendDrawPrefix(IReadOnlyList<NativePublicDrawKey> prefix)
    {
        if (prefix.Count < _drawPrefixKeys.Length || !prefix.Take(_drawPrefixKeys.Length).SequenceEqual(_drawPrefixKeys))
            throw new ArgumentException("An extended shuffle certificate must preserve its startup prefix", nameof(prefix));
        var counts = _deckKeys.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var key in prefix)
            if (!counts.TryGetValue(key, out int remaining) || remaining == 0)
                throw new ArgumentException("An extended shuffle prefix exceeds its public entry pool", nameof(prefix));
            else counts[key]--;
        return new(EntryJson, _deckKeys, prefix.ToArray());
    }

    internal bool MatchesEntryJson(string entryJson) => StringComparer.Ordinal.Equals(EntryJson, entryJson);

    /// <summary>The caller must check this before conditioning the actual native shuffle.</summary>
    internal bool MatchesInitialPool(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var actual = Counts(ids);
        return actual.Count == _deckCounts.Count
            && actual.All(pair => _deckCounts.TryGetValue(pair.Key, out int count) && count == pair.Value);
    }

    internal bool MatchesPublicInitialPool(IEnumerable<NativePublicDrawKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var actual = keys.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        var expected = _deckKeys.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        return actual.Count == expected.Count
            && actual.All(pair => expected.TryGetValue(pair.Key, out int count) && count == pair.Value);
    }

    internal static bool TryCreate(DecisionPacket publicRoot,
        out NativeInitialShuffleCondition? condition, out string? reason)
        => TryCreate(publicRoot, 1, out condition, out reason);

    // Explicit acceleration-profile opt-in. Legacy tape/corpus callers retain the v1 closure.
    internal static bool TryCreatePublicCombatV2(DecisionPacket publicRoot,
        out NativeInitialShuffleCondition? condition, out string? reason)
        => TryCreate(publicRoot, 2, out condition, out reason);

    internal static bool TryCreatePublicCombatV3(DecisionPacket publicRoot,
        out NativeInitialShuffleCondition? condition, out string? reason)
        => TryCreate(publicRoot, 3, out condition, out reason);

    internal static bool TryCreatePublicCombatV4(DecisionPacket publicRoot,
        out NativeInitialShuffleCondition? condition, out string? reason)
        => TryCreate(publicRoot, 4, out condition, out reason);

    internal static bool TryCreatePublicCombatV5(DecisionPacket publicRoot,
        out NativeInitialShuffleCondition? condition, out string? reason)
        => TryCreate(publicRoot, 5, out condition, out reason);

    private static bool TryCreate(DecisionPacket publicRoot, int publicCombatVersion,
        out NativeInitialShuffleCondition? condition, out string? reason)
    {
        condition = null;
        reason = null;
        try
        {
            condition = Create(publicRoot, publicCombatVersion);
            return true;
        }
        catch (Ineligible exception) { reason = exception.Message; return false; }
        catch (JsonException) { reason = "invalid_public_entry_json"; return false; }
        catch (ArgumentException) { reason = "invalid_public_entry"; return false; }
    }

    private static NativeInitialShuffleCondition Create(DecisionPacket root, int publicCombatVersion)
    {
        bool publicCombatV2 = publicCombatVersion >= 2;
        Require(root is { Status: "player_decision" or "card_choice", Observation: not null, Actions.Length: > 0 }, "active_decision_required");
        var observation = root.Observation!;
        Require(observation.History is { Length: >= 4 }
            && observation.History[0].Kind == "combat_started"
            && observation.History[0].Detail == "Silent:A10" && observation.Ascension == 10
            && observation.History[1].Kind == NativeEntryAssets.EventKind
            && observation.History.Count(item => item.Kind == NativeEntryAssets.EventKind) == 1,
            "entry_history_required");
        string entryJson = observation.History[1].Detail;
        var entry = PublicJson.Read<NativeEntryAssets>(entryJson);
        Require(entry.SchemaVersion == "nosl.native-entry-assets.v1" && entry.Deck is { Length: > 0 }
            && entry.Relics is not null && entry.Potions is not null && entry.OrbSlots == 0
            && entry.Hp == observation.StartHp, "invalid_public_entry");
        foreach (var card in entry.Deck)
        {
            Require(card is not null && card.Keywords is not null && !card.Keywords.Contains("Innate", StringComparer.Ordinal)
                && ((card.Enchantments?.Length ?? 0) == 0 || (publicCombatVersion >= 3
                    && card.Enchantments!.All(effect => effect.Id == nameof(Sharp)))) && card.Affliction is null,
                "initial_shuffle_card_reordering_not_certified");
            RequireSafeHooks(card.Id, typeof(CardModel));
        }
        foreach (var relic in entry.Relics)
        {
            Require(relic is not null && relic.Details is not null, "invalid_public_entry");
            RequireSafeHooks(relic.Id, typeof(RelicModel), publicCombatV3: publicCombatVersion >= 3);
        }
        foreach (string? potion in entry.Potions)
            if (potion is not null) RequireSafeHooks(potion, typeof(PotionModel), publicCombatV4: publicCombatVersion >= 4);

        // PublicKnowledge.CardMoved intentionally does not log arbitrary moves. The metadata
        // closure above is therefore necessary; an apparently plain history alone is not proof.
        // CombatEngine.StartCombatAsync/SetupPlayerTurnAsync and Player.PopulateCombatState
        // establish the order: ordinary shuffle, startup hooks, then the hand draw. We permit
        // only the reviewed count/counter modifiers below, and no Innate move. V3 also
        // admits the exact sealed Sharp enchantment: CanEnchant is a pure predicate,
        // EnchantDamageAdditive changes only damage; inherited OnDrawn/OnPlay and
        // ModifyShuffleOrder are no-ops. The legacy ID path stays coarse. Public combat
        // proposals refine by the invariant public upgrade level, still sample physical
        // copies separately, and retain final native equality for all other metadata.
        // CardModel's protected cloning paths preserve physical card type/order. The only current
        // concrete AfterCloned overrides (Abundance, Fetch, Regret) reset private working memory;
        // this fixed-source review is additional to the public hook reflection check.
        var prefix = new List<NativePublicDrawKey>();
        int index = 2;
        var startupPowers = new List<StartupPower>();
        if (publicCombatV2)
            while (index < observation.History.Length && observation.History[index].Kind == "power_changed")
            {
                var power = PublicJson.Read<StartupPower>(observation.History[index++].Detail);
                Require(power is { Target: nameof(CorpseSlug), Id: nameof(RavenousPower), Amount: 5,
                    TargetSlot: >= 0, SourceSlot: >= 0 } && power.TargetSlot == power.SourceSlot,
                    "startup_power_not_certified");
                startupPowers.Add(power);
            }
        while (index < observation.History.Length && observation.History[index].Kind == "draw")
        {
            var drawn = PublicJson.Read<PublicCard>(observation.History[index++].Detail);
            Require(!string.IsNullOrWhiteSpace(drawn.Id), "invalid_public_draw");
            prefix.Add(NativePublicDrawKey.From(drawn));
        }
        Require(prefix.Count > 0 && index < observation.History.Length
            && observation.History[index].Kind == "player_turn" && observation.History[index].Detail == "1",
            "uninterrupted_initial_draw_history_required");
        var startupEnemies = observation.History.Skip(index + 1)
            .TakeWhile(item => item.Kind == "intent_published")
            .Select(item => PublicJson.Read<StartupIntent>(item.Detail)).ToArray();
        Require(startupEnemies.Length > 0 && startupEnemies.Select(enemy => enemy.Slot)
            .SequenceEqual(Enumerable.Range(0, startupEnemies.Length))
            && startupEnemies.All(enemy => enemy.Id is not null && Models.TryGetValue(enemy.Id, out Type[]? types)
                && types.Any(type => type.IsSealed && (ReviewedStartupMonsters.Contains(type)
                    || (publicCombatV2 && type == typeof(CorpseSlug))))), "startup_monster_not_certified");
        if (publicCombatV2)
        {
            var slugSlots = startupEnemies.Where(enemy => enemy.Id == nameof(CorpseSlug)).Select(enemy => enemy.Slot);
            Require(startupPowers.Select(power => power.TargetSlot!.Value).SequenceEqual(slugSlots),
                "startup_power_roster_not_certified");
        }
        foreach (StartupIntent enemy in startupEnemies)
        {
            RequireSafeHooks(enemy.Id, typeof(MonsterModel));
            Type type = Models[enemy.Id].Single(candidate => typeof(MonsterModel).IsAssignableFrom(candidate));
            if (publicCombatV2 && type == typeof(CorpseSlug))
            {
                // Fixed-source CorpseSlug.AfterAddedToRoom only applies its own RavenousPower.
                // GenerateMoveStateMachine selects an ordinary MoveState from StarterMoveIdx;
                // none of its callbacks runs during startup. RavenousPower's sole hook is
                // AfterDeath, affecting slug AI/Strength only, never cards or their order.
                RequireSafeHooks(nameof(RavenousPower), typeof(PowerModel), ravenousV2: true);
            }
            Require(type.GetMethod(nameof(MonsterModel.AfterAddedToRoom))!.DeclaringType == typeof(MonsterModel)
                || (publicCombatV2 && type == typeof(CorpseSlug)),
                "startup_monster_hook_not_certified:" + enemy.Id);
        }
        // The requested draw count may depend on an unpublished room type or a safe relic
        // counter. Any nonempty observed prefix has the same permutation likelihood conditional
        // on this entry multiset; full native packet verification checks the actual draw count.
        Require(prefix.Count <= entry.Deck.Length, "initial_draw_pool_mismatch");
        var remaining = Counts(entry.Deck.Select(card => card.Id));
        foreach (string id in prefix.Select(card => card.Id))
        {
            Require(remaining.TryGetValue(id, out int count) && count > 0, "initial_draw_pool_mismatch");
            remaining[id]--;
        }
        if (publicCombatVersion >= 5)
        {
            var keys = entry.Deck.Select(NativePublicDrawKey.From).GroupBy(key => key)
                .ToDictionary(group => group.Key, group => group.Count());
            foreach (var key in prefix)
            {
                Require(keys.TryGetValue(key, out int count) && count > 0, "initial_draw_upgrade_pool_mismatch");
                keys[key]--;
            }
        }
        // Upgrade refinement is used only by the public combat proposal. Keep the legacy
        // coarse certificate behavior, including its ID-only probability, unchanged.
        return new(entryJson, entry.Deck.Select(NativePublicDrawKey.From).ToArray(), prefix.ToArray());
    }

    private static void RequireSafeHooks(string id, Type category, bool ravenousV2 = false, bool publicCombatV3 = false,
        bool publicCombatV4 = false)
    {
        Require(Models.TryGetValue(id, out Type[]? matches), "unknown_entry_model:" + id);
        Type[] candidates = matches.Where(type => category.IsAssignableFrom(type)).ToArray();
        Require(candidates.Length == 1 && candidates[0].IsSealed, "unknown_entry_model:" + id);
        Type type = candidates[0];
        foreach (MethodInfo hook in AbstractHooks)
        {
            MethodInfo implementation = type!.GetMethod(hook.Name,
                hook.GetParameters().Select(parameter => parameter.ParameterType).ToArray())!;
            if (implementation.DeclaringType == typeof(AbstractModel)) continue;
            // Complete sealed Fairy path: owner-only death veto, automatic slot consumption,
            // ordinary HP healing. All dispatched death/potion/HP/empty-hand listeners retain
            // this reflection guard; none of its own hooks changes a card pile. Opt-in only.
            if (publicCombatV4 && type == typeof(FairyInABottle)
                && implementation.DeclaringType == typeof(FairyInABottle)
                && hook.Name is nameof(AbstractModel.ShouldDie) or nameof(AbstractModel.AfterPreventingDeath)) continue;
            // CombatRoom creates rewards only in post-victory settlement. LavaRock's sole
            // hook cannot execute during startup or an unfinished first draw cycle.
            if (publicCombatV3 && type == typeof(LavaRock)
                && implementation.DeclaringType == typeof(LavaRock)
                && hook.Name == nameof(AbstractModel.ModifyRewards)) continue;
            if (ravenousV2 && type == typeof(RavenousPower) && implementation.DeclaringType == typeof(RavenousPower)
                && hook.Name == nameof(AbstractModel.AfterDeath)) continue;
            // CombatRoom.CompleteCombatOnceAsync/ResolveVictoryOnceAsync dispatch these only
            // after combat ends, so their earlier-run effects are already in the entry anchor.
            // In particular FishingRod.AfterCombatEnd cannot run before this initial draw prefix.
            if (hook.Name is nameof(AbstractModel.AfterCombatEnd) or nameof(AbstractModel.AfterCombatVictoryEarly)
                or nameof(AbstractModel.AfterCombatVictory)) continue;
            bool reviewed = ReviewedEntryHooks.TryGetValue(implementation.DeclaringType!, out var safeHooks)
                && safeHooks.Contains(hook.Name);
            Require(reviewed, "entry_hook_not_certified:" + id + "." + hook.Name);
        }
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> ids) => ids
        .GroupBy(id => id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static void Require([DoesNotReturnIf(false)] bool value, string reason)
    { if (!value) throw new Ineligible(reason); }
    private sealed record StartupIntent(int Slot, string Id);
    private sealed record StartupPower(string Target, int? TargetSlot, int? SourceSlot, string Id, decimal Amount);
    private sealed class Ineligible(string message) : Exception(message);
}
