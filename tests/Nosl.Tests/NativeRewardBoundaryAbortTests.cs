using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeRewardBoundaryAbortTests
{
    [Fact]
    public async Task NativeGenerationAbortsBothDependentScopesAndKeepsOriginalResourceMismatch()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.RewardsVersion,
            Execution = new(PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
        }.Freeze();
        var observedRecipe = new NativeTapeRecipe(73, 91, 117, 0, 0);
        PublicRunEvidence evidence;
        using (NativeLabelTape.ForDeclaredPrior(prior, observedRecipe).EnterScope())
        {
            var run = new RunState(observedRecipe.IndependentRunSeed, ascensionLevel: 10);
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
            player.Odds.PotionReward.OverrideCurrentValue(0f); // Constructed native no-potion branch.
            var rewards = RewardsSet.GenerateFor(player, RoomType.Monster, run);
            Assert.Null(rewards.Potion);
            var producer = new NativePublicRunEvidence(run); producer.BeginRun(true);
            producer.Rewards(rewards, RewardDecisionClassifier.ChooseDefault(rewards));
            var offers = producer.Capture().Events.Select(e => e.Payload).OfType<PublicOffersObserved>().Single();
            var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, NativePublicRunEvidence.Assets(player)));
            long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, run.TotalFloor);
            recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started));
            recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
            long reward = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, run.TotalFloor);
            recorder.Record(reward, offers);
            evidence = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(recorder.Capture()));
        }
        var cards = NativePublicRewardCondition.Create(evidence);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Null(resources.Targets[0].Potion);
        var hypothetical = new NativeTapeRecipe(741, 913, 1177, 0, 0);
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical,
            publicRewardCondition: cards, publicResourceCondition: resources);
        using (tape.EnterScope())
        {
            var run = new RunState(hypothetical.IndependentRunSeed, ascensionLevel: 10);
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
            await RelicCmd.Obtain((RelicModel)ModelDb.Relic<WhiteBeastStatue>().MutableClone(), player);
            tape.AttachHypotheticalRun(run);
            var a = NativePublicRunEvidence.Assets(player);
            tape.CombatEntering(0, new("nosl.native-entry-assets.v1", a.Hp, a.MaxHp, a.Gold,
                a.Deck.ToArray(), a.Relics.ToArray(), a.Potions.ToArray(), a.MaxEnergy, a.PotionSlots,
                a.OrbSlots, a.CardRemovalsUsed), run.Rng.Shuffle);
            int before = player.PlayerRng.Rewards.Counter;
            var error = Assert.Throws<NativePublicConstraintMismatchException>(() =>
                RewardsSet.GenerateFor(player, RoomType.Monster, run));
            Assert.Contains("potion presence", error.Message);
            Assert.DoesNotContain("card", error.Message);
            Assert.Equal(before, player.PlayerRng.Rewards.Counter);
            Assert.Equal(0, tape.ConditionedPublicRewardCards);
            Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        }
    }
}
