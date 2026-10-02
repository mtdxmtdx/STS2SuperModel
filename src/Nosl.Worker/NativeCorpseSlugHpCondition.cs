using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Public-combat-v2 only: the native unslotted two/three CorpseSlug factories create
/// enemies in published order. AfterAddedToRoom publishes each self-power in that
/// same order, assigning its lifetime public slot before the initial intents.
/// CorpseSlug/RavenousPower never change MaxHp; later damage does not alter targets.
/// No such slot-to-creation claim is made for the legacy unique-model HP helper.
/// </summary>
internal sealed class NativeCorpseSlugHpCondition
{
    private const int MinHp = 27, MaxHp = 29;
    private readonly int[] _targets;
    private readonly ulong[] _envelopes;
    internal int EnemyCount => _targets.Length;

    private NativeCorpseSlugHpCondition(int[] targets)
    {
        _targets = targets;
        _envelopes = targets.Select((_, slot) => NativeHpProposal.RootEnvelopeBucket(MinHp, MaxHp,
            targets.Where((_, other) => other != slot))).ToArray();
    }

    internal static bool TryCreate(DecisionPacket packet, out NativeCorpseSlugHpCondition? condition,
        out string? reason)
    {
        condition = null;
        if (!NativeInitialShuffleCondition.TryCreatePublicCombatV2(packet, out _, out reason)) return false;
        var observation = packet.Observation!;
        int turn = Array.FindIndex(observation.History, entry => entry.Kind == "player_turn");
        var startup = observation.History.Skip(turn + 1).TakeWhile(entry => entry.Kind == "intent_published")
            .Select(entry => PublicJson.Read<StartupIntent>(entry.Detail)).ToArray();
        var enemies = observation.Enemies.OrderBy(enemy => enemy.Slot).ToArray();
        if (enemies.Length is not (2 or 3) || enemies.Length != startup.Length
            || startup.Any(enemy => enemy.Id != nameof(CorpseSlug))
            || enemies.Any(enemy => enemy.Id != nameof(CorpseSlug))
            || !enemies.Select(enemy => enemy.Slot).SequenceEqual(Enumerable.Range(0, enemies.Length)))
        { reason = "corpse_slug_hp_complete_unslotted_roster_required"; return false; }
        if (enemies.Any(enemy => enemy.MaxHp < MinHp || enemy.MaxHp > MaxHp)
            || enemies.Select(enemy => enemy.MaxHp).Distinct().Count() != enemies.Length)
        { reason = "corpse_slug_hp_distinct_native_range_required"; return false; }
        condition = new(enemies.Select(enemy => enemy.MaxHp).ToArray());
        reason = null;
        return true;
    }

    internal NativeHpProposal CreateProposal(LabelMonsterHpContext context, int previouslyConditioned,
        Func<ulong> nextWord)
    {
        var creature = context.Creature;
        if (creature.Monster is not CorpseSlug || previouslyConditioned >= EnemyCount)
            throw new NativePublicConstraintMismatchException("Native CorpseSlug startup roster differs from public slots");
        var state = creature.CombatState as CombatState;
        if (state is null || context.MinHp != MinHp || context.MaxHp != MaxHp
            || !state.RunState.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || !state.NoslEncounterSlotsMatch() || creature.SlotName is not null
            || state.Enemies.Count != previouslyConditioned
            || state.SpawnedEnemies.Count != previouslyConditioned
            || !state.Enemies.SequenceEqual(state.SpawnedEnemies))
            throw new InvalidOperationException("Native CorpseSlug creation order differs from its unslotted startup certificate");
        for (int slot = 0; slot < previouslyConditioned; slot++)
        {
            var earlier = state.Enemies[slot];
            if (earlier.Monster is not CorpseSlug || earlier.SlotName is not null
                || earlier.MaxHp != _targets[slot] || earlier.CurrentHp != _targets[slot])
                throw new InvalidOperationException("Native CorpseSlug setup changed an earlier conditioned public slot");
        }
        if (!context.UsedHp.SequenceEqual(_targets.Take(previouslyConditioned).Distinct().Order()))
            throw new InvalidOperationException("Native CorpseSlug unique HP context differs from its public slots");
        return NativeHpProposal.Create(MinHp, MaxHp, _targets[previouslyConditioned], context.UsedHp,
            _envelopes[previouslyConditioned], nextWord)
            ?? throw new NativePublicConstraintMismatchException("Public CorpseSlug HP has no native unique-HP support");
    }

    private sealed record StartupIntent(int Slot, string Id);
}
