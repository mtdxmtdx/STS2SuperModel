using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>
/// Early rejection of an append-only owned public recorder prefix. This observes
/// no native state and changes no words or proposal probabilities. Complete final
/// packet equality is still required. Once the target prefix ends, continuation
/// is free to observe new events from its candidate action.
/// </summary>
internal sealed class NativePublicPrefixConstraint(PublicRunEvidence target)
{
    private readonly System.Collections.Immutable.ImmutableArray<PublicRunEvidenceEvent> _targetEvents = target.Events;
    private readonly string[] _events = target.Events.Select(PublicJson.Serialize).ToArray();
    internal int CheckedEvents { get; private set; }

    internal void Check(PublicRunEvidence? observed)
    {
        if (observed is null) throw new InvalidOperationException("Owned hybrid replay omitted its public evidence channel");
        if (observed.Events.Length < CheckedEvents)
            throw new InvalidOperationException("Owned public evidence prefix was truncated during replay");
        int through = Math.Min(_events.Length, observed.Events.Length);
        for (int i = CheckedEvents; i < through; i++)
        {
            if (PublicJson.Serialize(observed.Events[i]) != _events[i])
            {
                var payload = _targetEvents[i].Payload;
                string kind = payload is PublicCombatFact fact ? "combat_fact:" + fact.FactKind : payload.GetType().Name;
                throw new NativePublicConstraintMismatchException($"Published run evidence differs at event {i} ({kind})");
            }
            CheckedEvents++;
        }
    }
}
