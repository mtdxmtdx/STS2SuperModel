using System.Diagnostics;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeTapeProposalAudit(int SampleCall, int Attempt, NativeTapeRecipe Recipe,
    string Status, string Proposal, int DistinctTapeCells, int ConditionedTapeCells,
    double ElapsedSeconds, string? Detail = null, int ConditionedHpCount = 0, bool NeowConditionApplied = false, bool FirstRewardConditionApplied = false, int? ProposedFirstEncounterIndex = null);

/// <summary>Public-only conditional sampling under the explicit ideal tape prior.</summary>
internal sealed class NativeTapeReplaySource : ITeacherSource
{
    internal const string Profile = "owned-native-state-tape-structured-conditional-v6";
    private readonly string _serializedRoot;
    private readonly string _entryJson;
    private readonly NativeTapePrior _prior;
    private readonly NativeInitialShuffleCondition? _condition;
    private readonly NativeInitialHpCondition? _hpCondition;
    private readonly NativeNeowCondition? _neowCondition;
    private readonly NativeFirstRewardCondition? _firstRewardCondition;
    private readonly NativeFirstEncounterCondition? _firstEncounterCondition;
    private readonly CancellationToken _cancellation;
    private readonly List<NativeTapeProposalAudit> _attempts = [];
    private readonly string?[] _potions;
    private readonly int _publicDecisionIndex;
    private readonly int? _publicCombatIndex;
    private int _sampleCalls;

    internal NativeTapeReplaySource(DecisionPacket publicRoot, NativeTapePrior prior,
        bool enableConditioning = true, CancellationToken cancellationToken = default)
    {
        _prior = prior.Freeze(); _cancellation = cancellationToken;
        _serializedRoot = PublicJson.Serialize(publicRoot);
        var root = PublicJson.Read<DecisionPacket>(_serializedRoot);
        if (root.Status is not ("player_decision" or "card_choice") || root.Observation is null || root.Actions.Length == 0)
            throw new ArgumentException("An active public native decision is required");
        if (_prior.Execution.EmitsPublicRunContext)
        {
            if (root.Observation.Schema != PublicRunContext.ObservationSchema || root.Observation.RunContext is not { } context)
                throw new ArgumentException("The declared public context channel requires a complete v3 packet");
            context.Validate();
            bool unavailable = PublicRunContext.IsHistoryUnavailable(_prior.Execution.PublicCombatHistoryMode);
            if (context.CompleteFromRunStart == unavailable)
                throw new ArgumentException("Public recorder completeness differs from the declared observation channel");
            _publicCombatIndex = context.CombatEntryIndex;
            if (_publicCombatIndex is { } index && index >= _prior.EligibleCombats)
                throw new ArgumentException("Recorded public combat index is outside the declared coordinate prior");
        }
        else if (root.Observation.Schema != "nosl.public.v2" || root.Observation.RunContext is not null)
            throw new ArgumentException("Legacy tape prior requires the unchanged v2 public channel");
        _publicDecisionIndex = root.Actions[0].Revision;
        if (_publicDecisionIndex < 0 || _publicDecisionIndex >= _prior.EligibleDecisionsPerCombat
            || root.Actions.Any(action => action.Revision != _publicDecisionIndex)
            || root.Observation.History.Count(item => item.Kind == "action") != _publicDecisionIndex)
            throw new ArgumentException("Public native revision/history is outside the declared local decision prior");
        var entries = root.Observation.History.Where(e => e.Kind == NativeEntryAssets.EventKind).ToArray();
        if (entries.Length != 1) throw new ArgumentException("One complete published native entry anchor is required");
        _entryJson = entries[0].Detail;
        var entry = PublicJson.Read<NativeEntryAssets>(entries[0].Detail);
        if (entry.SchemaVersion != "nosl.native-entry-assets.v1" || entry.Hp != root.Observation.StartHp
            || entry.Hp > entry.MaxHp || entry.Potions is null)
            throw new ArgumentException("Invalid native public entry anchor");
        StartHp = entry.Hp; StartMaxHp = entry.MaxHp; _potions = entry.Potions.ToArray();
        if (enableConditioning && NativeInitialShuffleCondition.TryCreate(root, out var condition, out var reason))
        { _condition = condition; ConditioningReason = "certified_public_initial_draw_prefix"; }
        else
        {
            _condition = null;
            ConditioningReason = enableConditioning
                ? EligibilityReason(root) : "conditioning_disabled_for_reference";
        }
        if (enableConditioning && NativeInitialHpCondition.TryCreate(root, out var hpCondition, out _))
            _hpCondition = hpCondition;
        if (enableConditioning && NativeNeowCondition.TryCreate(root, _prior, out var neowCondition, out _))
            _neowCondition = neowCondition;
        if (enableConditioning && NativeFirstRewardCondition.TryCreate(root, _prior, out var firstRewardCondition, out _))
            _firstRewardCondition = firstRewardCondition;
        if (enableConditioning && NativeFirstEncounterCondition.TryCreate(root, _prior, out var firstEncounterCondition, out _))
            _firstEncounterCondition = firstEncounterCondition;
    }

    private static string EligibilityReason(DecisionPacket root)
    { NativeInitialShuffleCondition.TryCreate(root, out _, out var reason); return reason ?? "no_acceleration_certificate"; }
    public int StartHp { get; }
    public int StartMaxHp { get; }
    public string?[] StartPotions => _potions.ToArray();
    public DecisionPacket Observe() => PublicJson.Read<DecisionPacket>(_serializedRoot);
    public string PosteriorProfile => Profile;
    public string PriorWarning => "Separate ideal state-addressed random-tape law; SHA256 pseudorandom implementation, not the sequential run-seed posterior. Equal-state aliases and native primitive conversions are retained. Public local decision coordinates and certified primitive proposals use exact root-constant density corrections relative to the ideal law. Uncertified mechanisms retain native tape rejection; errors and budget exhaustion remain unresolved.";
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    internal bool UsesConditionalShuffle => _condition is not null;
    internal bool UsesConditionalHp => _hpCondition is not null;
    internal bool UsesConditionalNeow => _neowCondition is not null;
    internal bool UsesConditionalFirstReward => _firstRewardCondition is not null;
    internal bool UsesConditionalFirstEncounter => _firstEncounterCondition is not null;
    internal bool UsesPrimitiveConditioning => UsesConditionalShuffle || UsesConditionalHp || UsesConditionalNeow || UsesConditionalFirstReward || UsesConditionalFirstEncounter;
    internal int ConditionedPublicDecisionIndex => _publicDecisionIndex;
    internal int? ConditionedPublicCombatIndex => _publicCombatIndex;
    internal NativeTapeRecipe DrawConditionedRecipe(Rng random)
    {
        // Uniform coordinates are independent of all native/tape random words.
        // Complete public run history fixes combat C, cancelling a root-constant
        // 1/EligibleCombats. An unavailable recorder channel does not fix C.
        // Draw every original component before replacing known coordinates.
        var recipe = _prior.Draw(random);
        return recipe with { DecisionIndex = _publicDecisionIndex,
            CombatIndex = _publicCombatIndex ?? recipe.CombatIndex };
    }
    private string ProposalDescription => !UsesPrimitiveConditioning ? "plain_tape_rejection"
        : "conditional:" + string.Join("+", new[] { UsesConditionalNeow ? "neow" : null, UsesConditionalFirstReward ? "first_reward" : null, UsesConditionalFirstEncounter ? "first_encounters" : null,
            UsesConditionalHp ? "initial_hp" : null, UsesConditionalShuffle ? "initial_shuffle" : null }.OfType<string>());
    internal string ConditioningReason { get; }
    internal NativeTapeProposalAudit[] ProposalAudit => _attempts.ToArray();

    public async Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        int call = ++_sampleCalls;
        var random = new Rng(seed, "nosl-native-tape-independent-proposals-v1");
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _cancellation.ThrowIfCancellationRequested();
            // Native public revision equals this source bridge's local decision
            // count, including pending choices. Conditioning its uniform coordinate
            // removes only the root-constant factor 1/EligibleDecisionsPerCombat.
            // Consume the old draw first to keep the declared stream progression.
            var recipe = DrawConditionedRecipe(random);
            var tape = new NativeLabelTape(recipe, _condition, expectedEntryJson: _entryJson,
                hpCondition: _hpCondition, neowCondition: _neowCondition, firstRewardCondition: _firstRewardCondition, firstEncounterCondition: _firstEncounterCondition);
            var timer = Stopwatch.StartNew();
            NativeRunWorld? world = null; bool accepted = false; Exception? operationFailure = null;
            void Audit(string status, string? detail = null) => _attempts.Add(new(call, attempt, recipe, status,
                ProposalDescription, tape.DistinctCells, tape.ConditionedCells, timer.Elapsed.TotalSeconds, detail,
                tape.ConditionedHpCount, tape.NeowConditionApplied, tape.FirstRewardConditionApplied, tape.ProposedFirstEncounterIndex));
            try
            {
                world = await NativeRunWorld.OpenLabelTapeAsync(_prior.Execution, recipe, tape, _cancellation);
                if (world is null) Audit("absent_under_declared_source_horizon");
                else
                {
                    tape.ValidateProposalCompletion();
                    if (PublicJson.Serialize(world.Observe()) != _serializedRoot) Audit("public_packet_mismatch");
                    else if (!tape.AcceptCorrection(random.NextUnsignedLong)) Audit("exact_proposal_density_correction_rejected");
                    else { Audit("accepted"); accepted = true; return world; }
                }
            }
            catch (NativePublicConstraintMismatchException exception)
            { Audit("public_constraint_mismatch", exception.Message); }
            catch (Exception exception)
            {
                operationFailure = exception;
                Audit(exception is OperationCanceledException ? "computation_cancelled" : "proposal_engine_error",
                    exception.GetType().Name + ": " + exception.Message);
                throw;
            }
            finally
            {
                if (!accepted && world is not null)
                {
                    try { await world.DisposeAsync(); }
                    catch (Exception exception)
                    {
                        int index = _attempts.FindLastIndex(a => a.SampleCall == call && a.Attempt == attempt);
                        var failed = new NativeTapeProposalAudit(call, attempt, recipe, "proposal_cleanup_error",
                            ProposalDescription,
                            tape.DistinctCells, tape.ConditionedCells, timer.Elapsed.TotalSeconds,
                            exception.GetType().Name + ": " + exception.Message, tape.ConditionedHpCount, tape.NeowConditionApplied, tape.FirstRewardConditionApplied, tape.ProposedFirstEncounterIndex);
                        if (index < 0) _attempts.Add(failed); else _attempts[index] = failed;
                        if (operationFailure is not null)
                            throw new AggregateException("Tape proposal and cleanup failed", operationFailure, exception);
                        throw;
                    }
                }
            }
        }
        throw new PosteriorSamplingException(maxAttempts);
    }
}
