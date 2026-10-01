using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

public sealed record Scenario(string Seed="NOSL-M012", string Enemy="TwigSlimeS", string[]? Deck=null,
    string[]? Potions=null, string[]? Relics=null, int? Hp=null, int? MaxHp=null, int? EnemyHp=null);

public sealed class CombatSession : IAsyncDisposable, ICardSelectionDecisionSource
{
    // Limited technical closure, not the M5 full-content promise.
    public static readonly string[] SupportedCards = ["StrikeSilent","DefendSilent","Neutralize","Survivor","AscendersBane","Acrobatics","Backflip","Prepared","ThinkingAhead","DeadlyPoison","Slimed"];
    public static readonly string[] SupportedEnemies = ["TwigSlimeS","LeafSlimeS","Nibbit"];
    public static readonly string[] SupportedPotions = ["FirePotion","BlockPotion","EnergyPotion","SwiftPotion","FruitJuice"];
    public static readonly string[] SupportedRelics = ["RingOfTheSnake","MeatOnTheBone","ChosenCheese"];
    private readonly Scenario _scenario;
    private PublicKnowledge _knowledge;
    private CardSelectionRequest? _request;
    private TaskCompletionSource<IReadOnlyList<CardModel>>? _selection;
    private TaskCompletionSource _choiceReady = NewSignal();
    private Task? _operation;
    private readonly List<PublicAction> _replay = [];
    private int _revision;
    private TerminalFacts? _terminal;
    private Task<TerminalFacts>? _settlement;
    private bool _disposed;
    private Exception? _fault;
    private static readonly object InitLock = new();
    internal CombatState State { get; private set; } = null!;
    internal CombatRoom Room { get; private set; } = null!;
    internal PublicKnowledge Knowledge => _knowledge;
    public int StartHp { get; private set; }
    public int StartMaxHp { get; private set; }
    public bool HasPendingChoice => _request is not null;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CombatSession(Scenario scenario, PublicKnowledge knowledge) { _scenario=scenario; _knowledge=knowledge; }

    private static T Model<T>(string name, string[] allowed) where T:AbstractModel
    {
        if(!allowed.Contains(name,StringComparer.Ordinal)) throw new NotSupportedException($"Unsupported {typeof(T).Name}: {name}");
        return (T)ModelDb.All<T>().Single(x=>x.GetType().Name==name).MutableClone();
    }
    public static async Task<CombatSession> CreateAsync(Scenario? scenario=null)
    {
        scenario??=new();
        lock(InitLock) ModelDb.Init(ContentRegistry.AllTypes);
        // Validate every requested model before mutating the new state.
        var enemy=Model<MonsterModel>(scenario.Enemy,SupportedEnemies);
        if(enemy is Sts2Sim.Core.Models.Monsters.Nibbit nibbit) nibbit.IsAlone=true;
        if(scenario.Deck is {Length:0}) throw new ArgumentException("Empty scenario deck");
        var cards=scenario.Deck?.Select(x=>Model<CardModel>(x.TrimEnd('+'),SupportedCards)).ToArray();
        var potions=(scenario.Potions??[]).Select(x=>Model<PotionModel>(x,SupportedPotions)).ToArray();
        var relics=(scenario.Relics??[]).Select(x=>Model<RelicModel>(x,SupportedRelics)).ToArray();
        var run=new RunState(scenario.Seed,new Overgrowth(),ascensionLevel:10);
        var player=Player.CreateForNewRun(ModelDb.Character<Silent>(),run); run.AddPlayer(player);
        if(cards is not null)
        {
            foreach(var c in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(c);
            for(int i=0;i<cards.Length;i++)
            { cards[i].AssignOwner(player); if(scenario.Deck![i].EndsWith('+')) cards[i].Upgrade(); player.Deck.AddInternal(cards[i]); }
        }
        if(scenario.MaxHp is int max)
        { if(max<=0) throw new ArgumentOutOfRangeException(nameof(scenario.MaxHp)); player.Creature.SetMaxHpInternal(max); }
        if(scenario.Hp is int hp)
        { if(hp<=0 || hp>player.Creature.MaxHp) throw new ArgumentOutOfRangeException(nameof(scenario.Hp)); player.Creature.SetCurrentHpInternal(hp); }
        foreach(var potion in potions) { potion.AssignOwner(player); player.AddPotionInternal(potion); }
        foreach(var relic in relics) await RelicCmd.Obtain(relic,player);
        var session=new CombatSession(scenario,new());
        session.StartHp=player.Creature.CurrentHp; session.StartMaxHp=player.Creature.MaxHp;
        var room=new CombatRoom(()=>enemy,RoomType.Monster,CombatRoom.ForcedEncounterName);
        room.ConfigureCardSelectionSource(session); room.ConfigureObserver(session._knowledge);
        run.PushRoom(room); session.Room=room;
        await room.Enter(run); session.State=room.Engine.State;
        if(scenario.EnemyHp is int enemyHp)
        { if(enemyHp<=0) throw new ArgumentOutOfRangeException(nameof(scenario.EnemyHp)); enemy.Creature.SetMaxHpInternal(enemyHp); enemy.Creature.SetCurrentHpInternal(enemyHp); }
        session.AssertScope(); return session;
    }
    private void AssertScope()
    {
        var p=State.Players.Single();
        foreach(var c in p.PlayerCombatState!.AllPiles.SelectMany(x=>x.Cards))
            if(!SupportedCards.Contains(c.GetType().Name) || c.Enchantments.Count!=0 || c.Affliction is not null)
                throw new NotSupportedException($"Unsupported card mechanism: {c.GetType().Name}");
        if(State.Enemies.Any(x=>!SupportedEnemies.Contains(x.Monster!.GetType().Name))) throw new NotSupportedException("Unsupported monster posterior");
        if(p.Relics.Any(x=>!SupportedRelics.Contains(x.GetType().Name))) throw new NotSupportedException("Unsupported relic");
    }
    public DecisionPacket Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_fault is not null) return new("engine_error",null,[]);
        if(_terminal is not null) return new("terminal_settled",null,[]);
        if(!Room.Engine.IsInProgress) return new("terminal_pending_settlement",null,[]);
        AssertScope();
        if(_operation is {IsCompleted:false} && _request is null) throw new InvalidOperationException("No stable decision boundary");
        var choice=_request is null ? null : new PublicChoice(_request.Source?.GetType().Name??"unknown",_request.MinCount,_request.MaxCount,_request.Cancelable,_request.Candidates.Select(PublicViews.Card).ToArray());
        return new(choice is null?"player_decision":"card_choice",PublicViews.Observe(State,_knowledge,StartHp,choice),EnumerateActions());
    }
    private IReadOnlyList<Creature?> Targets(TargetType type, Player p) => type switch
    {
        TargetType.AnyEnemy => State.HittableEnemies.Cast<Creature?>().ToArray(),
        TargetType.Self => [p.Creature],
        TargetType.AnyPlayer => [p.Creature],
        TargetType.None or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies or TargetType.TargetedNoCreature => [null],
        _ => throw new NotSupportedException($"Unsupported target mechanism {type}"),
    };
    private int TargetSlot(Creature? target) => target is null?-1:target.IsPlayer?-2:State.Enemies.ToList().IndexOf(target);
    private Creature? ResolveTarget(int target) => target==-1?null:target==-2?State.Players[0].Creature:State.Enemies[target];
    public PublicAction[] EnumerateActions()
    {
        if(_fault is not null || _terminal is not null || !Room.Engine.IsInProgress) return [];
        if(_request is not null)
        {
            if(_request.IsBundleSelection) throw new NotSupportedException("Bundle choice is not in M1 supported scope");
            var result=new List<PublicAction>(); int n=_request.Candidates.Count;
            for(int mask=0;mask<(1<<n);mask++)
            {
                var indices=Enumerable.Range(0,n).Where(i=>(mask&(1<<i))!=0).ToArray();
                if((indices.Length>=_request.MinCount || indices.Length==0 && _request.Cancelable) && indices.Length<=_request.MaxCount)
                    result.Add(new(_revision,"choose",Selection:indices));
            }
            return result.ToArray();
        }
        var p=State.Players[0]; var s=p.PlayerCombatState!;
        if(State.CurrentSide!=CombatSide.Player || s.Phase!=PlayerTurnPhase.Play) throw new InvalidOperationException("Not player play phase");
        var actions=new List<PublicAction>();
        for(int i=0;i<s.Hand.Cards.Count;i++)
            if(s.Hand.Cards[i].CanPlay(out _))
                foreach(var target in Targets(s.Hand.Cards[i].TargetType,p)) actions.Add(new(_revision,"play",i,TargetSlot(target)));
        for(int i=0;i<p.PotionSlots.Count;i++)
            if(p.PotionSlots[i] is { } potion)
                foreach(var target in Targets(potion.TargetType,p))
                    if(PotionCmd.CanUseManually(State,p,potion,target)) actions.Add(new(_revision,"potion",i,TargetSlot(target)));
        actions.Add(new(_revision,"end_turn")); return actions.ToArray();
    }
    public async Task<DecisionPacket> StepAsync(PublicAction action)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_operation is {IsCompleted:false} && _request is null) throw new InvalidOperationException("An action is still running");
        if(!EnumerateActions().Any(a=>PublicJson.Serialize(a)==PublicJson.Serialize(action))) throw new ArgumentException("Illegal or stale public action token");
        _replay.Add(action); _knowledge.Events.Add(new("action",PublicJson.Serialize(action))); _revision++;
        if(action.Kind=="choose")
        {
            var selected=action.Selection!.Select(i=>_request!.Candidates[i]).ToArray();
            var signal=_selection!; _request=null; _selection=null; _choiceReady=NewSignal(); signal.SetResult(selected);
        }
        else
        {
            _choiceReady=NewSignal(); var p=State.Players[0];
            _operation=action.Kind switch
            {
                "play"=>Room.Engine.PlayCardAsync(p,p.PlayerCombatState!.Hand.Cards[action.Slot],ResolveTarget(action.Target)),
                "potion"=>PotionCmd.Use(p.PotionSlots[action.Slot]!,p,ResolveTarget(action.Target)),
                "end_turn"=>Room.Engine.EndPlayerTurnAsync(),
                _=>throw new ArgumentException("Unknown action"),
            };
        }
        try { await AwaitBoundaryAsync(); }
        catch(Exception e) { _fault=e; _operation=null; throw; }
        if(_request is null)
        {
            Room.Engine.CheckWinCondition();
            if(!Room.Engine.IsInProgress) await SettleAsync();
        }
        return Observe();
    }
    private async Task AwaitBoundaryAsync()
    {
        await Task.WhenAny(_operation!,_choiceReady.Task);
        if(_operation!.IsCompleted) { await _operation; _operation=null; }
    }
    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        if(_request is not null) throw new InvalidOperationException("Overlapping choices");
        if(request.Candidates.Any(c=>c.Pile?.Type!=PileType.Hand)) throw new NotSupportedException("Non-public-hand choice is outside supported scope");
        _request=request; _selection=new(TaskCreationOptions.RunContinuationsAsynchronously);
        _knowledge.Events.Add(new("choice",PublicJson.Serialize(new PublicChoice(request.Source?.GetType().Name??"unknown",request.MinCount,request.MaxCount,request.Cancelable,request.Candidates.Select(PublicViews.Card).ToArray()))));
        _choiceReady.TrySetResult(); return _selection.Task;
    }
    public Task<TerminalFacts> SettleAsync()
    {
        if(_settlement is not null) return _settlement;
        if(_request is not null || Room.Engine.IsInProgress) throw new InvalidOperationException("Combat has not ended");
        return _settlement=SettleOnceAsync();
    }
    private async Task<TerminalFacts> SettleOnceAsync()
    {
        var p=State.Players[0];
        _knowledge.Events.Add(new("pre_settlement",PublicJson.Serialize(new { hp=p.Creature.CurrentHp,maxHp=p.Creature.MaxHp,hand=p.PlayerCombatState!.Hand.Cards.Select(PublicViews.Card).ToArray(),exhaust=p.PlayerCombatState.ExhaustPile.Cards.Select(PublicViews.Card).ToArray() })));
        // Generate offers and execute BeforeCombatRewardOffered. Never choose/claim rewards or Exit before this boundary.
        await Room.ResolveOutcomeAsync(generateRewards:true);
        _terminal=new(Room.Engine.Won?"win":"loss",StartHp,p.Creature.CurrentHp,StartMaxHp,p.Creature.MaxHp,
            p.PotionSlots.Select(x=>x?.GetType().Name).ToArray(),_knowledge.Events.ToArray(),"AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",0);
        return _terminal;
    }
    public CombatSession ForkExact()
    {
        if(_request is not null || _operation is not null) throw new InvalidOperationException("Pending coroutine is not cloneable; use replay to the choice");
        var branch=new CombatSession(_scenario,_knowledge.Copy()) {StartHp=StartHp,StartMaxHp=StartMaxHp,_revision=_revision};
        branch.State=State.CloneForNosl(out _,branch._knowledge); branch.Room=(CombatRoom)branch.State.RunState.CurrentRoom!;
        branch.State.CardSelectionSource=branch; branch._replay.AddRange(_replay); return branch;
    }
    internal void ReseedFuture(ulong samplerSeed) => State.ReseedNoslFuture(samplerSeed);
    public async Task<CombatSession> ReplayToChoiceAsync()
    {
        if(_request is null) throw new InvalidOperationException("No pending choice");
        // Re-enter an independent coroutine by replay, not by copying its stack. This is exact replay, not belief sampling.
        var replay=await CreateAsync(_scenario);
        try { foreach(var a in _replay) await replay.StepAsync(a); }
        catch { await replay.DisposeAsync(); throw; }
        if(!replay.HasPendingChoice) { await replay.DisposeAsync(); throw new InvalidOperationException("Replay did not reach choice"); }
        return replay;
    }
    public async ValueTask DisposeAsync()
    {
        if(_disposed) return; _disposed=true;
        if(_selection is not null) _selection.TrySetCanceled();
        if(_operation is not null) { try { await _operation; } catch(OperationCanceledException) { } }
    }
}
