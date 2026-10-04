using System.Text.Json;

namespace Nosl.Contracts;

public sealed record RegenPlanAnchor(string PublicSummary, int StartPlayerTurn, int DeadlinePlayerTurn,
    int InitialRegen, string TemplateId, string BaselinePolicyId);
public sealed record RegenPublicController(RegenPlanAnchor Anchor, string Status, string? ExitReason, int LastObservedPlayerTurn);

/// <summary>A bounded public healing template. Native cards, powers and end turns alone execute the effects.</summary>
public sealed class FiniteRegenPolicy : IPublicContinuationPolicy
{
    public const string Version = "nosl-finite-regen-v1";
    private readonly PublicRulePolicy _baseline = new();
    private PublicEvent[] _lastHistory;
    public RegenPublicController Controller { get; private set; }
    public string Id => Controller.Anchor.TemplateId;

    public static RegenPlanAnchor Anchor(DecisionPacket packet)
    {
        var o = packet.Observation ?? throw new ArgumentException("Public anchor required");
        if (packet.Status != "player_decision" || !ReviewedScope(o) || !HistoryTurnMatches(o))
            throw new NotSupportedException("Outside reviewed finite Regen public scope");
        int regen = Regen(o);
        return new(PublicJson.Serialize(packet), o.Turn, checked(o.Turn + Math.Max(1, regen)), regen,
            Version, new PublicRulePolicy().Id);
    }

    public FiniteRegenPolicy(RegenPlanAnchor anchor)
    {
        var original = PublicJson.Read<DecisionPacket>(anchor.PublicSummary);
        if (Anchor(original) != anchor) throw new ArgumentException("Invalid immutable Regen anchor");
        Controller = new(anchor, "active", null, anchor.StartPlayerTurn);
        _lastHistory = original.Observation!.History.ToArray();
    }

    public PublicAction Choose(DecisionPacket packet)
    {
        var o = packet.Observation ?? throw new ArgumentException("Public observation required");
        if (o.Turn < Controller.LastObservedPlayerTurn) throw new ArgumentException("Public turn moved backwards");
        Controller = Controller with { LastObservedPlayerTurn = o.Turn };
        if (Controller.Status != "active") return _baseline.Choose(packet);
        var origin = PublicJson.Read<DecisionPacket>(Controller.Anchor.PublicSummary).Observation!;
        if (o.History.Length < _lastHistory.Length
            || PublicJson.Serialize(o.History.Take(_lastHistory.Length)) != PublicJson.Serialize(_lastHistory))
            return Exit(packet, "public_history_incomplete");
        _lastHistory = o.History.ToArray();
        if (!ReviewedScope(o) || o.StartHp != origin.StartHp || o.MaxHp != origin.MaxHp
            || Regen(o) > Controller.Anchor.InitialRegen || o.Hp < origin.Hp)
            return Exit(packet, "reviewed_safety_scope_failed");
        if (!HistoryTurnMatches(o) || !ObservedProgress(origin, o)) return Exit(packet, "public_history_incomplete");
        if (o.Turn > Controller.Anchor.DeadlinePlayerTurn) return Exit(packet, "fixed_deadline_expired");
        if (o.Hp == o.MaxHp) return Exit(packet, "full_hp", o.Hp > origin.Hp);
        if (Regen(o) == 0) return Exit(packet, "regen_exhausted", o.Hp > origin.Hp);
        if (o.Turn >= Controller.Anchor.DeadlinePlayerTurn) return Exit(packet, "fixed_deadline_expired");
        // The certificate excludes damage modifiers and alternative attacks. Only the
        // actual current public block is trusted; no card/healing simulator is duplicated.
        decimal incoming = o.Enemies.Single().Intents.Single().Damage!.Value;
        if (o.Block < incoming)
        {
            var defend = packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id == "Finesse");
            if (defend is not null) return defend;
            return Exit(packet, "no_safe_defense");
        }
        var end = packet.Actions.FirstOrDefault(a => a.Kind == "end_turn");
        return end ?? Exit(packet, "no_safe_defense");
    }

    private PublicAction Exit(DecisionPacket packet, string reason, bool finished = false)
    {
        Controller = Controller with { Status = finished ? "finished" : reason == "public_history_incomplete" ? "unresolved" : "aborted", ExitReason = reason };
        return _baseline.Choose(packet);
    }

    public static int Regen(PublicObservation o) => (int)(o.Powers.SingleOrDefault(p => p.Id == "RegenPower")?.Amount ?? 0);

    public static bool HistoryTurnMatches(PublicObservation o)
    {
        int turn = 0;
        foreach (var e in o.History.Where(e => e.Kind == "player_turn"))
        {
            if (!int.TryParse(e.Detail, out int next) || next < turn) return false;
            turn = next;
        }
        return turn == o.Turn;
    }

    public static bool ObservedProgress(PublicObservation origin, PublicObservation current)
    {
        decimal delta = 0; int? lastHp = null; bool consumedTick = false, ended = false;
        foreach (var e in current.History.Skip(origin.History.Length))
        {
            if (e.Kind == "player_turn_ended") ended = true;
            if (e.Kind is not ("power_changed" or "damage")) continue;
            using var parsed = JsonDocument.Parse(e.Detail); var detail = parsed.RootElement;
            if (detail.GetProperty("target").GetString() != "player") continue;
            if (e.Kind == "damage") lastHp = detail.GetProperty("hpAfter").GetInt32();
            else if (detail.GetProperty("id").GetString() == "RegenPower")
            {
                decimal amount = detail.GetProperty("amount").GetDecimal();
                delta += amount; consumedTick |= amount < 0;
            }
        }
        if (lastHp is null && delta == 0) return current.Hp == origin.Hp && Regen(current) == Regen(origin);
        return current.Turn > origin.Turn && ended && consumedTick && lastHp == current.Hp
            && Regen(origin) + delta == Regen(current);
    }

    public static bool ReviewedScope(PublicObservation o)
    {
        if (o.Schema != "nosl.public.v2" || o.Ascension != 10 || o.Turn < 1 || o.Hp <= 0 || o.Hp > o.MaxHp
            || o.Choice is not null || o.Potions.Any(p => p is not null) || o.Stars != 0
            || o.OrbCapacity != 0 || o.Orbs is not { Length: 0 } || o.Pets is not { Length: 0 }
            || o.Exhaust.Length != 0 || o.UnidentifiedDrawCount != 0 || o.DrawCount < 0
            || o.UnknownDraw.Any(x => x.Count <= 0) || o.UnknownDraw.Sum(x => (long)x.Count) + o.KnownDraw.Length != o.DrawCount
            || o.KnownDraw.Any(x => x.Position < 0 || x.Position >= o.DrawCount)
            || o.KnownDraw.Select(x => x.Position).Distinct().Count() != o.KnownDraw.Length
            || o.Enemies is not [{ Id: "TwigSlimeS", Hp: > 0 and <= 6, Block: 0, Powers.Length: 0,
                Intents: [{ Kind: "Attack", Damage: 5, Repeats: 1 }] }]
            || o.Powers.Length > 1 || o.Powers.Any(p => p.Id != "RegenPower" || p.Amount < 1 || p.Amount > 5
                || p.Amount != decimal.Truncate(p.Amount) || p.SkipNextDurationTick || p.SelectedCard is not null
                || p.SelectedUpgrade is not null)) return false;
        if (!o.Relics.SequenceEqual(new[] { "RingOfTheSnake" })
            || o.RelicStates is not [{ Id: "RingOfTheSnake", SelectedModel: null, Cards.Length: 0 }]
            || o.RelicStates[0].Details.Count != 4
            || !o.RelicStates[0].Details.TryGetValue("isWax", out var wax) || wax != 0
            || !o.RelicStates[0].Details.TryGetValue("isMelted", out var melted) || melted != 0
            || !o.RelicStates[0].Details.TryGetValue("isUsedUp", out var used) || used != 0
            || !o.RelicStates[0].Details.TryGetValue("stackCount", out var stack) || stack != 1) return false;
        if (o.Hand.Length + o.Discard.Length + o.DrawCount != 3) return false;
        var cards = o.Hand.Concat(o.Discard).Concat(o.KnownDraw.Select(x => x.Card))
            .Concat(o.UnknownDraw.SelectMany(x => Enumerable.Repeat(x.Card, x.Count))).ToArray();
        return cards.Length == 3 && cards.Count(c => c.Id == "Finesse") == 2
            && cards.Count(c => c.Id == "StrikeSilent") == 1 && cards.All(PlainCard);
    }

    private static bool PlainCard(PublicCard c)
    {
        bool attack = c.Id == "StrikeSilent"; int cost = attack ? 1 : 0;
        return c.Id is "Finesse" or "StrikeSilent" && c.Upgrade == 0 && c.Cost == cost && c.StarCost == -1
            && c.Type == (attack ? "Attack" : "Skill") && c.Keywords.Length == 0
            && c.Enchantments is { Length: 0 } && c.Affliction is null
            && c.PublicState is { Count: 2 } && c.PublicState.TryGetValue("targetType", out var target)
            && target == (attack ? "AnyEnemy" : "Self") && c.PublicState.TryGetValue("tags", out var tags)
            && tags == (attack ? "Strike" : "")
            && c.Details is { CostsXEnergy: false, CostsXStar: false, RetainThisTurn: false, SlyThisTurn: false,
                BaseReplayCount: 0, ExhaustOnNextPlay: false, FreeThisTurn: false, FreeUntilPlayed: false,
                FreeThisCombat: false, StarCostThisTurn: null, EnergyModifiers.Length: 0 } details
            && details.LocalEnergyCost == cost && details.LocalStarCost == -1;
    }
}
