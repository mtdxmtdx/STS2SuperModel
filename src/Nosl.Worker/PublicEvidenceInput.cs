using System.Text.Json.Nodes;
using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Explicit v4 public-input boundary; never erase evidence to satisfy a legacy consumer.</summary>
internal static class PublicEvidenceInput
{
    internal const string DatasetVersion = "nosl.dataset.public-run-evidence.v1";
    internal static PublicCombatDecision Validate(DecisionPacket packet)
    {
        var evidence = packet.PublicEvidence ?? throw new ArgumentException("The v4 public input requires run evidence");
        // Revalidate the detached typed channel with the same strict wire codec.
        _ = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence));
        if (packet.Observation is not { Schema: PublicRunContext.ObservationSchema, RunContext: { } context } observation
            || evidence.Events[^1] is not { OwnerOrdinal: long owner, Payload: PublicCombatDecision decision })
            throw new ArgumentException("The v4 evidence prefix must end at the current v3 public combat decision");
        var start = evidence.Events.First(e => e.OwnerOrdinal == owner).Payload as PublicOwnerStarted;
        if (start is not { OwnerKind: PublicEvidenceOwnerKind.Combat } || start.ActIndex != context.ActIndex || start.Floor != context.Floor
            || packet.Status != decision.Status
            || PublicJson.Serialize(observation with { History = [] }) != PublicJson.Serialize(decision.Observation)
            || PublicJson.Serialize(packet.Actions) != PublicJson.Serialize(decision.Actions))
            throw new ArgumentException("The v4 evidence decision differs from its public root");
        if (evidence.CompleteFromRunStart && context.CompleteFromRunStart)
        {
            var combats = evidence.Events.Where(e => e.Payload is PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToArray();
            if (context.CombatEntryIndex != combats.Length - 1 || combats[^1].OwnerOrdinal != owner)
                throw new ArgumentException("The complete public combat count differs from recorded combat owners");
        }
        return decision;
    }
    internal static object Extend(object legacyInput, DecisionPacket packet)
    {
        if (packet.PublicEvidence is null) return legacyInput;
        var decision = Validate(packet);
        var input = JsonNode.Parse(PublicJson.Serialize(legacyInput))!.AsObject();
        input["schema_version"] = PublicRunEvidence.StudentSchema;
        input["history_complete"] = decision.HistoryCompleteFromCombatStart;
        input["public_evidence"] = JsonNode.Parse(PublicRunEvidenceJson.Serialize(packet.PublicEvidence));
        return input;
    }
    internal static void ValidateProfile(NativeRunExecutionOptions options, DecisionPacket packet)
    {
        if (options.EmitsPublicEvidence != (packet.PublicEvidence is not null))
            throw new ArgumentException("The declared evidence channel differs from the public packet");
        PublicMapObservationProfiles.ValidateEvidence(options.PublicMapObservationProfile, packet.PublicEvidence);
        if (packet.PublicEvidence is not null) Validate(packet);
    }
}
