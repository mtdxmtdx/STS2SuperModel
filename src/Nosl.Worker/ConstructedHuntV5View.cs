using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Full public capture for a newly executed declared-setup combat. Its input is
/// the session's recorded public decisions, never a label file or hidden history.
/// The fixture did not observe a native run start or map choice; both remain absent.
/// </summary>
internal static class ConstructedHuntV5View
{
    internal static DecisionPacket Observe(CombatSession session)
    {
        if (session.HasNativeProvenance || session.InitialScenario.ForcedEvent is not null)
            throw new NotSupportedException("Constructed v5 capture cannot replace native run or event-owner evidence");
        if (session.State.RunState is not RunState run)
            throw new NotSupportedException("Fresh constructed v5 capture requires concrete independently replayed run ownership");
        var current = session.Observe();
        var context = new PublicRunContext(PublicRunContext.Version, run.CurrentActIndex, run.TotalFloor, null, false);
        context.Validate();
        var recorder = new PublicRunEvidenceRecorder(null, null, PublicRunEvidence.CompleteMapVersion);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, context.ActIndex, context.Floor);
        DecisionPacket? last = null;
        foreach (string recorded in session.PublicTrace)
        {
            var packet = PublicJson.Read<DecisionPacket>(recorded);
            if (packet.Status is "player_decision" or "card_choice")
            {
                last = WithContext(packet, context);
                recorder.ObserveCombatDecision(owner, last);
            }
        }
        if (current.Status is "player_decision" or "card_choice")
        {
            var enriched = WithContext(current, context);
            if (last is null || PublicJson.Serialize(last) != PublicJson.Serialize(enriched))
                throw new InvalidOperationException("Fresh constructed public decision transcript is incomplete");
            var result = enriched with { PublicEvidence = recorder.Capture() };
            PublicEvidenceInput.Validate(result);
            return result;
        }
        if (current.Status == "terminal_settled")
        {
            recorder.ObserveCombatHistory(owner, session.Knowledge.Events);
            recorder.Record(owner, new PublicOwnerEnded(session.Room.Engine.Won
                ? PublicEvidenceOwnerOutcome.Victory : PublicEvidenceOwnerOutcome.Defeat,
                NativePublicRunEvidence.Assets(session.State.Players.Single())));
        }
        return current with { PublicEvidence = recorder.Capture() };
    }

    private static DecisionPacket WithContext(DecisionPacket packet, PublicRunContext context)
    {
        if (packet.PublicEvidence is not null || packet.Observation is not { Schema: "nosl.public.v2", RunContext: null } observation)
            throw new ArgumentException("Fresh recorded constructed combat packets required; existing v3/v5 captures are not rewritten");
        return packet with { Observation = observation with { Schema = PublicRunContext.ObservationSchema, RunContext = context } };
    }

    internal static object InactiveInput(DecisionPacket packet)
    {
        if (packet.PublicEvidence?.SchemaVersion != PublicRunEvidence.CompleteMapVersion)
            throw new ArgumentException("An actual v5 evidence capture is required");
        return PublicEvidenceInput.Extend(HuntStudentContext.LegacyInput(packet), packet);
    }

    internal static object HuntInput(HuntPlanAnchor anchor, DecisionPacket current, HuntPublicController? controller = null)
    {
        var original = PublicJson.Read<DecisionPacket>(anchor.PublicSummary);
        var initial = InactiveInput(original);
        _ = InactiveInput(current);
        var prefix = original.PublicEvidence!.Events;
        var events = current.PublicEvidence!.Events;
        if (events.Length < prefix.Length
            || PublicRunEvidenceJson.Serialize(new(PublicRunEvidence.CompleteMapVersion,
                original.PublicEvidence.CompleteFromRunStart, events.Take(prefix.Length).ToImmutableArray()))
                != PublicRunEvidenceJson.Serialize(original.PublicEvidence)
            || current.Observation!.RunContext != original.Observation!.RunContext)
            throw new ArgumentException("Full public evidence and run context must extend the immutable v5 anchor");
        var input = JsonNode.Parse(PublicJson.Serialize(PublicEvidenceInput.Extend(
            HuntStudentContext.Input(anchor, current, controller), current)))!.AsObject();
        input["controller_context"]!["anchor"] = JsonNode.Parse(PublicJson.Serialize(initial));
        return input;
    }
}
