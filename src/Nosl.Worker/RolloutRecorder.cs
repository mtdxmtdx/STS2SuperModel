using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>One settled-fact recorder for ordinary teachers and opt-in finite-plan evaluations.</summary>
internal static class RolloutRecorder
{
    internal static RolloutOutcome Settled(CombatSession branch, TerminalFacts facts, string policyId, int lastPlayerTurn)
    {
        var ledger = branch.Knowledge.OutcomeLedger;
        var permanent = CombatAssetSnapshot.Changes(branch.InitialAssets, branch.FinalAssets!)
            .Concat(branch.Room.GeneratedRewards.SelectMany(set => set.ExtraRewards).GroupBy(reward => reward.GetType().Name, StringComparer.Ordinal)
                .Select(group => new PermanentChange("earned_extra_reward_opportunity:" + group.Key, group.Count(),
                    "actual offered extra rewards; option identities not inspected")))
            .Concat(branch.EventReturnOpportunities.Select(opportunity => new PermanentChange(
                "earned_event_reward_opportunity:" + opportunity.Kind, opportunity.Count,
                "native event return reward offer; option identities not inspected"))).ToArray();
        return new()
        {
            TerminalKind = facts.Result switch { "win" => TerminalKind.Win, "loss" => TerminalKind.Loss, _ => throw new InvalidOperationException("Unknown engine terminal result") },
            PlayerAlive = facts.FinalHp > 0, HpAtCombatStart = facts.StartHp, HpAfterSettlement = facts.FinalHp,
            MaxHpStart = facts.StartMaxHp, MaxHpAfterSettlement = facts.FinalMaxHp,
            CumulativeHpDamage = ledger.Damage, HealingReceived = ledger.Healing,
            OtherHpAdjustment = ledger.OtherHpAdjustment, HpEventDiagnosticsComplete = ledger.HpComplete,
            InventoryStart = Inventory(branch.StartPotions), InventoryEnd = Inventory(facts.Potions), InventorySnapshotsComplete = true,
            ResourceEvents = ledger.ResourceEvents.ToArray(),
            ResourceProvenanceComplete = ledger.ResourcesComplete, PermanentChanges = permanent, PermanentChangesComplete = true,
            PersistentAssetsAtStartJson = PublicJson.Serialize(branch.InitialAssets), PersistentAssetsAfterSettlementJson = PublicJson.Serialize(branch.FinalAssets),
            PlayerTurnsElapsed = lastPlayerTurn, AtomicActionsExecuted = facts.Events.Count(e => e.Kind == "action"), SettlementComplete = true,
            SettlementProfileId = facts.Boundary, ContinuationPolicyId = policyId,
        };
    }
    private static InventoryQuantity[] Inventory(IEnumerable<string?> items) => items.Where(x => x is not null)
        .GroupBy(x => x!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray();
}
