using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models.Encounters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>Audit metadata, not a claim that every listed model or combination is verified.</summary>
public sealed record MonsterCoverageEntry(string Id, string PosteriorMode, string Status,
    string Reason, string[] GeneratedCards, string[] SpawnedMonsters, string Source);

public sealed record ForcedEventCoverageEntry(string Event, string? CanonicalEncounter,
    string[] Monsters, string Status, string AdapterRequirement, string Source);

public sealed record EncounterCoverageEntry(string Name, string Act, int ActIndex, RoomType RoomType,
    bool IsWeak, bool RequiresEventContext, EncounterDefinition Definition, string Source);

/// <summary>
/// Content discovery and room construction delegate to the pinned simulator. No move, reward,
/// encounter composition, weighting, slot, or summon rule is implemented here.
/// Public-memory metadata applies only with complete public history from the declared setup;
/// card/relic/power applicability still has to be checked independently by the session.
/// </summary>
public static class EncounterCoverage
{
    public const string PublicMemory = "public-history-determined";
    public const string ReplayRequired = "public-history-rejection-replay-required";
    public const string OutOfScope = "not-an-enemy-encounter";
    private const string MonsterPath = "vendor/sts2-sim/src/Sts2Sim.Core/Models/Monsters/";

    public static IReadOnlyList<EncounterCoverageEntry> AllEncounters { get; } = CreateCatalog();
    public static IReadOnlyList<MonsterCoverageEntry> AllMonsters { get; } = CreateMonsterAudit();
    public static IReadOnlyList<ForcedEventCoverageEntry> AllForcedEvents { get; } = Array.AsReadOnly(new[]
    {
        Forced("BattlewornDummy", null, ["BattleFriendV1", "BattleFriendV2", "BattleFriendV3"],
            "Native event tier, time-limit/timeout outcome and automatic post-combat upgrade or reward offer."),
        Forced("DenseVegetation", null, ["Wriggler"],
            "Native event slotted four-Wriggler factory, prior rest healing and forced-combat reward context."),
        Forced("PunchOff", "PunchOffEventEncounter", ["PunchConstruct"],
            "Native event formation HP reductions and additional relic/potion reward offers."),
        Forced("FakeMerchant", "FakeMerchantEventEncounter", ["FakeMerchantMonster"],
            "Native merchant inventory, 300 fixed gold and unpurchased/fake-rug relic reward offers."),
        Forced("TheLanternKey", "MysteriousKnightEventEncounter", ["MysteriousKnight"],
            "Native event pending LanternKey special-card reward and post-combat return hook.")
    });

    private static ForcedEventCoverageEntry Forced(string id, string? encounter, string[] monsters, string requirement) =>
        new(id, encounter, monsters, "Partial", requirement +
            " Use Scenario.ForcedEvent with a legal native option path; independent concrete replay retains the event owner. " +
            "Constructed-event execution does not certify a natural-run posterior.",
            $"vendor/sts2-sim/src/Sts2Sim.Core/Models/Events/{id}.cs");

    public static string[] PublicMemoryEnemies => AllMonsters.Where(x => x.PosteriorMode == PublicMemory).Select(x => x.Id).ToArray();

    public static EncounterCoverageEntry Find(string name) => AllEncounters.SingleOrDefault(x => x.Name == name)
        ?? throw new NotSupportedException($"Unknown pinned encounter: {name}");

    public static MonsterCoverageEntry Monster(string id) => AllMonsters.SingleOrDefault(x => x.Id == id)
        ?? throw new NotSupportedException($"Unknown pinned monster: {id}");

    /// <summary>
    /// Creates a declared combat fixture with Silent already added. The full act list preserves
    /// final-act boss settlement. Advancing here does not claim a naturally traversed prior run.
    /// Callers must use the existing player instead of creating another after room generation.
    /// </summary>
    public static RunState CreateRun(string seed, string? encounter, int ascension = 10)
    {
        if(!ModelDb.Contains(typeof(Silent))) ModelDb.Init(ContentRegistry.AllTypes);
        var entry = encounter is null ? null : Find(encounter);
        if (entry?.RequiresEventContext == true)
            throw new NotSupportedException($"{encounter} requires its native forced-event context.");
        ActDefinition first = entry?.Act == "Underdocks" ? new Underdocks() : new Overgrowth();
        var run = new RunState(seed, new ActDefinition[] { first, new Hive(), new Glory() }, ascension);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        while (run.CurrentActIndex < (entry?.ActIndex ?? 0)) run.AdvanceToNextAct();
        return run;
    }

    /// <summary>Creates an unentered room; the caller installs the observer/choice source and enters it.</summary>
    public static CombatRoom CreateRoom(string name, RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var entry = Find(name);
        if (entry.RequiresEventContext)
            throw new NotSupportedException($"{name} requires its native forced-event settlement context; an ordinary combat room would omit mandatory event rewards or return hooks.");
        if (run.Act.GetType().Name != entry.Act)
            throw new ArgumentException($"{name} requires act {entry.Act}, not {run.Act.GetType().Name}.", nameof(run));
        if (entry.RoomType == RoomType.Boss && (run.CurrentActIndex == run.Acts.Count - 1) != (entry.ActIndex == 2))
            throw new ArgumentException("The run act list would misclassify final-act boss settlement.", nameof(run));
        return new CombatRoom(() => (entry.Definition, CreateMonsters(name, run)), entry.RoomType);
    }

    /// <summary>Uses the upstream encounter RNG derivation and factory unchanged.</summary>
    public static IReadOnlyList<(MonsterModel Monster, string? SlotName)> CreateMonsters(string name, RunState run)
    {
        var definition = Find(name).Definition;
        var seed = RoomFactory.EncounterMonsterSeed(run.Rng.Seed, run.TotalFloor, definition);
        return definition.CreateMonsters(new Rng(seed));
    }

    private static IReadOnlyList<EncounterCoverageEntry> CreateCatalog()
    {
        var result = new List<EncounterCoverageEntry>();
        foreach (ActDefinition act in new ActDefinition[] { new Overgrowth(), new Underdocks(), new Hive(), new Glory() })
        {
            Add(act.MonsterEncounterCandidates, RoomType.Monster);
            Add(act.EliteEncounterCandidates, RoomType.Elite);
            Add(act.BossEncounterCandidates, RoomType.Boss);
            void Add(IEnumerable<EncounterDefinition> definitions, RoomType type)
            {
                foreach (var definition in definitions)
                    result.Add(new(definition.Name, act.GetType().Name, act.Index, type, definition.IsWeak,
                        false, definition, $"vendor/sts2-sim/src/Sts2Sim.Core/Content/Acts/{act.GetType().Name}.cs"));
            }
        }
        // These canonical encounter definitions exist, but their event settlement is not equivalent
        // to a stand-alone monster room. Keep them visible and blocked rather than dropping them.
        foreach (var definition in new[] { FakeMerchantEventEncounter.Definition,
                     MysteriousKnightEventEncounter.Definition, PunchOffEventEncounter.Definition })
            result.Add(new(definition.Name, "Event", -1, RoomType.Monster, false, true, definition,
                $"vendor/sts2-sim/src/Sts2Sim.Core/Models/Encounters/{definition.Name}.cs"));
        return result.AsReadOnly();
    }

    private static IReadOnlyList<MonsterCoverageEntry> CreateMonsterAudit()
    {
        var result = ContentRegistry.AllTypes.Where(t => typeof(MonsterModel).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => new MonsterCoverageEntry(t.Name, ReplayRequired,
                "ImplementedUnverified", "The complete monster, attached-power and move-graph state has not been proven public-determined. Do not preserve source-private memory as an NOSL posterior; condition independent replay on the full public history and report budget exhaustion.",
                [], [], MonsterPath + t.Name + ".cs"), StringComparer.Ordinal);

        void Public(string names, string reason)
        {
            foreach (var id in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result[id] = result[id] with { PosteriorMode = PublicMemory, Status = "Partial", Reason = reason };
        }
        void Needs(string names, string reason)
        {
            foreach (var id in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result[id] = result[id] with { Reason = reason };
        }
        void Cards(string names, params string[] cards)
        {
            foreach (var id in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result[id] = result[id] with { GeneratedCards = cards };
        }
        void Spawns(string names, params string[] monsters)
        {
            foreach (var id in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result[id] = result[id] with { SpawnedMonsters = monsters };
        }

        Public("TwigSlimeS LeafSlimeM SnappingJaxfruit Seapunk SewerClam SpinyToad Stabbot Zapbot AssassinRubyRaider AxeRubyRaider BruteRubyRaider CrossbowRubyRaider TrackerRubyRaider BowlbugEgg BowlbugNectar BowlbugSilk Byrdonis CubexConstruct FuzzyWurmCrawler TurretOperator Guardbot",
            "Fixed initial move and deterministic move cycle; current graph position follows from complete published intent/turn history. Applied combat powers depend on public amounts. This is a source audit, not full card/relic combination validation.");
        Public("LeafSlimeS TwigSlimeM Flyconid Mawler SludgeSpinner FlailKnight MysteriousKnight",
            "Random branches use public-distinguishable move intents and repeat/cooldown/once-only memory from the complete intent history. Future branches must use independently sampled future RNG; preserving only the latest intent is insufficient.");
        Public("Nibbit KinFollower KinPriest Toadpole PunchConstruct Inklet Exoskeleton",
            "Encounter roles, initial graph branch and slot-dependent first move must come from the named vendor factory or a declared constructed setup. All future-relevant graph memory is determined by the complete per-enemy intent history; never replace a natural formation with solo defaults.");
        Public("BygoneEffigy", "Fixed graph; SlowPower._cardsPlayedSinceOwnerTurnStart is determined by public completed card-play events and enemy turn starts. Keep the complete history and independently audit any additional player powers.");
        Public("CalcifiedCultist DampCultist DevotedSculptor", "Fixed graph; RitualPower._skipFirstEnemyTurnEnd follows from the public application turn and enemy-turn boundary. Complete history is required.");

        Needs("ActOneGuardian HardyBrute TrainingDummy WanderingGrunt", "Simulator demo/placeholder, explicitly not a real game monster in its source; exclude from the Silent real-content enemy denominator.");
        foreach (var id in new[] { "ActOneGuardian", "HardyBrute", "TrainingDummy", "WanderingGrunt" })
            result[id] = result[id] with { PosteriorMode = OutOfScope, Status = "OutOfScope" };
        Needs("Osty", "Player-side Necrobinder companion, not an enemy encounter. Cross-pool access does not turn it into an enemy; companion mechanics require a separate player-state audit.");
        Needs("Byrdpip PaelsLegion", "Player-side pet supplied by a Silent-reachable relic, not an enemy encounter; retain its dependency under the relic/player-pet coverage audit.");
        foreach (var id in new[] { "Osty", "Byrdpip", "PaelsLegion" })
            result[id] = result[id] with { PosteriorMode = OutOfScope, Status = "OutOfScope" };

        Needs("BattleFriendV1 BattleFriendV2 BattleFriendV3", "BattlewornDummy event forced-combat context includes BattlewornDummyTimeLimitPower timeout and AfterForcedCombat tier rewards; ordinary room victory/loss settlement is not equivalent.");
        Needs("Axebot", "Nullable _stockAmount distinguishes first spawn from replacements; StockPower spawns an Axebot with a decremented stock and slot. Public replacement/death history and clone topology need validation.");
        Needs("BowlbugRock", "ImbalancedPower and _isOffBalance alter current/transient move state after public damage; targeted clone/reconstruction tests are still required.");
        Needs("CeremonialBeast", "PlowPower, IsStunnedByPlowRemoval and IsInSecondPhase redirect the move graph; test mid-turn forced-intent changes, phase history and clone rebinding.");
        Needs("CorpseSlug", "Factory correlates StarterMoveIdx across slugs; RavenousPower death triggers create a transient STUNNED move with a saved follow-up. Reconstruct jointly from the full formation/death/intent history.");
        Needs("Crusher Rocket", "KaiserCrabBoss requires the coupled formation, BackAttackLeft/Right, SurroundedPower and CrabRagePower applier/facing state; solo model construction is not the natural boss encounter.");
        Needs("DecimillipedeSegmentBack DecimillipedeSegmentFront DecimillipedeSegmentMiddle", "Shared DecimillipedeSegment base and named factory impose correlated StarterMoveIdx, segment death/revival and partner state; joint graph/public-history reconstruction is required.");
        Needs("Entomancer", "PersonalHivePower generates Dazed at random draw positions after each powered attack. Generated-card positions must remain hidden; public multiset/generation history and conditional draw-order sampling need validation.");
        Needs("Fabricator", "Private _lastSpawned controls no-repeat summon choice; offspring, minion death, slots and summon history must be jointly conditioned. Never preserve unknown actual _lastSpawned.");
        Needs("FakeMerchantMonster", "Random graph is only part of the requirement: native FakeMerchant event owns 300 fixed gold and extra relic rewards; ordinary room settlement loses required context.");
        Needs("FatGremlin SneakyGremlin GremlinMerc", "SurprisePower/ThieveryPower introduce spawned gremlins, escapes, stolen gold and encounter-specific reward proportion. Full spawn/escape/applier and settlement history is required.");
        Needs("Fogmog EyeWithTeeth Parafright", "Fogmog summons EyeWithTeeth into the encounter's illusion slot and has two equal-looking Swipe graph states with different follow-ups; IllusionPower and summon/death history need joint reconstruction and generated Dazed support.");
        Needs("FossilStalker", "Random graph plus SuckPower lifecycle/applier memory requires public-event reconstruction and targeted interaction validation.");
        Needs("FrogKnight", "Private _charged changes damage after the charge move; public-history reconstruction and dynamic intent refresh tests are required.");
        Needs("GasBomb LivingFog", "LivingFog._bloatAmount, GasBomb._hasExploded, six declared encounter slots and SmoggyPower card-affliction effects require spawn and modified-card coverage.");
        Needs("GlobeHead", "GalvanicPower afflicts player Power cards with Galvanized and damages their owner when played. Modified-card public payloads and generated-card inheritance need adapter coverage.");
        Needs("HunterKiller", "Random weighted graph and TenderPower event-dependent counters require intent-history and attached-power reconstruction tests.");
        Needs("InfestedPrism", "VitalSparkPower afflicts player Skills with Tainted and changes affliction amounts when its stacks change. Public card-affliction and power-removal cleanup surfaces require validation.");
        Needs("KnowledgeDemon", "Private _curseCounter selects distinct curse effects; Disintegration, MindRot, Sloth and WasteAway generation and identity/order semantics need full card coverage and history reconstruction.");
        Needs("LagavulinMatriarch", "AsleepPower and private wake/shell flags create transient wake-up moves. Public damage/HP-threshold history and transient follow-up restoration need validation.");
        Needs("LivingShield", "RampartPower modifies allies and depends on the natural LivingShield/TurretOperator formation; power applier/target linkage needs explicit coverage.");
        Needs("LouseProgenitor", "Curled and CurlUpPower trigger an immediate graph change on damage; public damage/intents and power runtime state need reconstruction tests.");
        Needs("MagiKnight", "DampenPower temporarily downgrades upgraded cards and stores original upgrade levels and caster references. Public original-upgrade history and clone identity coverage are required.");
        Needs("Ovicopter ToughEgg", "Ovicopter summons ToughEgg; HatchPower and ToughEgg._isHatched/AfterHatchedState redirect into nibble after a fresh randomized hatchling HP roll. Joint spawn/hatch/slot posterior is required.");
        Needs("OwlMagistrate", "Fixed public move cycle; SoarPower is a scalar damage multiplier removed by Verdict. Public move and power history determine memory.");
        Needs("PhantasmalGardener", "Private _enlargeTriggers and CurrentScale plus SkittishPower react to public card plays; retain per-enemy lifetime identity and full event history in the four-enemy elite.");
        Needs("PhrogParasite Wriggler", "InfestedPower/spawn lifecycle and generated Infection cards need dependency closure, public generation history and clone tests.");
        Needs("Queen TorchHeadAmalgam", "Queen._hasAmalgamDied and ChainsOfBindingPower bind cards and change graph after partner death. Named formation, binding and death history are required.");
        Needs("ScrollOfBiting", "Factory correlates StarterMoveIdx across scrolls, RandomBranchState remembers earlier choices, and PaperCutsPower has card-play effects; reconstruct the joint formation and full intent history.");
        Needs("ShrinkerBeetle SlitheringStrangler", "ShrinkPower/ConstrictPower removal is tied to the applier creature. Duplicate enemies require public source/target lifetime identity, not type-only power events.");
        Needs("SkulkingColony", "HardenedShellPower._damageReceivedThisTurn changes effective remaining cap (DisplayAmount differs from Amount); need per-enemy public damage history and dynamic power surface.");
        Needs("SlumberingBeetle", "SlumberPower creates a transient wake-up stun and immediate graph override after damage; retain published intent changes between player actions and validate cloned transient follow-up.");
        Needs("SoulFysh", "Private _isInvisible affects targeting/intent, plus Beckon generation and IntangiblePower lifecycle; ensure legal-target visibility and public generation reconstruction.");
        Needs("SoulNexus", "Random branches have three public-distinguishable attack/attack-multi/attack-debuff intents and CannotRepeat history; scalar Weak/Vulnerable powers are public.");
        Needs("SpectralKnight", "HexPower generates Dazed in response to card plays; clone/power state and generated-card closure require validation.");
        Needs("TerrorEel", "Private _terrorState and ShriekPower create immediate move changes; dynamic intent and graph-reference rebinding must be tested.");
        Needs("TestSubject", "Private _deadState, _respawns and _extraMultiClawCount interact with NemesisPower resurrection, Adaptable/Enrage/PainfulStabs and generated Burn; stable-boundary and terminal settlement validation required.");
        Needs("TheForgotten TheLost", "PossessSpeed/Strength powers link both enemies and revive/redirect after death; native paired encounter and joint death/applier history are required.");
        Needs("TheInsatiable", "SandpitPower plus FranticEscape generation alters combat escape/termination; generated-card and terminal outcome coverage are required.");
        Needs("TheObscura", "Fixed initial summon introduces Parafright, then random public-intent branches. Full generation/topology history and child dependency coverage are required.");
        Needs("ThievingHopper", "IsHovering, FlutterPower, SwipePower and EscapeArtistPower introduce stolen-card identity and escape state; raw copied card references are not a justified posterior.");
        Needs("Tunneler", "IsStunned and BurrowedPower cause immediate stun/move replacement; mid-player-turn intent-history reconstruction and clone tests are required.");
        Needs("TwoTailedRat", "Private starter, turnsUntilSummonable and callForBackupCount plus correlated factory start and five ordered slots determine future summons; joint replay is required.");
        Needs("WaterfallGiant", "Private pressure/steam damage counters and _aboutToBlowState with SteamEruptionPower must be reconstructed from visible move/damage history and rebound on clone.");
        Needs("Aeonglass", "WitheringPresencePower and AdditionalStrength/WitherUpgradeCount plus upgraded Wither generation require boss phase/history and generated-card dependency validation.");
        Needs("Chomper HauntedShip MechaKnight Myte Noisebot SlimedBerserker Vantom", "Move graphs are source-implemented, but generated status cards and associated power interactions need card dependency closure and full replay/clone tests before promotion.");
        Needs("VineShambler", "TangledPower modifies card-affliction state; public modified-card and affected-card relationship coverage is required.");

        Public("OwlMagistrate SoulNexus", "Public-distinguishable move graph and scalar public powers; complete published intent history determines CannotRepeat memory where applicable.");

        Cards("LeafSlimeS LeafSlimeM TwigSlimeM SlimedBerserker", "Slimed");
        Cards("Chomper HauntedShip EyeWithTeeth Noisebot SpectralKnight Entomancer", "Dazed");
        Cards("MechaKnight", "Burn"); Cards("TestSubject", "Burn", "Wound");
        Cards("PhrogParasite Wriggler", "Infection");
        Cards("KnowledgeDemon", "Disintegration", "MindRot", "Sloth", "WasteAway");
        Cards("Aeonglass", "Wither"); Cards("Myte", "Toxic"); Cards("SoulFysh", "Beckon");
        Cards("TheInsatiable", "FranticEscape"); Cards("Vantom", "Wound");
        Spawns("Fogmog", "EyeWithTeeth"); Spawns("TheObscura", "Parafright");
        Spawns("Fabricator", "Guardbot", "Noisebot", "Stabbot", "Zapbot");
        Spawns("LivingFog", "GasBomb"); Spawns("Ovicopter", "ToughEgg");
        Spawns("PhrogParasite", "Wriggler"); Spawns("Axebot", "Axebot");
        Spawns("GremlinMerc", "FatGremlin", "SneakyGremlin");
        Spawns("TwoTailedRat", "TwoTailedRat");
        return Array.AsReadOnly(result.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    }
}
