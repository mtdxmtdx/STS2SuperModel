using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

public static class ContentAudit
{
    public static void Write(string path)
    {
        ModelDb.Init(ContentRegistry.AllTypes);
        var supported=CombatSession.SupportedCards.Concat(CombatSession.SupportedEnemies).Concat(CombatSession.SupportedPotions).Concat(CombatSession.SupportedRelics).ToHashSet(StringComparer.Ordinal);
        var entries=ContentRegistry.AllTypes.Select(t=>new
        {
            id=t.Name,category=ModelDb.GetCategory(t),engine="REGISTERED_NOT_CLIENT_VERIFIED",
            m012=supported.Contains(t.Name)?"SUPPORTED_TECHNICAL_SCOPE":"NOT_ADAPTED_OR_POSTERIOR_NOT_VERIFIED",
            fullSilentReachability="CONSERVATIVE_SUPERSET_NOT_A_NATURAL_DISTRIBUTION",
        }).ToArray();
        var value=new
        {
            scope="Silent A10 single-player combat; restricted M0-M2 technical closure",
            fullContentReady=false,entries,
            silentCardPool=ModelDb.Character<Sts2Sim.Core.Models.Characters.Silent>().CardPool.AllCards.Select(c=>c.GetType().Name).ToArray(),
            upstreamKnownGaps=new[]{"ToyBox","GoldenCompass","NutritiousSoup","Driftwood","TouchOfOrobas","SeaGlass","BlackBlood","RingOfTheDrake","InfusedCore","DivineDestiny","PhylacteryUnbound","TezcatarasEmber","Ringing","Entangled","WhisperingEarring automatic selection","TheArchitect victory event"},
            noslGaps=new[]{"Full Silent reachable-content posterior and legality audit (M5)","Unknown monster latent-memory posteriors outside TwigSlimeS/LeafSlimeS/Nibbit", "Queued extra rewards cannot yet be projected", "Bundle/draw-pile/outside-combat choices", "Afflictions/enchantments", "Semantic-key RNG unsupported in this checkpoint", "General mid-choice sampled replay", "Client differential harness/version unverified"},
            note="Registered does not mean implemented correctly. Every unsupported entry is retained, never silently deleted.",
        };
        File.WriteAllText(path,PublicJson.Serialize(value));
    }
}
