using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nosl.Contracts;

public sealed record PublicCostModifier(string Kind, int Amount);
public sealed record PublicCardEffect(string Id, decimal Amount);
public sealed record PublicCardDetails(bool CostsXEnergy, bool CostsXStar, int LocalEnergyCost, int LocalStarCost,
    bool RetainThisTurn, bool SlyThisTurn, int BaseReplayCount, bool ExhaustOnNextPlay,
    bool FreeThisTurn, bool FreeUntilPlayed, bool FreeThisCombat, int? StarCostThisTurn,
    PublicCostModifier[] EnergyModifiers);
public sealed record PublicCard(string Id, int Upgrade, int Cost, int StarCost, string Type, string[] Keywords,
    PublicCardDetails? Details = null, PublicCardEffect[]? Enchantments = null,
    PublicCardEffect? Affliction = null, IReadOnlyDictionary<string,string>? PublicState = null);
public sealed record CardCount(PublicCard Card, int Count);
public sealed record KnownPosition(int Position, PublicCard Card);
public sealed record PublicPower(string Id, decimal Amount, int AmountOnTurnStart = 0, bool SkipNextDurationTick = false,
    string? SelectedCard = null, int? SelectedUpgrade = null, int? ApplierSlot = null);
public sealed record PublicRelic(string Id, IReadOnlyDictionary<string,int> Details, PublicCard[]? Cards = null, string? SelectedModel = null);
public sealed record PublicOrb(string Id, decimal Passive, decimal Evoke);
public sealed record PublicPet(int Slot, string Id, int Hp, int MaxHp, decimal Block, PublicPower[] Powers);
public sealed record PublicCounters(int AttacksPlayed, int SkillsPlayed, int ShivsPlayed, int CardsDiscarded,
    int CardsDrawnCombat, int CardsPlayedCombat, int CardsGeneratedCombat, int CardsPlayed,
    int ManualCardsPlayed, int PlaysStarted, int AttacksStarted, int ZeroCostAttacksStarted,
    int AttackOrSkillStarts, int FirstInSeriesStarts, decimal StarsGained);
public sealed record PublicIntent(string Kind, int? Damage, int? Repeats);
public sealed record PublicEnemy(int Slot, string Id, int Hp, int MaxHp, decimal Block, PublicPower[] Powers, PublicIntent[] Intents);
public sealed record PublicEvent(string Kind, string Detail);
public sealed record PublicChoice(string Source, int Min, int Max, bool Cancelable, PublicCard[] Candidates,
    string CandidateOrder = "public", PublicCard[][]? Bundles = null);
// No object references, internal IDs, seed, private RNG state, move-state IDs or teacher metadata.
public sealed record PublicObservation(string Schema, int StartHp, int Ascension, int Turn, int Hp, int MaxHp,
    decimal Block, int Energy, int Stars, PublicCard[] Hand, PublicCard[] Discard, PublicCard[] Exhaust,
    CardCount[] UnknownDraw, KnownPosition[] KnownDraw, int DrawCount, string?[] Potions,
    string[] Relics, PublicPower[] Powers, PublicEnemy[] Enemies, PublicEvent[] History, PublicChoice? Choice,
    PublicCounters? Counters = null, PublicRelic[]? RelicStates = null, int Gold = 0, int StartGold = 0,
    int OrbCapacity = 0, PublicOrb[]? Orbs = null, PublicPet[]? Pets = null, int UnidentifiedDrawCount = 0);
public sealed record PublicAction(int Revision, string Kind, int Slot = -1, int Target = -1, int[]? Selection = null);
public sealed record DecisionPacket(string Status, PublicObservation? Observation, PublicAction[] Actions);
public sealed record TerminalFacts(string Result, int StartHp, int FinalHp, int StartMaxHp, int FinalMaxHp,
    string?[] Potions, PublicEvent[] Events, string Boundary, int RewardSelectionsMade);

public static class PublicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Converters = { new CanonicalDecimalConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new ArgumentException("Empty JSON value");

    private sealed class CanonicalDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            // 29 significant digits retain all decimal precision and remove scale-only zeros.
            // Write a JSON number, never a string or an intermediate binary floating value.
            writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }
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
