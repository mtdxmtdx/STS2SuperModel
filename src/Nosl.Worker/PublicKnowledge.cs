using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

// Only public events enter this ledger. It has no source seed, card identity or monster move state.
internal sealed class PublicKnowledge : ICombatObserver
{
    internal readonly List<PublicEvent> Events = [];
    internal readonly SortedDictionary<int, PublicCard> Known = [];
    internal PublicKnowledge Copy()
    {
        var k = new PublicKnowledge(); k.Events.AddRange(Events);
        foreach (var (p,c) in Known) k.Known[p]=c;
        return k;
    }
    public void CombatStarted(CombatState state) => Events.Add(new("combat_started", "Silent:A10"));
    public void PlayerTurnStarted(CombatState state)
    {
        Events.Add(new("player_turn", state.Players[0].PlayerCombatState!.TurnNumber.ToString()));
        foreach(var (enemy,slot) in state.Enemies.Select((e,i)=>(e,i)))
            Events.Add(new("intent_published",PublicJson.Serialize(new {slot,id=enemy.Monster!.GetType().Name,intents=PublicViews.Intents(enemy)})));
    }
    public void CardDrawn(CardModel card)
    {
        // The engine's Draw path uses pile internal methods, not CardPileCmd.Add.
        var remaining=Known.Where(x=>x.Key>0).Select(x=>(x.Key-1,x.Value)).ToArray();
        Known.Clear(); foreach(var (p,c) in remaining) Known[p]=c;
        Events.Add(new("draw", PublicJson.Serialize(PublicViews.Card(card))));
    }
    public void CardPlayStarted(CardModel card, Creature? target) { }
    public void CardPlayFinished(CardModel card, Creature? target, CardPlay? play) =>
        Events.Add(new("card_played",PublicJson.Serialize(new {card=PublicViews.Card(card),energySpent=play?.Resources.EnergySpent,starsSpent=play?.Resources.StarsSpent,resultPile=play?.ResultPile.ToString()})));
    public void EnemyMoveStarted(Creature source, string moveId) { }
    public void EnemyMoveFinished(Creature source, string moveId) { }
    public void PlayerTurnEnded(CombatState state) => Events.Add(new("player_turn_ended", ""));
    public void PotionUseStarted(PotionModel potion, Creature? target) { }
    public void PotionUseFinished(PotionModel potion, Creature? target) => Events.Add(new("potion_used", potion.GetType().Name));
    public void DamageResolved(Creature? dealer, DamageResult result) => Events.Add(new("damage",PublicJson.Serialize(new
    {
        target=result.Receiver.IsPlayer?"player":result.Receiver.Monster!.GetType().Name,
        blocked=result.BlockedDamage,unblocked=result.UnblockedDamage,overkill=result.OverkillDamage,
        hpAfter=result.Receiver.CurrentHp,killed=result.WasTargetKilled,
    })));
    public void PowerAmountChanged(PowerModel power,decimal amount,Creature? applier,CardModel? cardSource) =>
        Events.Add(new("power_changed",PublicJson.Serialize(new {target=power.Owner.IsPlayer?"player":power.Owner.Monster!.GetType().Name,id=power.GetType().Name,amount})));
    public void CardsShuffled(Player player)
    {
        Known.Clear(); Events.Add(new("shuffle", "known_positions_reset"));
    }
    public void CardMoved(CardModel card, PileType? from, PileType to, CardPilePosition position)
    {
        if (from == PileType.Draw)
        {
            // Ordinary draws have their own CardDrawn event. An explicit arbitrary pile move
            // does not supply a public old offset, so do not invent one.
            Known.Clear();
        }
        if (to != PileType.Draw) return;
        bool publiclyKnown = from is PileType.Hand or PileType.Discard or PileType.Exhaust or PileType.Play;
        if (position == CardPilePosition.Top)
        {
            var shifted=Known.Select(x=>(x.Key+1,x.Value)).ToArray();
            Known.Clear(); foreach(var (p,c) in shifted) Known[p]=c;
            if(publiclyKnown) Known[0]=PublicViews.Card(card);
        }
        else if(position is CardPilePosition.Bottom or CardPilePosition.None)
        {
            if(publiclyKnown) Known[card.Owner.PlayerCombatState!.DrawPile.Cards.Count-1]=PublicViews.Card(card);
        }
        else { Known.Clear(); }
    }
}

internal static class PublicViews
{
    internal static PublicCard Card(CardModel c) => new(c.GetType().Name, c.CurrentUpgradeLevel, c.EnergyCost, c.StarCost, c.Type.ToString(), c.Keywords.Select(x=>x.ToString()).Order(StringComparer.Ordinal).ToArray());
    internal static PublicPower[] Powers(Creature c) => c.Powers.Select(p=>new PublicPower(p.GetType().Name,p.Amount)).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
    internal static PublicIntent[] Intents(Creature c) => c.Monster!.NextMove?.Intents.Select(i=>
        i is Sts2Sim.Core.MonsterMoves.Intents.AttackIntent a
        ? new PublicIntent(i.IntentType.ToString(),a.GetSingleDamage(c.CombatState!.PlayerCreatures,c),a.Repeats)
        : new PublicIntent(i.IntentType.ToString(),null,null)).ToArray() ?? [];
    internal static PublicObservation Observe(CombatState state, PublicKnowledge knowledge, int startHp, PublicChoice? choice)
    {
        var p=state.Players[0]; var s=p.PlayerCombatState!;
        // The unordered multiset is public for this supported deck: all cards entered through public setup/draw/discard/status events.
        var multiset=s.DrawPile.Cards.Select(Card).GroupBy(PublicJson.Serialize)
            .OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new CardCount(g.First(),g.Count())).ToArray();
        var unknown=multiset.ToDictionary(x=>PublicJson.Serialize(x.Card),x=>x.Count);
        foreach(var (_,card) in knowledge.Known)
        {
            string key=PublicJson.Serialize(card);
            if(!unknown.ContainsKey(key) || --unknown[key]<0) throw new InvalidOperationException("Public knowledge contradicts draw multiset");
        }
        var observation = new PublicObservation("nosl.public.v1",startHp,10,s.TurnNumber,p.Creature.CurrentHp,p.Creature.MaxHp,
            p.Creature.Block,s.Energy,s.Stars,s.Hand.Cards.Select(Card).ToArray(),s.DiscardPile.Cards.Select(Card).ToArray(),s.ExhaustPile.Cards.Select(Card).ToArray(),
            multiset.Where(x=>unknown[PublicJson.Serialize(x.Card)]>0).Select(x=>new CardCount(x.Card,unknown[PublicJson.Serialize(x.Card)])).ToArray(),
            knowledge.Known.Select(x=>new KnownPosition(x.Key,x.Value)).ToArray(),s.DrawPile.Cards.Count,
            p.PotionSlots.Select(x=>x?.GetType().Name).ToArray(),p.Relics.Select(x=>x.GetType().Name).ToArray(),Powers(p.Creature),
            state.Enemies.Select((c,i)=>new PublicEnemy(i,c.Monster!.GetType().Name,c.CurrentHp,c.MaxHp,c.Block,Powers(c),Intents(c))).ToArray(),knowledge.Events.ToArray(),choice);
        return observation;
    }
}
