using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

/// <summary>Registration, adapter capability and game-rule fidelity are deliberately separate.</summary>
public sealed record EnvironmentAudit(string Id, string Category, string Status, string Mechanism,
    string[] Dependencies, string[] AdapterRequirements)
{
    public string Source => $"vendor/sts2-sim/src/Sts2Sim.Core/Models/{Category}s/{Id}.cs";
    public string RuleAuthority => "PINNED_UPSTREAM_TRUSTED";
    public string VerificationLimit => "Catalog membership is not an executed adapter test or exhaustive combination proof";
}

public static class EnvironmentCoverage
{
    // The user's full-content target is the pinned upstream registry, not a manually selected ceiling.
    // Runtime adapter failures must remain explicit; registration must not silently erase an action.
    public static readonly string[] PotionIds = Catalog<PotionModel>();
    public static readonly string[] RelicIds = Catalog<RelicModel>();
    public static IReadOnlyList<EnvironmentAudit> Entries => PotionIds.Select(AuditPotion).Concat(RelicIds.Select(AuditRelic)).ToArray();
    private static string[] Catalog<T>() => ContentRegistry.AllTypes
        .Where(t => !t.IsAbstract && typeof(T).IsAssignableFrom(t))
        .Select(t => t.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static EnvironmentAudit AuditPotion(string id)
    {
        if (!PotionIds.Contains(id, StringComparer.Ordinal))
            throw new ArgumentException($"Potion is absent from the pinned upstream registry: {id}", nameof(id));
        string[] dependencies = id switch
        {
            "CunningPotion" => ["Shiv"], "PotOfGhouls" => ["Soul"],
            "KingsCourage" => ["SovereignBlade"], "EssenceOfDarkness" => ["DarkOrb"],
            "BoneBrew" => ["Osty"], _ => [],
        };
        string[] requirements = id switch
        {
            "AttackPotion" or "SkillPotion" or "PowerPotion" or "ColorlessPotion" => ["generated-card-choice", "generated-pool-closure", "public-temporary-cost"],
            "CosmicConcoction" or "OrobicAcid" => ["generated-pool-closure", "public-card-state"],
            "EntropicBrew" => ["generated-potion-pool-closure"],
            "Ashwater" or "GamblersBrew" or "TouchOfInsanity" => ["public-hand-choice", "public-card-state"],
            "LiquidMemories" => ["public-discard-choice", "public-temporary-cost"],
            "DropletOfPrecognition" => ["unordered-draw-choice", "hidden-order-preservation"],
            "DistilledChaos" => ["autoplay-revelation", "nested-card-choice", "public-movement-events"],
            "BottledPotential" => ["public-hand-to-draw", "shuffle-knowledge-reset"],
            "SneckoOil" => ["public-random-hand-cost", "public-temporary-cost"],
            "SoldiersStew" => ["public-deterministic-all-pile-replay-modifier"],
            "EssenceOfDarkness" or "PotionOfCapacity" => ["public-orb-queue"],
            "BoneBrew" => ["public-pet-state", "pet-clone-references"],
            "KingsCourage" => ["public-forge-state", "generated-pool-closure"],
            "FairyInABottle" => ["automatic-death-prevention", "terminal-settlement"],
            "Ambergris" => ["extra-turn-lifecycle"],
            _ => ["public-resources-powers-and-card-movement"],
        };
        return new(id, "Potion", "ENGINE_CATALOG", "Pinned engine potion effect", dependencies, requirements);
    }

    public static EnvironmentAudit AuditRelic(string id)
    {
        if (!RelicIds.Contains(id, StringComparer.Ordinal))
            throw new ArgumentException($"Relic is absent from the pinned upstream registry: {id}", nameof(id));
        string[] dependencies = id switch { "NinjaScroll" => ["Shiv"], "PaelsHorn" => ["Relax"], _ => [] };
        string[] requirements = id switch
        {
            "GamblingChip" or "Toolbox" or "TanxsWhistle" => ["startup-choice-boundary", "public-card-choice"],
            "SneckoEye" or "StoneCracker" or "BurningSticks" or "MusicBox" or "MummifiedHand" => ["public-card-state", "hidden-order-exchangeability"],
            "HistoryCourse" or "PaelsTooth" or "DustyTome" or "SeaGlass" => ["public-relic-selected-content", "clone-reference-remapping"],
            "ToyBox" or "WongosMysteryTicket" => ["automatic-relic-generation", "public-relic-inventory", "terminal-hook-boundary"],
            "Byrdpip" or "BoundPhylactery" => ["public-pet-state", "pet-clone-references"],
            _ => ["public-relic-counters", "pickup-and-combat-lifecycle", "clone-isolation"],
        };
        return new(id, "Relic", "ENGINE_CATALOG", "Pinned engine relic hooks", dependencies, requirements);
    }
}
