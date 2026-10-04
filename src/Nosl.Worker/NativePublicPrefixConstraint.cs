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

    /// <summary>Consumes one validated detached append without recapturing or rescanning its prefix.</summary>
    internal void Observe(PublicRunEvidenceEvent observed)
    {
        if (CheckedEvents == _events.Length) return;
        if (observed.EventOrdinal != CheckedEvents)
            throw new InvalidOperationException("Owned public evidence observer skipped or repeated an event during replay");
        int index = CheckedEvents;
        if (PublicJson.Serialize(observed) != _events[index])
        {
            var payload = _targetEvents[index].Payload;
            string kind = payload is PublicCombatFact fact ? "combat_fact:" + fact.FactKind : payload.GetType().Name;
            throw new NativePublicConstraintMismatchException($"Published run evidence differs at event {index} ({kind})");
        }
        CheckedEvents++;
    }

    internal void Check(PublicRunEvidence? observed)
    {
        if (observed is null) throw new InvalidOperationException("Owned hybrid replay omitted its public evidence channel");
        if (observed.Events.Length < CheckedEvents)
            throw new InvalidOperationException("Owned public evidence prefix was truncated during replay");
        int through = Math.Min(_events.Length, observed.Events.Length);
        for (int i = CheckedEvents; i < through; i++) Observe(observed.Events[i]);
    }
}
