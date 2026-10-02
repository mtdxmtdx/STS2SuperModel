using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Nosl.Tests;

public sealed class M012Tests
{
    private static PublicAction Play(CombatSession s,string card) => s.Observe().Actions.First(a=>a.Kind=="play" && s.Observe().Observation!.Hand[a.Slot].Id==card);
    private static async Task<TerminalFacts> Finish(CombatSession s,int cap=200)
    {
        var p=s.Observe();
        for(int i=0;i<cap && p.Status is "player_decision" or "card_choice";i++) p=await s.StepAsync(PublicDiagnosticPolicy.Choose(p));
        Assert.Equal("terminal_settled",p.Status); return await s.SettleAsync();
    }
    [Fact]
    public async Task L01_ActualSilentA10_LegalTargetsAndStaleTokens()
    {
        await using var s=await CombatSession.CreateAsync(); var p=s.Observe();
        Assert.Equal(10,p.Observation!.Ascension); Assert.Equal(7,p.Observation.Hand.Length);
        Assert.Contains(p.Observation.Hand.Concat(p.Observation.UnknownDraw.Select(x=>x.Card)),c=>c.Id=="AscendersBane");
        Assert.All(p.Actions.Where(a=>a.Kind=="play"),a=>Assert.DoesNotContain("Unplayable",p.Observation.Hand[a.Slot].Keywords));
        var attack=p.Actions.First(a=>a.Kind=="play" && a.Target==0);
        await Assert.ThrowsAsync<ArgumentException>(()=>s.StepAsync(attack with {Target=-1}));
        await s.StepAsync(attack);
        await Assert.ThrowsAsync<ArgumentException>(()=>s.StepAsync(attack));
    }
    [Fact]
    public async Task L01_EnginePlayabilityIsUsedRatherThanCostOnly()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["StrikeSilent","DefendSilent","AscendersBane"]));
        s.State.Players[0].PlayerCombatState!.Energy=0; var p=s.Observe();
        Assert.Single(p.Actions); Assert.Equal("end_turn",p.Actions[0].Kind);
    }
    [Fact]
    public async Task L02_L03_BranchOwnsRoomAndAllMutableObjects_OriginalUntouched()
    {
        await using var source=await CombatSession.CreateAsync(new(Potions:["FirePotion"],Relics:["MeatOnTheBone"],Hp:30,EnemyHp:10));
        string before=PublicJson.Serialize(source.Observe());
        var rngBefore=JsonSerializer.Serialize(source.State.RunState.Rng.ToSerializable());
        await using var branch=source.ForkExact();
        Assert.NotSame(source.Room,branch.Room); Assert.NotSame(source.State.Players[0],branch.State.Players[0]);
        Assert.Same(branch.Room,branch.State.RunState.CurrentRoom); Assert.Same(branch.Room,branch.State.RunState.BaseRoom);
        Assert.NotSame(source.State.Enemies[0],branch.State.Enemies[0]);
        Assert.All(branch.State.Players[0].PlayerCombatState!.Hand.Cards,c=>Assert.Same(branch.State.Players[0],c.Owner));
        await branch.StepAsync(branch.Observe().Actions.Single(a=>a.Kind=="potion"));
        var facts=await branch.SettleAsync(); Assert.Equal("win",facts.Result); Assert.Equal(42,facts.FinalHp);
        Assert.Equal(0,facts.RewardSelectionsMade); Assert.Single(branch.Room.GeneratedRewards);
        Assert.Empty(source.Room.GeneratedRewards); Assert.False(source.Room.Won);
        Assert.Equal(before,PublicJson.Serialize(source.Observe()));
        Assert.Equal(rngBefore,JsonSerializer.Serialize(source.State.RunState.Rng.ToSerializable()));
        Assert.Same(facts,await branch.SettleAsync()); Assert.Equal(42,branch.State.Players[0].Creature.CurrentHp);
    }
    [Fact]
    public async Task L04_RealDiscardChoice_IndependentReplay_NotCoroutineClone()
    {
        await using var source=await CombatSession.CreateAsync(new(Deck:["Survivor","StrikeSilent","DefendSilent","Neutralize"]));
        var packet=await source.StepAsync(Play(source,"Survivor"));
        Assert.Equal("card_choice",packet.Status); Assert.Equal(8,packet.Observation!.Block);
        Assert.Equal(1,packet.Observation.Choice!.Min); Assert.Throws<InvalidOperationException>(()=>source.ForkExact());
        await using var replay=await source.ReplayToChoiceAsync();
        Assert.Equal(PublicJson.Serialize(source.Observe()),PublicJson.Serialize(replay.Observe()));
        await replay.StepAsync(replay.Observe().Actions[0]);
        Assert.False(replay.HasPendingChoice); Assert.True(source.HasPendingChoice);
        Assert.Contains(replay.Observe().Observation!.Discard,c=>c.Id==packet.Observation.Choice.Candidates[0].Id);
        await source.StepAsync(source.Observe().Actions.Last()); Assert.False(source.HasPendingChoice);
    }
    [Theory]
    [InlineData(false,50,100)]
    [InlineData(true,51,101)]
    public async Task L05_L07_ActualVictoryHooksAndCleanupMatchOriginalRoom(bool cheese,int expectedHp,int expectedMax)
    {
        var relics=cheese?new[]{"MeatOnTheBone","ChosenCheese"}:new[]{"MeatOnTheBone"};
        await using var source=await CombatSession.CreateAsync(new(Deck:["StrikeSilent"],Potions:["FirePotion"],Relics:relics,Hp:38,MaxHp:100,EnemyHp:10));
        // Bone only: 38+12=50. Cheese first: 38+1+12=51.
        await using var branch=source.ForkExact();
        await branch.StepAsync(branch.Observe().Actions.Single(a=>a.Kind=="potion"));
        await source.StepAsync(source.Observe().Actions.Single(a=>a.Kind=="potion"));
        var a=await source.SettleAsync(); var b=await branch.SettleAsync();
        Assert.Equal(expectedHp,a.FinalHp); Assert.Equal(expectedMax,a.FinalMaxHp);
        Assert.Equal(PublicJson.Serialize(a),PublicJson.Serialize(b));
        Assert.Empty(branch.State.Players[0].Creature.Powers); Assert.Equal(0,branch.State.Players[0].Creature.Block);
        Assert.Equal("AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",b.Boundary);
        Assert.Contains(b.Events,e=>e.Kind=="pre_settlement");
        await branch.Room.ResolveOutcomeAsync(); Assert.Equal(expectedHp,branch.State.Players[0].Creature.CurrentHp);
    }
    [Fact]
    public async Task L06_LossIsNotHealedOrSilentlyChangedToWin()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["DefendSilent"],Hp:1,Relics:["MeatOnTheBone"],EnemyHp:100));
        await s.StepAsync(s.Observe().Actions.Single(a=>a.Kind=="end_turn"));
        var f=await s.SettleAsync(); Assert.Equal("loss",f.Result); Assert.Equal(0,f.FinalHp); Assert.Equal(0,f.RewardSelectionsMade);
    }
    [Fact]
    public async Task L01_EnemyPhaseAutomaticallyAdvancesToNextRealDecision()
    {
        await using var s=await CombatSession.CreateAsync(new(Enemy:"TwigSlimeS",EnemyHp:100));
        int hp=s.Observe().Observation!.Hp;
        var p=await s.StepAsync(s.Observe().Actions.Single(a=>a.Kind=="end_turn"));
        Assert.Equal("player_decision",p.Status); Assert.Equal(2,p.Observation!.Turn); Assert.Equal(hp-5,p.Observation.Hp);
        Assert.Equal(5,p.Observation.Hand.Length);
    }
    [Fact]
    public async Task L01_FruitJuiceChangesRealHpAndMaxHp_NoEnergyCost()
    {
        await using var s=await CombatSession.CreateAsync(new(Potions:["FruitJuice"]));
        var p=await s.StepAsync(s.Observe().Actions.Single(a=>a.Kind=="potion"));
        Assert.Equal(75,p.Observation!.Hp); Assert.Equal(75,p.Observation.MaxHp); Assert.Equal(3,p.Observation.Energy);
        Assert.All(p.Observation.Potions,x=>Assert.Null(x));
    }
    [Fact]
    public async Task N01_N02_N09_N10_HiddenReplacementDoesNotChangePublicInputsCandidatesOrRootDiagnostics()
    {
        await using var a=await CombatSession.CreateAsync(new(Enemy:"Nibbit",EnemyHp:35));
        await using var b=a.ForkExact();
        var pile=b.State.Players[0].PlayerCombatState!.DrawPile;
        var reverse=pile.Cards.Reverse().ToArray(); foreach(var c in pile.Cards.ToArray()) pile.RemoveInternal(c); foreach(var c in reverse) pile.AddInternal(c);
        b.ReseedFuture(999999); // Replace root seed and all private future streams, not just their counters.
        Assert.Equal(PublicJson.Serialize(a.Observe()),PublicJson.Serialize(b.Observe()));
        await using var wa=BeliefSampler.SampleWorld(a,17); await using var wb=BeliefSampler.SampleWorld(b,17);
        Assert.Equal(wa.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c=>c.GetType().Name),wb.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c=>c.GetType().Name));
        Assert.Equal(JsonSerializer.Serialize(wa.State.RunState.Rng.ToSerializable()),JsonSerializer.Serialize(wb.State.RunState.Rng.ToSerializable()));
        var ra=await BranchDiagnostics.EvaluateAsync(a,[2,7],100); var rb=await BranchDiagnostics.EvaluateAsync(b,[2,7],100);
        Assert.Equal(PublicJson.Serialize(ra),PublicJson.Serialize(rb));
        Assert.All(ra,r=>{ Assert.Equal(2,r.Completed+r.Truncated+r.Errors); Assert.Equal(2,r.Completed); Assert.Equal(0,r.Errors); });
        string json=PublicJson.Serialize(a.Observe());
        foreach(string forbidden in new[]{"seed","combatId","moveState","privateHash","teacher","rng"}) Assert.DoesNotContain(forbidden,json,StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task N03_N04_N08_N11_InformationFusionAndPublicHistory()
    {
        await using var source=await CombatSession.CreateAsync();
        await using var a=BeliefSampler.SampleWorld(source,1); await using var b=BeliefSampler.SampleWorld(source,4);
        Assert.Equal(PublicJson.Serialize(a.Observe()),PublicJson.Serialize(b.Observe()));
        Assert.Equal(PublicJson.Serialize(PublicDiagnosticPolicy.Choose(a.Observe())),PublicJson.Serialize(PublicDiagnosticPolicy.Choose(b.Observe())));
        // Different hidden worlds may diverge only after actual draw results become public.
        await a.StepAsync(a.Observe().Actions.Single(x=>x.Kind=="end_turn")); await b.StepAsync(b.Observe().Actions.Single(x=>x.Kind=="end_turn"));
        Assert.NotEqual(PublicJson.Serialize(a.Observe()),PublicJson.Serialize(b.Observe()));
        var observation=source.Observe().Observation!;
        var differentHistory=observation with {History=observation.History.Append(new PublicEvent("draw","previous-public-card")).ToArray()};
        Assert.NotEqual(PublicJson.Serialize(observation),PublicJson.Serialize(differentHistory));
    }
    [Fact]
    public async Task N05_N06_N07_RealKnownTopMove_DrawAndShuffleUpdate_NoIntentResampling()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["ThinkingAhead","StrikeSilent","DefendSilent","Neutralize","Survivor","Acrobatics","Prepared","Backflip","StrikeSilent","DefendSilent"],Enemy:"LeafSlimeS"));
        // Deterministic source seed may leave ThinkingAhead in draw: put a public-known deck into this constructed test hand.
        var p=s.State.Players[0]; var card=p.PlayerCombatState!.AllPiles.SelectMany(x=>x.Cards).Single(c=>c.GetType().Name=="ThinkingAhead");
        CardPileCmd.Add(card,PileType.Hand);
        var packet=await s.StepAsync(Play(s,"ThinkingAhead"));
        Assert.Equal("card_choice",packet.Status); var selected=packet.Observation!.Choice!.Candidates[0];
        await s.StepAsync(packet.Actions[0]); Assert.Equal(PublicJson.Serialize(selected),PublicJson.Serialize(s.Observe().Observation!.KnownDraw.Single().Card));
        string intent=PublicJson.Serialize(s.Observe().Observation!.Enemies[0].Intents);
        await using var sampled=BeliefSampler.SampleWorld(s,77);
        Assert.Equal(PublicJson.Serialize(selected),PublicJson.Serialize(PublicViews.Card(sampled.State.Players[0].PlayerCombatState!.DrawPile.Cards[0])));
        Assert.Equal(intent,PublicJson.Serialize(sampled.Observe().Observation!.Enemies[0].Intents));
        await CardPileCmd.Draw(s.State,1,p,false); Assert.Empty(s.Observe().Observation!.KnownDraw);
        var visible=p.PlayerCombatState.Hand.Cards[0]; CardPileCmd.Add(visible,PileType.Draw,CardPilePosition.Top);
        Assert.NotEmpty(s.Observe().Observation!.KnownDraw);
        await CardPileCmd.Shuffle(s.State,p); Assert.Empty(s.Observe().Observation!.KnownDraw);
    }
    [Fact]
    public async Task N12_ExactMultisetPosteriorAndSampledFrequencyAgree()
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:["StrikeSilent","DefendSilent","Neutralize"]));
        // Construct an explicitly public hand-to-draw transformation, without peeking at a hidden top.
        var p=s.State.Players[0]; foreach(var card in p.PlayerCombatState!.Hand.Cards.ToArray()) CardPileCmd.Add(card,PileType.Draw,CardPilePosition.Random);
        var o=s.Observe().Observation!; var exact=BeliefSampler.EnumerateDrawPosterior(o);
        Assert.Equal(6,exact.Count); Assert.Equal(1,exact.Sum(x=>x.Probability),12); Assert.All(exact,x=>Assert.Equal(1.0/6,x.Probability,12));
        int strike=0; const int n=1200;
        for(ulong seed=0;seed<n;seed++)
        { await using var w=BeliefSampler.SampleWorld(s,seed); if(w.State.Players[0].PlayerCombatState!.DrawPile.Cards[0].GetType().Name=="StrikeSilent") strike++; }
        Assert.InRange((double)strike/n,1.0/3-.05,1.0/3+.05);
    }
    [Fact]
    public async Task M2_StandalonePolicyProcessReceivesOnlyWhitelistedJson()
    {
        await using var s=await CombatSession.CreateAsync(); var packet=s.Observe();
        string path=Assembly.Load("Nosl.PublicPolicy").Location;
        var start=new ProcessStartInfo("dotnet",$"\"{path}\"") {RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        using var process=Process.Start(start)!;
        await process.StandardInput.WriteLineAsync(PublicJson.Serialize(packet)); process.StandardInput.Close();
        string json=await process.StandardOutput.ReadLineAsync() ?? ""; await process.WaitForExitAsync();
        Assert.Equal(0,process.ExitCode); Assert.Equal(PublicJson.Serialize(PublicDiagnosticPolicy.Choose(packet)),json);
        var dependencies=Assembly.Load("Nosl.PublicPolicy").GetReferencedAssemblies().Select(x=>x.Name!);
        Assert.DoesNotContain("Sts2Sim.Core",dependencies); Assert.DoesNotContain("Nosl.Worker",dependencies);
        var dtoDependencies=typeof(PublicObservation).Assembly.GetReferencedAssemblies().Select(x=>x.Name!);
        Assert.DoesNotContain("Sts2Sim.Core",dtoDependencies);
    }
    [Theory]
    [InlineData("TwigSlimeS")]
    [InlineData("LeafSlimeS")]
    [InlineData("Nibbit")]
    public async Task M1_M2_PurePublicPolicyCompletesActualCombatWithoutLiveState(string enemy)
    {
        await using var s=await CombatSession.CreateAsync(new(Enemy:enemy));
        var f=await Finish(s); Assert.Contains(f.Result,new[]{"win","loss"}); Assert.Equal(0,f.RewardSelectionsMade);
    }
    [Fact]
    public async Task M0_UnsupportedContentIsExplicit_NotSilentlyRemoved()
    {
        await Assert.ThrowsAsync<NotSupportedException>(()=>CombatSession.CreateAsync(new(Enemy:"NotRegisteredEnemy")));
        await Assert.ThrowsAsync<NotSupportedException>(()=>CombatSession.CreateAsync(new(Deck:["NotRegisteredCard"])));
        await Assert.ThrowsAsync<NotSupportedException>(()=>CombatSession.CreateAsync(new(Relics:["NotRegisteredRelic"])));
    }
    [Theory]
    [InlineData("TwigSlimeS")]
    [InlineData("LeafSlimeS")]
    [InlineData("Nibbit")]
    public async Task L02_N07_A10ClonePreservesPublishedIntentAndSubsequentEngineTrace(string enemy)
    {
        await using var source=await CombatSession.CreateAsync(new(Enemy:enemy,EnemyHp:100));
        await using var branch=source.ForkExact();
        Assert.Equal(PublicJson.Serialize(source.Observe()),PublicJson.Serialize(branch.Observe()));
        for(int turn=0;turn<3;turn++)
        {
            await source.StepAsync(source.Observe().Actions.Single(a=>a.Kind=="end_turn"));
            await branch.StepAsync(branch.Observe().Actions.Single(a=>a.Kind=="end_turn"));
            Assert.Equal(PublicJson.Serialize(source.Observe()),PublicJson.Serialize(branch.Observe()));
        }
    }
    [Theory]
    [InlineData("Acrobatics")]
    [InlineData("Acrobatics+")]
    [InlineData("Prepared")]
    [InlineData("Prepared+")]
    [InlineData("ThinkingAhead+")]
    [InlineData("Survivor+")]
    public async Task L04_DrawDiscardAndTopChoicesUsePublicCandidateOrder(string card)
    {
        await using var s=await CombatSession.CreateAsync(new(Deck:[card,"StrikeSilent","DefendSilent","Neutralize","Backflip","StrikeSilent","DefendSilent","DeadlyPoison","StrikeSilent","DefendSilent"],EnemyHp:100));
        var target=s.State.Players[0].PlayerCombatState!.AllPiles.SelectMany(p=>p.Cards).Single(c=>c.GetType().Name==card.TrimEnd('+'));
        CardPileCmd.Add(target,PileType.Hand);
        var packet=await s.StepAsync(Play(s,card.TrimEnd('+')));
        Assert.Equal("card_choice",packet.Status); Assert.NotEmpty(packet.Actions);
        Assert.All(packet.Actions,a=>{ Assert.NotNull(a.Selection); Assert.InRange(a.Selection!.Length,packet.Observation!.Choice!.Min,packet.Observation.Choice.Max); });
        var chosen=packet.Actions[0]; int count=chosen.Selection!.Length;
        var next=await s.StepAsync(chosen); Assert.Equal("player_decision",next.Status);
        Assert.False(s.HasPendingChoice);
        Assert.True(card.StartsWith("ThinkingAhead") ? next.Observation!.KnownDraw.Length==1 : next.Observation!.Discard.Length>=count);
    }
    [Theory]
    [InlineData("BlockPotion")]
    [InlineData("EnergyPotion")]
    [InlineData("SwiftPotion")]
    public async Task L01_PotionAdapterMatchesExactEngineBranch(string potion)
    {
        await using var source=await CombatSession.CreateAsync(new(Potions:[potion],EnemyHp:100));
        await using var branch=source.ForkExact();
        await source.StepAsync(source.Observe().Actions.Single(a=>a.Kind=="potion"));
        await branch.StepAsync(branch.Observe().Actions.Single(a=>a.Kind=="potion"));
        Assert.Equal(PublicJson.Serialize(source.Observe()),PublicJson.Serialize(branch.Observe()));
        Assert.All(source.Observe().Observation!.Potions,x=>Assert.Null(x));
    }
    [Fact]
    public async Task M2_TruncatedDiagnosticsConserveWorldsAndDoNotInventLoss()
    {
        await using var s=await CombatSession.CreateAsync(new(EnemyHp:1000));
        var rows=await BranchDiagnostics.EvaluateAsync(s,[1,2],1);
        Assert.All(rows,r=>{Assert.Equal(2,r.Truncated);Assert.Equal(0,r.Completed);Assert.Equal(0,r.Errors);Assert.All(r.Outcomes,x=>Assert.Null(x.FinalHp));});
    }
}
