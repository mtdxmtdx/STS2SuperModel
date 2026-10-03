using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Detach mutable packet DTOs while retaining its validated immutable evidence prefix.</summary>
internal static class PublicDecisionSnapshot
{
    internal static DecisionPacket Copy(DecisionPacket packet)
    {
        if (packet.PublicEvidence is null)
            return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet));
        // PublicRunEvidence owns immutable events and payloads. Legacy mutable
        // DTO leaves are copied on construction and on access; Append returns a
        // new prefix. Sharing that snapshot therefore shares no mutable policy
        // state. Keep the established JSON detach for every other packet field.
        // External wire input still passes through the strict evidence decoder.
        var detached = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet with { PublicEvidence = null }));
        return detached with { PublicEvidence = packet.PublicEvidence };
    }
}
