namespace Nosl.Contracts;

/// <summary>
/// Opt-in v3 continuation: the unchanged v2 mechanic certificate also applies to
/// a validated public run-context envelope. Progress/history fields do not change
/// the admitted combat mechanics, and never supply hidden information.
/// </summary>
public sealed class ContextualReviewedPublicRulePolicy : IPublicContinuationPolicy
{
    private readonly ReviewedPublicRulePolicy _reviewed = new();
    private readonly PublicRulePolicy _legacy = new();
    public string Id => PublicContinuationPolicies.ContextualReviewedId;

    public PublicAction Choose(DecisionPacket packet)
    {
        var observation = packet.Observation;
        if (observation is { Schema: "nosl.public.v2", RunContext: null })
            return _reviewed.Choose(packet);
        if (observation is not { Schema: PublicRunContext.ObservationSchema, RunContext: { } context })
            return _legacy.Choose(packet);
        try { context.Validate(); }
        catch (ArgumentException) { return _legacy.Choose(packet); }

        // Adapt only this local DTO view. Preserve the caller's observation,
        // evidence, legal action objects and v2 policy behavior byte for byte.
        return _reviewed.Choose(packet with
        {
            Observation = observation with { Schema = "nosl.public.v2", RunContext = null },
        });
    }
}
