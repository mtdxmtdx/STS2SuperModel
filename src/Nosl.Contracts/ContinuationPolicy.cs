namespace Nosl.Contracts;

/// <summary>A decision-only boundary: implementations cannot receive a simulator world.</summary>
public interface IPublicContinuationPolicy
{
    string Id { get; }
    PublicAction Choose(DecisionPacket packet);
}

public sealed class PublicRulePolicy : IPublicContinuationPolicy
{
    public string Id => "nosl-public-rules-v1";
    public PublicAction Choose(DecisionPacket packet)
    {
        var o = packet.Observation ?? throw new ArgumentException("A public observation is required");
        if (packet.Actions.Length == 0) throw new ArgumentException("No legal candidates");
        if (o.Choice is not null) return packet.Actions.First(a => a.Kind == "choose");
        // Intent is the engine's public base preview, not a reimplementation of damage rules.
        var incoming = o.Enemies.Sum(e => e.Intents.Sum(i => (i.Damage ?? 0) * (i.Repeats ?? 1)));
        double Score(PublicAction a)
        {
            if (a.Kind == "end_turn") return 0;
            if (a.Kind != "play") return -1; // Potions are compared at roots; no invented permanent prices.
            var c = o.Hand[a.Slot];
            if (c.Type == "Attack") return 100 - c.Cost * .01;
            if (c.Id is "DefendSilent" or "Survivor") return incoming > o.Block ? 60 : -1;
            return 40 - c.Cost * .01;
        }
        return packet.Actions.Select((a, i) => (a, i, score: Score(a)))
            .OrderByDescending(x => x.score).ThenBy(x => x.i).First().a;
    }
}

public sealed class FrozenPublicTreePolicy : IPublicContinuationPolicy
{
    private readonly IReadOnlyDictionary<string, PublicAction> _actions;
    private readonly IPublicContinuationPolicy _fallback;
    public string Id { get; }
    public int NodeCount => _actions.Count;
    public FrozenPublicTreePolicy(string id, IReadOnlyDictionary<string, PublicAction> actions,
        IPublicContinuationPolicy? fallback = null)
    {
        Id = id; _actions = new Dictionary<string, PublicAction>(actions, StringComparer.Ordinal);
        _fallback = fallback ?? new PublicRulePolicy();
    }
    public static string InformationKey(DecisionPacket packet) => PublicJson.Serialize(packet);
    public PublicAction Choose(DecisionPacket packet)
    {
        if (_actions.TryGetValue(InformationKey(packet), out var action))
        {
            var encoded = PublicJson.Serialize(action);
            return packet.Actions.Single(a => PublicJson.Serialize(a) == encoded);
        }
        return _fallback.Choose(packet);
    }
}
