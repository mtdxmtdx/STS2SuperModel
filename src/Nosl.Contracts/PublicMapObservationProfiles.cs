namespace Nosl.Contracts;

/// <summary>
/// Explicit observation choices over the existing map DTO. Omission retains the
/// original producer enumeration; coordinate order intentionally forgets only
/// option enumeration. This is not a claim about a live client's display order.
/// </summary>
public static class PublicMapObservationProfiles
{
    public const string CoordinateOrderV1 = "nosl.public-map-options.coordinate-order.v1";

    public static bool UsesCoordinateOrder(string? profile) => profile switch
    {
        null => false,
        CoordinateOrderV1 => true,
        _ => throw new ArgumentException("Unknown public map observation profile", nameof(profile)),
    };

    public static bool ValidateChannel(string? profile, string? evidenceProfile, string? contextProfile)
    {
        bool canonical = UsesCoordinateOrder(profile);
        bool evidence = PublicRunEvidence.ValidateChannel(evidenceProfile, contextProfile);
        if (canonical && !evidence)
            throw new ArgumentException("A public map observation profile requires public run evidence");
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
        if (!UsesCoordinateOrder(profile)) return;
        if (evidence is null || evidence.Events.Select(e => e.Payload).OfType<PublicMapObserved>()
            .Any(map => !IsCoordinateOrdered(map)))
            throw new ArgumentException("The declared coordinate-order map channel requires canonical public map options");
    }
}
