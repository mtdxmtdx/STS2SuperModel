namespace Nosl.Contracts;

public sealed record HuntPlanAnchor(string PublicSummary, int StartPlayerTurn, int DeadlinePlayerTurn,
    int HpSafetyFloor, string TemplateId, string BaselinePolicyId)
{
    public string Goal => "TheHunt fatal with an actually offered extra CardReward";
}
public sealed record HuntPublicController(HuntPlanAnchor Anchor, string Status, string? ExitReason, int LastObservedPlayerTurn);

/// <summary>One explicit finite public-information template, not a damage predictor or general bonus planner.</summary>
public sealed class FiniteHuntPolicy : IPublicContinuationPolicy
{
    public const string Version = "nosl-finite-hunt-next-turn-v1";
    private readonly PublicRulePolicy _baseline = new();
    public HuntPublicController Controller { get; private set; }
    public string Id => Controller.Anchor.TemplateId;

    public static HuntPlanAnchor Anchor(DecisionPacket packet, int hpSafetyFloor = 10)
    {
        var o = packet.Observation ?? throw new ArgumentException("Public anchor required");
        if (packet.Status != "player_decision" || o.Turn < 1 || hpSafetyFloor < 0)
            throw new ArgumentException("A stable player anchor and nonnegative public HP guard are required");
        return new(PublicJson.Serialize(packet), o.Turn, checked(o.Turn + 1), hpSafetyFloor,
            Version + ":hp-floor=" + hpSafetyFloor, new PublicRulePolicy().Id);
    }

    public FiniteHuntPolicy(HuntPlanAnchor anchor)
    {
        if (anchor.StartPlayerTurn < 1 || anchor.DeadlinePlayerTurn != checked(anchor.StartPlayerTurn + 1)
            || anchor.HpSafetyFloor < 0 || string.IsNullOrWhiteSpace(anchor.PublicSummary)
            || anchor.TemplateId != Version + ":hp-floor=" + anchor.HpSafetyFloor || anchor.BaselinePolicyId != _baseline.Id)
            throw new ArgumentException("Invalid immutable Hunt anchor");
        Controller = new(anchor, "active", null, anchor.StartPlayerTurn);
    }

    public PublicAction Choose(DecisionPacket packet)
    {
        var o = packet.Observation ?? throw new ArgumentException("Public observation required");
        if (o.Turn < Controller.LastObservedPlayerTurn) throw new ArgumentException("Public turn moved backwards");
        Controller = Controller with { LastObservedPlayerTurn = o.Turn };
        if (Controller.Status != "active") return _baseline.Choose(packet);
        if (o.Turn > Controller.Anchor.DeadlinePlayerTurn) return Exit(packet, "fixed_deadline_expired");
        if (o.Choice is not null) return _baseline.Choose(packet);
        // This is a conservative heuristic exit, not a guarantee against death or a new risk preference.
        if (o.Hp <= Controller.Anchor.HpSafetyFloor) return Exit(packet, "public_hp_safety_guard");
        if (o.Exhaust.Any(c => c.Id == "TheHunt")) return Exit(packet, "designated_card_spent_without_finish");
        var hunt = packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id == "TheHunt");
        if (hunt is not null) return hunt; // Never duplicate TheHunt's damage/fatal rules here.
        var draw = packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id is "Backflip" or "Acrobatics" or "Prepared" or "ThinkingAhead");
        if (draw is not null) return draw;
        if (o.Turn == Controller.Anchor.DeadlinePlayerTurn) return Exit(packet, "deadline_turn_no_legal_hunt_or_draw");
        var preview = o.Enemies.Sum(e => e.Intents.Sum(i => (i.Damage ?? 0) * (i.Repeats ?? 1)));
        var defend = packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id is "DefendSilent" or "Survivor");
        if (defend is not null && preview > o.Block) return defend;
        return packet.Actions.Single(a => a.Kind == "end_turn");
    }

    private PublicAction Exit(DecisionPacket packet, string reason)
    {
        Controller = Controller with { Status = "aborted", ExitReason = reason };
        return _baseline.Choose(packet);
    }
}
