using System.Diagnostics;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeRunProposalAudit(int SampleCall, int Attempt, NativeRunRecipe Recipe,
    string Status, double ElapsedSeconds, string? Detail = null);

/// <summary>
/// Generic public posterior. This object receives a detached public packet and a
/// declared generative prior, never the original run seed, state or audit trace.
/// Every accepted graph is generated independently by the native run lifecycle.
/// </summary>
internal sealed class NativeRunReplaySource : ITeacherSource
{
    internal const string Profile = "owned-native-run-slot-rejection-v1";
    private readonly DecisionPacket _root;
    private readonly string _serializedRoot;
    private readonly NativeRunPrior _prior;
    private readonly CancellationToken _cancellation;
    private readonly Func<NativeRunRecipe, CancellationToken, Task<ITeacherWorld?>> _open;
    private readonly List<NativeRunProposalAudit> _attempts = [];
    private readonly string?[] _startPotions;
    private int _sampleCalls;

    internal NativeRunReplaySource(DecisionPacket publicRoot, NativeRunPrior prior,
        CancellationToken cancellationToken = default,
        Func<NativeRunRecipe, CancellationToken, Task<ITeacherWorld?>>? open = null)
    {
        _prior = prior.Freeze(); _cancellation = cancellationToken;
        _serializedRoot = PublicJson.Serialize(publicRoot);
        _root = PublicJson.Read<DecisionPacket>(_serializedRoot);
        if (_root.Status is not ("player_decision" or "card_choice") || _root.Observation is null || _root.Actions.Length == 0)
            throw new ArgumentException("An active public native root is required");
        PublicEvidenceInput.ValidateProfile(_prior.Execution, _root);
        var entryEvents = _root.Observation.History.Where(e => e.Kind == NativeEntryAssets.EventKind).ToArray();
        if (entryEvents.Length != 1) throw new ArgumentException("A complete published native entry asset anchor is required");
        var entry = PublicJson.Read<NativeEntryAssets>(entryEvents[0].Detail);
        if (entry.SchemaVersion != "nosl.native-entry-assets.v1" || entry.Hp != _root.Observation.StartHp
            || entry.MaxHp < entry.Hp || entry.Potions is null)
            throw new ArgumentException("Invalid published native entry anchor");
        StartHp = entry.Hp; StartMaxHp = entry.MaxHp; _startPotions = entry.Potions.ToArray();
        _open = open ?? OpenNativeAsync;
    }

    public int StartHp { get; }
    public int StartMaxHp { get; }
    public string?[] StartPotions => _startPotions.ToArray();
    public DecisionPacket Observe() => PublicJson.Read<DecisionPacket>(_serializedRoot);
    public string PosteriorProfile => _prior.Execution.EmitsPublicEvidence ? Profile + "-public-evidence-v1" : Profile;
    public string PriorWarning => _prior.Execution.EmitsPublicEvidence
        ? "Independent owned native run and fixed-slot prior; all recorded public run evidence is included in exact packet equality. Unrecorded histories remain marginalized. No actual source seed or hidden graph is input. Declared source horizons limit coverage; proposal exhaustion is computationally inconclusive."
        : "Independent owned native run and fixed-slot prior; conditioned only on the published combat packet/history; earlier run histories are marginalized. No actual source seed or hidden graph is input. Declared source horizons limit coverage; proposal exhaustion is computationally inconclusive.";
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    internal NativeRunPrior Prior => _prior.Freeze();
    internal NativeRunProposalAudit[] ProposalAudit => _attempts.ToArray();

    private async Task<ITeacherWorld?> OpenNativeAsync(NativeRunRecipe recipe, CancellationToken cancellation) =>
        await NativeRunWorld.OpenAsync(_prior.Execution, recipe.IndependentRunSeed, recipe.Slot, cancellation);

    public async Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        int call = ++_sampleCalls;
        var random = new Rng(seed, "nosl-owned-native-run-independent-proposals-v1");
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var recipe = _prior.Draw(random);
            var timer = Stopwatch.StartNew();
            ITeacherWorld? world = null;
            bool accepted = false;
            Exception? operationFailure = null;
            try
            {
                world = await _open(recipe, _cancellation);
                if (world is null)
                    _attempts.Add(new(call, attempt, recipe, "absent_under_declared_source_horizon", timer.Elapsed.TotalSeconds));
                else if (PublicJson.Serialize(world.Observe()) != _serializedRoot)
                    _attempts.Add(new(call, attempt, recipe, "public_packet_mismatch", timer.Elapsed.TotalSeconds));
                else
                {
                    accepted = true;
                    _attempts.Add(new(call, attempt, recipe, "accepted", timer.Elapsed.TotalSeconds));
                    return world;
                }
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                // Retrying failures as if they were observed nonmatches would select
                // cheap/easy proposals. Stop this draw and retain unresolved mass.
                _attempts.Add(new(call, attempt, recipe,
                    exception is OperationCanceledException ? "computation_cancelled" : "proposal_engine_error",
                    timer.Elapsed.TotalSeconds, exception.GetType().Name + ": " + exception.Message));
                throw;
            }
            finally
            {
                if (!accepted && world is not null)
                {
                    try { await world.DisposeAsync(); }
                    catch (Exception cleanupFailure)
                    {
                        int index = _attempts.FindLastIndex(a => a.SampleCall == call && a.Attempt == attempt);
                        var failed = new NativeRunProposalAudit(call, attempt, recipe, "proposal_cleanup_error",
                            timer.Elapsed.TotalSeconds, cleanupFailure.GetType().Name + ": " + cleanupFailure.Message);
                        if (index >= 0) _attempts[index] = failed; else _attempts.Add(failed);
                        if (operationFailure is not null)
                            throw new AggregateException("Proposal execution and cleanup both failed", operationFailure, cleanupFailure);
                        throw;
                    }
                }
            }
        }
        throw new PosteriorSamplingException(maxAttempts);
    }
}
