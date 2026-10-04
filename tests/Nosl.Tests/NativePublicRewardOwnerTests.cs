using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicRewardOwnerTests
{
    private static (RunState Run, Player Player) CreateRun(string seed)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState(seed, ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        return (run, player);
    }

    [Fact]
    public async Task NativeBattlewornPotionReturnDoesNotRequireAStandardCardGeneration()
    {
        // Constructed lifecycle coverage of the actual event/driver/recorder;
        // this is not a natural source-seed or posterior acceptance fixture.
        var (run, player) = CreateRun("public-reward-owner-battleworn");
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        for (int i = 0; i < 5; i++)
        {
            var card = (GrandFinale)ModelDb.Card<GrandFinale>().MutableClone();
            card.AssignOwner(player); player.Deck.AddInternal(card);
        }
        var bridge = new NaturalSourceCollector.SourceBridge(run, new(MaxRoots: 100,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
            PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId), [], "fixture", "unused", null, null, default,
            startsAtNativeRunBeginning: true);
        bridge.BeginRun(run);
        bridge.EnterFloor(run.Map.StartingMapPoint, RoomType.Event);
        var driver = new RunDriver(run, bridge, recorder: bridge, useAvailablePotions: false)
            { CombatObserverDecorator = bridge.Decorate, AutomaticCombatSettlementCompleted = bridge.CompleteOutcome };
        var room = new EventRoom(() => (EventModel)ModelDb.Event<BattlewornDummy>().MutableClone());
        run.PushRoom(room); await room.Enter(run);
        try { await driver.DriveEventAsync(room); }
        finally { bridge.DetachOutcome(); await bridge.ReleaseChoiceOriginAsync(); }

        var evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(bridge.CaptureEvidence()!));
        Assert.True(evidence.CompleteFromRunStart);
        var combat = Assert.Single(evidence.Events.Where(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat }));
        Assert.Contains(evidence.Events, e => e.OwnerOrdinal == combat.OwnerOrdinal
            && e.Payload is PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Victory });
        var reward = Assert.Single(evidence.Events.Where(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Reward }));
        Assert.Equal(((PublicOwnerStarted)combat.Payload).ParentOwnerOrdinal,
            ((PublicOwnerStarted)reward.Payload).ParentOwnerOrdinal);
        var offers = evidence.Events.Select(e => e.Payload).OfType<PublicOffersObserved>()
            .SelectMany(offer => offer.Groups).SelectMany(group => group.Offers).ToArray();
        Assert.Contains(offers, offer => offer.OfferKind == PublicOfferKind.Potion);
        Assert.DoesNotContain(offers, offer => offer.OfferKind == PublicOfferKind.Card);
        Assert.Empty(NativePublicRewardCondition.Create(evidence).Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstCustomSnapshotStaysUnconditionedAndLaterStandardCombatKeepsItsIndex(bool relic)
    {
        var (run, player) = CreateRun("public-reward-owner-refresh:" + relic);
        var custom = relic
            ? RewardsSet.CreateCustom(player, relic: new RelicReward((RelicModel)ModelDb.Relic<Anchor>().MutableClone(), player))
            : RewardsSet.CreateCustom(player, potion: new PotionReward((PotionModel)ModelDb.Potion<FirePotion>().MutableClone(), player));
        var standard = RewardsSet.GenerateFor(player, RoomType.Monster, run);
        PublicOffersObserved Project(RewardsSet rewards)
        {
            var producer = new NativePublicRunEvidence(run);
            producer.BeginRun(true);
            producer.Rewards(rewards, RewardDecisionClassifier.ChooseDefault(rewards));
            return producer.Capture().Events.Select(e => e.Payload).OfType<PublicOffersObserved>().Single();
        }
        var customOffer = Project(custom); var standardOffer = Project(standard);
        Assert.DoesNotContain(customOffer.Groups.SelectMany(group => group.Offers), offer => offer.OfferKind == PublicOfferKind.Card);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, NativePublicRunEvidence.Assets(player)));
        long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2);
        recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, 2);
        recorder.Record(owner, customOffer);
        // Deliberately challenge classification with a later card-shaped snapshot.
        // These are native projections, not a claim that this event changes shape.
        recorder.Record(owner, standardOffer);
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        long laterOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, 2);
        recorder.Record(laterOwner, standardOffer);
        recorder.Record(laterOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        long nextCombat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 3);
        recorder.Record(nextCombat, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(nextCombat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        long standardOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, 3);
        long firstOffer = recorder.Record(standardOwner, standardOffer);
        recorder.Record(standardOwner, customOffer); // A later refresh does not erase the standard target.
        var evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(recorder.Capture()));
        var condition = NativePublicRewardCondition.Create(evidence);
        var target = Assert.Single(condition.Targets);
        Assert.Equal(1, target.Key); Assert.Equal(1, target.Value.CombatIndex);
        Assert.Equal(firstOffer, target.Value.OfferEventOrdinal);
        Assert.Equal(standard.Card.Options.Take(3).Select(card => card.GetType().Name), target.Value.BaseCardIds);
        var proposal = new NativePublicRewardProposal(condition, _ => false, (_, _) => { }, new Rng(73).NextUnsignedLong);
        // A certified standard target still requires its actual native hook.
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }
}
