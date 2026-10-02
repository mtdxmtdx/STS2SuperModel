namespace Nosl.Contracts;

/// <summary>Opt-in public student contract. No counterfactual values or sampling metadata.</summary>
public static class HuntStudentContext
{
    public const string PublicSchema = "nosl.student.public.v2";
    public const string ContextSchema = "nosl.controller.finite-hunt.v1";

    public static object LegacyInput(DecisionPacket packet) => new
    {
        schema_version = "nosl.student.public.v1", observation = packet.Observation,
        history_complete = true, controller_context = new { status = "inactive" },
        candidate_actions = packet.Actions, legal_mask = packet.Actions.Select(_ => true).ToArray(),
    };

    public static object Input(HuntPlanAnchor anchor, DecisionPacket packet, HuntPublicController? controller = null)
    {
        _ = new FiniteHuntPolicy(anchor); // Validate the immutable, versioned template identity.
        var initial = PublicJson.Read<DecisionPacket>(anchor.PublicSummary);
        var origin = initial.Observation ?? throw new ArgumentException("Public anchor missing");
        var current = packet.Observation ?? throw new ArgumentException("Public observation missing");
        if (initial.Status != "player_decision" || origin.Turn != anchor.StartPlayerTurn || origin.Enemies.Length != 1
            || packet.Status is not ("player_decision" or "card_choice") || current.Turn < origin.Turn
            || current.History.Length < origin.History.Length
            || PublicJson.Serialize(current.History.Take(origin.History.Length)) != PublicJson.Serialize(origin.History))
            throw new ArgumentException("Current public history must extend the original anchor");
        controller ??= new(anchor, "active", null, current.Turn);
        if (controller.Anchor != anchor || controller.LastObservedPlayerTurn != current.Turn
            || controller.Status is not ("active" or "aborted" or "unresolved")
            || (controller.Status == "active") != (controller.ExitReason is null))
            throw new ArgumentException("Controller does not describe this immutable anchor and public boundary");
        return new
        {
            schema_version = PublicSchema, observation = current, history_complete = true,
            controller_context = new
            {
                schemaVersion = ContextSchema, status = controller.Status, exitReason = controller.ExitReason,
                anchor = LegacyInput(initial), target = new { cardId = "TheHunt", enemySlot = origin.Enemies[0].Slot },
                startPlayerTurn = anchor.StartPlayerTurn, deadlinePlayerTurn = anchor.DeadlinePlayerTurn,
                hpSafetyFloor = anchor.HpSafetyFloor, lastObservedPlayerTurn = current.Turn,
                templateId = anchor.TemplateId, baselinePolicyId = anchor.BaselinePolicyId,
                observedEvents = current.History.Skip(origin.History.Length).ToArray(),
            },
            candidate_actions = packet.Actions, legal_mask = packet.Actions.Select(_ => true).ToArray(),
        };
    }
}
