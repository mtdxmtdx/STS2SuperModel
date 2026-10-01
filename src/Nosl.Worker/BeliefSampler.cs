using Nosl.Contracts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

public static class BeliefSampler
{
    // Public constraints define an exchangeable posterior for this finite supported scope.
    // Canonicalize BEFORE shuffling: shuffling the private source order with a fixed RNG leaks its permutation.
    public static CombatSession SampleWorld(CombatSession source, ulong samplerSeed)
    {
        var packet=source.Observe();
        if(packet.Status!="player_decision") throw new InvalidOperationException("Sample worlds only at stable player decisions");
        var world=source.ForkExact();
        try
        {
            world.ReseedFuture(samplerSeed);
            var pile=world.State.Players[0].PlayerCombatState!.DrawPile;
            var bySignature=pile.Cards.GroupBy(c=>PublicJson.Serialize(PublicViews.Card(c)))
                .OrderBy(g=>g.Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>new Queue<CardModel>(g));
            var slots=new CardModel?[pile.Cards.Count];
            foreach(var k in packet.Observation!.KnownDraw)
            {
                if(k.Position<0 || k.Position>=slots.Length || slots[k.Position] is not null) throw new InvalidOperationException("Invalid public position constraint");
                slots[k.Position]=bySignature[PublicJson.Serialize(k.Card)].Dequeue();
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
