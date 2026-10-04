using System.Globalization;
using System.Text.Json;
using Nosl.Contracts;

namespace Nosl.Tests;

public sealed class PublicNumericTests
{
    private static DecisionPacket Packet(decimal value)
    {
        var card = new PublicCard("StrikeSilent", 0, 1, 0, "Attack", [],
            Enchantments: [new("Sharp", value)], PublicState: new Dictionary<string, string> { ["damage"] = "2.0" });
        var power = new PublicPower("WeakPower", value);
        var detail = PublicJson.Serialize(new { target = "player", blocked = value, unblocked = value,
            overkill = 0M, hpAfter = 58, killed = false, card });
        var observation = new PublicObservation("nosl.public.v2", 60, 10, 1, 60, 70, value, 3, 0,
            [card], [], [], [new(card, 1)], [], 1, [], [], [power],
            [new(0, "TwigSlimeS", 20, 20, value, [power], [])], [new("damage", detail)], null,
            Orbs: [new("Lightning", value, value)]);
        return new("player_decision", observation, [new(0, "play", 0, 0), new(0, "end_turn")]);
    }

    [Fact]
    public void EquivalentDecimalScalesHaveIdenticalPublicDtoSerialization()
    {
        Assert.Equal("2", PublicJson.Serialize(2M));
        Assert.Equal(PublicJson.Serialize(2M), PublicJson.Serialize(2.000M));
        Assert.Equal("0", PublicJson.Serialize(new decimal(0, 0, 0, true, 28)));
        Assert.Equal(PublicJson.Serialize(Packet(2M)), PublicJson.Serialize(Packet(2.000M)));
        using var json = JsonDocument.Parse(PublicJson.Serialize(Packet(2.000M)));
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("observation").GetProperty("block").ValueKind);
    }

    [Fact]
    public void ProducerGeneratedEmbeddedEventDetailIsCanonicalRecursively()
    {
        var a = Packet(2M).Observation!.History[0];
        var b = Packet(2.000M).Observation!.History[0];
        Assert.Equal(a.Detail, b.Detail);
        Assert.Equal(PublicJson.Serialize(a), PublicJson.Serialize(b));
        using var json = JsonDocument.Parse(b.Detail);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("blocked").ValueKind);
        Assert.Equal(2M, json.RootElement.GetProperty("card").GetProperty("enchantments")[0].GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData("0.1234567890123456789012345678")]
    [InlineData("1.2345678901234567890123456789")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("-2.5000")]
    public void FractionsAndFullPrecisionRoundTripWithoutBinaryFloat(string literal)
    {
        var value = decimal.Parse(literal, CultureInfo.InvariantCulture);
        var encoded = PublicJson.Serialize(value);
        Assert.Equal(value, PublicJson.Read<decimal>(encoded));
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(JsonValueKind.Number, json.RootElement.ValueKind);
        Assert.Equal(value, json.RootElement.GetDecimal());
        Assert.Equal(encoded, PublicJson.Serialize(PublicJson.Read<decimal>(literal)));
    }

    [Fact]
    public void DecimalWriterIsInvariantCultureAndReaderKeepsNumberContract()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("2.5", PublicJson.Serialize(2.500M));
            Assert.Equal(2.5M, PublicJson.Read<decimal>("2.5"));
            Assert.Equal(2M, PublicJson.Read<decimal>("2e0"));
            Assert.Equal(2.5M, PublicJson.Read<PublicPower>("{\"ID\":\"WeakPower\",\"AMOUNT\":2.5}").Amount);
            Assert.Throws<JsonException>(() => PublicJson.Read<decimal>("\"2.5\""));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void PublicTreeKeysAndChoicesAreInvariantToDecimalScale()
    {
        var a = Packet(2M);
        var b = Packet(2.000M);
        Assert.Equal(FrozenPublicTreePolicy.InformationKey(a), FrozenPublicTreePolicy.InformationKey(b));
        var policy = new FrozenPublicTreePolicy("numeric-test", new Dictionary<string, PublicAction>
        { [FrozenPublicTreePolicy.InformationKey(a)] = a.Actions[1] });
        Assert.Equal(b.Actions[1], policy.Choose(b));
        Assert.NotEqual(FrozenPublicTreePolicy.InformationKey(a), FrozenPublicTreePolicy.InformationKey(Packet(2.5M)));
    }

    [Fact]
    public void StringsAndArrayOrderRemainUntouched()
    {
        Assert.Equal("[\"2\",\"2.0\",\"02\"]", PublicJson.Serialize(new[] { "2", "2.0", "02" }));
        Assert.Equal("[2,1]", PublicJson.Serialize(new[] { 2.00M, 1.0M }));
        // PublicEvent.Detail is text: arbitrary supplied strings are not parsed or rewritten.
        Assert.NotEqual(PublicJson.Serialize(new PublicEvent("damage", "{\"blocked\":2}")),
            PublicJson.Serialize(new PublicEvent("damage", "{\"blocked\":2.0}")));
        var a = Packet(2M);
        var reversed = a with { Actions = a.Actions.Reverse().ToArray() };
        Assert.NotEqual(FrozenPublicTreePolicy.InformationKey(a), FrozenPublicTreePolicy.InformationKey(reversed));
    }
}
