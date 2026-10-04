using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;

namespace Nosl.Worker;

/// <summary>
/// The sole pinned Toadpole content factory creates two unslotted creatures, front
/// then rear. No setup/self-power hook publishes earlier identities: CombatStarted
/// assigns public slots in creation order before their Buff/Whirl startup intents.
/// MaxHp is unchanged by the complete Toadpole/Thorns closure; later damage is immaterial.
/// This does not widen the legacy unique-model HP API or any other duplicate roster.
/// </summary>
internal sealed class NativeToadpoleHpCondition
{
    private const int MinHp = 22, MaxHp = 26;
    private static readonly EncounterDefinition Factory = UnderdocksEncounters.BatchB.Single(item => item.Name == "ToadpolesWeak");
    private readonly int[] _targets;
    private readonly ulong[] _envelopes;
    internal int EnemyCount => 2;

    private NativeToadpoleHpCondition(int[] targets)
    {
        _targets = targets.ToArray();
        // Creation order is public-fixed. This existing conservative primitive
        // bounds all subsets of the fixed earlier targets, never a sampled pool.
        _envelopes = targets.Select((_, slot) => NativeHpProposal.RootEnvelopeBucket(MinHp, MaxHp,
            targets.Take(slot))).ToArray();
    }

    internal static bool TryCreate(DecisionPacket packet, out NativeToadpoleHpCondition? condition,
        out string? reason)
    {
        condition = null;
        if (!NativeInitialShuffleCondition.TryCreatePublicCombatV3(packet, out _, out reason)) return false;
        var observation = packet.Observation!;
        int turn = Array.FindIndex(observation.History, item => item.Kind == "player_turn");
        var startup = observation.History.Skip(turn + 1).TakeWhile(item => item.Kind == "intent_published")
            .Select(item => PublicJson.Read<StartupIntent>(item.Detail)).ToArray();
        var enemies = observation.Enemies.OrderBy(enemy => enemy.Slot).ToArray();
        if (enemies.Length != 2 || startup.Length != 2
            || startup.Any(enemy => enemy.Id != nameof(Toadpole)) || enemies.Any(enemy => enemy.Id != nameof(Toadpole))
            || !startup.Select(enemy => enemy.Slot).SequenceEqual([0, 1])
            || !enemies.Select(enemy => enemy.Slot).SequenceEqual([0, 1]))
        { reason = "toadpole_hp_complete_ordered_roster_required"; return false; }
        if (startup[0].Intents is not [{ Kind: "Buff", Damage: null, Repeats: null }]
            || startup[1].Intents is not [{ Kind: "Attack", Damage: 8, Repeats: 1 }])
        { reason = "toadpole_hp_front_rear_startup_required"; return false; }
        if (enemies.Any(enemy => enemy.MaxHp < MinHp || enemy.MaxHp > MaxHp)
            || enemies[0].MaxHp == enemies[1].MaxHp)
        { reason = "toadpole_hp_distinct_native_range_required"; return false; }
        condition = new(enemies.Select(enemy => enemy.MaxHp).ToArray()); reason = null; return true;
    }

    internal NativeHpProposal CreateProposal(LabelMonsterHpContext context, int previouslyConditioned,
        Func<ulong> nextWord)
    {
        var creature = context.Creature;
        if (creature.Monster is not Toadpole toadpole || previouslyConditioned is < 0 or >= 2)
            throw new NativePublicConstraintMismatchException("Native Toadpole roster differs from the two public slots");
        var state = creature.CombatState as CombatState;
        if (state is null || state.IsProjection || context.MinHp != MinHp || context.MaxHp != MaxHp
            || !ReferenceEquals(context.Rng, state.RunState.Rng.Niche)
            || !state.RunState.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || state.RunState.CurrentRoom is not CombatRoom { RoomType: RoomType.Monster, Engine: null } room
            || !ReferenceEquals(room.Encounter, Factory) || Factory.Slots.Count != 0
            || !state.NoslEncounterSlotsMatch() || creature.SlotName is not null
            || toadpole.IsFront != (previouslyConditioned == 0)
            || state.Enemies.Count != previouslyConditioned || state.SpawnedEnemies.Count != previouslyConditioned
            || !state.Enemies.SequenceEqual(state.SpawnedEnemies))
            throw new InvalidOperationException("Native Toadpole creation escaped its exact unslotted front/rear factory");
        for (int slot = 0; slot < previouslyConditioned; slot++)
        {
            var earlier = state.Enemies[slot];
            if (earlier.Monster is not Toadpole { IsFront: true } || earlier.SlotName is not null
                || earlier.MaxHp != _targets[slot] || earlier.CurrentHp != _targets[slot])
                throw new InvalidOperationException("Native Toadpole setup changed an earlier conditioned public slot");
        }
        if (!context.UsedHp.SequenceEqual(_targets.Take(previouslyConditioned).Order()))
            throw new InvalidOperationException("Native Toadpole unique HP context differs from its ordered public slots");
        return NativeHpProposal.Create(MinHp, MaxHp, _targets[previouslyConditioned], context.UsedHp,
            _envelopes[previouslyConditioned], nextWord)
            ?? throw new NativePublicConstraintMismatchException("Public Toadpole HP has no native unique-HP support");
    }

    private sealed record StartupIntent(int Slot, string Id, PublicIntent[] Intents);
}
