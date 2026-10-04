using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Factories;

namespace Nosl.Worker;

/// <summary>
/// Untouched entry-potion prior only. The owned continuation executes the upstream
/// generation rules; generated states do not become new exchangeable roots.
/// </summary>
internal static class NativeGenerationPotionMemory
{
    internal const string Profile = "native-public-entry-generation-potions-exchangeable-v1";

    // Pinned ordered native metadata, from CardChoicePotionFidelityTests.NativePool
    // at the vendored 0.111.0 baseline. These are closure guards, not generation rules.
    internal static readonly string[] Attacks = "Assassinate Backstab DaggerSpray DaggerThrow Dash EchoingSlash Finisher Flechettes FlickFlack GrandFinale LeadingStrike MementoMori Murder Pinpoint PoisonedStab Pounce PreciseCut Predator Ricochet Skewer Slice Strangle SuckerPunch".Split(' ');
    internal static readonly string[] Powers = "Abrasive Accelerant Accuracy Afterimage Envenom FanOfKnives Footwork InfiniteBlades MasterPlanner NoxiousFumes PhantomBlades SerpentForm Speedster ToolsOfTheTrade Tracking WellLaidPlans".Split(' ');

    internal static bool IsGenerationPotion(string? id) => id is "AttackPotion" or "PowerPotion";
    internal static bool Untouched(PublicObservation observation) =>
        !observation.History.Any(e => e.Kind == "potion_used" && IsGenerationPotion(e.Detail));

    internal static bool PoolsMatch(Player player)
    {
        var candidates = CardPoolProjection.OrderedCombatCandidates(player, player.Character.CardPool);
        return candidates.Where(c => c.Type == CardType.Attack).Select(c => c.GetType().Name).SequenceEqual(Attacks)
            && candidates.Where(c => c.Type == CardType.Power).Select(c => c.GetType().Name).SequenceEqual(Powers);
    }
}
