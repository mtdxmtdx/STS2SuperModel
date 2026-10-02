using System.Diagnostics;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeTapeProposalAudit(int SampleCall, int Attempt, NativeTapeRecipe Recipe,
    string Status, string Proposal, int DistinctTapeCells, int ConditionedTapeCells,
    double ElapsedSeconds, string? Detail = null, int ConditionedHpCount = 0, bool NeowConditionApplied = false, bool FirstRewardConditionApplied = false, int? ProposedFirstEncounterIndex = null, NativeTapeRecipe? AuxiliaryRecipe = null,
    NativeComponentStats? InitialPrefixStats = null, int? InitialPrefixMaxTrials = null, string? InitialPrefixCorrection = null, int? InitialPrefixRunSeedDraws = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RewardsTapeCells = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedPublicRewardCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicRewardLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicRewardEnvelope = null);

/// <summary>Public-only conditional sampling under the explicit ideal tape prior.</summary>
internal sealed class NativeTapeReplaySource : ITeacherSource
{
    internal const string Profile = "owned-native-state-tape-structured-conditional-v7";
    private readonly string _serializedRoot;
    private readonly string _entryJson;
    private readonly NativeTapePrior _prior;
    private readonly NativeInitialShuffleCondition? _condition;
    private readonly NativeInitialHpCondition? _hpCondition;
    private readonly NativeNeowCondition? _neowCondition;
    private readonly NativeFirstRewardCondition? _firstRewardCondition;
    private readonly NativeFirstEncounterCondition? _firstEncounterCondition;
    private readonly NativeInitialPrefixCondition? _initialPrefixCondition;
    private readonly NativePublicRewardCondition? _publicRewardCondition;
    private readonly NativePublicCombatPrefixCondition? _publicCombatCondition;
    private readonly PublicRunEvidence? _expectedPublicEvidence;
    private readonly int _initialPrefixMaxTrials;
    private readonly CancellationToken _cancellation;
    private readonly List<NativeTapeProposalAudit> _attempts = [];
    private readonly string?[] _potions;
    private readonly int _publicDecisionIndex;
    private readonly int? _publicCombatIndex;
    private int _sampleCalls;

    internal NativeTapeReplaySource(DecisionPacket publicRoot, NativeTapePrior prior,
        bool enableConditioning = true, CancellationToken cancellationToken = default, int initialPrefixMaxTrials = 64)
    {
        _prior = prior.Freeze(); _cancellation = cancellationToken;
        if (initialPrefixMaxTrials is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(initialPrefixMaxTrials));
        _initialPrefixMaxTrials = initialPrefixMaxTrials;
        _serializedRoot = PublicJson.Serialize(publicRoot);
        var root = PublicJson.Read<DecisionPacket>(_serializedRoot);
        if (root.Status is not ("player_decision" or "card_choice") || root.Observation is null || root.Actions.Length == 0)
            throw new ArgumentException("An active public native decision is required");
        PublicEvidenceInput.ValidateProfile(_prior.Execution, root);
        _expectedPublicEvidence = _prior.UsesRewardsProvenance ? root.PublicEvidence : null;
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
        if (enableConditioning && !_prior.UsesRewardsProvenance && NativeFirstRewardCondition.TryCreate(root, _prior, out var firstRewardCondition, out _))
            _firstRewardCondition = firstRewardCondition;
        if (enableConditioning && NativeFirstEncounterCondition.TryCreate(root, _prior, out var firstEncounterCondition, out _))
            _firstEncounterCondition = firstEncounterCondition;
        if (enableConditioning && NativeInitialPrefixCondition.TryCreate(root, _prior, out var initialPrefixCondition, out _))
            _initialPrefixCondition = initialPrefixCondition;
        if (enableConditioning && _prior.UsesRewardsProvenance && root.PublicEvidence is { CompleteFromRunStart: true } evidence)
        {
            NaturalSourceCollector.InitializeNativeModels();
            var publicRewards = NativePublicRewardCondition.Create(evidence);
            if (publicRewards.Targets.Count > 0) _publicRewardCondition = publicRewards;
        }
        if (enableConditioning && _prior.UsesRewardsProvenance && root.PublicEvidence is not null)
        {
            var combats = NativePublicCombatPrefixCondition.Create(root);
            if (combats.EligibleShuffleCount > 0)
            {
                _publicCombatCondition = combats;
                _condition = null; _hpCondition = null; // Never force a current startup twice.
            }
        }
    }

    private static string EligibilityReason(DecisionPacket root)
    { NativeInitialShuffleCondition.TryCreate(root, out _, out var reason); return reason ?? "no_acceleration_certificate"; }
    public int StartHp { get; }
    public int StartMaxHp { get; }
    public string?[] StartPotions => _potions.ToArray();
    public DecisionPacket Observe() => PublicJson.Read<DecisionPacket>(_serializedRoot);
    public string PosteriorProfile => _prior.UsesRewardsProvenance ? "owned-native-rewards-state-tape-conditional-v1-public-evidence-v1"
        : _prior.Execution.EmitsPublicEvidence ? Profile + "-public-evidence-v1" : Profile;
    public string PriorWarning => _prior.UsesRewardsProvenance
        ? "Separate hybrid ideal oracle: player Rewards use shared origin/initial-seed/raw-cursor cells, independent of all other native full-state cells. Exact clones, source-partition restores and same-lineage recreation retain sharing. Cross-partition and Rewards orbit-offset coincidences no longer share words. SHA256 is a reproducible ideal-oracle implementation, not exact finite-seed inference. Full recorded public evidence is conditioned; primary card identity proposals retain exact latent-rarity likelihood and a fixed-root catalog envelope. Other mechanisms retain native replay; errors and budgets remain unresolved."
        : "Separate ideal state-addressed random-tape law; SHA256 pseudorandom implementation, not the sequential run-seed posterior. Equal-state aliases and native primitive conversions are retained. Public local decision coordinates and certified primitive proposals use exact root-constant density corrections relative to the ideal law. Uncertified mechanisms retain native tape rejection; errors and budget exhaustion remain unresolved.";
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    internal bool UsesConditionalShuffle => _condition is not null;
    internal bool UsesConditionalHp => _hpCondition is not null;
    internal bool UsesConditionalNeow => _neowCondition is not null;
    internal bool UsesConditionalFirstReward => _firstRewardCondition is not null;
    internal bool UsesConditionalFirstEncounter => _firstEncounterCondition is not null;
    internal bool UsesConditionalInitialPrefix => _initialPrefixCondition is not null;
    internal bool UsesConditionalPublicRewards => _publicRewardCondition is not null;
    internal int PublicRewardTargetCount => _publicRewardCondition?.Targets.Count ?? 0;
    internal bool UsesConditionalPublicCombats => _publicCombatCondition is not null;
    internal int PublicCombatShuffleTargets => _publicCombatCondition?.EligibleShuffleCount ?? 0;
    internal int PublicCombatHpTargets => _publicCombatCondition?.EligibleHpCount ?? 0;
    internal bool UsesPrimitiveConditioning => UsesConditionalShuffle || UsesConditionalHp || UsesConditionalNeow || UsesConditionalFirstReward || UsesConditionalFirstEncounter || UsesConditionalInitialPrefix || UsesConditionalPublicRewards || UsesConditionalPublicCombats;
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
        : "conditional:" + string.Join("+", new[] { UsesConditionalNeow ? "neow" : null, UsesConditionalFirstReward ? "first_reward" : null, UsesConditionalFirstEncounter ? "first_encounters" : null, UsesConditionalInitialPrefix ? "joint_initial_prefix" : null, UsesConditionalPublicRewards ? "public_reward_identities" : null, UsesConditionalPublicCombats ? "public_combat_startups" : null,
            UsesConditionalHp ? "initial_hp" : null, UsesConditionalShuffle ? "initial_shuffle" : null }.OfType<string>());
    internal string ConditioningReason { get; }
    internal NativeTapeProposalAudit[] ProposalAudit => _attempts.ToArray();
    private static string? Fraction(ShuffleRational? value) => value is { } p
        ? p.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
            + p.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

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
            var auxiliaryRecipe = DrawConditionedRecipe(random);
            var recipe = auxiliaryRecipe;
            NativeInitialPrefixProposal? prefixPlan = null;
            NativeComponentStats? prefixStats = null;
            int? prefixRunSeeds = null;
            var tape = NativeLabelTape.ForDeclaredPrior(_prior, recipe, _condition, expectedEntryJson: _entryJson,
                hpCondition: _hpCondition, neowCondition: _neowCondition, firstRewardCondition: _firstRewardCondition, firstEncounterCondition: _firstEncounterCondition,
                publicRewardCondition: _publicRewardCondition, publicCombatCondition: _publicCombatCondition,
                expectedPublicEvidence: _expectedPublicEvidence);
            var timer = Stopwatch.StartNew();
            NativeRunWorld? world = null; bool accepted = false; Exception? operationFailure = null;
            void Audit(string status, string? detail = null) => _attempts.Add(new(call, attempt, recipe, status,
                ProposalDescription, tape.DistinctCells, tape.ConditionedCells, timer.Elapsed.TotalSeconds, detail,
                tape.ConditionedHpCount, tape.NeowConditionApplied, tape.FirstRewardConditionApplied, tape.ProposedFirstEncounterIndex,
                _initialPrefixCondition is null ? null : auxiliaryRecipe, prefixStats,
                _initialPrefixCondition is null ? null : _initialPrefixMaxTrials,
                prefixPlan is null ? null : NativeInitialPrefixProposal.CorrectionClaim, prefixRunSeeds,
                _prior.UsesRewardsProvenance ? tape.RewardsCells : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedPublicRewardCards : null,
                Fraction(tape.PublicRewardRatio), Fraction(tape.PublicRewardEnvelope)));
            try
            {
                if (_initialPrefixCondition is not null)
                {
                    prefixPlan = _initialPrefixCondition.Prepare(auxiliaryRecipe, _initialPrefixMaxTrials, _cancellation);
                    prefixStats = prefixPlan.Stats;
                    prefixRunSeeds = prefixPlan.RunSeedDraws;
                    recipe = prefixPlan.SelectedRecipe;
                    tape = NativeLabelTape.ForDeclaredPrior(_prior, recipe, _condition, expectedEntryJson: _entryJson,
                        hpCondition: _hpCondition, neowCondition: _neowCondition,
                        firstRewardCondition: _firstRewardCondition, firstEncounterCondition: _firstEncounterCondition,
                        initialPrefixPlan: prefixPlan, publicRewardCondition: _publicRewardCondition, publicCombatCondition: _publicCombatCondition,
                        expectedPublicEvidence: _expectedPublicEvidence);
                }
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
            catch (NativeComponentBudgetExceededException exception)
            {
                prefixStats = exception.Stats;
                prefixRunSeeds = NativeInitialPrefixCondition.FailureRunSeedDraws(exception);
                Audit("component_budget_exhausted", exception.Message);
                var inconclusive = new PosteriorSamplingException(attempt);
                inconclusive.Data["component"] = exception.Component;
                inconclusive.Data["component_complete_trials"] = exception.Stats.CompletedTrials;
                operationFailure = inconclusive;
                throw inconclusive;
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                prefixStats ??= NativeComponentRejection.FailureStats(exception);
                prefixRunSeeds ??= NativeInitialPrefixCondition.FailureRunSeedDraws(exception);
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
                            exception.GetType().Name + ": " + exception.Message, tape.ConditionedHpCount, tape.NeowConditionApplied, tape.FirstRewardConditionApplied, tape.ProposedFirstEncounterIndex,
                            _initialPrefixCondition is null ? null : auxiliaryRecipe, prefixStats,
                            _initialPrefixCondition is null ? null : _initialPrefixMaxTrials,
                            prefixPlan is null ? null : NativeInitialPrefixProposal.CorrectionClaim, prefixRunSeeds,
                            _prior.UsesRewardsProvenance ? tape.RewardsCells : null,
                            _prior.UsesRewardsProvenance ? tape.ConditionedPublicRewardCards : null,
                            Fraction(tape.PublicRewardRatio), Fraction(tape.PublicRewardEnvelope));
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
