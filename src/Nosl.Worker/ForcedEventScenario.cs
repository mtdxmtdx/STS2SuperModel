namespace Nosl.Worker;

/// <summary>
/// A declared event fixture, not a naturally traversed run or a certified carry-in prior.
/// The native event validates each option and owns every pre-combat effect and reward.
/// FixtureFloor positions its native floor counter without simulating preceding rooms.
/// Merchant slots refer to the native visible inventory; potion slots refer to the fixture inventory.
/// </summary>
public sealed record ForcedEventScenario(string Event, string Act, string[] OptionKeys,
    int FixtureFloor = 6, int? FoulPotionSlot = null, int[]? PurchasedRelicSlots = null,
    bool InspectMerchantInventory = false)
{
    internal ForcedEventScenario Copy() => this with
    {
        OptionKeys = OptionKeys?.ToArray() ?? throw new ArgumentException("An explicit event option path is required."),
        PurchasedRelicSlots = PurchasedRelicSlots?.ToArray(),
    };
}

/// <summary>
/// A declared pre-combat choice is unavailable under this sampled setup. This is
/// a rejected prior proposal, not a game defeat or an unexpected engine failure.
/// Direct fixture creation still reports it as an invalid scenario argument.
/// </summary>
public sealed class ConstructedSetupRejectedException(string message) : ArgumentException(message);
