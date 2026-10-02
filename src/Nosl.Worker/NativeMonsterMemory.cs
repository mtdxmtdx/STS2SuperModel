using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Rooms;

namespace Nosl.Worker;

/// <summary>
/// Checks public sufficiency, not game rules. The upstream state machine still owns
/// transitions and random weights. In these reviewed models, each selected move is
/// identified by the published intent shape and the preceding public move. Thus its
/// complete anti-repeat log is public, including random SludgeSpinner/slime moves.
/// Models with summons, stuns, hidden counters or ambiguous transitions are excluded.
/// </summary>
internal static class NativeMonsterMemory
{
    internal static readonly string[] Encounters = ["ToadpolesWeak", "SeapunkWeak", "SludgeSpinnerWeak",
        "ShrinkerBeetle", "FuzzyWurmCrawler", "HauntedShipNormal", "SlimesWeak",
        "ShrinkerBeetleAndFuzzyWurmCrawler", "LoneNibbit", "CubexConstruct", "VineShambler", "SkulkingColonyElite"];

    internal static RoomType RoomTypeFor(string encounter) => encounter == "SkulkingColonyElite" ? RoomType.Elite : RoomType.Monster;

    private sealed record Published(int Slot, string Id, PublicIntent[] Intents);

    internal static bool Matches(CombatState state, PublicKnowledge knowledge, string encounter, PublicObservation observation)
    {
        if (NativeEncounterMemory.Encounters.Contains(encounter))
            return NativeEncounterMemory.Matches(state, knowledge, encounter, observation);
        var publications = new Dictionary<int, List<(int Turn, Published Intent)>>();
        int turn = 0;
        foreach (var item in observation.History)
        {
            if (item.Kind == "player_turn")
            {
                if (!int.TryParse(item.Detail, out var next) || next != turn + 1) return false;
                turn = next;
            }
            if (item.Kind != "intent_published") continue;
            var intent = PublicJson.Read<Published>(item.Detail);
            if (turn < 1 || intent.Slot < 0) return false;
            if (!publications.TryGetValue(intent.Slot, out var entries)) publications[intent.Slot] = entries = [];
            if (entries.Any(x => x.Turn == turn)) return false;
            entries.Add((turn, intent));
        }
        if (turn != observation.Turn || !MatchesLayout(encounter, publications)) return false;
        if (state.Enemies.Count == 0 || state.Enemies.Select(knowledge.Slot).Distinct().Count() != state.Enemies.Count) return false;
        foreach (var enemy in state.Enemies)
        {
            int slot = knowledge.Slot(enemy);
            if (!publications.TryGetValue(slot, out var entries) || enemy.Monster is not { } monster
                || entries.Any(x => x.Intent.Id != monster.GetType().Name)) return false;
            // The only model-specific fields in this family are fixed encounter roles.
            if (monster is Toadpole toad && toad.IsFront != (slot == 0)) return false;
            if (monster is Nibbit nibbit && (!nibbit.IsAlone || nibbit.IsFront)) return false;
            // Dead models have no future move or revival hooks in the reviewed family.
            if (!enemy.IsAlive) continue;
            if (entries.Count != turn || monster.IsPerformingMove || monster.MoveStateMachine is not { } machine
                || machine.PerformedFirstMove != (turn > 1)) return false;
            var expected = new List<string>();
            MoveState? previous = null;
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index].Turn != index + 1) return false;
                IEnumerable<MoveState> candidates;
                if (previous is null)
                {
                    string? first = InitialMove(monster, slot);
                    candidates = first is null ? machine.States.Values.OfType<MoveState>()
                        : machine.States.TryGetValue(first, out var initial) && initial is MoveState move ? [move] : [];
                }
                else if (previous.FollowUpState is MoveState next) candidates = [next];
                else if (previous.FollowUpState is RandomBranchState random)
                    candidates = random.States.Select(x => machine.States[x.StateId]).OfType<MoveState>();
                else return false;
                var matching = candidates.Where(c => SameShape(c, entries[index].Intent.Intents)).ToArray();
                // Never preserve a privately selected move when public evidence is ambiguous.
                if (matching.Length != 1 || matching[0].MustPerformOnceBeforeTransitioning) return false;
                previous = matching[0]; expected.Add(previous.Id);
            }
            if (monster.NextMove?.Id != previous!.Id || machine.CurrentState.Id != previous.Id
                || !machine.StateLog.Select(x => x.Id).SequenceEqual(expected)) return false;
        }
        return true;
    }

    private static bool SameShape(MoveState move, PublicIntent[] published) =>
        move.Intents.Count == published.Length && move.Intents.Zip(published).All(pair =>
            pair.First.IntentType.ToString() == pair.Second.Kind
            && (pair.First is AttackIntent attack ? attack.Repeats : (int?)null) == pair.Second.Repeats);

    private static string? InitialMove(MonsterModel monster, int slot) => monster switch
    {
        Toadpole => slot == 0 ? "SPIKEN_MOVE" : "WHIRL_MOVE",
        Seapunk => "SEA_KICK_MOVE",
        SludgeSpinner => "OIL_SPRAY_MOVE",
        ShrinkerBeetle => "SHRINKER_MOVE",
        FuzzyWurmCrawler => "FIRST_ACID_GOOP",
        HauntedShip => "HAUNT_MOVE",
        Nibbit => "BUTT_MOVE",
        CubexConstruct => "CHARGE_UP_MOVE",
        VineShambler => "SWIPE_MOVE",
        SkulkingColony => "ZOOM_MOVE",
        TwigSlimeS => "TACKLE_MOVE",
        LeafSlimeS => null, // The initial random branch is fully revealed by its intent.
        TwigSlimeM => "STICKY_SHOT_MOVE",
        LeafSlimeM => "STICKY_SHOT",
        _ => throw new NotSupportedException("Unreviewed native monster memory"),
    };

    private static bool MatchesLayout(string encounter, Dictionary<int, List<(int Turn, Published Intent)>> publications)
    {
        var initial = publications.OrderBy(x => x.Key).ToArray();
        if (!initial.Select(x => x.Key).SequenceEqual(Enumerable.Range(0, initial.Length))
            || initial.Any(x => x.Value.Count == 0 || x.Value[0].Turn != 1)) return false;
        var ids = initial.Select(x => x.Value[0].Intent.Id).ToArray();
        string[] expected = encounter switch
        {
            "ToadpolesWeak" => ["Toadpole", "Toadpole"],
            "SeapunkWeak" => ["Seapunk"],
            "SludgeSpinnerWeak" => ["SludgeSpinner"],
            "ShrinkerBeetle" => ["ShrinkerBeetle"],
            "FuzzyWurmCrawler" => ["FuzzyWurmCrawler"],
            "HauntedShipNormal" => ["HauntedShip"],
            "ShrinkerBeetleAndFuzzyWurmCrawler" => ["ShrinkerBeetle", "FuzzyWurmCrawler"],
            "LoneNibbit" => ["Nibbit"],
            "CubexConstruct" => ["CubexConstruct"],
            "VineShambler" => ["VineShambler"],
            "SkulkingColonyElite" => ["SkulkingColony"],
            _ => [],
        };
        if (encounter != "SlimesWeak") return expected.Length > 0 && ids.SequenceEqual(expected);
        return ids.Length == 3 && ids[1] is "TwigSlimeM" or "LeafSlimeM"
            && new[] { ids[0], ids[2] }.Order(StringComparer.Ordinal).SequenceEqual(new[] { "LeafSlimeS", "TwigSlimeS" });
    }
}
