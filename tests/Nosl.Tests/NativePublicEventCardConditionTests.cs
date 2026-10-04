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

public sealed class NativePublicEventCardConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Fact]
    public async Task ActualNativeGorgeConditionsAllEightAndReplaysPublicOffersAndOrdinaryContinuation()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var sourceOracle = new NativeRewardsOracle(71);
        var source = await Fixture(sourceOracle.Word);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Root));
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var target = Assert.Single(condition!.Targets);
        var offer = Assert.Single(root.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCardsObserved>()).Choice;
        Assert.Equal(offer.Candidates.Select(c => c.Id), target.Certificate.BaseCardIds);
        Assert.Equal(8, target.Certificate.BaseCardIds.Count);
        Assert.Equal(nameof(RoomFullOfCheese), target.Source);
        Assert.Equal(8, sourceOracle.DistinctCells);

        var oracle = new NativeRewardsOracle(914);
        var proposal = new NativePublicEventCardProposal(condition, oracle.WasVisited, oracle.ForceFresh, new Rng(1928).NextUnsignedLong);
        var actual = await Fixture(oracle.Word, proposal);
        proposal.ValidateCompletion();
        Assert.Equal(8, proposal.ConditionedCardCount); Assert.Equal(1, proposal.ConditionedOfferCount);
        Assert.Equal(8, oracle.ConditionedCells); Assert.Equal(8, oracle.DistinctCells);
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
        // Gorge's helper sets NoUpgradeRoll: all eight cells are identities. The
        // next native Rewards cell, outside the event, remains an ordinary draw.
        ulong ordinary, replayOrdinary;
        using (LabelRandomScope.EnterRewardProvenance(oracle.Word, StateWord))
            ordinary = actual.Run.Players[0].PlayerRng.Rewards.NextUnsignedLong();
        using (LabelRandomScope.EnterRewardProvenance(replayOracle.Word, StateWord))
            replayOrdinary = fork.Run.Players[0].PlayerRng.Rewards.NextUnsignedLong();
        Assert.Equal(ordinary, replayOrdinary);
        var provenance = actual.Run.Players[0].PlayerRng.Rewards.ToSerializable().LabelProvenance!;
        Assert.Equal(new NativeRewardsOracle(914).Word(new(LabelRandomProvenance.RewardsOrigin,
            provenance.InitialSeed!.Value, provenance.RawCursor!.Value - 1)), ordinary);
        Assert.Equal(8, oracle.ConditionedCells); Assert.Equal(9, oracle.DistinctCells);
    }

    [Theory]
    [InlineData("missing_unpicked", "gorge_requires_all_eight_displayed_cards")]
    [InlineData("unordered", "gorge_requires_all_eight_displayed_cards")]
    [InlineData("wrong_option", "gorge_requires_public_source_option_boundary")]
    [InlineData("missing_assets", "gorge_requires_immediate_public_before_assets")]
    [InlineData("modifier", "gorge_public_inventory_hook_closure_not_certified")]
    [InlineData("duplicate", "gorge_public_cards_have_no_native_pool_support")]
    public async Task IneligiblePublicEvidenceDeclinesProposalWithoutNarrowingTheSource(string change, string expected)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var fixture = await Fixture(new NativeRewardsOracle(71).Word);
        var events = fixture.Root.PublicEvidence!.Events;
        int at = events.IndexOf(events.Single(e => e.Payload is PublicCardsObserved));
        var choice = ((PublicCardsObserved)events[at].Payload).Choice;
        if (change is "missing_unpicked" or "unordered" or "duplicate")
        {
            if (change == "missing_unpicked") choice = choice with { Candidates = choice.Candidates.Take(7).ToArray() };
            if (change == "unordered") choice = choice with { CandidateOrder = "canonical_unordered_reveal" };
            if (change == "duplicate") choice.Candidates[7] = choice.Candidates[0];
            events = events.SetItem(at, new(events[at].EventOrdinal, events[at].OwnerOrdinal, new PublicCardsObserved(choice)));
        }
        else if (change == "wrong_option")
            events = events.SetItem(at - 2, new(events[at - 2].EventOrdinal, events[at - 2].OwnerOrdinal,
                new PublicOptionChosen(events[at - 3].EventOrdinal, "SEARCH")));
        else
        {
            var assets = ((PublicOwnerEnded)events[at - 5].Payload).Assets!;
            if (change == "modifier") assets = new(assets.Hp, assets.MaxHp, assets.Gold, assets.Deck,
                assets.Relics.Append(new PublicRelic(nameof(DingyRug), new Dictionary<string, int>())).ToArray(),
                assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
            events = events.SetItem(at - 5, new(events[at - 5].EventOrdinal, events[at - 5].OwnerOrdinal,
                new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, change == "missing_assets" ? null : assets)));
        }
        var root = fixture.Root with { PublicEvidence = new(PublicRunEvidence.Version, true, events) };
        Assert.False(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
        Assert.False(NativePublicEventCardCondition.TryCreate(fixture.Root,
            Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out reason));
        Assert.Equal("event_cards_require_rewards_hybrid_prior", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasedOrUntaggedRewardsFailBeforeForcedWrites(bool legacyLaw)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        int writes = 0;
        var proposal = new NativePublicEventCardProposal(condition!, _ => true, (_, _) => writes++, new Rng(981).NextUnsignedLong);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture(_ => 0, proposal, legacyLaw));
        Assert.Contains(legacyLaw ? "tagged native Rewards provenance" : "fresh Rewards cells", error.Message);
        Assert.Equal(0, writes); Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public async Task IncompleteAndChangedNativeDrawContractsRemainFailures()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        var oracle = new NativeRewardsOracle(914);
        var proposal = new NativePublicEventCardProposal(condition!, oracle.WasVisited, oracle.ForceFresh, new Rng(1928).NextUnsignedLong);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture(oracle.Word, proposal,
            extraSelectionWord: true));
        Assert.Contains("skipped, restored, or added", error.Message);
        Assert.Equal(1, oracle.ConditionedCells); Assert.Equal(2, oracle.DistinctCells);
        Assert.Equal(0, proposal.ConditionedCardCount); Assert.Equal(0, proposal.ConditionedOfferCount);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
    }

    [Fact]
    public async Task ReviewedPublicInventoryHasFixedClosureButBeforeAssetsMismatchCannotForceWords()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var root = (await Fixture(new NativeRewardsOracle(71).Word)).Root;
        var events = root.PublicEvidence!.Events;
        int at = events.IndexOf(events.Single(e => e.Payload is PublicCardsObserved));
        var assets = ((PublicOwnerEnded)events[at - 5].Payload).Assets!;
        var reviewedAssets = new PublicEvidenceAssets(assets.Hp, assets.MaxHp, assets.Gold, assets.Deck,
            assets.Relics.Append(new PublicRelic(nameof(PreciseScissors), new Dictionary<string, int>())).ToArray(),
            ["BlessingOfTheForge", null], assets.MaxEnergy, 2, assets.OrbSlots, assets.CardRemovalsUsed);
        Assert.True(NativeEventCardPoolCertificate.UnmodifiedInventory(reviewedAssets));
        var mismatch = new PublicEvidenceAssets(assets.Hp - 1, assets.MaxHp, assets.Gold, assets.Deck,
            assets.Relics, assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
        events = events.SetItem(at - 5, new(events[at - 5].EventOrdinal, events[at - 5].OwnerOrdinal,
            new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, mismatch)));
        root = root with { PublicEvidence = new(PublicRunEvidence.Version, true, events) };
        Assert.True(NativePublicEventCardCondition.TryCreate(root, Prior, out var condition, out _));
        int writes = 0;
        var proposal = new NativePublicEventCardProposal(condition!, _ => false, (_, _) => writes++, new Rng(17).NextUnsignedLong);
        var error = await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => Fixture(_ => 0, proposal));
        Assert.Contains("before-assets", error.Message); Assert.Equal(0, writes);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FiniteUniformLawUsesNativeDistinctThenModelIdExclusionsAndUnpickedCards(int bits)
    {
        NaturalSourceCollector.InitializeNativeModels();
        // The repeated reference disappears under Distinct; a distinct clone of
        // the same ModelId survives, contributes its bucket, and is then excluded.
        var backflip = ModelDb.Card<Backflip>();
        CardModel[] pool = [backflip, ModelDb.Card<DeadlyPoison>(), backflip,
            (CardModel)backflip.MutableClone(), ModelDb.Card<Deflect>(), ModelDb.Card<Prepared>()];
        string[] targets = [nameof(Backflip), nameof(Deflect), nameof(DeadlyPoison)];
        int domain = 1 << bits;
        var certificate = NativeEventCardPoolCertificate.FromPool(pool, targets, bits);
        Assert.Equal(5, certificate.SlotPools[0].Count);
        Assert.Equal(3, certificate.SlotPools[1].Count);
        Assert.DoesNotContain(certificate.SlotPools[1], c => c.Id == backflip.Id);
        long Count(CardModel[] remaining, int slot)
        {
            if (slot == targets.Length) return 1;
            long count = 0;
            for (int word = 0; word < domain; word++)
            {
                var selected = remaining[(int)((double)word / domain * remaining.Length)];
                if (selected.GetType().Name == targets[slot])
                    count += Count(remaining.Where(c => c.Id != selected.Id).ToArray(), slot + 1);
            }
            return count;
        }
        long accepted = Count(pool.Distinct().ToArray(), 0);
        Assert.Equal(new ShuffleRational(accepted, BigInteger.Pow(domain, targets.Length)), certificate.Envelope);
        var chosenOnly = NativeEventCardPoolCertificate.FromPool(pool, targets.Take(2).ToArray(), bits);
        Assert.True(certificate.Envelope.Numerator * chosenOnly.Envelope.Denominator
            < chosenOnly.Envelope.Numerator * certificate.Envelope.Denominator);
        // With fixed-root B = Z, correction is one. For each supported raw
        // sequence q(w) Z = p(w); discarded paths retain zero proposal mass.
        Assert.Equal(new ShuffleRational(1, BigInteger.Pow(domain, targets.Length)),
            new ShuffleRational(1, accepted).Multiply(certificate.Envelope.Numerator, certificate.Envelope.Denominator));
    }

    internal static async Task<(DecisionPacket Root, RunState Run)> Fixture(Func<LabelRandomAddressV1, ulong> words,
        NativePublicEventCardProposal? proposal = null, bool legacyLaw = false, bool extraSelectionWord = false)
    {
        IDisposable? Begin(LabelRewardCardSelectionContext context)
        {
            var inner = proposal?.BeginSelection(context);
            return inner is null || !extraSelectionWord ? inner : new Completion(() =>
            {
                context.Rng.NextUnsignedLong(); inner.Dispose();
            });
        }
        using var scope = legacyLaw
            ? LabelRandomScope.Enter(StateWord, beginRewardCardSelection: Begin)
            : LabelRandomScope.EnterRewardProvenance(words, StateWord,
                beginRewardCardSelection: Begin);
        var run = new RunState("constructed-public-gorge-fixture", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        // Explicit constructed path, not a natural-source reachability claim.
        var point = run.Map.StartingMapPoint.Children.OrderBy(p => p.coord.col).First();
        point.PointType = MapPointType.Unknown;
        var evidence = new NativePublicRunEvidence(run); evidence.BeginRun(true);
        evidence.Map([point], point); run.AddVisitedMapCoord(point.coord);
        var source = new FixtureSource(evidence); _ = new RunDriver(run, source);
        proposal?.AttachHypotheticalRun(run);
        var room = new EventRoom(() => (RoomFullOfCheese)ModelDb.Event<RoomFullOfCheese>().MutableClone());
        run.PushRoom(room); await room.Enter(run); evidence.EnterFloor();
        var gorge = room.Event.CurrentOptions.Single(o => o.Key == "GORGE");
        evidence.EventOptions(room.Event.CurrentOptions, gorge);
        await room.Event.ChooseOption(gorge); Assert.True(room.Event.IsFinished);
        evidence.ExitFloor();
        return (new("fixture", null, [], evidence.Capture()), run);
    }

    private static ulong StateWord(LabelRandomState state) => state.State0 ^ state.State1 ^ state.State2 ^ state.State3;

    private sealed class Completion(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class FixtureSource(NativePublicRunEvidence evidence) : IRunDecisionSource
    {
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new NotSupportedException();
        public Task<CombatDecision> ChooseCombatActionAsync(CombatState state) => throw new NotSupportedException();
        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            Assert.IsType<RoomFullOfCheese>(request.Source);
            var choice = new PublicChoice(request.Source!.GetType().Name, request.MinCount, request.MaxCount,
                request.Cancelable, request.Candidates.Select(PublicViews.Card).ToArray());
            evidence.OutsideCards(choice, [0, 1]);
            return Task.FromResult<IReadOnlyList<CardModel>>(request.Candidates.Take(2).ToArray());
        }
    }
}
