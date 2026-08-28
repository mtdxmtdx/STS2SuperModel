namespace STS2BestChoice.Core.Execution;

/// <summary>
/// Prevents an executor from mistaking the idle frame immediately after enqueue
/// for completion. Completion requires an observable state change and an idle
/// action surface; a busy observation is retained for diagnostics but is not by
/// itself sufficient because some actions begin and finish between polls.
/// </summary>
public sealed class ActionSettlementTracker(string initialFingerprint)
{
    public bool SawBusy { get; private set; }
    public bool SawStateChange { get; private set; }

    public bool Observe(bool isBusy, string? fingerprint)
    {
        SawBusy |= isBusy;
        SawStateChange |= fingerprint is not null &&
                          !string.Equals(fingerprint, initialFingerprint, StringComparison.Ordinal);
        return !isBusy && SawStateChange;
    }
}
