namespace Nosl.Contracts;

public static class RegenStudentContext
{
    public const string ContextSchema = "nosl.controller.finite-regen.v1";
    public static object Input(RegenPlanAnchor anchor, DecisionPacket packet, RegenPublicController? controller = null)
    {
        _ = new FiniteRegenPolicy(anchor);
        var initial = PublicJson.Read<DecisionPacket>(anchor.PublicSummary);
        var origin = initial.Observation!;
        var current = packet.Observation ?? throw new ArgumentException("Public observation required");
        if (packet.Status != "player_decision" || current.Turn < origin.Turn || current.History.Length < origin.History.Length
            || PublicJson.Serialize(current.History.Take(origin.History.Length)) != PublicJson.Serialize(origin.History))
            throw new ArgumentException("Current public history must extend original anchor");
        controller ??= new(anchor, "active", null, current.Turn);
        if (controller.Anchor != anchor || controller.LastObservedPlayerTurn != current.Turn
            || controller.Status is not ("active" or "finished" or "aborted" or "unresolved")
            || (controller.Status == "active") != (controller.ExitReason is null))
            throw new ArgumentException("Controller must describe the immutable anchor and public boundary");
        if (!FiniteRegenPolicy.HistoryTurnMatches(current)) throw new ArgumentException("Public turn differs from its history");
        if (controller.Status == "finished" && (!FiniteRegenPolicy.ReviewedScope(current)
            || current.StartHp != origin.StartHp || current.MaxHp != origin.MaxHp || current.Hp <= origin.Hp
            || current.Turn > anchor.DeadlinePlayerTurn || !FiniteRegenPolicy.ObservedProgress(origin, current)
            || controller.ExitReason is not ("full_hp" or "regen_exhausted")
            || controller.ExitReason == "full_hp" && current.Hp != current.MaxHp
            || controller.ExitReason == "regen_exhausted" && FiniteRegenPolicy.Regen(current) != 0))
            throw new ArgumentException("Finished benefit requires matching observed public progress and safety scope");
        return new
        {
            schema_version = HuntStudentContext.PublicSchema, observation = current, history_complete = true,
            controller_context = new
            {
                schemaVersion = ContextSchema, kind = "safe_finite_regen", status = controller.Status, exitReason = controller.ExitReason,
                anchor = HuntStudentContext.LegacyInput(initial), target = new { powerId = "RegenPower" },
                startPlayerTurn = anchor.StartPlayerTurn, deadlinePlayerTurn = anchor.DeadlinePlayerTurn,
                initialRegen = anchor.InitialRegen, lastObservedPlayerTurn = current.Turn,
                templateId = anchor.TemplateId, baselinePolicyId = anchor.BaselinePolicyId,
                observedEvents = current.History.Skip(origin.History.Length).ToArray(),
            },
            candidate_actions = packet.Actions, legal_mask = packet.Actions.Select(_ => true).ToArray(),
        };
    }
}
