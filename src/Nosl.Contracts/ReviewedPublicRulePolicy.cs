namespace Nosl.Contracts;

/// <summary>Explicit replay identities. Defaults stay v1; v2 is a separate data-generating policy.</summary>
public static class PublicContinuationPolicies
{
    public const string LegacyId = "nosl-public-rules-v1";
    public const string ReviewedId = "nosl-public-rules-v2";
    public const string ReviewedTreeId = "nosl-public-uct-frozen-v2-rules-v2";
    public const string ReviewedDatasetVersion = "nosl.teacher-data.public-rules-v2.v1";
    public static IPublicContinuationPolicy Create(string id) => id switch
    {
        LegacyId => new PublicRulePolicy(),
        ReviewedId => new ReviewedPublicRulePolicy(),
        _ => throw new ArgumentException("Unknown public continuation policy: " + id),
    };
    public static string DatasetVersion(string continuationId)
    {
        if (continuationId == ReviewedId || continuationId.StartsWith(ReviewedTreeId + ":", StringComparison.Ordinal))
            return ReviewedDatasetVersion;
        if (continuationId == LegacyId || continuationId.StartsWith("nosl-public-uct-frozen-v1:", StringComparison.Ordinal))
            return "nosl.teacher-data.public-rules-v1.v1";
        throw new ArgumentException("Unknown dataset continuation family: " + continuationId);
    }
}

/// <summary>
/// Public DTO-only, stateless, opt-in successor. Its narrow certificate is not a general
/// loop detector: unreviewed states retain v1 behavior and may still exhaust compute.
/// </summary>
public sealed class ReviewedPublicRulePolicy : IPublicContinuationPolicy
{
    private readonly PublicRulePolicy _legacy = new();
    public string Id => PublicContinuationPolicies.ReviewedId;

    public PublicAction Choose(DecisionPacket packet)
    {
        var baseline = _legacy.Choose(packet); // Includes unchanged choice/error behavior.
        var o = packet.Observation!;
        if (o.Choice is not null || !Certified(o) || packet.Status != "player_decision") return baseline;
        var end = packet.Actions.FirstOrDefault(a => a.Kind == "end_turn");
        if (end is null) return baseline;
        var circulating = o.Hand.Concat(o.Discard).Concat(o.UnknownDraw.Select(x => x.Card))
            .Concat(o.KnownDraw.Select(x => x.Card)).ToArray();
        // In this closed family, defense/draw has no route to damage, a reward, healing,
        // or a persistent gain. Exit the stall by ordinary end turns, never by declaring
        // defeat/nontermination. The simulator alone decides whether/when combat ends.
        if (circulating.Length > 0 && circulating.All(IsLoopCard)) return end;

        // Do not suppress drawing when any useful or unreviewed card could be drawn.
        // UnknownDraw is an unordered PUBLIC multiset; its order is never consulted.
        var drawable = o.Discard.Concat(o.UnknownDraw.Select(x => x.Card))
            .Concat(o.KnownDraw.Select(x => x.Card)).ToArray();
        if (!drawable.All(IsLoopCard)) return baseline;
        decimal incoming = o.Enemies.Single().Intents.Single().Damage!.Value;
        // Drawing another Finesse can still supply needed defense. Impatience's
        // native no-attacks-in-hand condition matters even for unplayable attacks.
        bool usefulDefensiveDraw = o.Block < incoming && drawable.Any(c => c.Id == "Finesse")
            && !o.Hand.Any(c => c.Type == "Attack");
        double Score(PublicAction action)
        {
            if (action.Kind == "end_turn") return 0;
            if (action.Kind != "play") return -1;
            var card = o.Hand[action.Slot];
            if (card.Id == "Impatience" && !usefulDefensiveDraw || card.Id == "Finesse" && o.Block >= incoming) return -2;
            if (card.Type == "Attack") return 100 - card.Cost * .01;
            return 40 - card.Cost * .01;
        }
        return packet.Actions.Select((action, index) => (action, index, score: Score(action)))
            .OrderByDescending(x => x.score).ThenBy(x => x.index).First().action;
    }

    private static bool IsLoopCard(PublicCard card) => card.Id is "Finesse" or "Impatience";

    private static bool Certified(PublicObservation o)
    {
        if (o.Schema != "nosl.public.v2" || o.Ascension != 10 || o.Turn < 1
            || o.Powers.Length != 0 || o.Potions.Any(p => p is not null) || o.Stars != 0
            || o.OrbCapacity != 0 || o.Orbs is not { Length: 0 } || o.Pets is not { Length: 0 }
            || o.UnidentifiedDrawCount != 0 || o.DrawCount < 0 || o.UnknownDraw.Any(x => x.Count <= 0)
            || o.UnknownDraw.Sum(x => (long)x.Count) + o.KnownDraw.Length != o.DrawCount
            || o.KnownDraw.Any(x => x.Position < 0 || x.Position >= o.DrawCount)
            || o.KnownDraw.Select(x => x.Position).Distinct().Count() != o.KnownDraw.Length
            || o.Enemies is not [{ Id: "TwigSlimeS", Hp: > 0, Powers.Length: 0,
                Intents: [{ Kind: "Attack", Damage: 5, Repeats: 1 }] }]) return false;
        // RingOfTheSnake only changes the native opening hand draw. All other relics,
        // including skill/play/discard counter triggers and wax variants, fail closed.
        if (!o.Relics.SequenceEqual(new[] { "RingOfTheSnake" })
            || o.RelicStates is not [{ Id: "RingOfTheSnake", SelectedModel: null, Cards.Length: 0 }]
            || o.RelicStates[0].Details.Count != 4
            || !o.RelicStates[0].Details.TryGetValue("isWax", out var wax) || wax != 0
            || !o.RelicStates[0].Details.TryGetValue("isMelted", out var melted) || melted != 0
            || !o.RelicStates[0].Details.TryGetValue("isUsedUp", out var used) || used != 0
            || !o.RelicStates[0].Details.TryGetValue("stackCount", out var stack) || stack != 1) return false;
        return o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(x => x.Card))
            .Concat(o.KnownDraw.Select(x => x.Card)).All(PlainReviewedCard);
    }

    private static bool PlainReviewedCard(PublicCard c)
    {
        if (c.Id is not ("Finesse" or "Impatience" or "FlashOfSteel" or "StrikeSilent" or "BladeDance" or "Shiv")) return false;
        int cost = c.Id is "StrikeSilent" or "BladeDance" ? 1 : 0;
        bool attack = c.Id is "StrikeSilent" or "FlashOfSteel" or "Shiv";
        string tags = c.Id == "StrikeSilent" ? "Strike" : c.Id == "Shiv" ? "Shiv" : "";
        string[] keywords = c.Id is "BladeDance" or "Shiv" ? ["Exhaust"] : [];
        return c.Upgrade is 0 or 1 && c.Cost == cost && c.StarCost == -1
            && c.Type == (attack ? "Attack" : "Skill") && c.Keywords.SequenceEqual(keywords)
            && c.Enchantments is { Length: 0 } && c.Affliction is null
            && c.PublicState is { Count: 2 } && c.PublicState.TryGetValue("targetType", out var target)
            && target == (attack ? "AnyEnemy" : "Self") && c.PublicState.TryGetValue("tags", out var actualTags) && actualTags == tags
            && c.Details is { CostsXEnergy: false, CostsXStar: false, RetainThisTurn: false,
                SlyThisTurn: false, BaseReplayCount: 0, ExhaustOnNextPlay: false, FreeThisTurn: false,
                FreeUntilPlayed: false, FreeThisCombat: false, StarCostThisTurn: null, EnergyModifiers.Length: 0 } details
            && details.LocalEnergyCost == cost && details.LocalStarCost == -1;
    }
}
