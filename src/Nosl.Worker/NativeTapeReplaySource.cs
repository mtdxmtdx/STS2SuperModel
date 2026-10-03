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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicRewardEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedNeowCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NeowCardLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NeowCardEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedResourcePresence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedResourceGold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedResourcePotions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicResourceLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicResourceEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedSlugIntents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SlugIntentLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SlugIntentEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedWeakFormations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WeakFormationLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WeakFormationEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedReshuffles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReshuffleLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReshuffleEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedMonsterRolls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedMonsterBranches = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MonsterBranchLikelihood = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MonsterBranchEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedWeakEncounters = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WeakEncounterEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeComponentStats? EventPermutationStats = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EventPermutationMaxTrials = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EventPermutationCorrection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConditionedPublicEvents = null);

internal sealed record NativePublicCombatConditionAudit(int CombatIndex, bool ShuffleEligible, int HpTargets,
    string? ShuffleReason, string? HpReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativePublicDrawPrefixAudit? DrawPrefix = null);

internal sealed record NativePublicReshuffleTargetAudit(int CombatIndex, int ReshuffleOrdinal,
    int PoolCount, int DrawPrefixCount, long ShuffleEventOrdinal, long WitnessEventOrdinal);
internal sealed record NativePublicMonsterRollAudit(int CombatIndex, int Slot, string Model,
    int RollOrdinal, long IntentEventOrdinal, string Shape);

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
    private readonly NativeNeowCardCondition? _neowCardCondition;
    private readonly NativeFirstRewardCondition? _firstRewardCondition;
    private readonly NativeFirstEncounterCondition? _firstEncounterCondition;
    private readonly NativePublicWeakEncounterSequenceCondition? _weakEncounterCondition;
    private readonly NativePublicOpeningEncounterCondition? _publicOpeningEncounterCondition;
    private readonly NativePublicEventPermutationCondition? _eventPermutationCondition;
    private readonly int _eventPermutationMaxTrials;
    private readonly NativeInitialPrefixCondition? _initialPrefixCondition;
    private readonly NativePublicRewardCondition? _publicRewardCondition;
    private readonly NativePublicRewardResourceCondition? _publicResourceCondition;
    private readonly NativePublicCombatPrefixCondition? _publicCombatCondition;
    private readonly NativePublicCorpseSlugIntentCondition? _slugIntentCondition;
    private readonly NativePublicWeakSlimeFormationCondition? _weakFormationCondition;
    private readonly NativePublicReshuffleCondition? _publicReshuffleCondition;
    private readonly NativePublicMonsterBranchIntentCondition? _monsterBranchCondition;
    private readonly PublicRunEvidence? _expectedPublicEvidence;
    private readonly int _initialPrefixMaxTrials;
    private readonly CancellationToken _cancellation;
    // Internal exception-path seam. Production callers always use the independent
    // native recipe factory; this function is never a serialized worker option.
    private readonly Func<NativeRunExecutionOptions, NativeTapeRecipe, NativeLabelTape,
        CancellationToken, Task<NativeRunWorld?>> _openWorld;
    private readonly List<NativeTapeProposalAudit> _attempts = [];
    private readonly string?[] _potions;
    private readonly int _publicDecisionIndex;
    private readonly int? _publicCombatIndex;
    private int _sampleCalls;

    internal NativeTapeReplaySource(DecisionPacket publicRoot, NativeTapePrior prior,
        bool enableConditioning = true, CancellationToken cancellationToken = default, int initialPrefixMaxTrials = 64,
        Func<NativeRunExecutionOptions, NativeTapeRecipe, NativeLabelTape, CancellationToken, Task<NativeRunWorld?>>? nativeOpenerForTests = null, int eventPermutationMaxTrials = 256)
    {
        _prior = prior.Freeze(); _cancellation = cancellationToken;
        _openWorld = nativeOpenerForTests ?? NativeRunWorld.OpenLabelTapeAsync;
        if (initialPrefixMaxTrials is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(initialPrefixMaxTrials));
        _initialPrefixMaxTrials = initialPrefixMaxTrials;
        if (eventPermutationMaxTrials is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(eventPermutationMaxTrials));
        _eventPermutationMaxTrials = eventPermutationMaxTrials;
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
        if (enableConditioning && _prior.UsesRewardsProvenance)
        {
            NaturalSourceCollector.InitializeNativeModels();
            if (NativeNeowCardCondition.TryCreate(root, _prior, out var neowCards, out _)) _neowCardCondition = neowCards;
        }
        if (enableConditioning && !_prior.UsesRewardsProvenance && NativeFirstRewardCondition.TryCreate(root, _prior, out var firstRewardCondition, out _))
            _firstRewardCondition = firstRewardCondition;
        if (enableConditioning && _prior.UsesRewardsProvenance
            && NativePublicWeakEncounterSequenceCondition.TryCreate(root, _prior, out var weakEncounters, out _))
            _weakEncounterCondition = weakEncounters;
        if (enableConditioning && _prior.UsesRewardsProvenance && _weakEncounterCondition is null
            && NativePublicOpeningEncounterCondition.TryCreate(root, _prior, out var openingEncounter, out _))
            _publicOpeningEncounterCondition = openingEncounter;
        if (enableConditioning && _publicOpeningEncounterCondition is null && _weakEncounterCondition is null
            && NativeFirstEncounterCondition.TryCreate(root, _prior, out var firstEncounterCondition, out _))
            _firstEncounterCondition = firstEncounterCondition;
        if (enableConditioning && _prior.UsesRewardsProvenance
            && NativePublicEventPermutationCondition.TryCreate(root, _prior, out var eventPermutation, out _))
            _eventPermutationCondition = eventPermutation;
        if (enableConditioning && NativeInitialPrefixCondition.TryCreate(root, _prior, out var initialPrefixCondition, out _))
            _initialPrefixCondition = initialPrefixCondition;
        if (enableConditioning && _prior.UsesRewardsProvenance && root.PublicEvidence is { CompleteFromRunStart: true } evidence)
        {
            NaturalSourceCollector.InitializeNativeModels();
            var publicRewards = NativePublicRewardCondition.Create(evidence);
            if (publicRewards.Targets.Count > 0)
            {
                _publicRewardCondition = publicRewards;
                _publicResourceCondition = NativePublicRewardResourceCondition.Create(evidence, publicRewards);
            }
        }
        if (enableConditioning && _prior.UsesRewardsProvenance && root.PublicEvidence is not null)
        {
            if (NativePublicWeakSlimeFormationCondition.TryCreate(root, _prior, out var formation, out _))
                _weakFormationCondition = formation;
            var reshuffles = NativePublicReshuffleCondition.Create(root);
            PublicReshuffleDiagnostics = reshuffles.Combats.SelectMany(pair => pair.Value.Targets.Select(target =>
                new NativePublicReshuffleTargetAudit(pair.Key, target.ReshuffleOrdinal, target.PoolIds.Count,
                    target.DrawPrefixIds.Count, target.ShuffleEventOrdinal, target.WitnessEventOrdinal))).ToArray();
            if (reshuffles.EligibleShuffleCount > 0) _publicReshuffleCondition = reshuffles;
            var monsterBranches = NativePublicMonsterBranchIntentCondition.Create(root);
            PublicMonsterRollDiagnostics = monsterBranches.Combats.SelectMany(pair => pair.Value.Targets.Select(target =>
                new NativePublicMonsterRollAudit(pair.Key, target.Slot, target.Model, target.RollOrdinal,
                    target.IntentEventOrdinal, target.Shape.ToString()))).ToArray();
            if (monsterBranches.EligibleBranchCount > 0) _monsterBranchCondition = monsterBranches;
            var slugIntents = NativePublicCorpseSlugIntentCondition.Create(root);
            if (slugIntents.EligibleCombatCount > 0) _slugIntentCondition = slugIntents;
            var combats = NativePublicCombatPrefixCondition.Create(root);
            PublicCombatDiagnostics = combats.Combats.Values.Select(input => new NativePublicCombatConditionAudit(
                input.CombatIndex, input.Shuffle is not null, input.HpCount, input.ShuffleReason, input.HpReason, input.DrawPrefix)).ToArray();
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
    public string PosteriorProfile => _prior.UsesRewardsProvenance ? "owned-native-rewards-state-tape-conditional-v7-public-evidence-v1"
        : _prior.Execution.EmitsPublicEvidence ? Profile + "-public-evidence-v1" : Profile;
    public string PriorWarning => _prior.UsesRewardsProvenance
        ? "Separate hybrid ideal oracle: player Rewards use shared origin/initial-seed/raw-cursor cells, independent of all other native full-state cells. Exact clones, source-partition restores and same-lineage recreation retain sharing. Cross-partition and Rewards orbit-offset coincidences no longer share words. SHA256 is a reproducible ideal-oracle implementation, not exact finite-seed inference. Full recorded public evidence is conditioned; primary card identity proposals retain exact latent-rarity likelihood and a fixed-root catalog envelope. Other mechanisms retain native replay; errors and budgets remain unresolved."
        : "Separate ideal state-addressed random-tape law; SHA256 pseudorandom implementation, not the sequential run-seed posterior. Equal-state aliases and native primitive conversions are retained. Public local decision coordinates and certified primitive proposals use exact root-constant density corrections relative to the ideal law. Uncertified mechanisms retain native tape rejection; errors and budget exhaustion remain unresolved.";
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    internal bool UsesConditionalShuffle => _condition is not null;
    internal bool UsesConditionalHp => _hpCondition is not null;
    internal bool UsesConditionalNeow => _neowCondition is not null;
    internal bool UsesConditionalNeowCards => _neowCardCondition is not null;
    internal bool UsesConditionalFirstReward => _firstRewardCondition is not null;
    internal bool UsesConditionalFirstEncounter => _firstEncounterCondition is not null;
    internal bool UsesConditionalWeakEncounterSequence => _weakEncounterCondition is not null;
    internal int WeakEncounterTargets => _weakEncounterCondition?.Targets.Count ?? 0;
    internal int WeakEncounterPrefixLength => _weakEncounterCondition?.PrefixLength ?? 0;
    internal bool UsesConditionalPublicOpeningEncounter => _publicOpeningEncounterCondition is not null;
    internal bool UsesConditionalEventPermutation => _eventPermutationCondition is not null;
    internal int PublicEventTargets => _eventPermutationCondition?.TargetCount ?? 0;
    internal bool UsesConditionalInitialPrefix => _initialPrefixCondition is not null;
    internal bool UsesConditionalPublicRewards => _publicRewardCondition is not null;
    internal bool UsesConditionalPublicResources => _publicResourceCondition is not null;
    internal int PublicRewardTargetCount => _publicRewardCondition?.Targets.Count ?? 0;
    internal bool UsesConditionalReshuffles => _publicReshuffleCondition is not null;
    internal int PublicReshuffleTargets => _publicReshuffleCondition?.EligibleShuffleCount ?? 0;
    internal bool UsesConditionalMonsterBranches => _monsterBranchCondition is not null;
    internal int PublicMonsterRollTargets => _monsterBranchCondition?.EligibleRollCount ?? 0;
    internal int PublicMonsterBranchTargets => _monsterBranchCondition?.EligibleBranchCount ?? 0;
    internal bool UsesConditionalWeakFormation => _weakFormationCondition is not null;
    internal bool UsesConditionalSlugIntents => _slugIntentCondition is not null;
    internal int SlugIntentTargets => _slugIntentCondition?.EligibleCombatCount ?? 0;
    internal bool UsesConditionalPublicCombats => _publicCombatCondition is not null;
    internal int PublicCombatShuffleTargets => _publicCombatCondition?.EligibleShuffleCount ?? 0;
    internal int PublicCombatHpTargets => _publicCombatCondition?.EligibleHpCount ?? 0;
    internal NativePublicCombatConditionAudit[] PublicCombatDiagnostics { get; } = [];
    internal NativePublicReshuffleTargetAudit[] PublicReshuffleDiagnostics { get; } = [];
    internal NativePublicMonsterRollAudit[] PublicMonsterRollDiagnostics { get; } = [];
    internal bool UsesPrimitiveConditioning => UsesConditionalShuffle || UsesConditionalHp || UsesConditionalNeow || UsesConditionalFirstReward || UsesConditionalFirstEncounter || UsesConditionalInitialPrefix || UsesConditionalPublicRewards || UsesConditionalPublicCombats || UsesConditionalPublicOpeningEncounter || UsesConditionalNeowCards || UsesConditionalSlugIntents || UsesConditionalWeakFormation || UsesConditionalReshuffles || UsesConditionalMonsterBranches || UsesConditionalWeakEncounterSequence || UsesConditionalEventPermutation;
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
        : "conditional:" + string.Join("+", new[] { UsesConditionalNeow ? "neow" : null, UsesConditionalNeowCards ? "neow_cards" : null, UsesConditionalFirstReward ? "first_reward" : null, UsesConditionalFirstEncounter ? "first_encounters" : null, UsesConditionalPublicOpeningEncounter ? "public_opening_encounter" : null, UsesConditionalWeakEncounterSequence ? "public_weak_encounter_sequence" : null, UsesConditionalInitialPrefix ? "joint_initial_prefix" : null, UsesConditionalEventPermutation ? "public_event_permutation" : null, UsesConditionalPublicRewards ? "public_reward_identities" : null, UsesConditionalPublicResources ? "public_reward_resources" : null, UsesConditionalPublicCombats ? "public_combat_startups" : null,
            UsesConditionalReshuffles ? "public_witnessed_reshuffles" : null, UsesConditionalMonsterBranches ? "public_monster_branches" : null, UsesConditionalWeakFormation ? "public_weak_slime_formation" : null, UsesConditionalSlugIntents ? "public_slug_initial_intents" : null, UsesConditionalHp ? "initial_hp" : null, UsesConditionalShuffle ? "initial_shuffle" : null }.OfType<string>());
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
            NativeEventPermutationPlan? eventPlan = null;
            NativeComponentStats? eventStats = null;
            NativeComponentStats? prefixStats = null;
            int? prefixRunSeeds = null;
            var tape = NativeLabelTape.ForDeclaredPrior(_prior, recipe, _condition, expectedEntryJson: _entryJson,
                hpCondition: _hpCondition, neowCondition: _neowCondition, firstRewardCondition: _firstRewardCondition, firstEncounterCondition: _firstEncounterCondition,
                publicRewardCondition: _publicRewardCondition, publicCombatCondition: _publicCombatCondition,
                expectedPublicEvidence: _expectedPublicEvidence, publicOpeningEncounterCondition: _publicOpeningEncounterCondition, neowCardCondition: _neowCardCondition, publicResourceCondition: _publicResourceCondition, slugIntentCondition: _slugIntentCondition, weakFormationCondition: _weakFormationCondition, publicReshuffleCondition: _publicReshuffleCondition, monsterBranchCondition: _monsterBranchCondition, weakEncounterCondition: _weakEncounterCondition);
            var timer = Stopwatch.StartNew();
            NativeRunWorld? world = null; bool accepted = false, preparingPrefix = false, preparingEvent = false; Exception? operationFailure = null;
            void Audit(string status, string? detail = null) => _attempts.Add(new(call, attempt, recipe, status,
                ProposalDescription, tape.DistinctCells, tape.ConditionedCells, timer.Elapsed.TotalSeconds, detail,
                tape.ConditionedHpCount, tape.NeowConditionApplied, tape.FirstRewardConditionApplied, tape.ProposedFirstEncounterIndex,
                _initialPrefixCondition is null ? null : auxiliaryRecipe, prefixStats,
                _initialPrefixCondition is null ? null : _initialPrefixMaxTrials,
                prefixPlan is null ? null : NativeInitialPrefixProposal.CorrectionClaim, prefixRunSeeds,
                _prior.UsesRewardsProvenance ? tape.RewardsCells : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedPublicRewardCards : null,
                Fraction(tape.PublicRewardRatio), Fraction(tape.PublicRewardEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedNeowCards : null, Fraction(tape.NeowCardRatio), Fraction(tape.NeowCardEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedResourcePresence : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedResourceGold : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedResourcePotions : null,
                Fraction(tape.PublicResourceRatio), Fraction(tape.PublicResourceEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedSlugIntents : null,
                Fraction(tape.SlugIntentRatio), Fraction(tape.SlugIntentEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedWeakFormations : null,
                Fraction(tape.WeakFormationRatio), Fraction(tape.WeakFormationEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedReshuffles : null,
                Fraction(tape.ReshuffleRatio), Fraction(tape.ReshuffleEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedMonsterRolls : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedMonsterBranches : null,
                Fraction(tape.MonsterBranchRatio), Fraction(tape.MonsterBranchEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedWeakEncounters : null, Fraction(tape.WeakEncounterEnvelope), eventStats,
                _eventPermutationCondition is null ? null : _eventPermutationMaxTrials,
                eventPlan is null ? null : NativeEventPermutationPlan.CorrectionClaim,
                _prior.UsesRewardsProvenance ? tape.ConditionedPublicEvents : null));
            try
            {
                if (_initialPrefixCondition is not null)
                {
                    preparingPrefix = true;
                    prefixPlan = _initialPrefixCondition.Prepare(auxiliaryRecipe, _initialPrefixMaxTrials, _cancellation);
                    preparingPrefix = false;
                    prefixStats = prefixPlan.Stats;
                    prefixRunSeeds = prefixPlan.RunSeedDraws;
                    recipe = prefixPlan.SelectedRecipe;
                }
                if (_eventPermutationCondition is not null)
                {
                    preparingEvent = true;
                    eventPlan = _eventPermutationCondition.Prepare(recipe, _eventPermutationMaxTrials, _cancellation);
                    preparingEvent = false;
                    eventStats = eventPlan.Stats;
                }
                if (prefixPlan is not null || eventPlan is not null)
                {
                    tape = NativeLabelTape.ForDeclaredPrior(_prior, recipe, _condition, expectedEntryJson: _entryJson,
                        hpCondition: _hpCondition, neowCondition: _neowCondition,
                        firstRewardCondition: _firstRewardCondition, firstEncounterCondition: _firstEncounterCondition,
                        initialPrefixPlan: prefixPlan, publicRewardCondition: _publicRewardCondition, publicCombatCondition: _publicCombatCondition,
                        expectedPublicEvidence: _expectedPublicEvidence, publicOpeningEncounterCondition: _publicOpeningEncounterCondition, neowCardCondition: _neowCardCondition, publicResourceCondition: _publicResourceCondition, slugIntentCondition: _slugIntentCondition, weakFormationCondition: _weakFormationCondition, publicReshuffleCondition: _publicReshuffleCondition, monsterBranchCondition: _monsterBranchCondition, weakEncounterCondition: _weakEncounterCondition, eventPermutationCondition: _eventPermutationCondition, eventPermutationPlan: eventPlan);
                }
                world = await _openWorld(_prior.Execution, recipe, tape, _cancellation);
                tape.RequireSuccessfulConditionedWords();
                if (world is null) Audit("absent_under_declared_source_horizon");
                else
                {
                    tape.ValidateProposalCompletion();
                    if (PublicJson.Serialize(world.Observe()) != _serializedRoot) Audit("public_packet_mismatch");
                    else if (!tape.AcceptCorrection(random.NextUnsignedLong)) Audit("exact_proposal_density_correction_rejected");
                    else { Audit("accepted"); accepted = true; return world; }
                }
            }
            catch (NativePublicConstraintMismatchException exception) when (!tape.HasConditionedWordFailure)
            { Audit("public_constraint_mismatch", exception.Message); }
            catch (NativeComponentBudgetExceededException exception) when (!tape.HasConditionedWordFailure)
            {
                if (preparingEvent && exception.Component == "public_event_permutation") eventStats = exception.Stats;
                else if (preparingPrefix && exception.Component == "initial_prefix")
                {
                    prefixStats = exception.Stats;
                    prefixRunSeeds = NativeInitialPrefixCondition.FailureRunSeedDraws(exception);
                }
                Audit("component_budget_exhausted", exception.Message);
                // A fixed-K whole fresh-prefix draw has an explicit null outcome
                // when every complete trial misses its root-only public predicate.
                // Retrying a complete independent outer recipe consumes this
                // attempt; its root-constant successful subdensity is unchanged.
                // This deliberately does not make arbitrary component errors or
                // partial/canceled trials retryable, and preserves legacy v7.
                if (_prior.UsesRewardsProvenance && preparingPrefix && prefixPlan is null && world is null
                    && _initialPrefixCondition is not null && exception.Component == "initial_prefix"
                    && exception.Stats.CompletedTrials == _initialPrefixMaxTrials
                    && prefixRunSeeds == _initialPrefixMaxTrials)
                    continue;
                if (_prior.UsesRewardsProvenance && preparingEvent && eventPlan is null && world is null
                    && _eventPermutationCondition is not null && exception.Component == "public_event_permutation"
                    && exception.Stats.CompletedTrials == _eventPermutationMaxTrials)
                    continue;
                var inconclusive = new PosteriorSamplingException(attempt);
                inconclusive.Data["component"] = exception.Component;
                inconclusive.Data["component_complete_trials"] = exception.Stats.CompletedTrials;
                operationFailure = inconclusive;
                throw inconclusive;
            }
            catch (Exception exception)
            {
                // A later public mismatch, absence, component null outcome or
                // cancellation must not hide an earlier caught callback failure.
                Exception failure = tape.ConditionedWordError ?? exception;
                operationFailure = failure;
                if (preparingEvent) eventStats ??= NativeComponentRejection.FailureStats(exception);
                else if (preparingPrefix)
                {
                    prefixStats ??= NativeComponentRejection.FailureStats(exception);
                    prefixRunSeeds ??= NativeInitialPrefixCondition.FailureRunSeedDraws(exception);
                }
                Audit(failure is OperationCanceledException ? "computation_cancelled" : "proposal_engine_error",
                    failure.GetType().Name + ": " + failure.Message);
                if (!ReferenceEquals(failure, exception)) throw failure;
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
                            Fraction(tape.PublicRewardRatio), Fraction(tape.PublicRewardEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedNeowCards : null, Fraction(tape.NeowCardRatio), Fraction(tape.NeowCardEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedResourcePresence : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedResourceGold : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedResourcePotions : null,
                Fraction(tape.PublicResourceRatio), Fraction(tape.PublicResourceEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedSlugIntents : null,
                Fraction(tape.SlugIntentRatio), Fraction(tape.SlugIntentEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedWeakFormations : null,
                Fraction(tape.WeakFormationRatio), Fraction(tape.WeakFormationEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedReshuffles : null,
                Fraction(tape.ReshuffleRatio), Fraction(tape.ReshuffleEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedMonsterRolls : null,
                _prior.UsesRewardsProvenance ? tape.ConditionedMonsterBranches : null,
                Fraction(tape.MonsterBranchRatio), Fraction(tape.MonsterBranchEnvelope),
                _prior.UsesRewardsProvenance ? tape.ConditionedWeakEncounters : null, Fraction(tape.WeakEncounterEnvelope), eventStats,
                _eventPermutationCondition is null ? null : _eventPermutationMaxTrials,
                eventPlan is null ? null : NativeEventPermutationPlan.CorrectionClaim,
                _prior.UsesRewardsProvenance ? tape.ConditionedPublicEvents : null);
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
