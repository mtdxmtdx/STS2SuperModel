using Nosl.Contracts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

public static class BeliefSampler
{
    public const string ImplementationVersion = "nosl-belief-dispatch-v7";
    public const string NativeConditionalChoiceProfile = "native-public-entry-reviewed-memory-conditional-choice-v1";
    public const string ExchangeableProfile = "reviewed-stable-exchangeable-v1";
    public const string ConditionalChoiceProfile = "reviewed-stable-origin-conditional-choice-v1";
    public const string SlyExchangeableProfile = "reviewed-constructed-reflex-tactician-exchangeable-v1";
    public const string SlyConditionalChoiceProfile = "reviewed-constructed-reflex-tactician-conditional-choice-v1";
    public const string WholeSetupReplayProfile = "whole-setup-rejection-v1";
    public const string UnsupportedProfile = "unsupported-posterior-provenance-v1";

    // Audit-only implementation identity. No profile/seed/provenance enters policy DTOs.
    public static string PosteriorProfileFor(CombatSession source)
    {
        try
        {
            if(UsesExchangeablePosterior(source)) return source.HasNativeProvenance ? source.NativeCertificate!.StableProfile : HasSlyInitialPrior(source) ? SlyExchangeableProfile : ExchangeableProfile;
            if(UsesConditionalChoicePosterior(source)) return source.HasNativeProvenance ? source.NativeCertificate!.ChoiceProfile : HasSlyInitialPrior(source) ? SlyConditionalChoiceProfile : ConditionalChoiceProfile;
        }
        catch(NotSupportedException) { return UnsupportedProfile; }
        return source.HasNativeProvenance || source.HasInPlaceSampledProvenance ? UnsupportedProfile : WholeSetupReplayProfile;
    }
    // Family identity comes from the declared prior, even after Sly cards leave combat.
    private static bool HasSlyInitialPrior(CombatSession source) =>
        (source.InitialScenario.Deck ?? []).Any(c => c.TrimEnd('+') is "Reflex" or "Tactician");

    // Public constraints define an exchangeable posterior for this finite supported scope.
    // Canonicalize BEFORE shuffling: shuffling the private source order with a fixed RNG leaks its permutation.
    public static CombatSession SampleWorld(CombatSession source, ulong samplerSeed) =>
        SampleWorldAsync(source,samplerSeed).GetAwaiter().GetResult();

    // The fast exchangeable posterior is deliberately capability-bound. Catalog execution
    // remains unrestricted; complex materialized state uses independent native replay.
    public static bool UsesExchangeablePosterior(CombatSession source)
    {
        if(source.NativeCertificate is { } certificate) return certificate.AllowsCurrent(source);
        var p=source.State.Players.Single();
        string[] cards=["StrikeSilent","DefendSilent","Neutralize","Survivor","AscendersBane","Acrobatics","Backflip","Prepared","ThinkingAhead","DeadlyPoison","Slimed","CloakAndDagger","DaggerThrow","Dash","LegSweep","Blur","DodgeAndRoll","BladeDance","PoisonedStab","Slice","NoxiousFumes","DaggerSpray","Footwork","Shiv","Reflex","Tactician"];
        string[] relics=["RingOfTheSnake","MeatOnTheBone","ChosenCheese"];
        string[] potions=["FirePotion","BlockPotion","EnergyPotion","SwiftPotion","FruitJuice"];
        string[] enemies=["TwigSlimeS","LeafSlimeS","Nibbit","TwigSlimeM"];
        var setup=source.InitialScenario;
        if(setup.ForcedEvent is not null) return false;
        var declaredEnemies=setup.Enemies??[setup.Enemy];
        if(setup.Encounter is not null || declaredEnemies.Length!=1 || !enemies.Contains(declaredEnemies[0])) return false;
        // Eligibility is pinned to the declared source prior, not whatever remains after
        // broader potions/powers/cards have been consumed or left the piles.
        if((setup.Deck??[]).Any(c=>!cards.Contains(c.TrimEnd('+')))
            || (setup.Relics??[]).Any(r=>!relics.Contains(r))
            || (setup.Potions??[]).Any(p=>!potions.Contains(p))) return false;
        return source.Observe().Status=="player_decision" && source.Observe().Observation?.UnidentifiedDrawCount==0 && source.InitialScenario.Encounter is null && source.State.Enemies.Count==1
            && source.State.Enemies.All(e=>enemies.Contains(e.Monster!.GetType().Name))
            && p.Relics.All(r=>relics.Contains(r.GetType().Name))
            && p.PotionSlots.Where(x=>x is not null).All(x=>potions.Contains(x!.GetType().Name))
            && p.PlayerCombatState!.AllPiles.SelectMany(x=>x.Cards).All(c=>cards.Contains(c.GetType().Name)&&c.Enchantments.Count==0&&c.Affliction is null&&!c.TemporarySlyThisTurn);
    }
    public static Task<CombatSession> SampleWorldAsync(CombatSession source,ulong samplerSeed,int maxReplayAttempts=256)
    {
        if(UsesExchangeablePosterior(source)) return Task.FromResult(SampleExchangeable(source,samplerSeed));
        if(UsesConditionalChoicePosterior(source)) return SampleConditionalChoiceAsync(source,samplerSeed,maxReplayAttempts);
        return SampleByReplayAsync(source,samplerSeed,maxReplayAttempts);
    }
    public static bool UsesConditionalChoicePosterior(CombatSession source) =>
        source.TryGetConditionalChoiceOrigin(out _, out _, out _);

    private static async Task<CombatSession> SampleConditionalChoiceAsync(CombatSession source, ulong samplerSeed, int maxAttempts)
    {
        if(maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if(!source.TryGetConditionalChoiceOrigin(out var origin, out var actions, out var packets))
            throw new NotSupportedException("No reviewed conditional choice posterior");
        var proposalRng = new Rng(samplerSeed, "nosl-independent-choice-prior-v1");
        for(int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Resample every outcome-relevant hidden variable at the stable boundary,
            // before any observed draw/choice suffix. Never reseed at the pending choice.
            var proposed = SampleExchangeable(origin, proposalRng.NextUnsignedLong());
            try
            {
                if(await CombatSession.ReplayChoiceSuffixAsync(proposed, actions, packets)) return proposed;
            }
            catch { await proposed.DisposeAsync(); throw; }
            await proposed.DisposeAsync();
        }
        throw new PosteriorSamplingException(maxAttempts);
    }

    public static async Task<CombatSession> SampleByReplayAsync(CombatSession source,ulong samplerSeed,int maxAttempts=256)
    {
        if(source.HasNativeProvenance) throw new NotSupportedException("native_prior_mismatch: fresh Scenario replay is forbidden for native carry-in");
        if(maxAttempts<1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if(source.HasInPlaceSampledProvenance) throw new NotSupportedException("posterior_prior_mismatch: an in-place sampled world cannot be silently switched to the whole-setup replay prior");
        if(source.PublicTrace.Count!=source.ReplayActions.Count+1 || source.PublicTrace.Last()!=PublicJson.Serialize(source.Observe()))
            throw new NotSupportedException("public_history_incomplete: replay posterior requires every decision since declared setup");
        if(source.Observe().Status is not ("player_decision" or "card_choice")) throw new InvalidOperationException("No active public decision");
        var proposalRng=new Rng(samplerSeed,"nosl-independent-setup-prior-v1");
        // Full setup prior: the actual source seed is discarded before each proposal.
        // Current/earlier public observations alone determine acceptance, never branch outcomes.
        for(int attempt=0;attempt<maxAttempts;attempt++)
        {
            var setup=source.InitialScenario with {Seed=$"NOSL-REPLAY:{proposalRng.NextUnsignedLong():X16}"};
            CombatSession proposed;
            try { proposed=await CombatSession.CreateAsync(setup); }
            catch(ConstructedSetupRejectedException) { continue; }
            bool accepted=true;
            try
            {
                for(int step=0;step<=source.ReplayActions.Count;step++)
                {
                    if(PublicJson.Serialize(proposed.Observe())!=source.PublicTrace[step]) { accepted=false; break; }
                    if(step<source.ReplayActions.Count) await proposed.StepAsync(source.ReplayActions[step]);
                }
                if(accepted) return proposed; // Keep its RNG/deck/memory unchanged: it is the accepted world.
            }
            catch { await proposed.DisposeAsync(); throw; }
            await proposed.DisposeAsync();
        }
        throw new PosteriorSamplingException(maxAttempts);
    }
    private static CombatSession SampleExchangeable(CombatSession source, ulong samplerSeed)
    {
        var packet=source.Observe();
        if(packet.Status!="player_decision") throw new InvalidOperationException("Sample worlds only at stable player decisions");
        var world=source.ForkExact();
        try
        {
            world.NativeCertificate?.PrepareSample(world);
            world.ReseedFuture(samplerSeed);
            var pile=world.State.Players[0].PlayerCombatState!.DrawPile;
            var bySignature=pile.Cards.GroupBy(c=>PublicJson.Serialize(PublicViews.Card(c)))
                .OrderBy(g=>g.Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>new Queue<CardModel>(g));
            var slots=new CardModel?[pile.Cards.Count];
            foreach(var k in packet.Observation!.KnownDraw)
            {
                if(k.Position<0 || k.Position>=slots.Length || slots[k.Position] is not null) throw new InvalidOperationException("Invalid public position constraint");
                var knownCard=world.Knowledge.Known[k.Position];
                string signature=PublicJson.Serialize(k.Card);
                if(!bySignature[signature].Contains(knownCard)) throw new InvalidOperationException("Known physical card is absent from its public signature group");
                slots[k.Position]=knownCard;
                bySignature[signature]=new Queue<CardModel>(bySignature[signature].Where(c=>!ReferenceEquals(c,knownCard)));
            }
            var unknown=bySignature.OrderBy(x=>x.Key,StringComparer.Ordinal).SelectMany(x=>x.Value).ToList();
            new Rng(samplerSeed,"nosl-hidden-order").Shuffle(unknown);
            int next=0; for(int i=0;i<slots.Length;i++) slots[i]??=unknown[next++];
            foreach(var c in pile.Cards.ToArray()) pile.RemoveInternal(c);
            foreach(var c in slots) pile.AddInternal(c!);
            // Current published intents and the supported monsters' public-determined memory are unchanged.
            return world;
        }
        catch { world.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    // Exact draw-order posterior for small enumeratable cases. This is not enumeration of every future RNG draw.
    public static IReadOnlyList<(PublicCard[] Order,double Probability)> EnumerateDrawPosterior(PublicObservation o)
    {
        if(o.UnidentifiedDrawCount>0) throw new NotSupportedException("This finite multiset enumerator does not enumerate unknown generated identities; use native replay conditioning");
        if(o.DrawCount>8) throw new NotSupportedException("Exact posterior is capped at eight draw cards");
        var known=o.KnownDraw.ToDictionary(x=>x.Position,x=>x.Card);
        var counts=o.UnknownDraw.ToDictionary(x=>PublicJson.Serialize(x.Card),x=>(x.Card,x.Count));
        if(counts.Values.Sum(x=>x.Count)+known.Count!=o.DrawCount) throw new ArgumentException("Public counts do not conserve cards");
        var output=new List<(PublicCard[],double)>(); var order=new PublicCard[o.DrawCount];
        void Recurse(int pos,double probability)
        {
            if(pos==order.Length) { output.Add((order.ToArray(),probability)); return; }
            if(known.TryGetValue(pos,out var card)) { order[pos]=card; Recurse(pos+1,probability); return; }
            int total=counts.Values.Sum(x=>x.Count);
            foreach(var key in counts.Keys.Order(StringComparer.Ordinal).ToArray())
            {
                var entry=counts[key]; if(entry.Count==0) continue;
                order[pos]=entry.Card; counts[key]=(entry.Card,entry.Count-1);
                Recurse(pos+1,probability*entry.Count/total); counts[key]=entry;
            }
        }
        Recurse(0,1); return output;
    }
}

public sealed class PosteriorSamplingException(int attempts) : Exception($"posterior_budget_exhausted: no accepted world in {attempts} independent proposals; computationally inconclusive, not a game loss or proof of impossible content")
{
    public int Attempts { get; } = attempts;
}
