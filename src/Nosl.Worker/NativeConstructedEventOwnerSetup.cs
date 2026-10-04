using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// A fresh declared native event location and public option rule. No prior rooms,
/// map observations, owner graph, source seed or private event state are imported.
/// </summary>
internal sealed record NativeConstructedEventOwnerSetup
{
    internal const string EncounterName = "MysteriousKnightEventEncounter";
    internal const string FixedChoiceRule = "keep-the-key-then-fight-v1";
    public string Event { get; init; } = "TheLanternKey";
    public string Act { get; init; } = "Hive";
    public int FixtureFloor { get; init; } = 1;
    public string ChoiceRule { get; init; } = FixedChoiceRule;

    internal NativeConstructedEventOwnerSetup Freeze()
    {
        if (Event != "TheLanternKey" || Act != "Hive" || ChoiceRule != FixedChoiceRule
            || FixtureFloor < 1 || FixtureFloor > new Hive().BaseNumberOfRooms)
            throw new ArgumentException("Declared event setup requires TheLanternKey in Hive, a legal fixture floor, and keep-the-key-then-fight-v1");
        return this with { };
    }

    internal RunState CreateRun(string independentRunSeed) => CombatSession.CreateForcedEventRun(independentRunSeed,
        new(Event, Act, ["KEEP_THE_KEY", "FIGHT"], FixtureFloor));

    internal int Choose(int choiceIndex, IReadOnlyList<(string Key, bool IsLocked)> visibleOptions)
    {
        string required = choiceIndex switch
        {
            0 => "KEEP_THE_KEY", 1 => "FIGHT",
            _ => throw new InvalidOperationException("The declared event option rule cannot select an additional event page"),
        };
        var matching = Enumerable.Range(0, visibleOptions.Count)
            .Where(i => visibleOptions[i].Key == required && !visibleOptions[i].IsLocked).ToArray();
        if (matching.Length != 1)
            throw new InvalidOperationException("Declared native event option is not uniquely available: " + required);
        return matching[0];
    }

    internal void ValidatePublicOwner(PublicRunEvidence evidence, PublicRunEvidenceEvent combat)
    {
        var events = evidence.Events.Where(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Event }).ToArray();
        if (events.Length != 1 || events[0].OwnerOrdinal is not long owner
            || events[0].Payload is not PublicOwnerStarted { CompleteFromOwnerStart: true,
                ParentOwnerOrdinal: null, ActIndex: 1 } start || start.Floor != FixtureFloor
            || combat.Payload is not PublicOwnerStarted { CompleteFromOwnerStart: true, ActIndex: 1 } child
            || child.Floor != FixtureFloor || child.ParentOwnerOrdinal != owner)
            throw new ArgumentException("One complete declared TheLanternKey event-to-combat owner chain is required");
        var history = evidence.Events.Where(e => e.OwnerOrdinal == owner).ToArray();
        if (history.Length != 5 || history[0] != events[0]
            || history[1].Payload is not PublicOptionsObserved first
            || !first.Options.Select(o => (o.Key, o.IsLocked)).SequenceEqual(new[]
                { ("RETURN_THE_KEY", false), ("KEEP_THE_KEY", false) })
            || history[2].Payload is not PublicOptionChosen keep || keep.OfferEventOrdinal != history[1].EventOrdinal
            || keep.Key != "KEEP_THE_KEY" || first.Options.Any(o => o.Price is not null)
            || history[3].Payload is not PublicOptionsObserved second
            || !second.Options.Select(o => (o.Key, o.IsLocked)).SequenceEqual(new[] { ("FIGHT", false) })
            || history[4].Payload is not PublicOptionChosen fight || fight.OfferEventOrdinal != history[3].EventOrdinal
            || fight.Key != "FIGHT" || second.Options.Any(o => o.Price is not null)
            || history[4].EventOrdinal >= combat.EventOrdinal)
            throw new ArgumentException("Declared TheLanternKey public options and KEEP_THE_KEY then FIGHT choices must be recorded exactly");
    }
}
