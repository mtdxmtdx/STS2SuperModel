using System.Buffers.Binary;
using System.Security.Cryptography;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

// Abort suppresses only the incomplete-word check during native failure cleanup.
// The owning component remains incomplete, and any captured callback failure stays fatal.
internal interface IAbortableConditionedWordScope : IDisposable
{
    void Abort();
}

/// <summary>
/// Hypothetical tape only. No constructor accepts an actual source graph or its RNG.
/// SHA256 instantiates the ideal oracle reproducibly; correctness of conditional
/// proposal weights refers to the declared ideal law, not a finite SHA256-seed law.
/// </summary>
internal sealed class NativeLabelTape
{
    private readonly NativeTapeRecipe _recipe;
    private readonly NativeRewardsOracle? _rewardsOracle;
    private readonly NativeMapOracle? _mapOracle;
    private readonly NativePublicMapReconstructionCondition? _mapReconstructionCondition;
    private readonly NativePublicMapReconstructionProposal? _mapReconstructionProposal;
    private readonly NativeNeowPotionCondition? _neowPotionCondition;
    private readonly NativeNeowPotionProposal? _neowPotionProposal;
    private readonly NativeNeowCardCondition? _neowCardCondition;
    private readonly NativeNeowCardProposal? _neowCardProposal;
    private readonly NativePublicEventCardCondition? _publicEventCardCondition;
    private readonly NativePublicEventCardProposal? _publicEventCardProposal;
    private readonly NativePublicUnknownRoomCondition? _publicUnknownRoomCondition;
    private readonly NativePublicUnknownRoomProposal? _publicUnknownRoomProposal;
    private readonly NativePublicShopCondition? _publicShopCondition;
    private readonly NativePublicShopProposal? _publicShopProposal;
    private readonly NativePublicRewardCondition? _publicRewardCondition;
    private readonly NativePublicRewardProposal? _publicRewardProposal;
    private readonly NativePublicRewardResourceCondition? _publicResourceCondition;
    private readonly NativePublicRewardResourceProposal? _publicResourceProposal;
    private readonly NativePublicCombatPrefixCondition? _publicCombatCondition;
    private readonly NativePublicCombatPrefixProposal? _publicCombatProposal;
    private readonly NativePublicCorpseSlugIntentCondition? _slugIntentCondition;
    private readonly NativePublicCorpseSlugIntentProposal? _slugIntentProposal;
    private readonly NativePublicWeakSlimeFormationCondition? _weakFormationCondition;
    private readonly NativePublicWeakSlimeFormationProposal? _weakFormationProposal;
    private readonly NativePublicReshuffleCondition? _publicReshuffleCondition;
    private readonly NativePublicReshuffleProposal? _publicReshuffleProposal;
    private readonly NativePublicMonsterBranchIntentCondition? _monsterBranchCondition;
    private readonly NativePublicMonsterBranchIntentProposal? _monsterBranchProposal;
    private readonly PublicRunEvidence? _expectedPublicEvidence;
    private readonly NativePublicPrefixConstraint? _publicPrefixConstraint;
    private readonly NativeInitialShuffleCondition? _condition;
    private readonly NativeInitialHpCondition? _hpCondition;
    private readonly NativeNeowCondition? _neowCondition;
    private readonly NativeFirstRewardCondition? _firstRewardCondition;
    private readonly NativeFirstEncounterCondition? _firstEncounterCondition;
    private NativeFirstEncounterProposal? _firstEncounterPlan;
    private readonly NativePublicWeakEncounterSequenceCondition? _weakEncounterCondition;
    private readonly NativePublicWeakEncounterSequenceProposal? _weakEncounterProposal;
    private readonly NativePublicOpeningEncounterCondition? _publicOpeningEncounterCondition;
    private readonly NativePublicOpeningEncounterProposal? _publicOpeningEncounterProposal;
    private readonly NativePublicEventPermutationCondition? _eventPermutationCondition;
    private readonly NativeEventPermutationPlan? _eventPermutationPlan;
    private readonly NativePublicEventPermutationProposal? _eventPermutationProposal;
    private readonly NativeInitialPrefixProposal? _initialPrefixPlan;
    private readonly NativePublicActPrefixPlan? _publicActPrefixPlan;
    private readonly NativePublicActPrefixReplay? _publicActPrefixReplay;
    private RunState? _constructingRun;
    private int _prefixWordsReplayed;
    private bool _prefixComplete, _prefixFailed;
    private NativeFirstRewardProposal? _firstRewardPlan;
    private int _currentCombatIndex = -1;
    private readonly string? _expectedEntryJson;
    private readonly Dictionary<LabelRandomState, ulong> _overrides;
    private readonly HashSet<LabelRandomState> _visited = [];
    private Exception? _conditionedWordFailure;
    private bool _awaitingInitialShuffle;
    private Rng? _initialShuffleRng;
    private ConditionalShufflePlan? _plan;
    private readonly HashSet<string> _conditionedHpIds = new(StringComparer.Ordinal);
    private readonly List<NativeHpProposal> _hpPlans = [];
    private bool _conditioningInitialHp;
    private NativeNeowProposal? _neowPlan;
    private NativeNeowOpeningProposal? _neowOpeningPlan;
    private RunState? _run;
    internal bool ConditionApplied { get; private set; }
    internal int DistinctCells => _visited.Count + (_rewardsOracle?.DistinctCells ?? 0) + (_mapOracle?.DistinctCells ?? 0);
    internal int MapCells => _mapOracle?.DistinctCells ?? 0;
    internal bool UsesMapProvenance => _mapOracle is not null;
    internal int ReconstructedMaps => _mapReconstructionProposal?.ReconstructedMapCount ?? 0;
    internal int RewardsCells => _rewardsOracle?.DistinctCells ?? 0;
    internal bool UsesRewardsProvenance => _rewardsOracle is not null;
    internal int ConditionedNeowPoolShuffles => _neowCardProposal?.ConditionedPoolShuffleCount ?? 0;
    internal int ConditionedNeowPotions => _neowPotionProposal?.ConditionedPotionCount ?? 0;
    internal ShuffleRational? NeowPotionRatio => _neowPotionProposal?.NativeToProposalRatio;
    internal ShuffleRational? NeowPotionEnvelope => _neowPotionProposal?.Envelope;
    internal int ConditionedNeowCards => _neowCardProposal?.ConditionedCardCount ?? 0;
    internal ShuffleRational? NeowCardRatio => _neowCardProposal?.NativeToProposalRatio;
    internal ShuffleRational? NeowCardEnvelope => _neowCardProposal?.Envelope;
    internal int ConditionedEventCards => _publicEventCardProposal?.ConditionedCardCount ?? 0;
    internal int ConditionedEventCardOffers => _publicEventCardProposal?.ConditionedOfferCount ?? 0;
    internal ShuffleRational? EventCardRatio => _publicEventCardProposal?.NativeToProposalRatio;
    internal ShuffleRational? EventCardEnvelope => _publicEventCardProposal?.Envelope;
    internal int ConditionedUnknownRooms => _publicUnknownRoomProposal?.ConditionedRoomCount ?? 0;
    internal ShuffleRational? UnknownRoomRatio => _publicUnknownRoomProposal?.NativeToProposalRatio;
    internal ShuffleRational? UnknownRoomEnvelope => _publicUnknownRoomProposal?.Envelope;
    internal int ConditionedShopOffers => _publicShopProposal?.ConditionedOfferCount ?? 0;
    internal int ConditionedShopBags => _publicShopProposal?.ConditionedBagCount ?? 0;
    internal ShuffleRational? ShopRatio => _publicShopProposal?.NativeToProposalRatio;
    internal ShuffleRational? ShopEnvelope => _publicShopProposal?.Envelope;
    internal int ConditionedPublicRewardCards => _publicRewardProposal?.ConditionedCardCount ?? 0;
    internal ShuffleRational? PublicRewardRatio => _publicRewardProposal?.NativeToProposalRatio;
    internal ShuffleRational? PublicRewardEnvelope => _publicRewardProposal?.Envelope;
    internal int ConditionedResourcePresence => _publicResourceProposal?.ConditionedPresenceCount ?? 0;
    internal int ConditionedResourceGold => _publicResourceProposal?.ConditionedGoldCount ?? 0;
    internal int ConditionedResourcePotions => _publicResourceProposal?.ConditionedPotionCount ?? 0;
    internal ShuffleRational? PublicResourceRatio => _publicResourceProposal?.NativeToProposalRatio;
    internal ShuffleRational? PublicResourceEnvelope => _publicResourceProposal?.Envelope;
    internal int ConditionedPublicCombatShuffles => _publicCombatProposal?.ConditionedShuffleCount ?? 0;
    internal int ConditionedPublicCombatHp => _publicCombatProposal?.ConditionedHpCount ?? 0;
    internal int ConditionedReshuffles => _publicReshuffleProposal?.ConditionedShuffleCount ?? 0;
    internal ShuffleRational? ReshuffleRatio => _publicReshuffleProposal?.NativeToProposalRatio;
    internal ShuffleRational? ReshuffleEnvelope => _publicReshuffleProposal?.Envelope;
    internal int ConditionedMonsterRolls => _monsterBranchProposal?.ConditionedRollCount ?? 0;
    internal int ConditionedMonsterBranches => _monsterBranchProposal?.ConditionedBranchCount ?? 0;
    internal ShuffleRational? MonsterBranchRatio => _monsterBranchProposal?.NativeToProposalRatio;
    internal ShuffleRational? MonsterBranchEnvelope => _monsterBranchProposal?.Envelope;
    internal int ConditionedWeakFormations => _weakFormationProposal?.ConditionedCombatCount ?? 0;
    internal ShuffleRational? WeakFormationRatio => _weakFormationProposal?.NativeToProposalRatio;
    internal ShuffleRational? WeakFormationEnvelope => _weakFormationProposal?.Envelope;
    internal int ConditionedSlugIntents => _slugIntentProposal?.ConditionedCombatCount ?? 0;
    internal ShuffleRational? SlugIntentRatio => _slugIntentProposal?.NativeToProposalRatio;
    internal ShuffleRational? SlugIntentEnvelope => _slugIntentProposal?.Envelope;
    internal int PublicPrefixEventsChecked => _publicPrefixConstraint?.CheckedEvents ?? 0;
    internal int ConditionedPublicActWords => _publicActPrefixReplay?.ReplayedWords ?? 0;
    internal int ConditionedCells => _overrides.Count + (_rewardsOracle?.ConditionedCells ?? 0) + (_mapOracle?.ConditionedCells ?? 0);
    internal int ConditionedHpCount => _hpPlans.Count;
    internal bool NeowConditionApplied => _neowPlan is not null;
    internal bool FirstRewardConditionApplied => _firstRewardPlan is not null;
    internal int ConditionedPublicEvents => _eventPermutationProposal?.ConditionedEventCount ?? 0;
    internal int ConditionedEventPermutations => _eventPermutationProposal?.ConditionedShuffleCount ?? 0;
    internal int ConditionedWeakEncounters => _weakEncounterProposal?.ConditionedEncounterCount ?? 0;
    internal ShuffleRational? WeakEncounterEnvelope => _weakEncounterProposal?.Envelope;
    internal bool FirstEncounterConditionApplied => _firstEncounterPlan is not null || _publicOpeningEncounterProposal?.Applied == true || _weakEncounterProposal?.Applied == true;
    internal int? ProposedFirstEncounterIndex => _firstEncounterPlan?.FirstEncounterIndex ?? _publicOpeningEncounterProposal?.FirstEncounterIndex ?? _weakEncounterProposal?.FirstEncounterIndex;

    internal NativeLabelTape(NativeTapeRecipe recipe, NativeInitialShuffleCondition? condition = null,
        IReadOnlyDictionary<LabelRandomState, ulong>? overrides = null, string? expectedEntryJson = null,
        NativeInitialHpCondition? hpCondition = null, NativeNeowCondition? neowCondition = null,
        NativeFirstRewardCondition? firstRewardCondition = null, NativeFirstEncounterCondition? firstEncounterCondition = null, NativeInitialPrefixProposal? initialPrefixPlan = null,
        NativeRewardsOracle? rewardsOracle = null, NativePublicRewardCondition? publicRewardCondition = null,
        NativePublicCombatPrefixCondition? publicCombatCondition = null, PublicRunEvidence? expectedPublicEvidence = null,
        NativePublicOpeningEncounterCondition? publicOpeningEncounterCondition = null, NativeNeowCardCondition? neowCardCondition = null,
        NativePublicRewardResourceCondition? publicResourceCondition = null,
        NativePublicCorpseSlugIntentCondition? slugIntentCondition = null,
        NativePublicWeakSlimeFormationCondition? weakFormationCondition = null,
        NativePublicReshuffleCondition? publicReshuffleCondition = null,
        NativePublicMonsterBranchIntentCondition? monsterBranchCondition = null,
        NativePublicWeakEncounterSequenceCondition? weakEncounterCondition = null,
        NativePublicEventPermutationCondition? eventPermutationCondition = null,
        NativeEventPermutationPlan? eventPermutationPlan = null, NativeMapOracle? mapOracle = null,
        NativePublicMapReconstructionCondition? mapReconstructionCondition = null,
        NativeNeowPotionCondition? neowPotionCondition = null,
        NativePublicEventCardCondition? publicEventCardCondition = null,
        NativePublicUnknownRoomCondition? publicUnknownRoomCondition = null,
        NativePublicShopCondition? publicShopCondition = null,
        NativePublicActPrefixPlan? publicActPrefixPlan = null)
    {
        _recipe = recipe; _condition = condition; _expectedEntryJson = expectedEntryJson ?? condition?.EntryJson;
        _rewardsOracle = rewardsOracle; _mapOracle = mapOracle;
        if (mapOracle is not null && rewardsOracle is null)
            throw new ArgumentException("The Map law requires its Rewards partition");
        _mapReconstructionCondition = mapReconstructionCondition;
        if (mapReconstructionCondition is not null)
        {
            if (mapOracle is null || initialPrefixPlan is not null)
                throw new ArgumentException("Public map reconstruction requires the new Map law and cannot overlap map-prefix rejection");
            _mapReconstructionProposal = new(mapReconstructionCondition);
        }
        if (mapOracle is not null && initialPrefixPlan is not null)
            throw new ArgumentException("The old full-state prefix plan cannot condition the independent Map partition");
        _publicActPrefixPlan = publicActPrefixPlan;
        if (publicActPrefixPlan is not null)
        {
            if (mapOracle is null || mapReconstructionCondition is null || initialPrefixPlan is not null)
                throw new ArgumentException("Public act prefix requires complete Map reconstruction and exclusive prefix ownership");
            if (publicActPrefixPlan.Recipe != recipe)
                throw new ArgumentException("Public act prefix and native replay recipe differ", nameof(publicActPrefixPlan));
            _publicActPrefixReplay = new(publicActPrefixPlan, CaptureConditionedFailure);
        }
        _expectedPublicEvidence = expectedPublicEvidence;
        if (expectedPublicEvidence is not null)
        {
            if (rewardsOracle is null) throw new ArgumentException("Incremental evidence constraints belong to the explicit hybrid sampler profile");
            _publicPrefixConstraint = new(expectedPublicEvidence);
        }
        _neowCardCondition = neowCardCondition;
        if (neowCardCondition is not null)
        {
            if (rewardsOracle is null) throw new ArgumentException("Initial public Neow cards require the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-neow-cards-v1");
            _neowCardProposal = new(neowCardCondition, rewardsOracle.WasVisited, rewardsOracle.ForceFresh, random.NextUnsignedLong, ForceWords, CaptureConditionedFailure);
        }
        _neowPotionCondition = neowPotionCondition;
        if (neowPotionCondition is not null)
        {
            if (rewardsOracle is null) throw new ArgumentException("Public Neow potions require an explicit hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-neow-potions-v1");
            _neowPotionProposal = new(neowPotionCondition, random.NextUnsignedLong, ForcePrefixWords, CaptureConditionedFailure);
        }
        _publicEventCardCondition = publicEventCardCondition;
        if (publicEventCardCondition is not null)
        {
            if (rewardsOracle is null) throw new ArgumentException("Public event cards require an explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-event-card-identities-v1");
            _publicEventCardProposal = new(publicEventCardCondition, rewardsOracle.WasVisited, rewardsOracle.ForceFresh, random.NextUnsignedLong, CaptureConditionedFailure);
        }
        _publicUnknownRoomCondition = publicUnknownRoomCondition;
        if (publicUnknownRoomCondition is not null)
        {
            if (mapOracle is null || expectedPublicEvidence is null)
                throw new ArgumentException("Public unknown-room conditioning requires the Map law and complete public-prefix validation");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-unknown-room-prefix-v1");
            _publicUnknownRoomProposal = new(publicUnknownRoomCondition, random.NextUnsignedLong, ForcePrefixWords, CaptureConditionedFailure);
        }
        _publicShopCondition = publicShopCondition;
        if (publicShopCondition is not null)
        {
            if (mapOracle is null || rewardsOracle is null || expectedPublicEvidence is null)
                throw new ArgumentException("Public first-shop conditioning requires the Map law and complete public-prefix validation");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-first-shop-stock-v1");
            _publicShopProposal = new(publicShopCondition, rewardsOracle.WasVisited, rewardsOracle.ForceFresh,
                random.NextUnsignedLong, ForcePrefixWords, CaptureConditionedFailure);
        }
        _publicReshuffleCondition = publicReshuffleCondition;
        if (publicReshuffleCondition is not null)
        {
            if (rewardsOracle is null)
                throw new ArgumentException("Public reshuffles require the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-witnessed-reshuffles-v1");
            _publicReshuffleProposal = new(publicReshuffleCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        _monsterBranchCondition = monsterBranchCondition;
        if (monsterBranchCondition is not null)
        {
            if (rewardsOracle is null)
                throw new ArgumentException("Public monster branches require the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-monster-branch-intents-v2");
            _monsterBranchProposal = new(monsterBranchCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        _weakFormationCondition = weakFormationCondition;
        if (weakFormationCondition is not null)
        {
            if (rewardsOracle is null)
                throw new ArgumentException("Public weak formations require the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-weak-slime-formations-v1");
            _weakFormationProposal = new(weakFormationCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        _slugIntentCondition = slugIntentCondition;
        if (slugIntentCondition is not null)
        {
            if (rewardsOracle is null)
                throw new ArgumentException("Public slug intents require the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-slug-initial-intents-v1");
            _slugIntentProposal = new(slugIntentCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        _publicRewardCondition = publicRewardCondition;
        _publicCombatCondition = publicCombatCondition;
        if (publicCombatCondition is not null)
        {
            if (rewardsOracle is null || condition is not null || hpCondition is not null)
                throw new ArgumentException("Composed public combat conditioning requires hybrid law and replaces current-only startup plans");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-combat-prefixes-v1");
            _publicCombatProposal = new(publicCombatCondition, random.NextUnsignedLong, ForceWords);
        }
        if (publicRewardCondition is not null)
        {
            if (rewardsOracle is null) throw new ArgumentException("Public reward identity conditioning requires the explicit Rewards hybrid law");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-reward-identities-v1");
            _publicRewardProposal = new(publicRewardCondition, rewardsOracle.WasVisited, rewardsOracle.ForceFresh, random.NextUnsignedLong, CaptureConditionedFailure);
        }
        _publicResourceCondition = publicResourceCondition;
        if (publicResourceCondition is not null)
        {
            if (rewardsOracle is null || publicRewardCondition is null)
                throw new ArgumentException("Public resource conditioning requires the hybrid law and the same certified primary card owners");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-reward-resources-v1");
            _publicResourceProposal = new(publicResourceCondition, rewardsOracle.WasVisited, rewardsOracle.ForceFresh, random.NextUnsignedLong, CaptureConditionedFailure);
        }
        if (rewardsOracle is not null && firstRewardCondition is not null)
            throw new ArgumentException("The old state-addressed first-reward proposal cannot condition the hybrid Rewards partition");
        _hpCondition = hpCondition;
        if (neowCondition?.ObservedCurseId is not null && rewardsOracle is null)
            throw new ArgumentException("Observed Neow opening conditioning requires the explicit Rewards hybrid law");
        _neowCondition = neowCondition;
        _firstRewardCondition = firstRewardCondition;
        _firstEncounterCondition = firstEncounterCondition;
        _publicOpeningEncounterCondition = publicOpeningEncounterCondition;
        if (publicOpeningEncounterCondition is not null)
        {
            if (rewardsOracle is null || firstEncounterCondition is not null)
                throw new ArgumentException("Public opening encounter conditioning requires hybrid law and cannot overlap the legacy first-encounter cells");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-opening-encounter-v1");
            _publicOpeningEncounterProposal = new(publicOpeningEncounterCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        _weakEncounterCondition = weakEncounterCondition;
        if (weakEncounterCondition is not null)
        {
            if (rewardsOracle is null || firstEncounterCondition is not null || publicOpeningEncounterCondition is not null)
                throw new ArgumentException("Public weak encounter sequence requires hybrid law and exclusive ownership of encounter cells");
            var random = new Rng(recipe.ProposalSeed, "nosl-public-weak-encounter-sequence-v1");
            _weakEncounterProposal = new(weakEncounterCondition, random.NextUnsignedLong, ForcePrefixWords);
        }
        if ((eventPermutationCondition is null) != (eventPermutationPlan is null))
            throw new ArgumentException("Public event permutation requires its paired prepared plan");
        _eventPermutationCondition = eventPermutationCondition; _eventPermutationPlan = eventPermutationPlan;
        if (eventPermutationCondition is not null)
        {
            if (rewardsOracle is null)
                throw new ArgumentException("Public event permutation requires the explicit hybrid law");
            _eventPermutationProposal = new(eventPermutationCondition, eventPermutationPlan!, ForcePrefixWords);
        }
        _initialPrefixPlan = initialPrefixPlan;
        if (initialPrefixPlan is not null && initialPrefixPlan.SelectedRecipe != recipe)
            throw new ArgumentException("Initial prefix and native replay recipe differ", nameof(initialPrefixPlan));
        _overrides = overrides is null ? [] : new(overrides);
        if (initialPrefixPlan is not null)
            foreach (var word in initialPrefixPlan.Trace)
            {
                if (_overrides.TryGetValue(word.State, out var previous) && previous != word.Word)
                    throw new InvalidOperationException("Prepared initial prefix conflicts with a replayed oracle cell");
                _overrides[word.State] = word.Word;
            }
        if (publicActPrefixPlan is not null)
            foreach (var word in publicActPrefixPlan.Trace)
            {
                if (_overrides.TryGetValue(word.State, out var previous) && previous != word.Word)
                    throw new InvalidOperationException("Prepared public act prefix conflicts with a replayed oracle cell");
                _overrides[word.State] = word.Word;
            }
    }

    internal static NativeLabelTape ForDeclaredPrior(NativeTapePrior prior, NativeTapeRecipe recipe,
        NativeInitialShuffleCondition? condition = null, string? expectedEntryJson = null,
        NativeInitialHpCondition? hpCondition = null, NativeNeowCondition? neowCondition = null,
        NativeFirstRewardCondition? firstRewardCondition = null, NativeFirstEncounterCondition? firstEncounterCondition = null,
        NativeInitialPrefixProposal? initialPrefixPlan = null, NativePublicRewardCondition? publicRewardCondition = null,
        NativePublicCombatPrefixCondition? publicCombatCondition = null, PublicRunEvidence? expectedPublicEvidence = null,
        NativePublicOpeningEncounterCondition? publicOpeningEncounterCondition = null, NativeNeowCardCondition? neowCardCondition = null,
        NativePublicRewardResourceCondition? publicResourceCondition = null,
        NativePublicCorpseSlugIntentCondition? slugIntentCondition = null,
        NativePublicWeakSlimeFormationCondition? weakFormationCondition = null,
        NativePublicReshuffleCondition? publicReshuffleCondition = null,
        NativePublicMonsterBranchIntentCondition? monsterBranchCondition = null,
        NativePublicWeakEncounterSequenceCondition? weakEncounterCondition = null,
        NativePublicEventPermutationCondition? eventPermutationCondition = null,
        NativeEventPermutationPlan? eventPermutationPlan = null,
        NativePublicMapReconstructionCondition? mapReconstructionCondition = null,
        NativeNeowPotionCondition? neowPotionCondition = null,
        NativePublicEventCardCondition? publicEventCardCondition = null,
        NativePublicUnknownRoomCondition? publicUnknownRoomCondition = null,
        NativePublicShopCondition? publicShopCondition = null,
        NativePublicActPrefixPlan? publicActPrefixPlan = null) =>
        new(recipe, condition, expectedEntryJson: expectedEntryJson, hpCondition: hpCondition, neowCondition: neowCondition,
            firstRewardCondition: firstRewardCondition, firstEncounterCondition: firstEncounterCondition, initialPrefixPlan: initialPrefixPlan,
            rewardsOracle: prior.Freeze().UsesRewardsProvenance ? new(recipe.TapeSeed) : null,
            publicRewardCondition: publicRewardCondition, publicCombatCondition: publicCombatCondition,
            expectedPublicEvidence: expectedPublicEvidence, publicOpeningEncounterCondition: publicOpeningEncounterCondition, neowCardCondition: neowCardCondition, publicResourceCondition: publicResourceCondition, slugIntentCondition: slugIntentCondition, weakFormationCondition: weakFormationCondition, publicReshuffleCondition: publicReshuffleCondition, monsterBranchCondition: monsterBranchCondition, weakEncounterCondition: weakEncounterCondition, eventPermutationCondition: eventPermutationCondition, eventPermutationPlan: eventPermutationPlan,
            mapOracle: prior.UsesMapProvenance ? new(recipe.TapeSeed) : null, mapReconstructionCondition: mapReconstructionCondition, neowPotionCondition: neowPotionCondition, publicEventCardCondition: publicEventCardCondition, publicUnknownRoomCondition: publicUnknownRoomCondition, publicShopCondition: publicShopCondition, publicActPrefixPlan: publicActPrefixPlan);

    internal IDisposable EnterScope()
    {
        var scope = EnterTapeScope();
        try
        {
            if (_mapReconstructionProposal is not null)
                scope = new NestedScope(scope, LabelMapConstructionScope.Enter(_mapReconstructionProposal.Construct));
            // Both scopes are active before native run/player construction.
            // Shop bag initialization therefore remains on its original native path.
            if (_publicUnknownRoomProposal is not null)
                scope = new NestedScope(scope, LabelUnknownRoomScope.Enter(_publicUnknownRoomProposal.BeginResolution));
            if (_publicShopProposal is not null)
                scope = new NestedScope(scope, _publicShopProposal.EnterScope());
            if (_neowCardCondition?.RelicId == nameof(Sts2Sim.Core.Models.Relics.LostCoffer))
                scope = new NestedScope(scope, _neowCardProposal!.EnterResourceScope());
            if (_neowCardCondition?.RelicId == nameof(Sts2Sim.Core.Models.Relics.NewLeaf))
                scope = new NestedScope(scope, LabelCardTransformScope.Enter(_neowCardProposal!.BeginTransform));
            if (_neowPotionProposal is not null)
                scope = new NestedScope(scope, _neowPotionProposal.EnterScope());
            if (_eventPermutationProposal is not null)
                scope = new NestedScope(scope, LabelEventGenerationScope.Enter(_eventPermutationProposal.BeginGeneration));
            if (_slugIntentProposal is not null)
                scope = new NestedScope(scope, LabelCorpseSlugScope.Enter(_slugIntentProposal.BeginInitialIntents));
            if (_weakFormationProposal is not null)
                scope = new NestedScope(scope, LabelSlimesWeakScope.Enter(_weakFormationProposal.BeginFormation));
            if (_publicReshuffleProposal is not null)
                scope = new NestedScope(scope, _publicReshuffleProposal.EnterScope());
            if (_monsterBranchProposal is not null)
                scope = new NestedScope(scope, LabelMonsterMoveScope.Enter(_monsterBranchProposal.BeginRoll, _monsterBranchProposal.BeginBranch));
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
    private sealed class NestedScope(IDisposable outer, IDisposable inner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { inner.Dispose(); } finally { outer.Dispose(); }
        }
    }
    private IDisposable EnterTapeScope() => _mapOracle is not null
        ? LabelRandomScope.EnterMapRewardProvenance(_mapOracle.Word, _rewardsOracle!.Word, Word,
            BeginShuffle, BeginMonsterHp, BeginCombatRewardGeneration, BeginNormalEncounterGeneration, BeginMapGeneration,
            _neowCardProposal is null && _publicRewardProposal is null && _publicEventCardProposal is null ? null : BeginRewardCardSelection,
            BeginNeowInitialOptions, _neowCardProposal is null ? null : _neowCardProposal.BeginScrollBoxes)
        : _rewardsOracle is null
            ? LabelRandomScope.Enter(Word, BeginShuffle, BeginMonsterHp, BeginCombatRewardGeneration, BeginNormalEncounterGeneration, BeginMapGeneration)
            : LabelRandomScope.EnterRewardProvenance(_rewardsOracle.Word, Word, BeginShuffle, BeginMonsterHp, BeginCombatRewardGeneration,
                BeginNormalEncounterGeneration, BeginMapGeneration,
                _neowCardProposal is null && _publicRewardProposal is null && _publicEventCardProposal is null ? null : BeginRewardCardSelection,
                BeginNeowInitialOptions, _neowCardProposal is null ? null : _neowCardProposal.BeginScrollBoxes);
    internal NativeLabelTape ReplayCopy()
    {
        RequireSuccessfulConditionedWords();
        return new(_recipe, _condition, _overrides, _expectedEntryJson, _hpCondition, _neowCondition,
            _firstRewardCondition, _firstEncounterCondition, _initialPrefixPlan, _rewardsOracle?.ReplayCopy(), _publicRewardCondition, _publicCombatCondition, _expectedPublicEvidence, _publicOpeningEncounterCondition, _neowCardCondition, _publicResourceCondition, _slugIntentCondition, _weakFormationCondition, _publicReshuffleCondition, _monsterBranchCondition, _weakEncounterCondition, _eventPermutationCondition, _eventPermutationPlan, _mapOracle?.ReplayCopy(), _mapReconstructionCondition, _neowPotionCondition, _publicEventCardCondition, _publicUnknownRoomCondition, _publicShopCondition, _publicActPrefixPlan);
    }
    private void CaptureConditionedFailure(Exception error)
    {
        // A genuine public nonmatch and external cancellation retain their own
        // categories. Fatal callback failures cannot disappear behind a later
        // mismatch, absent world, cleanup, or replay reconstruction.
        if (error is not (NativePublicConstraintMismatchException or OperationCanceledException))
            _conditionedWordFailure ??= error;
    }
    internal bool HasConditionedWordFailure => _conditionedWordFailure is not null;
    internal InvalidOperationException? ConditionedWordError => _conditionedWordFailure is { } original
        ? new InvalidOperationException("A conditioned native word callback failed; this tape remains unresolved: "
            + original.Message, original) : null;
    internal void RequireSuccessfulConditionedWords()
    {
        if (ConditionedWordError is { } error) throw error;
    }
    internal void CheckPublicPrefix(PublicRunEvidence? evidence)
    {
        RequireSuccessfulConditionedWords();
        _publicPrefixConstraint?.Check(evidence);
    }
    internal void ObservePublicEvidence(PublicRunEvidenceEvent evidence)
    {
        RequireSuccessfulConditionedWords();
        _publicPrefixConstraint?.Observe(evidence);
        // Observe only after the existing exact public-prefix comparison succeeds.
        _publicUnknownRoomProposal?.ObservePublicEvidence(evidence);
    }

    private IDisposable EnterStateOverride(Func<LabelRandomState, ulong> nextState) => _mapOracle is not null
        ? LabelRandomScope.EnterMapRewardProvenance(_mapOracle.Word, _rewardsOracle!.Word, nextState)
        : _rewardsOracle is null ? LabelRandomScope.Enter(nextState)
            : LabelRandomScope.EnterRewardProvenance(_rewardsOracle.Word, nextState);
    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("A hypothetical tape already owns a native run");
        if (_initialPrefixPlan is not null && (!_prefixComplete || !ReferenceEquals(_constructingRun, run)))
            throw new InvalidOperationException("Initial prefix did not finish in this native construction");
        _publicActPrefixReplay?.Attach(run);
        _run = run;
        _mapReconstructionProposal?.AttachHypotheticalRun(run);
        _publicCombatProposal?.AttachHypotheticalRun(run);
        _slugIntentProposal?.AttachHypotheticalRun(run);
        _weakFormationProposal?.AttachHypotheticalRun(run);
        _publicReshuffleProposal?.AttachHypotheticalRun(run);
        _monsterBranchProposal?.AttachHypotheticalRun(run);
        _publicOpeningEncounterProposal?.AttachHypotheticalRun(run);
        _weakEncounterProposal?.AttachHypotheticalRun(run);
        _eventPermutationProposal?.AttachHypotheticalRun(run);
        _neowCardProposal?.AttachHypotheticalRun(run);
        _neowPotionProposal?.AttachHypotheticalRun(run);
        _publicEventCardProposal?.AttachHypotheticalRun(run);
        _publicUnknownRoomProposal?.AttachHypotheticalRun(run);
        _publicShopProposal?.AttachHypotheticalRun(run);
    }

    internal void CombatEntering(int combatIndex, NativeEntryAssets entry, Rng initialShuffleRng)
    {
        _currentCombatIndex = combatIndex;
        _publicCombatProposal?.CombatEntering(combatIndex, entry, initialShuffleRng);
        _slugIntentProposal?.CombatEntering(combatIndex, entry, initialShuffleRng);
        _weakFormationProposal?.CombatEntering(combatIndex, entry, initialShuffleRng);
        _publicReshuffleProposal?.CombatEntering(combatIndex, entry, initialShuffleRng);
        _monsterBranchProposal?.CombatEntering(combatIndex, entry, initialShuffleRng);
        if (combatIndex != _recipe.CombatIndex) return;
        if (_expectedEntryJson is not null && _expectedEntryJson != PublicJson.Serialize(entry))
            throw new NativePublicConstraintMismatchException("Published target combat entry differs");
        if (_condition is null && _hpCondition is null) return;
        _awaitingInitialShuffle = true;
        _initialShuffleRng = initialShuffleRng;
        _conditioningInitialHp = _hpCondition is not null;
    }

    private ulong Word(LabelRandomState state)
    {
        if (_publicActPrefixReplay is { Bound: false } replay)
            return replay.Word(state, expected =>
            {
                if (!_visited.Add(state) || !_overrides.TryGetValue(state, out var value) || value != expected.Word)
                    throw new InvalidOperationException("Native public act prefix changed or revisited its conditioned oracle cell");
                return value;
            });
        if (_initialPrefixPlan is not null && !_prefixComplete)
        {
            try
            {
                if (_prefixWordsReplayed >= _initialPrefixPlan.Trace.Count
                    || _initialPrefixPlan.Trace[_prefixWordsReplayed].State != state)
                    throw new InvalidOperationException("Native initial prefix changed its complete ordered RNG trace");
                var expected = _initialPrefixPlan.Trace[_prefixWordsReplayed++];
                if (!_overrides.TryGetValue(state, out var value) || value != expected.Word)
                    throw new InvalidOperationException("Native initial prefix changed an aliased oracle word");
                // Internal act/map aliases were jointly sampled. Reuse is required.
                _visited.Add(state); return value;
            }
            catch { _prefixFailed = true; throw; }
        }
        _visited.Add(state);
        if (_overrides.TryGetValue(state, out ulong forced)) return forced;
        Span<byte> bytes = stackalloc byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x4E4F534C54415031UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _recipe.TapeSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], state.State0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], state.State1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], state.State2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], state.State3);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        if (_neowCondition is not null && _run?.CurrentRoom is EventRoom { Event: Neow neow }
            && ReferenceEquals(rng, neow.Rng) && items.Count > 0 && items.All(item => item is Type))
        {
            if (_neowPlan is not null) throw new InvalidOperationException("Native Neow repeated its certified initial positive shuffle");
            var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-neow-v1");
            if (_neowCondition.ObservedCurseId is not null && _neowOpeningPlan is null)
                throw new InvalidOperationException("Observed Neow shuffle was reached without its native opening rolls");
            _neowPlan = _neowCondition.CreateProposal(items.Cast<Type>().ToArray(), random.NextUnsignedLong, _neowOpeningPlan);
            return ForceWords(_neowPlan.Plan.RawWords, "Neow");
        }
        if (_neowCardProposal?.BeginShuffle(rng, items) is { } neowCards) return neowCards;
        if (_publicReshuffleProposal?.BeginShuffle(rng, items) is { } reshuffle) return reshuffle;
        if (_publicCombatProposal is not null) return _publicCombatProposal.BeginShuffle(rng, items);
        if (!_awaitingInitialShuffle || items.Count == 0 || items.Any(x => x is not CardModel)) return null;
        if (!ReferenceEquals(rng, _initialShuffleRng)) return null;
        _awaitingInitialShuffle = false;
        _conditioningInitialHp = false;
        if (_hpCondition is not null && _conditionedHpIds.Count != _hpCondition.EnemyCount)
            throw new NativePublicConstraintMismatchException("Native initial roster has fewer monsters than the public opening");
        var cards = items.Cast<CardModel>().ToArray();
        var owner = cards[0].Owner;
        if (cards.Any(c => !ReferenceEquals(c.Owner, owner) || c.DeckVersion is null || c.Pile?.Type != PileType.Draw)
            || owner.PlayerCombatState is null || !owner.PlayerCombatState.DrawPile.Cards.SequenceEqual(cards))
            throw new InvalidOperationException("Conditional shuffle did not identify the native initial combat draw pile");
        if (_condition is null) return null;
        var ids = cards.Select(c => c.GetType().Name).ToArray();
        if (!_condition!.MatchesInitialPool(ids))
            throw new InvalidOperationException("Certified entry does not match the native initial shuffle pool");
        var proposalRandom = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-shuffle-v1");
        _plan = ConditionalShuffleProposal.Create(ids, _condition.DrawPrefixIds, proposalRandom.NextUnsignedLong)
            ?? throw new NativePublicConstraintMismatchException("Published draw prefix has no permutation support");
        ConditionApplied = true;
        return ForceWords(_plan.RawWords, "shuffle");
    }

    internal void ValidateProposalCompletion()
    {
        RequireSuccessfulConditionedWords();
        _neowCardProposal?.ValidateCompletion();
        _neowPotionProposal?.ValidateCompletion();
        _publicEventCardProposal?.ValidateCompletion();
        _publicUnknownRoomProposal?.ValidateCompletion();
        _publicShopProposal?.ValidateCompletion();
        _publicOpeningEncounterProposal?.ValidateCompletion();
        _weakEncounterProposal?.ValidateCompletion();
        _eventPermutationProposal?.ValidateCompletion();
        _mapReconstructionProposal?.ValidateCompletion();
        _publicActPrefixReplay?.ValidateCompletion();
        _publicRewardProposal?.ValidateCompletion();
        _publicResourceProposal?.ValidateCompletion();
        _publicCombatProposal?.ValidateCompletion();
        _slugIntentProposal?.ValidateCompletion();
        _weakFormationProposal?.ValidateCompletion();
        _publicReshuffleProposal?.ValidateCompletion();
        _monsterBranchProposal?.ValidateCompletion();
        if (_initialPrefixPlan is not null && !_prefixComplete)
            throw new InvalidOperationException("Native initial prefix is incomplete");
        if (_firstEncounterCondition is not null && _firstEncounterPlan is null)
            throw new InvalidOperationException("An encounter-conditioned root was reached without its native normal encounter generation");
        if (_firstRewardCondition is not null && _firstRewardPlan is null)
            throw new InvalidOperationException("A first-reward-conditioned root was reached without its native first reward generation");
        if (_neowCondition?.ObservedCurseId is not null && _neowOpeningPlan is null)
            throw new InvalidOperationException("A Neow-opening-conditioned root was reached without its native opening rolls");
        if (_neowCondition is not null && _neowPlan is null)
            throw new InvalidOperationException("A Neow-conditioned root was reached without its native positive shuffle");
        if (_hpCondition is not null && (_conditioningInitialHp || _hpPlans.Count != _hpCondition.EnemyCount))
            throw new InvalidOperationException("An HP-conditioned root was reached without its complete native initial roster");
        if (_condition is not null && (!ConditionApplied || _plan is null))
            throw new InvalidOperationException("An accelerated target was reached without its certified initial shuffle");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        // No random rejection may hide a missing required hook or a partial plan.
        ValidateProposalCompletion();
        if (_initialPrefixPlan is not null && !_initialPrefixPlan.AcceptCorrection(nextWord)) return false;
        if (_publicActPrefixPlan is not null && !_publicActPrefixPlan.Selection.AcceptCorrection(nextWord)) return false;
        if (_firstEncounterPlan is not null && !_firstEncounterPlan.AcceptCorrection(nextWord)) return false;
        if (_publicOpeningEncounterProposal is not null && !_publicOpeningEncounterProposal.AcceptCorrection(nextWord)) return false;
        if (_weakEncounterProposal is not null && !_weakEncounterProposal.AcceptCorrection(nextWord)) return false;
        if (_eventPermutationProposal is not null && !_eventPermutationProposal.AcceptCorrection(nextWord)) return false;
        if (_neowPlan is not null && !_neowPlan.AcceptCorrection(nextWord)) return false;
        if (_neowCardProposal is not null && !_neowCardProposal.AcceptCorrection(nextWord)) return false;
        if (_firstRewardPlan is not null && !_firstRewardPlan.AcceptCorrection(nextWord)) return false;
        if (_publicRewardProposal is not null && !_publicRewardProposal.AcceptCorrection(nextWord)) return false;
        if (_publicResourceProposal is not null && !_publicResourceProposal.AcceptCorrection(nextWord)) return false;
        if (_neowPotionProposal is not null && !_neowPotionProposal.AcceptCorrection(nextWord)) return false;
        if (_publicEventCardProposal is not null && !_publicEventCardProposal.AcceptCorrection(nextWord)) return false;
        if (_publicUnknownRoomProposal is not null && !_publicUnknownRoomProposal.AcceptCorrection(nextWord)) return false;
        if (_publicShopProposal is not null && !_publicShopProposal.AcceptCorrection(nextWord)) return false;
        if (_publicCombatProposal is not null && !_publicCombatProposal.AcceptCorrection(nextWord)) return false;
        if (_slugIntentProposal is not null && !_slugIntentProposal.AcceptCorrection(nextWord)) return false;
        if (_weakFormationProposal is not null && !_weakFormationProposal.AcceptCorrection(nextWord)) return false;
        if (_publicReshuffleProposal is not null && !_publicReshuffleProposal.AcceptCorrection(nextWord)) return false;
        if (_monsterBranchProposal is not null && !_monsterBranchProposal.AcceptCorrection(nextWord)) return false;
        foreach (var hp in _hpPlans) if (!hp.AcceptCorrection(nextWord)) return false;
        return _plan?.AcceptCorrection(nextWord) ?? true;
    }

    private IDisposable? BeginNeowInitialOptions(LabelNeowInitialOptionsContext context)
    {
        if (_neowCondition?.ObservedCurseId is null) return null;
        if (_run?.CurrentRoom is not EventRoom { Event: Neow neow }
            || !ReferenceEquals(neow, context.Neow) || !ReferenceEquals(neow.Rng, context.Rng))
            throw new InvalidOperationException("Neow opening conditioning escaped its owned hypothetical event");
        if (_neowOpeningPlan is not null)
            throw new InvalidOperationException("Native Neow repeated its certified opening rolls");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-neow-opening-v1");
        _neowOpeningPlan = _neowCondition.CreateOpeningProposal(context.AllowedCurses, random.NextUnsignedLong);
        return ForceNeowOpeningWords(_neowOpeningPlan.RawWords, context.Rng);
    }

    private IDisposable ForceNeowOpeningWords(IReadOnlyList<ulong?> words, Rng nativeRng)
    {
        var snapshot = nativeRng.ToSerializable();
        var clone = new MegaRandom(snapshot);
        var expected = new LabelRandomState[words.Count];
        for (int i = 0; i < expected.Length; i++)
        {
            clone.FillSerializableState(snapshot);
            expected[i] = new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3);
            clone.NextULong();
        }
        int ordinal = 0; bool callbackFailed = false;
        var inner = EnterStateOverride(state =>
        {
            try
            {
                if (ordinal >= words.Count || state != expected[ordinal])
                    throw new InvalidOperationException("Native Neow opening used an unexpected RNG state or draw count");
                ulong? forced = words[ordinal++];
                if (forced is null) return Word(state);
                if (_visited.Contains(state))
                    throw new InvalidOperationException("Conditional Neow opening revisited an earlier tape cell; alias correction is unresolved");
                if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != forced.Value)
                    throw new InvalidOperationException("Owned hypothetical Neow opening replay changed its conditioned tape");
                _overrides[state] = forced.Value; _visited.Add(state);
                return forced.Value;
            }
            catch (Exception exception) { callbackFailed = true; _conditionedWordFailure ??= exception; throw; }
        });
        return new CompleteWordScope(inner, () => callbackFailed || ordinal == words.Count, "Neow opening", CaptureConditionedFailure);
    }

    private IDisposable? BeginMonsterHp(LabelMonsterHpContext context)
    {
        if (_publicCombatProposal is not null) return _publicCombatProposal.BeginMonsterHp(context);
        if (!_conditioningInitialHp) return null;
        string id = context.Creature.Monster?.GetType().Name
            ?? throw new InvalidOperationException("Initial HP conditioning requires a monster");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-hp-v1:" + id);
        var plan = _hpCondition!.CreateProposal(context, _conditionedHpIds, random.NextUnsignedLong);
        _conditionedHpIds.Add(id); _hpPlans.Add(plan);
        return ForceWords([plan.RawWord], "HP");
    }

    private IDisposable? BeginRewardCardSelection(LabelRewardCardSelectionContext context) =>
        _neowCardProposal?.BeginSelection(context) ?? _publicEventCardProposal?.BeginSelection(context) ?? _publicRewardProposal?.BeginSelection(context);

    private IDisposable? BeginCombatRewardGeneration(LabelCombatRewardContext context)
    {
        try { return BeginOwnedCombatRewardGeneration(context); }
        catch (Exception error) { CaptureConditionedFailure(error); throw; }
    }

    private IDisposable? BeginOwnedCombatRewardGeneration(LabelCombatRewardContext context)
    {
        if (_publicRewardProposal is not null)
        {
            if (_run is null || !ReferenceEquals(context.Player.RunState, _run))
                throw new InvalidOperationException("Public reward conditioning escaped its owned hypothetical run");
            var cards = _publicRewardProposal.BeginCombatReward(context, _currentCombatIndex);
            if (_publicResourceProposal is null) return cards;
            IDisposable? resources = null;
            try
            {
                resources = _publicResourceProposal.BeginCombatReward(context, _currentCombatIndex);
                if (cards is null && resources is null) return null;
                if (cards is null || resources is null)
                    throw new InvalidOperationException("Card and resource reward owner certificates disagree");
                return new RewardGenerationScope(_publicRewardProposal, cards, _publicResourceProposal, resources);
            }
            catch
            {
                _publicResourceProposal.AbortActiveBoundary(); resources?.Dispose();
                _publicRewardProposal.AbortActiveBoundary(); cards?.Dispose();
                throw;
            }
        }
        if (_firstRewardCondition is null || _currentCombatIndex != 0) return null;
        if (_firstRewardPlan is not null)
            throw new InvalidOperationException("Native first-combat reward generation occurred twice");
        if (_run is null || !ReferenceEquals(context.Player.RunState, _run))
            throw new InvalidOperationException("Native reward conditioning escaped its owned hypothetical run");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-first-reward-v1");
        _firstRewardPlan = _firstRewardCondition.CreateProposal(context, random.NextUnsignedLong);
        return ForcePrefixWords(_firstRewardPlan.RawWords, context.Rng, "first reward");
    }

    private sealed class RewardGenerationScope(NativePublicRewardProposal cardProposal, IDisposable cards,
        NativePublicRewardResourceProposal resourceProposal, IDisposable resources) : IAbortableLabelRewardBoundary
    {
        private bool _disposed;
        public void Abort()
        { resourceProposal.AbortActiveBoundary(); cardProposal.AbortActiveBoundary(); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { resources.Dispose(); }
            catch { cardProposal.AbortActiveBoundary(); throw; }
            finally
            {
                if (resourceProposal.HasFailed) cardProposal.AbortActiveBoundary();
                cards.Dispose();
            }
        }
    }

    private IDisposable? BeginMapGeneration(LabelMapGenerationContext context)
    {
        // This callback precedes LabelMapConstructionScope, which may consume no words.
        _publicActPrefixReplay?.BeginMap(context);
        if (_initialPrefixPlan is null) return null;
        if (_prefixComplete)
        {
            // Later acts and native map-replacement effects keep their ordinary
            // oracle law. A callback before Attach or from another run is invalid.
            if (_run is null || !ReferenceEquals(context.Run, _run))
                throw new InvalidOperationException("Later map escaped its owned hypothetical run");
            return null;
        }
        if (_constructingRun is not null || _run is not null || _prefixComplete)
            throw new InvalidOperationException("Initial map prefix was entered again outside native construction");
        _initialPrefixPlan.ValidateMapBoundary(context, _prefixWordsReplayed);
        _constructingRun = context.Run;
        return new PrefixCompletionScope(() =>
        {
            if (_prefixFailed) return;
            if (_prefixWordsReplayed != _initialPrefixPlan.Trace.Count)
                throw new InvalidOperationException("Native map did not replay its complete selected prefix");
            _prefixComplete = true;
        });
    }

    private sealed class PrefixCompletionScope(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }

    private IDisposable? BeginNormalEncounterGeneration(LabelNormalEncounterContext context)
    {
        if (_weakEncounterProposal is not null) return _weakEncounterProposal.BeginGeneration(context);
        if (_publicOpeningEncounterProposal is not null) return _publicOpeningEncounterProposal.BeginGeneration(context);
        if (_firstEncounterCondition is null) return null;
        if (_firstEncounterPlan is not null)
            throw new InvalidOperationException("Native first-act normal encounter generation occurred twice");
        if (_run is null || !ReferenceEquals(context.Run, _run))
            throw new InvalidOperationException("Encounter conditioning escaped its owned hypothetical run");
        var random = new Rng(_recipe.ProposalSeed, "nosl-native-tape-conditional-first-encounters-v1");
        _firstEncounterPlan = _firstEncounterCondition.CreateProposal(context, random.NextUnsignedLong);
        return ForcePrefixWords(_firstEncounterPlan.RawWords, context.Rng, "first encounters");
    }

    private IDisposable ForcePrefixWords(IReadOnlyList<ulong> words, Rng nativeRng, string purpose)
    {
        // This callback runs with label interception suppressed. Advancing this
        // exact private clone records expected addresses without reading source
        // data, consuming the native stream, or changing hypothetical tape cells.
        var snapshot = nativeRng.ToSerializable();
        // Use the primitive copy so hypothetical address enumeration does not
        // emit native Rng diagnostics or copy its per-draw observer.
        var clone = new MegaRandom(snapshot);
        var expected = new LabelRandomState[words.Count];
        for (int i = 0; i < expected.Length; i++)
        {
            clone.FillSerializableState(snapshot);
            expected[i] = new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3);
            clone.NextULong();
        }
        int ordinal = 0; bool callbackFailed = false;
        var inner = EnterStateOverride(state =>
        {
            if (ordinal == words.Count) return Word(state);
            try
            {
                if (state != expected[ordinal])
                    throw new InvalidOperationException($"Native {purpose} prefix used an unexpected RNG state or stream");
                if (_visited.Contains(state))
                    throw new InvalidOperationException($"Conditional {purpose} revisited an earlier tape cell; alias correction is unresolved");
                ulong value = words[ordinal++];
                if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != value)
                    throw new InvalidOperationException($"Owned hypothetical {purpose} replay changed its conditioned tape");
                _overrides[state] = value; _visited.Add(state);
                return value;
            }
            catch (Exception exception) { callbackFailed = true; _conditionedWordFailure ??= exception; throw; }
        });
        return new CompleteWordScope(inner, () => callbackFailed || ordinal == words.Count, $"{purpose} prefix", CaptureConditionedFailure);
    }

    private IDisposable ForceWords(IReadOnlyList<ulong> words, string purpose)
    {
        int ordinal = 0; bool callbackFailed = false;
        var inner = EnterStateOverride(state =>
        {
            try
            {
                if (ordinal >= words.Count) throw new InvalidOperationException($"Native {purpose} consumed an unexpected extra word");
                if (_visited.Contains(state))
                    throw new InvalidOperationException($"Conditional {purpose} revisited an earlier tape cell; alias correction is unresolved");
                ulong value = words[ordinal++];
                if (_overrides.TryGetValue(state, out ulong replayValue) && replayValue != value)
                    throw new InvalidOperationException($"Owned hypothetical {purpose} replay changed its conditioned tape");
                _overrides[state] = value; _visited.Add(state);
                return value;
            }
            catch (Exception exception) { callbackFailed = true; _conditionedWordFailure ??= exception; throw; }
        });
        return new CompleteWordScope(inner, () => callbackFailed || ordinal == words.Count, purpose, CaptureConditionedFailure);
    }

    private sealed class CompleteWordScope(IDisposable inner, Func<bool> complete, string purpose,
        Action<Exception> captureFailure) : IAbortableConditionedWordScope
    {
        private bool _disposed, _aborted;
        public void Abort() => _aborted = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                inner.Dispose();
                if (!_aborted && !complete())
                    throw new InvalidOperationException($"Native {purpose} did not consume its complete conditioned word sequence");
            }
            catch (Exception error) { captureFailure(error); throw; }
        }
    }
}
