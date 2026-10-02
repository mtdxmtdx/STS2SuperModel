using Nosl.Contracts;

namespace Nosl.Worker;

public sealed partial class CombatSession
{
    // Owned, stable execution origin. Never clone a suspended coroutine and never
    // recover a sampled choice by going back to its original Scenario seed.
    private CombatSession? _choiceReplayOrigin;
    private readonly List<PublicAction> _choiceReplayActions = [];
    private readonly List<string> _choiceReplayPackets = [];
    private static readonly string[] ReviewedChoiceCards = ["Survivor", "Prepared", "Acrobatics", "ThinkingAhead", "DaggerThrow"];

    internal bool HasConditionalChoiceOrigin => _request is not null && _choiceReplayOrigin is not null;

    private async ValueTask CaptureChoiceOriginAsync(PublicAction action)
    {
        if (_request is not null) return; // A suffix can contain another choice response.
        await ReleaseChoiceOriginAsync();
        // This optimization is intentionally only the existing constructed 24-card
        // family. Native import and arbitrary setup/generation choices remain blocked
        // or use their existing conservative whole-setup posterior.
        if (HasNativeProvenance || action.Kind != "play"
            || !ReviewedChoiceCards.Contains(State.Players[0].PlayerCombatState!.Hand.Cards[action.Slot].GetType().Name)
            || !BeliefSampler.UsesExchangeablePosterior(this)) return;
        _choiceReplayOrigin = ForkExact();
        _choiceReplayPackets.Add(PublicJson.Serialize(_choiceReplayOrigin.Observe()));
    }

    private async ValueTask RecordChoiceSuffixAsync(PublicAction action, DecisionPacket packet)
    {
        if (_choiceReplayOrigin is null) return;
        if (packet.Status != "card_choice") { await ReleaseChoiceOriginAsync(); return; }
        _choiceReplayActions.Add(action with { Selection = action.Selection?.ToArray() });
        _choiceReplayPackets.Add(PublicJson.Serialize(packet));
    }

    private async ValueTask ReleaseChoiceOriginAsync()
    {
        var origin = _choiceReplayOrigin;
        _choiceReplayOrigin = null;
        _choiceReplayActions.Clear(); _choiceReplayPackets.Clear();
        if (origin is not null) await origin.DisposeAsync();
    }

    internal bool TryGetConditionalChoiceOrigin(out CombatSession origin,
        out PublicAction[] actions, out string[] packets)
    {
        origin = null!; actions = []; packets = [];
        if (!HasConditionalChoiceOrigin || HasNativeProvenance
            || !BeliefSampler.UsesExchangeablePosterior(_choiceReplayOrigin!)) return false;
        if (_choiceReplayPackets.Count != _choiceReplayActions.Count + 1
            || _choiceReplayPackets[0] != PublicJson.Serialize(_choiceReplayOrigin!.Observe())
            || _choiceReplayPackets[^1] != PublicJson.Serialize(Observe()))
            throw new NotSupportedException("public_history_incomplete: conditional choice replay requires its exact complete public suffix");
        origin = _choiceReplayOrigin!;
        actions = _choiceReplayActions.Select(a => a with { Selection = a.Selection?.ToArray() }).ToArray();
        packets = _choiceReplayPackets.ToArray();
        return true;
    }

    private async Task<CombatSession> ForkConditionalChoiceAsync()
    {
        if (!TryGetConditionalChoiceOrigin(out var origin, out var actions, out var packets))
            throw new NotSupportedException("No reviewed stable choice replay origin");
        var branch = origin.ForkExact();
        try
        {
            // Exact re-execution of this world's origin, including its sampled future
            // streams. Every resulting branch owns a fresh coroutine and a fresh origin.
            if (!await ReplayChoiceSuffixAsync(branch, actions, packets))
                throw new InvalidOperationException("Sampled choice origin diverged from its public suffix");
            return branch;
        }
        catch { await branch.DisposeAsync(); throw; }
    }

    internal static async Task<bool> ReplayChoiceSuffixAsync(CombatSession proposed,
        IReadOnlyList<PublicAction> actions, IReadOnlyList<string> packets)
    {
        for (int index = 0; index <= actions.Count; index++)
        {
            if (PublicJson.Serialize(proposed.Observe()) != packets[index]) return false;
            if (index < actions.Count) await proposed.StepAsync(actions[index]);
        }
        return proposed.HasConditionalChoiceOrigin;
    }
}
