using System.Collections.Immutable;
using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicBrainLeechCardTests
{
    internal static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Fact]
    public async Task NativeOfferConditionsAllFiveWithoutPityMutationAndReplaysOrdinaryContinuation()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var sourceOracle = new NativeRewardsOracle(71);
        var source = await Fixture(sourceOracle.Word, pity: 0.4f);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Root));
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var target = Assert.Single(condition!.Targets);
        var offer = Assert.Single(root.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCardsObserved>()).Choice;
        Assert.Equal(nameof(BrainLeech), target.Source);
        Assert.Equal(offer.Candidates.Select(c => c.Id), target.Certificate.BaseCardIds);
        Assert.Equal(5, target.Certificate.BaseCardIds.Count);
        Assert.Equal(10, sourceOracle.DistinctCells);
        Assert.Equal(0.4f, source.Run.Players[0].Odds.CardRarity.CurrentValue);

        var oracle = new NativeRewardsOracle(914);
        var proposal = new NativePublicEventCardProposal(condition, oracle.WasVisited, oracle.ForceFresh, new Rng(1928).NextUnsignedLong);
        var actual = await Fixture(oracle.Word, proposal, pity: -0.05f);
        proposal.ValidateCompletion();
        Assert.Equal(5, proposal.ConditionedCardCount); Assert.Equal(1, proposal.ConditionedOfferCount);
        Assert.Equal(10, oracle.ConditionedCells); Assert.Equal(10, oracle.DistinctCells);
        Assert.Equal(-0.05f, actual.Run.Players[0].Odds.CardRarity.CurrentValue);
        Assert.Equal(condition.Envelope, proposal.NativeToProposalRatio);
        Assert.True(proposal.AcceptCorrection(() => throw new Exception("Constant correction draws no word")));
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(actual.Root));

        var replayOracle = oracle.ReplayCopy();
        var replay = new NativePublicEventCardProposal(condition, replayOracle.WasVisited, replayOracle.ForceFresh, new Rng(1928).NextUnsignedLong);
        var fork = await Fixture(replayOracle.Word, replay);
        replay.ValidateCompletion();
        Assert.Equal(PublicJson.Serialize(actual.Root), PublicJson.Serialize(fork.Root));
        Assert.Equal(PublicJson.Serialize(actual.Run.Players[0].PlayerRng.Rewards.ToSerializable()),
            PublicJson.Serialize(fork.Run.Players[0].PlayerRng.Rewards.ToSerializable()));
        ulong ordinary, replayOrdinary;
        using (LabelRandomScope.EnterRewardProvenance(oracle.Word, StateWord))
            ordinary = actual.Run.Players[0].PlayerRng.Rewards.NextUnsignedLong();
        using (LabelRandomScope.EnterRewardProvenance(replayOracle.Word, StateWord))
            replayOrdinary = fork.Run.Players[0].PlayerRng.Rewards.NextUnsignedLong();
        Assert.Equal(ordinary, replayOrdinary);
        var provenance = actual.Run.Players[0].PlayerRng.Rewards.ToSerializable().LabelProvenance!;
        Assert.Equal(new NativeRewardsOracle(914).Word(new(LabelRandomProvenance.RewardsOrigin,
            provenance.InitialSeed!.Value, provenance.RawCursor!.Value - 1)), ordinary);
        Assert.Equal(10, oracle.ConditionedCells); Assert.Equal(11, oracle.DistinctCells);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FiniteFiveSlotLawCountsEveryRarityArmDuplicateBucketAndUnpickedCard(int bits)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var backflip = ModelDb.Card<Backflip>();
        CardModel[] pool = [backflip, ModelDb.Card<Backstab>(), backflip, (CardModel)backflip.MutableClone(),
            ModelDb.Card<Deflect>(), ModelDb.Card<Prepared>(), ModelDb.Card<Finisher>()];
        string[] ids = [nameof(Backflip), nameof(Backstab), nameof(Finisher), nameof(Deflect), nameof(Prepared)];
        var certificate = NativeEventCardPoolCertificate.FromDefaultOdds(pool, ids, 10, bits);
        Assert.Equal(6, certificate.SlotPools[0].Count);
        Assert.Equal(4, certificate.SlotPools[1].Count);
        Assert.DoesNotContain(certificate.SlotPools[1], card => card.Id == backflip.Id);
        int domain = 1 << bits;
        var thresholds = certificate.RarityThresholds!.Value;
        BigInteger accepted = BigInteger.One;
        for (int slot = 0; slot < ids.Length; slot++)
        {
            var remaining = certificate.SlotPools[slot];
            long pairs = 0;
            // Independent finite native decoder: float rarity, cyclic fallback,
            // double index, then all matching ModelIds disappear before next slot.
            for (int rarityWord = 0; rarityWord < domain; rarityWord++)
            {
                float roll = (float)((double)rarityWord / domain);
                CardRarity rarity = roll < thresholds.RareUpperExclusive ? CardRarity.Rare
                    : roll < thresholds.UncommonUpperExclusive ? CardRarity.Uncommon : CardRarity.Common;
                while (!remaining.Any(card => card.Rarity == rarity))
                    rarity = rarity switch { CardRarity.Rare => CardRarity.Common, CardRarity.Common => CardRarity.Uncommon, _ => CardRarity.Rare };
                var candidates = remaining.Where(card => card.Rarity == rarity).ToArray();
                for (int indexWord = 0; indexWord < domain; indexWord++)
                    if (candidates[(int)((double)indexWord / domain * candidates.Length)].GetType().Name == ids[slot]) pairs++;
            }
            Assert.Equal(new ShuffleRational(pairs, domain * domain), certificate.SlotMasses[slot]);
            accepted *= pairs;
        }
        Assert.Equal(new ShuffleRational(accepted, BigInteger.Pow(domain, 10)), certificate.Envelope);
        var chosenOnly = NativeEventCardPoolCertificate.FromDefaultOdds(pool, ids.Take(1).ToArray(), 10, bits);
        Assert.True(certificate.Envelope.Numerator * chosenOnly.Envelope.Denominator
            < chosenOnly.Envelope.Numerator * certificate.Envelope.Denominator);
        Assert.Equal(new ShuffleRational(1, BigInteger.Pow(domain, 10)),
            new ShuffleRational(1, accepted).Multiply(certificate.Envelope.Numerator, certificate.Envelope.Denominator));

        var arms = NativeRewardIdentityMath.Arms(certificate.SlotBranches[0], thresholds, ids[0], bits);
        Assert.Equal(new CardRarity?[] { CardRarity.Rare, CardRarity.Common }, arms.Where(arm => arm.Mass > 0).Select(arm => arm.RolledRarity));
        Assert.All(arms.Where(arm => arm.Mass > 0), arm => Assert.Equal(2, arm.MatchingIndices.Count));
        Assert.All(certificate.SlotBranches[3], branch => Assert.All(branch.Candidates, card => Assert.Equal(CardRarity.Common, card.Rarity)));
        var context = new LabelRewardCardSelectionContext(null!, null!, LabelRewardCardSelectionKind.CreationOptions,
            0, 5, CardCreationSource.Other, CardCreationFlags.NoUpgradeRoll, CardRarityOddsType.RegularEncounter,
            thresholds, false, certificate.SlotPools[0], certificate.SlotBranches[0]);
        var random = new Rng(278);
        var plans = Enumerable.Range(0, 64).Select(_ => NativeRewardIdentityMath.Create(context, ids[0], random.NextUnsignedLong, bits)).ToArray();
        Assert.Contains(plans, plan => plan.RolledRarity == CardRarity.Rare);
        Assert.Contains(plans, plan => plan.RolledRarity == CardRarity.Common);
        ulong lowMask = (1UL << (64 - bits)) - 1;
        Assert.All(plans, plan =>
        {
            Assert.Equal(2, plan.RawWords.Count);
            Assert.Equal(certificate.SlotMasses[0], plan.NativeToProposalRatio);
        });
        Assert.True(plans.SelectMany(plan => plan.RawWords).Select(word => word & lowMask).Distinct().Count() > 1);
    }

    [Theory]
    [InlineData("missing_unpicked", "brain_leech_requires_all_five_displayed_cards")]
    [InlineData("unordered", "brain_leech_requires_all_five_displayed_cards")]
    [InlineData("wrong_option", "brain_leech_requires_public_source_option_boundary")]
    [InlineData("missing_assets", "brain_leech_requires_immediate_public_before_assets")]
    [InlineData("modifier", "brain_leech_public_inventory_hook_closure_not_certified")]
    [InlineData("duplicate", "brain_leech_public_cards_have_no_native_pool_support")]
    public async Task IncompleteOrUncertifiedPublicOfferDeclinesTheProposal(string change, string expected)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        var events = root.PublicEvidence!.Events;
        int at = events.IndexOf(events.Single(e => e.Payload is PublicCardsObserved));
        var choice = ((PublicCardsObserved)events[at].Payload).Choice;
        if (change is "missing_unpicked" or "unordered" or "duplicate")
        {
            if (change == "missing_unpicked") choice = choice with { Candidates = choice.Candidates.Take(4).ToArray() };
            if (change == "unordered") choice = choice with { CandidateOrder = "canonical_unordered_reveal" };
            if (change == "duplicate") choice.Candidates[4] = choice.Candidates[0];
            events = events.SetItem(at, new(events[at].EventOrdinal, events[at].OwnerOrdinal, new PublicCardsObserved(choice)));
        }
        else if (change == "wrong_option")
            events = events.SetItem(at - 2, new(events[at - 2].EventOrdinal, events[at - 2].OwnerOrdinal,
                new PublicOptionChosen(events[at - 3].EventOrdinal, "RIP")));
        else
        {
            var assets = ((PublicOwnerEnded)events[at - 5].Payload).Assets!;
            if (change == "modifier") assets = new(assets.Hp, assets.MaxHp, assets.Gold, assets.Deck,
                assets.Relics.Append(new PublicRelic(nameof(DingyRug), new Dictionary<string, int>())).ToArray(),
                assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
            events = events.SetItem(at - 5, new(events[at - 5].EventOrdinal, events[at - 5].OwnerOrdinal,
                new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, change == "missing_assets" ? null : assets)));
        }
        root = root with { PublicEvidence = new(PublicRunEvidence.Version, true, events) };
        Assert.False(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task EitherAliasedCellOrUntaggedLineageFailsBeforeAnyForcedWrite(int aliasedCell, bool legacyLaw)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        int writes = 0; ulong? firstCursor = null;
        bool WasVisited(LabelRandomAddressV1 address)
        {
            firstCursor ??= address.RawCursor;
            return address.RawCursor == firstCursor + (ulong)aliasedCell;
        }
        var proposal = new NativePublicEventCardProposal(condition!, WasVisited, (_, _) => writes++, new Rng(17).NextUnsignedLong);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture(_ => 0, proposal, legacyLaw: legacyLaw));
        Assert.Contains(legacyLaw ? "tagged native Rewards provenance" : "fresh Rewards cells", error.Message);
        Assert.Equal(0, writes); Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public async Task IncompleteOrExtraNativeDrawLeavesTheProposalFailed()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        var oracle = new NativeRewardsOracle(914);
        var proposal = new NativePublicEventCardProposal(condition!, oracle.WasVisited, oracle.ForceFresh, new Rng(1928).NextUnsignedLong);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture(oracle.Word, proposal, extraSelectionWord: true));
        Assert.Contains("skipped, restored, or added", error.Message);
        Assert.Equal(2, oracle.ConditionedCells); Assert.Equal(3, oracle.DistinctCells);
        Assert.Equal(0, proposal.ConditionedCardCount); Assert.Equal(0, proposal.ConditionedOfferCount);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
    }

    [Fact]
    public async Task LavaRockAndPowderedDemiseHaveClosureButBeforeAssetsMismatchCannotForce()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        var events = root.PublicEvidence!.Events;
        int at = events.IndexOf(events.Single(e => e.Payload is PublicCardsObserved));
        var assets = ((PublicOwnerEnded)events[at - 5].Payload).Assets!;
        var reviewed = new PublicEvidenceAssets(assets.Hp, assets.MaxHp, assets.Gold, assets.Deck,
            assets.Relics.Append(new PublicRelic(nameof(LavaRock), new Dictionary<string, int>())).ToArray(),
            ["PowderedDemise", null], assets.MaxEnergy, 2, assets.OrbSlots, assets.CardRemovalsUsed);
        Assert.True(NativeEventCardPoolCertificate.UnmodifiedInventory(reviewed));
        events = events.SetItem(at - 5, new(events[at - 5].EventOrdinal, events[at - 5].OwnerOrdinal,
            new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, reviewed)));
        root = root with { PublicEvidence = new(PublicRunEvidence.Version, true, events) };
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        int writes = 0;
        var proposal = new NativePublicEventCardProposal(condition!, _ => false, (_, _) => writes++, new Rng(17).NextUnsignedLong);
        var error = await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => Fixture(_ => 0, proposal));
        Assert.Contains("before-assets", error.Message); Assert.Equal(0, writes);
    }

    [Fact]
    public void IdentityRequiresTheOrderedCompleteUnlockedUnpricedInitialPage()
    {
        var options = new[] { new PublicVisibleOption("SHARE_KNOWLEDGE", false), new PublicVisibleOption("RIP", false) }.ToImmutableArray();
        Assert.Equal(nameof(BrainLeech), NativeEventSelectionCertificate.Identify(new(options)));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Reverse().ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.RemoveAt(1))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(0, new("SHARE_KNOWLEDGE", true)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(1, new("RIP", false, 1)))));
    }

    private static async Task<(DecisionPacket Root, RunState Run)> Fixture(Func<LabelRandomAddressV1, ulong> words,
        NativePublicEventCardProposal? proposal = null, float pity = -0.05f, bool legacyLaw = false, bool extraSelectionWord = false)
    {
        IDisposable? Begin(LabelRewardCardSelectionContext context)
        {
            var inner = proposal?.BeginSelection(context);
            return inner is null || !extraSelectionWord ? inner : new Completion(() => { context.Rng.NextUnsignedLong(); inner.Dispose(); });
        }
        using var scope = legacyLaw ? LabelRandomScope.Enter(StateWord, beginRewardCardSelection: Begin)
            : LabelRandomScope.EnterRewardProvenance(words, StateWord, beginRewardCardSelection: Begin);
        var run = new RunState("constructed-public-brain-leech-fixture", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        player.Odds.CardRarity.OverrideCurrentValue(pity);
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        var point = run.Map.StartingMapPoint.Children.OrderBy(p => p.coord.col).First(); point.PointType = MapPointType.Unknown;
        var evidence = new NativePublicRunEvidence(run); evidence.BeginRun(true);
        evidence.Map([point], point); run.AddVisitedMapCoord(point.coord);
        _ = new RunDriver(run, new FixtureSource(evidence)); proposal?.AttachHypotheticalRun(run);
        var room = new EventRoom(() => (BrainLeech)ModelDb.Event<BrainLeech>().MutableClone());
        run.PushRoom(room); await room.Enter(run); evidence.EnterFloor();
        var option = room.Event.CurrentOptions.Single(o => o.Key == "SHARE_KNOWLEDGE");
        evidence.EventOptions(room.Event.CurrentOptions, option); await room.Event.ChooseOption(option);
        Assert.True(room.Event.IsFinished); evidence.ExitFloor();
        return (new("fixture", null, [], evidence.Capture()), run);
    }

    private static ulong StateWord(LabelRandomState state) => state.State0 ^ state.State1 ^ state.State2 ^ state.State3;
    private sealed class Completion(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class FixtureSource(NativePublicRunEvidence evidence) : IRunDecisionSource
    {
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new NotSupportedException();
        public Task<CombatDecision> ChooseCombatActionAsync(CombatState state) => throw new NotSupportedException();
        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            Assert.IsType<BrainLeech>(request.Source);
            var choice = new PublicChoice(nameof(BrainLeech), request.MinCount, request.MaxCount,
                request.Cancelable, request.Candidates.Select(PublicViews.Card).ToArray());
            evidence.OutsideCards(choice, [0]);
            return Task.FromResult<IReadOnlyList<CardModel>>(request.Candidates.Take(1).ToArray());
        }
    }
}
