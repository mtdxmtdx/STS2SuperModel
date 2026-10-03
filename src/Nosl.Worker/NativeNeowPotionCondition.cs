using Nosl.Contracts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.PotionPools;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Worker;

/// <summary>Only the complete, immediately settled public initial PhialHolster grant, never root inventory inference.</summary>
internal sealed class NativeNeowPotionCondition
{
    internal IReadOnlyList<string> PotionIds { get; }
    internal IReadOnlyList<PotionModel> Pool { get; }
    internal IReadOnlyList<IReadOnlyList<PotionModel>> SlotPools { get; }
    internal IReadOnlyList<ShuffleRational> SlotMasses { get; }
    internal ShuffleRational Envelope { get; }

    private NativeNeowPotionCondition(string[] ids)
    {
        PotionIds = Array.AsReadOnly(ids);
        var unlock = PlayerUnlockState.AllUnlocked();
        var pool = ModelDb.Character<Silent>().PotionPool.GetUnlockedPotions(unlock)
            .Concat(SharedPotionPool.Instance.GetUnlockedPotions(unlock)).DistinctBy(p => p.Id).ToArray();
        Pool = Array.AsReadOnly(pool);
        var first = pool.SingleOrDefault(p => p.GetType().Name == ids[0]);
        Require(first is not null, "neow_potion_has_no_native_pool_support");
        // Pinned PhialHolster excludes the first generated ModelId before the second rarity-specific index draw.
        SlotPools = Array.AsReadOnly<IReadOnlyList<PotionModel>>([Pool,
            Array.AsReadOnly(pool.Where(p => p.Id != first!.Id).ToArray())]);
        SlotMasses = Array.AsReadOnly(SlotPools.Select((candidates, index) =>
            NativeRewardResourceMath.PotionMass(candidates, ids[index])).ToArray());
        Require(SlotMasses.All(m => m.Numerator > 0), "neow_potion_has_no_native_pool_support");
        Envelope = SlotMasses.Aggregate(new ShuffleRational(1, 1), (p, q) => p.Multiply(q.Numerator, q.Denominator));
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeNeowPotionCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try
        {
            Require(prior.UsesRewardsProvenance, "neow_potions_require_explicit_hybrid_prior");
            Require(root.PublicEvidence is { CompleteFromRunStart: true }, "neow_potions_require_complete_public_evidence");
            Require(NativeNeowCondition.TryCreate(root, prior, out var neow, out _) && neow!.ObservedCurseId is not null
                && neow.TargetRelicId == nameof(PhialHolster), "neow_potions_require_typed_initial_phial_choice");
            var events = root.PublicEvidence!.Events;
            var before = ((PublicRunStarted)events[0].Payload).Assets;
            Require(before.Relics.Select(r => r.Id).SequenceEqual(new[] { nameof(RingOfTheSnake) })
                && before.PotionSlots == 2 && before.Potions.All(p => p is null), "neow_potions_require_empty_native_starter_inventory");
            Require(events.Length > 4 && events[4] is { OwnerOrdinal: 0, Payload: PublicOwnerEnded
                { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } }, "neow_potions_public_settlement_missing");
            var after = ((PublicOwnerEnded)events[4].Payload).Assets!;
            Require(after.Relics.Select(r => r.Id).SequenceEqual(new[] { nameof(RingOfTheSnake), nameof(PhialHolster) })
                && after.PotionSlots == 3 && after.Potions[0] is not null && after.Potions[1] is not null && after.Potions[2] is null
                && after.Potions[0] != after.Potions[1], "neow_potions_require_two_ordered_unique_grants");
            condition = new([after.Potions[0]!, after.Potions[1]!]); return true;
        }
        catch (NotCertifiedException error) { reason = error.Message; return false; }
    }

    private static void Require(bool valid, string reason) { if (!valid) throw new NotCertifiedException(reason); }
    private sealed class NotCertifiedException(string reason) : Exception(reason);
}
