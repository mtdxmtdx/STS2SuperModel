using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;

namespace Nosl.Tests;

public sealed class BridgeSafetyTests
{
    private static string Json(CombatSession s)=>PublicJson.Serialize(s.Observe());
    private static PublicAction Play(CombatSession s,string id)=>s.Observe().Actions.First(a=>a.Kind=="play"&&s.Observe().Observation!.Hand[a.Slot].Id==id);
    [Fact]
    public async Task KnownTopTracksPublicUpgradeAndCostMutationAcrossClone()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["ThinkingAhead","Apotheosis","StrikeSilent","DefendSilent"],EnemyHp:200));
        await s.StepAsync(Play(s,"ThinkingAhead"));
        var choice=s.Observe(); int index=Array.FindIndex(choice.Observation!.Choice!.Candidates,c=>c.Id=="StrikeSilent");
        await s.StepAsync(choice.Actions.Single(a=>a.Selection!.SequenceEqual(new[]{index})));
        await s.StepAsync(Play(s,"Apotheosis"));
        Assert.Equal(1,s.Observe().Observation!.KnownDraw.Single().Card.Upgrade);
        s.State.Players[0].PlayerCombatState!.DrawPile.Cards[0].MakeTemporaryFreeThisTurn();
        Assert.Equal(0,s.Observe().Observation!.KnownDraw.Single().Card.Cost);
        await using var branch=s.ForkExact(); Assert.Equal(Json(s),Json(branch));
        Assert.NotSame(s.Knowledge.Known[0],branch.Knowledge.Known[0]);
    }
    [Theory]
    [InlineData(31)] [InlineData(32)]
    public async Task LargeDrawChoiceEnumeratesEverySingleSelectionWithoutBitOverflow(int remaining)
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:Enumerable.Repeat("StrikeSilent",remaining+7).ToArray(),Potions:["DropletOfPrecognition"],EnemyHp:200));
        await s.StepAsync(s.Observe().Actions.Single(a=>a.Kind=="potion"));
        var p=s.Observe(); Assert.Equal("card_choice",p.Status);
        Assert.Equal(remaining,p.Observation!.Choice!.Candidates.Length);
        Assert.Equal(remaining,p.Actions.Length); Assert.All(p.Actions,a=>Assert.Single(a.Selection!));
        await s.StepAsync(p.Actions.Last());
        Assert.False(s.HasPendingChoice);
    }
    [Fact]
    public async Task StartupChoiceReturnsBeforeTheAsynchronousOperationCompletes()
    {
        await using var s=await CombatSession.CreateAsync(new(Relics:["GamblingChip"])).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.HasPendingChoice); Assert.Equal("card_choice",s.Observe().Status);
        await using var replay=await s.ReplayToChoiceAsync(); Assert.Equal(Json(s),Json(replay));
        await s.StepAsync(s.Observe().Actions.First(a=>a.Selection!.Length==0));
        Assert.Equal("player_decision",s.Observe().Status);
    }
    [Fact]
    public async Task ChoicePosteriorReplaysOwnedSampledOriginsAndUnreviewedInPlaceStillFailsClosed()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["Survivor","StrikeSilent","StrikeSilent"],EnemyHp:200));
        await s.StepAsync(Play(s,"Survivor"));
        await using var world=await BeliefSampler.SampleWorldAsync(s,33);
        Assert.Equal(Json(s),Json(world)); Assert.True(world.HasPendingChoice);
        await using var live=await CombatSession.CreateAsync(new(Deck:["Survivor","StrikeSilent","StrikeSilent"],EnemyHp:200));
        await using var sampled=BeliefSampler.SampleWorld(live,1);
        await sampled.StepAsync(Play(sampled,"Survivor"));
        Assert.True(BeliefSampler.UsesConditionalChoicePosterior(sampled));
        await using var exactChoice=await sampled.ReplayToChoiceAsync();
        Assert.Equal(Json(sampled),Json(exactChoice));
        // Even a reviewed choice cannot fall back to original-Scenario replay.
        await Assert.ThrowsAsync<NotSupportedException>(()=>BeliefSampler.SampleByReplayAsync(sampled,2));
        await using var broad=await CombatSession.CreateAsync(new(Deck:["Survivor","Adrenaline","StrikeSilent"],EnemyHp:200));
        await using var broadSampled=broad.ForkExact(); broadSampled.ReseedFuture(1);
        await broadSampled.StepAsync(Play(broadSampled,"Survivor"));
        Assert.False(BeliefSampler.UsesConditionalChoicePosterior(broadSampled));
        Assert.Equal(BeliefSampler.UnsupportedProfile,BeliefSampler.PosteriorProfileFor(broadSampled));
        await Assert.ThrowsAsync<NotSupportedException>(()=>broadSampled.ReplayToChoiceAsync());
        await Assert.ThrowsAsync<NotSupportedException>(()=>BeliefSampler.SampleWorldAsync(broadSampled,2));
    }
    [Fact]
    public async Task BroaderInitialPriorDoesNotBecomeFastAfterPotionDisappears()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["StrikeSilent"],Potions:["AttackPotion"],EnemyHp:200));
        Assert.False(BeliefSampler.UsesExchangeablePosterior(s));
        await s.StepAsync(s.Observe().Actions.Single(a=>a.Kind=="discard_potion"));
        Assert.False(BeliefSampler.UsesExchangeablePosterior(s));
    }
    [Fact]
    public async Task HiddenGeneratedIdentitiesAreWithheldUntilAnActualReveal()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["Metamorphosis","StrikeSilent"],EnemyHp:200));
        await s.StepAsync(Play(s,"Metamorphosis"));
        var before=s.Observe(); Assert.Equal(3,before.Observation!.UnidentifiedDrawCount);
        Assert.Empty(before.Observation.UnknownDraw);
        await using var branch=s.ForkExact(); Assert.Equal(Json(s),Json(branch));
        await CardPileCmd.Draw(s.State,1,s.State.Players[0],false);
        Assert.Equal(2,s.Observe().Observation!.UnidentifiedDrawCount);
        Assert.NotEqual(Json(s),Json(branch));
    }
    [Fact]
    public async Task AutomaticHiddenPileSelectionNeverSerializesPrivateOrder()
    {
        await using var a=await CombatSession.CreateAsync(new(Deck:["Charge","StrikeSilent","DefendSilent"],EnemyHp:200));
        var player=a.State.Players[0];
        foreach(var card in player.PlayerCombatState!.Hand.Cards.Where(c=>c.GetType().Name!="Charge").ToArray())
            CardPileCmd.Add(card,PileType.Draw,CardPilePosition.Random);
        await using var b=a.ForkExact();
        var pile=b.State.Players[0].PlayerCombatState!.DrawPile;
        var reversed=pile.Cards.Reverse().ToArray(); foreach(var card in reversed) pile.RemoveInternal(card); foreach(var card in reversed) pile.AddInternal(card);
        Assert.Equal(Json(a),Json(b));
        await a.StepAsync(Play(a,"Charge")); await b.StepAsync(Play(b,"Charge"));
        Assert.Equal(Json(a),Json(b));
    }
    [Fact]
    public async Task OrderedMultiSelectionPreservesBothSlyOrders()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["Prepared+","Reflex","Abrasive","StrikeSilent","DefendSilent"],EnemyHp:200));
        await s.StepAsync(Play(s,"Prepared"));
        var packet=s.Observe(); var cards=packet.Observation!.Choice!.Candidates;
        int reflex=Array.FindIndex(cards,c=>c.Id=="Reflex"), abrasive=Array.FindIndex(cards,c=>c.Id=="Abrasive");
        Assert.Contains(packet.Actions,a=>a.Selection!.SequenceEqual(new[]{reflex,abrasive}));
        Assert.Contains(packet.Actions,a=>a.Selection!.SequenceEqual(new[]{abrasive,reflex}));
    }
    [Fact]
    public async Task RejectionExhaustionIsAnExplicitComputationalOutcome()
    {
        await using var s=await CombatSession.CreateAsync(new(Potions:["AttackPotion"]));
        var error=await Assert.ThrowsAsync<PosteriorSamplingException>(()=>BeliefSampler.SampleByReplayAsync(s,1,1));
        Assert.Equal(1,error.Attempts); Assert.Contains("inconclusive",error.Message);
        Assert.Equal("player_decision",s.Observe().Status);
    }
}
