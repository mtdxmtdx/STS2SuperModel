using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>
/// Reviewed native mechanics closure for one small constructed family. This does not upgrade the
/// recorder's diagnostic event ledger and is not a general-purpose inventory/future-value certificate.
/// </summary>
public static class EmptyPotionContinuationProof
{
    public const string Version = "twig-slime-poison-empty-potion-closure-v1";
    public const string Endpoint = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION";
    private static readonly string[] Cards = ["NoxiousFumes", "DeadlyPoison", "Backflip", "Footwork", "DefendSilent", "Slimed"];

    public static EmptyInventoryClosure Certify(CombatSession source)
    {
        if (source.HasNativeProvenance) throw new NotSupportedException("This closure certifies declared constructed scenarios only, not native-run carry-in");
        var scenario = source.InitialScenario;
        if (scenario.Enemy != "TwigSlimeM" || scenario.Encounter is not null || scenario.Enemies is not null
            || scenario.Deck is null || scenario.Deck.Any(c => !Cards.Contains(c))
            || (scenario.Relics?.Length ?? 0) != 0 || (scenario.Potions ?? []).Any(p => p != "SwiftPotion")
            || source.Room.RoomType != Sts2Sim.Core.Rooms.RoomType.Monster
            || source.Room.EncounterName != Sts2Sim.Core.Rooms.CombatRoom.ForcedEncounterName
            || source.StartMaxHp != source.Observe().Observation?.MaxHp)
            throw new NotSupportedException("A verified declared constructed scenario and unchanged maximum HP are required");
        return CertifyConstructedRoot(source.Observe());
    }

    // The public entry point binds the packet to a real constructed session. Internal access also lets
    // regression tests inspect the independently replay-verified, archived original packet.
    internal static EmptyInventoryClosure CertifyConstructedRoot(DecisionPacket root)
    {
        var o = root.Observation;
        bool Plain(PublicCard c) => Cards.Contains(c.Id) && c.Upgrade == 0
            && c.Cost == 1 && c.StarCost == -1 && c.Type == (c.Id is "NoxiousFumes" or "Footwork" ? "Power" : c.Id == "Slimed" ? "Status" : "Skill")
            && c.Keywords.SequenceEqual(c.Id == "Slimed" ? new[] { "Exhaust" } : [])
            && c.Details is { CostsXEnergy: false, CostsXStar: false, LocalEnergyCost: 1, LocalStarCost: -1,
                RetainThisTurn: false, SlyThisTurn: false, BaseReplayCount: 0, ExhaustOnNextPlay: false,
                FreeThisTurn: false, FreeUntilPlayed: false, FreeThisCombat: false, StarCostThisTurn: null }
            && c.Details.EnergyModifiers.Length == 0
            && c.Enchantments is { Length: 0 } && c.Affliction is null
            && c.PublicState is not null && c.PublicState.Keys.All(k => k is "tags" or "targetType")
            && c.PublicState.GetValueOrDefault("tags") == (c.Id == "DefendSilent" ? "Defend" : "")
            && c.PublicState.GetValueOrDefault("targetType") == (c.Id == "DeadlyPoison" ? "AnyEnemy" : c.Id == "Slimed" ? "None" : "Self");
        if (root.Status != "player_decision" || o is null || o.Choice is not null || root.Actions.Length == 0
            || o.Schema != "nosl.public.v2" || o.Ascension != 10 || o.StartHp <= 0 || o.Hp <= 0 || o.Hp > o.StartHp || o.MaxHp < o.StartHp
            || o.Potions.Any(x => x is not null) || o.UnidentifiedDrawCount != 0
            || o.DrawCount != o.UnknownDraw.Sum(x => x.Count) + o.KnownDraw.Length
            || !o.Relics.SequenceEqual(new[] { "RingOfTheSnake" }) || (o.RelicStates?.Length ?? 0) != 1
            || o.RelicStates![0].Id != "RingOfTheSnake"
            || o.RelicStates[0].Details.Count != 4
            || !o.RelicStates[0].Details.TryGetValue("isWax", out var wax) || wax != 0
            || !o.RelicStates[0].Details.TryGetValue("isMelted", out var melted) || melted != 0
            || !o.RelicStates[0].Details.TryGetValue("isUsedUp", out var used) || used != 0
            || !o.RelicStates[0].Details.TryGetValue("stackCount", out var stacks) || stacks != 1
            || o.RelicStates[0].Cards is not { Length: 0 } || o.RelicStates[0].SelectedModel is not null
            || o.OrbCapacity != 0 || o.Orbs is not { Length: 0 } || o.Pets is not { Length: 0 }
            || o.Enemies.Length != 1 || o.Enemies[0].Id != "TwigSlimeM"
            || o.Powers.Any(p => p.Id is not ("DexterityPower" or "NoxiousFumesPower"))
            || o.Enemies[0].Powers.Any(p => p.Id != "PoisonPower")
            || !o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(c => c.Card))
                .Concat(o.KnownDraw.Select(c => c.Card)).All(Plain)
            || root.Actions.Any(a => a.Kind is not ("play" or "end_turn")))
            throw new NotSupportedException("Public root is outside the reviewed empty-inventory native continuation family");
        // This family generates only combat Slimed cards, block, draws, dexterity and poison.
        // RingOfTheSnake modifies opening draw only. TwigSlimeM attacks or generates Slimed.
        // No source heals, generates inventory, mutates persistent assets or earns extra rewards;
        // ordinary postcombat offers are not selected at Endpoint. The empty inventory stays empty.
        return new(RootKey(root), Endpoint, Version);
    }

    public static RelativeDifferenceSupport CostDifferenceSupport(CombatSession source, ObjectiveProfile? profile = null)
    {
        Certify(source);
        return ConstructedCostDifferenceSupport(source.Observe(), profile);
    }

    internal static RelativeDifferenceSupport ConstructedCostDifferenceSupport(DecisionPacket root, ObjectiveProfile? profile = null)
    {
        CertifyConstructedRoot(root); profile ??= ObjectiveProfile.Candidate; profile.Validate();
        // Do not condition this support on the observed all-win sample. Future worlds may lose.
        double width = profile.DefeatCost + root.Observation!.StartHp * (1 + profile.DownsideCoefficient);
        return new(-width, width, Version + ":no-healing-and-common-inventory-full-world-support-including-loss");
    }

    public static string RootKey(DecisionPacket root) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(root)))).ToLowerInvariant();
}
