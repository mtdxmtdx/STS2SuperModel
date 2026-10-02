using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>One settled-fact recorder for ordinary teachers and opt-in finite-plan evaluations.</summary>
internal static class RolloutRecorder
{
    internal static RolloutOutcome Settled(CombatSession branch, TerminalFacts facts, string policyId, int lastPlayerTurn)
    {
        double damage = 0;
        foreach (var e in facts.Events.Where(e => e.Kind == "damage"))
        {
            using var parsed = JsonDocument.Parse(e.Detail);
            var entry = parsed.RootElement;
            if (entry.GetProperty("target").GetString() == "player")
                damage += Math.Max(0, entry.GetProperty("unblocked").GetDouble() - entry.GetProperty("overkill").GetDouble());
        }
        var permanent = CombatAssetSnapshot.Changes(branch.InitialAssets, branch.FinalAssets!)
            .Concat(branch.Room.GeneratedRewards.SelectMany(set => set.ExtraRewards).GroupBy(reward => reward.GetType().Name, StringComparer.Ordinal)
                .Select(group => new PermanentChange("earned_extra_reward_opportunity:" + group.Key, group.Count(),
                    "actual offered extra rewards; option identities not inspected"))).ToArray();
        return new()
        {
            TerminalKind = facts.Result switch { "win" => TerminalKind.Win, "loss" => TerminalKind.Loss, _ => throw new InvalidOperationException("Unknown engine terminal result") },
            PlayerAlive = facts.FinalHp > 0, HpAtCombatStart = facts.StartHp, HpAfterSettlement = facts.FinalHp,
            MaxHpStart = facts.StartMaxHp, MaxHpAfterSettlement = facts.FinalMaxHp,
            CumulativeHpDamage = damage, HealingReceived = null, HpEventDiagnosticsComplete = false,
            InventoryStart = Inventory(branch.StartPotions), InventoryEnd = Inventory(facts.Potions), InventorySnapshotsComplete = true,
            ResourceEvents = facts.Events.Where(e => e.Kind == "potion_used").Select(e => new ResourceEvent("consumed", e.Detail, 1, "public_potion_used")).ToArray(),
            ResourceProvenanceComplete = false, PermanentChanges = permanent, PermanentChangesComplete = true,
            PersistentAssetsAtStartJson = PublicJson.Serialize(branch.InitialAssets), PersistentAssetsAfterSettlementJson = PublicJson.Serialize(branch.FinalAssets),
            PlayerTurnsElapsed = lastPlayerTurn, AtomicActionsExecuted = facts.Events.Count(e => e.Kind == "action"), SettlementComplete = true,
            SettlementProfileId = facts.Boundary, ContinuationPolicyId = policyId,
        };
    }
    private static InventoryQuantity[] Inventory(IEnumerable<string?> items) => items.Where(x => x is not null)
        .GroupBy(x => x!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray();
}
