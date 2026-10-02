using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

// Only public facts are exported. Worker-private card bindings preserve public known-position identity across mutation and clone; never serialize these references.
internal sealed class PublicKnowledge : ICombatObserver
{
    internal readonly List<PublicEvent> Events = [];
    internal OutcomeEventLedger OutcomeLedger { get; private set; } = new();
    // Called after inventory acquisition and fixed start snapshots, before room
    // setup hooks. Upstream CombatStarted arrives after some legal setup effects.
    internal void BeginCombat()
    {
        if (Events.Count != 0) throw new InvalidOperationException("Combat history already started");
        Events.Add(new("combat_started", "Silent:A10"));
    }
    internal readonly SortedDictionary<int, CardModel> Known = [];
    private readonly HashSet<CardModel> _unrevealedGenerated = new(ReferenceEqualityComparer.Instance);
    internal bool IsUnrevealed(CardModel card) => _unrevealedGenerated.Contains(card);
    internal void Reveal(IEnumerable<CardModel> cards) { foreach(var card in cards) _unrevealedGenerated.Remove(card); }
    internal void Rebind(CombatCloneMap map)
    {
        foreach(var position in Known.Keys.ToArray()) Known[position]=map.Card(Known[position]);
        var rebound=_unrevealedGenerated.Where(c=>c.Pile?.Type==PileType.Draw).Select(c=>map.TryCard(c,out var mapped)?mapped:null).OfType<CardModel>().ToArray();
        _unrevealedGenerated.Clear(); foreach(var card in rebound) _unrevealedGenerated.Add(card);
    }
    // CombatId is a private lookup key only. The emitted slot is an independent public
    // lifetime ordinal, so death/summons never renumber previously published targets.
    private readonly Dictionary<uint,int> _slots = [];
    private int _nextSlot;
    internal int Slot(Creature creature)
    {
        if (creature.IsPlayer) return -2;
        uint id = creature.CombatId ?? throw new InvalidOperationException("Creature lacks combat identity");
        if (!_slots.TryGetValue(id,out int slot)) _slots[id] = slot = _nextSlot++;
        return slot;
    }
    internal PublicKnowledge Copy()
    {
        var k = new PublicKnowledge { OutcomeLedger = OutcomeLedger.Copy() }; k.Events.AddRange(Events);
        foreach (var (p,c) in Known) k.Known[p]=c;
        foreach (var (id,slot) in _slots) k._slots[id]=slot; k._nextSlot=_nextSlot;
        foreach(var card in _unrevealedGenerated) k._unrevealedGenerated.Add(card);
        return k;
    }
    public void CombatStarted(CombatState state)
    {
        foreach(var creature in state.Enemies.Concat(state.Allies)) Slot(creature);
        // The explicit pre-setup boundary already owns the single start marker.
    }
    public void PlayerTurnStarted(CombatState state)
    {
        Events.Add(new("player_turn", state.Players[0].PlayerCombatState!.TurnNumber.ToString()));
        foreach(var (enemy,slot) in state.Enemies.Select((e,i)=>(e,i)))
            Events.Add(new("intent_published",PublicJson.Serialize(new {slot=Slot(enemy),id=enemy.Monster!.GetType().Name,intents=PublicViews.Intents(enemy)})));
    }
    public void CardDrawn(CardModel card)
    {
        _unrevealedGenerated.Remove(card);
        // The engine's Draw path uses pile internal methods, not CardPileCmd.Add.
        var remaining=Known.Where(x=>x.Key>0).Select(x=>(x.Key-1,x.Value)).ToArray();
        Known.Clear(); foreach(var (p,c) in remaining) Known[p]=c;
        Events.Add(new("draw", PublicJson.Serialize(PublicViews.Card(card))));
    }
    public void CardPlayStarted(CardModel card, Creature? target)
    {
        _unrevealedGenerated.Remove(card);
        Events.Add(new("card_started",PublicJson.Serialize(PublicViews.Card(card))));
    }
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
        targetSlot=Slot(result.Receiver),sourceSlot=dealer is null?(int?)null:Slot(dealer),
        blocked=result.BlockedDamage,unblocked=result.UnblockedDamage,overkill=result.OverkillDamage,
        hpAfter=result.Receiver.CurrentHp,killed=result.WasTargetKilled,
    })));
    public void PowerAmountChanged(PowerModel power,decimal amount,Creature? applier,CardModel? cardSource) =>
        Events.Add(new("power_changed",PublicJson.Serialize(new {target=power.Owner.IsPlayer?"player":power.Owner.Monster!.GetType().Name,targetSlot=Slot(power.Owner),sourceSlot=applier is null?(int?)null:Slot(applier),id=power.GetType().Name,amount})));
    public void CardsShuffled(Player player)
    {
        Known.Clear(); Events.Add(new("shuffle", "known_positions_reset"));
    }
    public void CardMoved(CardModel card, PileType? from, PileType to, CardPilePosition position)
    {
        if(to!=PileType.Draw) _unrevealedGenerated.Remove(card);
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
            if(publiclyKnown) Known[0]=card;
        }
        else if(position is CardPilePosition.Bottom or CardPilePosition.None)
        {
            if(publiclyKnown) Known[card.Owner.PlayerCombatState!.DrawPile.Cards.Count-1]=card;
        }
        else { Known.Clear(); }
    }
    public void CardEnteredCombat(CardModel card,PileType to,CardPilePosition position)
    {
        CardMoved(card,null,to,position);
        if(to==PileType.Draw)
        {
            // Conservatively withhold newly generated hidden identities (Metamorphosis,
            // for example) until actually revealed. Fixed generated statuses lose some
            // optional information here, never gain access to an unknown future.
            _unrevealedGenerated.Add(card);
            Events.Add(new("hidden_card_generated","draw"));
        }
        else Events.Add(new("card_generated",PublicJson.Serialize(PublicViews.Card(card))));
    }
}

internal static class PublicViews
{
    internal static PublicCard Card(CardModel c) => PublicCardDetailsBuilder.Card(c);
    internal static PublicPower[] Powers(Creature c, PublicKnowledge? knowledge=null) => c.Powers.Select(p=>new PublicPower(
        p.GetType().Name,p.Amount,p.AmountOnTurnStart,p.SkipNextDurationTick,
        (p as Sts2Sim.Core.Models.Powers.NightmarePower)?.NoslSelectedCard?.GetType().Name,
        (p as Sts2Sim.Core.Models.Powers.NightmarePower)?.NoslSelectedUpgrade,
        p.Applier is null?null:knowledge?.Slot(p.Applier))).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
    internal static PublicIntent[] Intents(Creature c) => c.Monster!.NextMove?.Intents.Select(i=>
        i is Sts2Sim.Core.MonsterMoves.Intents.AttackIntent a
        ? new PublicIntent(i.IntentType.ToString(),a.GetSingleDamage(c.CombatState!.PlayerCreatures,c),a.Repeats)
        : new PublicIntent(i.IntentType.ToString(),null,null)).ToArray() ?? [];
    internal static PublicObservation Observe(CombatState state, PublicKnowledge knowledge, int startHp, PublicChoice? choice, int startGold=0,
        PublicRunContext? runContext = null)
    {
        runContext?.Validate();
        var p=state.Players[0]; var s=p.PlayerCombatState!;
        // The unordered multiset is public for this supported deck: all cards entered through public setup/draw/discard/status events.
        var multiset=s.DrawPile.Cards.Where(c=>!knowledge.IsUnrevealed(c)).Select(Card).GroupBy(PublicJson.Serialize)
            .OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new CardCount(g.First(),g.Count())).ToArray();
        var unknown=multiset.ToDictionary(x=>PublicJson.Serialize(x.Card),x=>x.Count);
        foreach(var (_,card) in knowledge.Known)
        {
            string key=PublicJson.Serialize(Card(card));
            if(!unknown.ContainsKey(key) || --unknown[key]<0) throw new InvalidOperationException("Public knowledge contradicts draw multiset");
        }
        var observation = new PublicObservation(runContext is null ? "nosl.public.v2" : PublicRunContext.ObservationSchema,startHp,10,s.TurnNumber,p.Creature.CurrentHp,p.Creature.MaxHp,
            p.Creature.Block,s.Energy,s.Stars,s.Hand.Cards.Select(Card).ToArray(),s.DiscardPile.Cards.Select(Card).ToArray(),s.ExhaustPile.Cards.Select(Card).ToArray(),
            multiset.Where(x=>unknown[PublicJson.Serialize(x.Card)]>0).Select(x=>new CardCount(x.Card,unknown[PublicJson.Serialize(x.Card)])).ToArray(),
            knowledge.Known.Select(x=>new KnownPosition(x.Key,Card(x.Value))).ToArray(),s.DrawPile.Cards.Count,
            p.PotionSlots.Select(x=>x?.GetType().Name).ToArray(),p.Relics.Select(x=>x.GetType().Name).ToArray(),Powers(p.Creature,knowledge),
            state.Enemies.Select(c=>new PublicEnemy(knowledge.Slot(c),c.Monster!.GetType().Name,c.CurrentHp,c.MaxHp,c.Block,Powers(c,knowledge),Intents(c))).ToArray(),knowledge.Events.ToArray(),choice,
            new(s.AttackCardsPlayedThisTurn,s.SkillCardsPlayedThisTurn,s.ShivsPlayedThisTurn,s.CardsDiscardedThisTurn,
                s.CardsDrawnThisCombat,s.CardsPlayedThisCombat,s.CardsGeneratedThisCombat,s.CardsPlayedThisTurn,
                s.ManualCardsPlayedThisTurn,s.CardPlaysStartedThisTurn,s.AttackCardsStartedThisTurn,s.ZeroCostAttacksStartedThisTurn,
                s.AttackOrSkillCardPlaysStartedThisTurn,s.FirstInSeriesCardPlaysStartedThisTurn,s.StarsGainedThisTurn),
            p.Relics.Select(r=>new PublicRelic(r.GetType().Name,PublicRelicDetails.Details(r),PublicRelicDetails.Cards(r),PublicRelicDetails.SelectedModel(r))).ToArray(),
            p.Gold,startGold,s.OrbQueue.Capacity,s.OrbQueue.Orbs.Select(o=>new PublicOrb(o.GetType().Name,o.PassiveVal,o.EvokeVal)).ToArray(),
            s.Pets.Select(c=>new PublicPet(knowledge.Slot(c),c.Monster!.GetType().Name,c.CurrentHp,c.MaxHp,c.Block,Powers(c,knowledge))).ToArray(),
            s.DrawPile.Cards.Count(knowledge.IsUnrevealed), runContext);
        return observation;
    }
}
