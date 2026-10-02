using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Models.Powers;

namespace Nosl.Worker;

/// <summary>Outcome-relevant bindings/counters not determined by the public amount alone.</summary>
internal static class NativePowerMemory
{
    internal static bool Matches(CombatState state, PublicKnowledge knowledge, PublicObservation observation)
    {
        // Shrinker casts once. Its permanent Shrink is removed when that specific
        // creature dies; the original application (including lifetime source slot)
        // is in the public history and the clone must preserve that exact binding.
        foreach (var shrink in state.Players.SelectMany(p => p.Creature.Powers).OfType<ShrinkPower>())
        {
            var applied = observation.History.Where(x => x.Kind == "power_changed")
                .Select(x => PublicJson.Read<PowerChange>(x.Detail))
                .FirstOrDefault(x => x.Id == "ShrinkPower" && x.TargetSlot == -2 && x.Amount != 0);
            if (applied is null || shrink.Applier is null || applied.SourceSlot != knowledge.Slot(shrink.Applier)) return false;
        }
        foreach (var enemy in state.Enemies)
        {
            if (enemy.GetPower<HardenedShellPower>() is not { } shell) continue;
            int slot = knowledge.Slot(enemy);
            int start = Array.FindLastIndex(observation.History, x => x.Kind == "player_turn");
            if (start < 0) return false;
            decimal received = 0;
            foreach (var item in observation.History.Skip(start + 1).Where(x => x.Kind == "damage"))
            {
                using var document = JsonDocument.Parse(item.Detail);
                var damage = document.RootElement;
                if (damage.GetProperty("targetSlot").GetInt32() == slot)
                    received += damage.GetProperty("unblocked").GetDecimal();
            }
            // HardenedShell resets before each side. The reviewed closure has no
            // player-start enemy damage before intent publication. Damage events
            // contain actual post-cap HP loss. Saturated counters are equivalent
            // until reset; only the remaining allowance can affect future rules.
            if (shell.DisplayAmount != Math.Max(0m, shell.Amount - received)) return false;
        }
        return true;
    }

    private sealed record PowerChange(int TargetSlot, int? SourceSlot, string Id, decimal Amount);
}
