using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;

namespace Nosl.Worker;

/// <summary>
/// Inventory follows the trusted, pinned upstream registry. These are not per-name permission
/// gates: the adapter reports unavailable mechanisms at their actual capability boundary.
/// Registration is deliberately distinct from adapter test coverage and client verification.
/// </summary>
public static class CardCoverage
{
    public sealed record Entry(string Id, bool SilentPool, bool MultiplayerOnly, bool Colorless,
        string Type, string Rarity, int MaxUpgradeLevel, bool CanGenerateInCombat,
        string[] RequiredMechanisms, string[] Dependencies, string Status, string Reason,
        string[] TestEvidence)
    {
        public string SourcePath => $"vendor/sts2-sim/src/Sts2Sim.Core/Models/Cards/{Id}.cs";
    }

    private static readonly Lazy<Entry[]> Inventory = new(Build);
    public static IReadOnlyList<Entry> Entries => Inventory.Value;
    // A conservative reachable superset includes foreign-color generated cards. The engine's
    // pool/rules decide what can actually be generated; membership does not claim natural reachability.
    public static string[] SupportedCards => Entries.Where(e => !e.MultiplayerOnly).Select(e => e.Id).ToArray();
    public static string[] SilentCards => Entries.Where(e => e.SilentPool && !e.MultiplayerOnly).Select(e => e.Id).ToArray();
    public static Entry? Get(string id) => Entries.SingleOrDefault(e => e.Id == id);

    private static Entry[] Build()
    {
        ModelDb.Init(ContentRegistry.AllTypes);
        var silent = ModelDb.Character<Silent>().CardPool.AllCards.Select(c => c.GetType().Name).ToHashSet(StringComparer.Ordinal);
        return ModelDb.All<CardModel>().Select(card =>
        {
            string id = card.GetType().Name;
            var mechanisms = new List<string> { "engine-owned legality/effects/upgrades", "public card instance state", "stable-boundary clone" };
            var dependencies = new List<string>();
            if (card.CostsXEnergy) mechanisms.Add("X energy resource snapshot");
            if (card.CostsXStar) mechanisms.Add("X star resource snapshot");
            if (card.IsMultiplayerOnly) mechanisms.Add("multiplayer-only direct-pool eligibility");
            switch (id)
            {
                case "BladeOfInk": mechanisms.Add("public enchantment attachments"); goto case "BladeDance";
                case "BladeDance": case "CloakAndDagger": case "LeadingStrike": case "HiddenDaggers":
                case "StormOfSteel": case "FanOfKnives": case "InfiniteBlades": case "UpMySleeve":
                    dependencies.Add("Shiv"); mechanisms.Add("fixed public token generation/overflow"); break;
                case "Nightmare": mechanisms.Add("public selected-card ID/upgrade power payload"); break;
                case "TheHunt": mechanisms.Add("pending earned-reward projection settlement"); break;
                case "Finisher": case "MementoMori": case "Murder": case "PhantomBlades":
                    mechanisms.Add("public turn/combat counters"); break;
                case "BulletTime": case "Pinpoint": case "Pounce":
                    mechanisms.Add("ordered public cost modifiers and consumption"); break;
                case "Expertise": case "HandTrick": mechanisms.Add("temporary retain/Sly flags"); break;
                case "Afterimage": case "SerpentForm": case "Strangle":
                    mechanisms.Add("transient per-play snapshots; clone only stable boundary"); break;
                case "ThinkingAhead": mechanisms.Add("public draw-top constraint"); break;
                case "ToolsOfTheTrade": case "Burst": mechanisms.Add("phase-local sequential public choices"); break;
                case "KnifeTrap": mechanisms.Add("exhaust-pile auto-play"); dependencies.Add("Shiv"); break;
                case "Abundance": case "MadScience": mechanisms.Add("generated choice/instance payload"); break;
            }
            string[] evidence = silent.Contains(id) && !card.IsMultiplayerOnly
                ? ["CardCoverageTests.BaseAndUpgradeExecuteAcrossIndependentClone"]
                : [];
            return new Entry(id, silent.Contains(id), card.IsMultiplayerOnly, card.IsColorless,
                card.Type.ToString(), card.Rarity.ToString(), card.MaxUpgradeLevel, card.CanBeGeneratedInCombat,
                mechanisms.ToArray(), dependencies.ToArray(), "UPSTREAM_REGISTERED",
                card.IsMultiplayerOnly ? "Not in the single-player direct pool; retained in inventory."
                    : "Trusted upstream rule implementation; registry admission is not exhaustive adapter/NOSL test verification.", evidence);
        }).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
}
