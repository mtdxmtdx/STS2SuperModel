namespace Nosl.Contracts;

/// <summary>
/// Explicit observation choices. Omission retains the original slice and native
/// option enumeration. Coordinate order forgets only option enumeration; the
/// separate complete-graph channel adds a declared current-act capture. None of
/// these profiles certify visibility or display order on a live client.
/// </summary>
public static class PublicMapObservationProfiles
{
    public const string CoordinateOrderV1 = "nosl.public-map-options.coordinate-order.v1";
    public const string CompleteGraphV1 = "nosl.public-map-complete-graph.v1";

    public static bool UsesCoordinateOrder(string? profile) => profile switch
    {
        null => false,
        CoordinateOrderV1 or CompleteGraphV1 => true,
        _ => throw new ArgumentException("Unknown public map observation profile", nameof(profile)),
    };

    public static bool UsesCompleteGraph(string? profile)
    {
        _ = UsesCoordinateOrder(profile);
        return profile == CompleteGraphV1;
    }

    public static bool ValidateChannel(string? profile, string? evidenceProfile, string? contextProfile)
    {
        bool canonical = UsesCoordinateOrder(profile);
        bool evidence = PublicRunEvidence.ValidateChannel(evidenceProfile, contextProfile);
        if (canonical && !evidence)
            throw new ArgumentException("A public map observation profile requires public run evidence");
        if ((profile == CompleteGraphV1) != (evidenceProfile == PublicRunEvidence.CompleteMapVersion))
            throw new ArgumentException("The complete-graph map channel requires the separate v2 evidence version");
        return evidence;
    }

    public static bool IsCoordinateOrdered(PublicMapObserved map)
    {
        for (int i = 1; i < map.Options.Length; i++)
        {
            var previous = map.Options[i - 1].Coordinate;
            var current = map.Options[i].Coordinate;
            if (previous.Row > current.Row || previous.Row == current.Row && previous.Col >= current.Col)
                return false;
        }
        return true;
    }

    public static void ValidateEvidence(string? profile, PublicRunEvidence? evidence)
    {
        bool canonical = UsesCoordinateOrder(profile);
        bool completeGraph = UsesCompleteGraph(profile);
        if (evidence is not null && completeGraph != (evidence.SchemaVersion == PublicRunEvidence.CompleteMapVersion))
            throw new ArgumentException("The public map profile differs from its evidence version");
        var maps = evidence?.Events.Select(e => e.Payload).OfType<PublicMapObserved>().ToArray() ?? [];
        if (maps.Any(map => completeGraph != (map.CurrentMap is not null)))
            throw new ArgumentException("The declared public map channel differs from its current map capture field");
        if (completeGraph && maps.Any(map =>
            !map.Nodes.Select(n => n.Coordinate).SequenceEqual(map.Nodes.OrderBy(n => n.Coordinate.Row)
                .ThenBy(n => n.Coordinate.Col).Select(n => n.Coordinate))
            || !map.Edges.Select(e => (e.From, e.To)).SequenceEqual(map.Edges.OrderBy(e => e.From.Row)
                .ThenBy(e => e.From.Col).ThenBy(e => e.To.Row).ThenBy(e => e.To.Col).Select(e => (e.From, e.To)))))
            throw new ArgumentException("The complete-graph map channel requires canonical slice arrays");
        if (canonical && (evidence is null || maps.Any(map => !IsCoordinateOrdered(map))))
            throw new ArgumentException("The declared coordinate-order map channel requires canonical public map options");
    }
}
