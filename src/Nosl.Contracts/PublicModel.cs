using System.Text.Json;

namespace Nosl.Contracts;

public sealed record PublicCard(string Id, int Upgrade, int Cost, int StarCost, string Type, string[] Keywords);
public sealed record CardCount(PublicCard Card, int Count);
public sealed record KnownPosition(int Position, PublicCard Card);
public sealed record PublicPower(string Id, decimal Amount);
public sealed record PublicIntent(string Kind, int? Damage, int? Repeats);
public sealed record PublicEnemy(int Slot, string Id, int Hp, int MaxHp, decimal Block, PublicPower[] Powers, PublicIntent[] Intents);
public sealed record PublicEvent(string Kind, string Detail);
public sealed record PublicChoice(string Source, int Min, int Max, bool Cancelable, PublicCard[] Candidates);
// No object references, internal IDs, seed, private RNG state, move-state IDs or teacher metadata.
public sealed record PublicObservation(string Schema, int StartHp, int Ascension, int Turn, int Hp, int MaxHp,
    decimal Block, int Energy, int Stars, PublicCard[] Hand, PublicCard[] Discard, PublicCard[] Exhaust,
    CardCount[] UnknownDraw, KnownPosition[] KnownDraw, int DrawCount, string?[] Potions,
    string[] Relics, PublicPower[] Powers, PublicEnemy[] Enemies, PublicEvent[] History, PublicChoice? Choice);
public sealed record PublicAction(int Revision, string Kind, int Slot = -1, int Target = -1, int[]? Selection = null);
public sealed record DecisionPacket(string Status, PublicObservation? Observation, PublicAction[] Actions);
public sealed record TerminalFacts(string Result, int StartHp, int FinalHp, int StartMaxHp, int FinalMaxHp,
    string?[] Potions, PublicEvent[] Events, string Boundary, int RewardSelectionsMade);

public static class PublicJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new ArgumentException("Empty JSON value");
}

// This policy is a diagnostic continuation, NOT the V4 trained student or M4 teacher.
// Its type signature makes access to a hypothetical world impossible.
public static class PublicDiagnosticPolicy
{
    public static PublicAction Choose(DecisionPacket packet)
    {
        if (packet.Observation is null || packet.Actions.Length == 0) throw new InvalidOperationException("No decision");
        var o = packet.Observation;
        if (o.Choice is not null) return packet.Actions.First(a => a.Kind == "choose");
        var actions = packet.Actions;
        return actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Type == "Attack")
            ?? actions.FirstOrDefault(a => a.Kind == "play")
            ?? actions.First(a => a.Kind == "end_turn");
    }
}
