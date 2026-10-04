namespace Sts2Sim.Core.Entities.Players;

// Optional committed-value diagnostics. This binding belongs to a player lifetime,
// not Creature.CombatState, so automatic settlement remains observable after detach.
// It is deliberately absent from Player.CloneForCombat and restored only by its owner.
internal interface IPlayerOutcomeObserver
{
    void HpChanged(Player player, HpMutationKind kind, int before, int after, int maxBefore, int maxAfter);
    void PotionChanged(Player player, string potionId, PotionMutationKind kind);
}

internal enum HpMutationKind { Loss, Heal, Set, MaxCap }
internal enum PotionMutationKind { Acquired, Consumed, Discarded, Removed }
