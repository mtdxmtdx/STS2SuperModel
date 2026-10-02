using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;

namespace Nosl.Worker;

/// <summary>
/// Public sufficiency for the additional native encounter prior. This reconstructs
/// memory, never transitions a source model or substitutes a rule/RNG implementation.
/// Summoned-rat imports and slugs with an earlier-turn allied death remain outside
/// this certificate; their owned sampled continuations execute the native engine.
/// </summary>
internal static class NativeEncounterMemory
{
    internal const string Profile = "native-public-entry-reviewed-encounters-exchangeable-v1";
    internal const string ChoiceProfile = "native-public-entry-reviewed-encounters-conditional-choice-v1";
    internal static readonly string[] Encounters = ["CorpseSlugsWeak", "TwoTailedRatsNormal", "RubyRaiders"];
    internal static bool AllowsPower(string encounter, string power) =>
        encounter == "CorpseSlugsWeak" && power is "RavenousPower" or "FrailPower"
        || encounter == "TwoTailedRatsNormal" && power == "FrailPower";

    private sealed record Published(int Slot, string Id, PublicIntent[] Intents);
    private sealed record Damage(int TargetSlot, bool Killed);
    private sealed class Memory(string id, string initial)
    {
        internal string Id = id;
        internal string Current = initial;
        internal readonly List<string> Log = [initial];
        internal readonly HashSet<int> PublishedTurns = [];
        internal bool Dead;
        internal string? StunFollowUp;
    }

    internal static bool Matches(CombatState state, PublicKnowledge knowledge, string encounter, PublicObservation observation)
    {
        var initial = new SortedDictionary<int, Published>();
        int turn = 0;
        foreach (var item in observation.History)
        {
            if (item.Kind == "player_turn")
            {
                if (!int.TryParse(item.Detail, out int next) || next != turn + 1) return false;
                turn = next;
            }
            if (item.Kind != "intent_published") continue;
            var publication = PublicJson.Read<Published>(item.Detail);
            if (turn < 1 || publication.Slot < 0) return false;
            if (!initial.ContainsKey(publication.Slot))
            {
                if (turn != 1) return false; // No unrepresented summoned origin.
                initial.Add(publication.Slot, publication);
            }
        }
        if (turn != observation.Turn || !LayoutMatches(encounter, initial)) return false;
        if (encounter == "TwoTailedRatsNormal" && turn != 1) return false;
        if (encounter == "TwoTailedRatsNormal"
            && (!state.NoslEncounterSlotsMatch("first", "second", "third", "fourth", "fifth")
                || !state.SpawnedEnemies.Select(knowledge.Slot).SequenceEqual(new[] { 0, 1, 2 }))) return false;
        var memory = new Dictionary<int, Memory>();
        foreach (var (slot, entry) in initial)
        {
            string? first = InitialMove(entry);
            if (first is null) return false;
            memory.Add(slot, new(entry.Id, first));
        }
        if (encounter is "CorpseSlugsWeak" or "TwoTailedRatsNormal")
        {
            int first = StarterIndex(memory[0].Current);
            if (memory.Any(x => StarterIndex(x.Value.Current) != (first + x.Key) % 3)) return false;
        }
        int currentTurn = 0;
        bool playerPhase = false;
        foreach (var item in observation.History)
        {
            if (item.Kind == "player_turn")
            {
                currentTurn++;
                playerPhase = true;
                if (currentTurn > 1)
                {
                    // Death-time ordering inside an enemy side is intentionally not
                    // inferred from incomplete move-start/end telemetry. Existing
                    // sampled worlds still finish those native effects normally.
                    if (encounter == "CorpseSlugsWeak" && memory.Values.Any(x => x.Dead)) return false;
                    foreach (var itemMemory in memory.Values.Where(x => !x.Dead))
                    {
                        itemMemory.Current = NextMove(itemMemory.Id, itemMemory.Current);
                        itemMemory.Log.Add(itemMemory.Current);
                    }
                }
            }
            else if (item.Kind == "action" && PublicJson.Read<PublicAction>(item.Detail).Kind == "end_turn")
                playerPhase = false;
            else if (item.Kind == "intent_published")
            {
                var publication = PublicJson.Read<Published>(item.Detail);
                if (!memory.TryGetValue(publication.Slot, out var m) || m.Dead || publication.Id != m.Id
                    || !ShapeMatches(m.Current, publication.Intents)) return false;
                // Re-publications in a player turn do not append a monster move.
                // Every publication is checked, including a revealed transient stun.
                m.PublishedTurns.Add(currentTurn);
            }
            else if (item.Kind == "damage")
            {
                var damage = PublicJson.Read<Damage>(item.Detail);
                if (!damage.Killed || damage.TargetSlot < 0) continue;
                if (!memory.TryGetValue(damage.TargetSlot, out var dead) || dead.Dead) return false;
                dead.Dead = true;
                if (encounter != "CorpseSlugsWeak") continue;
                if (!playerPhase || currentTurn < 1) return false;
                foreach (var survivor in memory.Values.Where(x => !x.Dead))
                {
                    survivor.StunFollowUp = survivor.Log[^1];
                    survivor.Current = "STUNNED";
                }
            }
        }
        var alive = memory.Where(x => !x.Value.Dead).Select(x => x.Key).Order().ToArray();
        if (alive.Length == 0 || !state.Enemies.Select(knowledge.Slot).Order().SequenceEqual(alive)) return false;
        foreach (var enemy in state.Enemies)
        {
            int slot = knowledge.Slot(enemy);
            var m = memory[slot];
            var monster = enemy.Monster;
            if (!enemy.IsAlive || monster is null || monster.GetType().Name != m.Id || monster.IsPerformingMove
                || monster.MoveStateMachine is not { } machine || monster.NextMove is not { } move
                || !ReferenceEquals(machine.CurrentState, move) || move.Id != m.Current
                || machine.PerformedFirstMove != (turn > 1)
                || !machine.StateLog.Select(x => x.Id).SequenceEqual(m.Log)
                || !m.PublishedTurns.Order().SequenceEqual(Enumerable.Range(1, turn))
                || !ShapeMatches(m.Current, PublicViews.Intents(enemy))) return false;
            if (monster is CorpseSlug slug)
            {
                if (enemy.SlotName is not null || slug.StarterMoveIdx != StarterIndex(m.Log[0])
                    || slug.IsRavenous != (m.StunFollowUp is not null)
                    || enemy.GetPower<RavenousPower>() is not { Amount: 5 }) return false;
                if (m.StunFollowUp is { } saved)
                {
                    if (!move.MustPerformOnceBeforeTransitioning || move.CanTransitionAway
                        || move.FollowUpState is not null || move.FollowUpStateId != saved) return false;
                }
                else if (move.MustPerformOnceBeforeTransitioning) return false;
            }
            else if (monster is TwoTailedRat rat)
            {
                if (rat.StarterMoveIndex != StarterIndex(m.Log[0]) || rat.NoslTurnsUntilSummonable != 2
                    || rat.CallForBackupCount != 0 || enemy.SlotName != new[] { "third", "fourth", "fifth" }[slot]
                    || move.MustPerformOnceBeforeTransitioning) return false;
            }
            else if (enemy.SlotName is not null || move.MustPerformOnceBeforeTransitioning) return false;
            // At the pinned baseline public attack previews use DamageCalc directly,
            // without Weak/Strength modifiers. Check the native selected move as well
            // as the historical public damage, notably rat Scratch 9 vs Disease 7.
            if (move.Intents.OfType<AttackIntent>().Any(a => a.GetSingleDamage(state.PlayerCreatures, enemy)
                != Shape(m.Current).Single(x => x.Kind == "Attack").Damage)) return false;
        }
        return true;
    }

    private static bool LayoutMatches(string encounter, SortedDictionary<int, Published> initial)
    {
        if (!initial.Keys.SequenceEqual(Enumerable.Range(0, initial.Count))) return false;
        var ids = initial.Values.Select(x => x.Id).ToArray();
        return encounter switch
        {
            "CorpseSlugsWeak" => ids.SequenceEqual(new[] { "CorpseSlug", "CorpseSlug" }),
            "TwoTailedRatsNormal" => ids.SequenceEqual(new[] { "TwoTailedRat", "TwoTailedRat", "TwoTailedRat" }),
            "RubyRaiders" => ids.Order(StringComparer.Ordinal).SequenceEqual(new[] { "AssassinRubyRaider", "AxeRubyRaider", "BruteRubyRaider" }),
            _ => false,
        };
    }

    private static string? InitialMove(Published p)
    {
        string[] candidates = p.Id switch
        {
            "CorpseSlug" => ["WHIP_SLAP_MOVE", "GLOMP_MOVE", "GOOP_MOVE"],
            "TwoTailedRat" => ["SCRATCH_MOVE", "DISEASE_BITE_MOVE", "SCREECH_MOVE"],
            "BruteRubyRaider" => ["BEAT_MOVE"],
            "AxeRubyRaider" => ["SWING_1"],
            "AssassinRubyRaider" => ["KILLSHOT_MOVE"],
            _ => [],
        };
        return candidates.SingleOrDefault(x => ShapeMatches(x, p.Intents));
    }
    private static int StarterIndex(string move) => move switch
    {
        "WHIP_SLAP_MOVE" or "SCRATCH_MOVE" => 0,
        "GLOMP_MOVE" or "DISEASE_BITE_MOVE" => 1,
        "GOOP_MOVE" or "SCREECH_MOVE" => 2,
        _ => -1,
    };
    private static string NextMove(string id, string move) => (id, move) switch
    {
        ("CorpseSlug", "WHIP_SLAP_MOVE") => "GLOMP_MOVE",
        ("CorpseSlug", "GLOMP_MOVE") => "GOOP_MOVE",
        ("CorpseSlug", "GOOP_MOVE") => "WHIP_SLAP_MOVE",
        ("BruteRubyRaider", "BEAT_MOVE") => "ROAR_MOVE",
        ("BruteRubyRaider", "ROAR_MOVE") => "BEAT_MOVE",
        ("AxeRubyRaider", "SWING_1") => "SWING_2",
        ("AxeRubyRaider", "SWING_2") => "BIG_SWING",
        ("AxeRubyRaider", "BIG_SWING") => "SWING_1",
        ("AssassinRubyRaider", "KILLSHOT_MOVE") => "KILLSHOT_MOVE",
        _ => "UNREVIEWED",
    };
    private static bool ShapeMatches(string move, PublicIntent[] published) => Shape(move).SequenceEqual(published);
    private static PublicIntent[] Shape(string move) => move switch
    {
        "WHIP_SLAP_MOVE" => [new("Attack", 3, 2)],
        "GLOMP_MOVE" or "SCRATCH_MOVE" => [new("Attack", 9, 1)],
        "DISEASE_BITE_MOVE" => [new("Attack", 7, 1)],
        "GOOP_MOVE" or "SCREECH_MOVE" => [new("Debuff", null, null)],
        "STUNNED" => [new("Stun", null, null)],
        "BEAT_MOVE" => [new("Attack", 8, 1)],
        "ROAR_MOVE" => [new("Buff", null, null)],
        "SWING_1" or "SWING_2" => [new("Attack", 6, 1), new("Defend", null, null)],
        "BIG_SWING" => [new("Attack", 13, 1)],
        "KILLSHOT_MOVE" => [new("Attack", 11, 1)],
        _ => [],
    };
}
