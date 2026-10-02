using System.Diagnostics;
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
    string[]? Potions=null, string[]? Relics=null, int? Hp=null, int? MaxHp=null, int? EnemyHp=null, string? Encounter=null, string[]? Enemies=null, int? Gold=null);

public sealed partial class CombatSession : IAsyncDisposable, ICardSelectionDecisionSource, IAutomaticCardSelectionObserver
{
    // Content rules and registration come directly from the pinned simulator.
    // These are discovery lists, not a claim that every interaction has an integration test.
    public static string[] SupportedCards => CardCoverage.SupportedCards;
    public static string[] SupportedEnemies => ContentRegistry.AllTypes.Where(t=>typeof(MonsterModel).IsAssignableFrom(t)).Select(t=>t.Name).ToArray();
    public static string[] SupportedPotions => ContentRegistry.AllTypes.Where(t=>typeof(PotionModel).IsAssignableFrom(t)).Select(t=>t.Name).ToArray();
    public static string[] SupportedRelics => ContentRegistry.AllTypes.Where(t=>typeof(RelicModel).IsAssignableFrom(t)).Select(t=>t.Name).ToArray();
    private readonly Scenario? _scenario;
    internal NativeBeliefCertificate? NativeCertificate { get; private set; }
    internal bool HasNativeProvenance => NativeCertificate is not null;
    private PublicKnowledge _knowledge;
    private CardSelectionRequest? _request;
    private TaskCompletionSource<IReadOnlyList<CardModel>>? _selection;
    private TaskCompletionSource _choiceReady = NewSignal();
    private Task? _operation;
    private readonly List<PublicAction> _replay = [];
    private readonly List<string> _publicTrace = [];
    private bool _sampledInPlace;
    private string _choiceOrder = "public";
    internal Scenario InitialScenario => _scenario ?? throw new NotSupportedException("native_prior_mismatch: native carry-in has no fresh Scenario prior");
    internal IReadOnlyList<PublicAction> ReplayActions => _replay;
    internal IReadOnlyList<string> PublicTrace => _publicTrace;
    internal bool HasInPlaceSampledProvenance => _sampledInPlace;
    public int StartGold { get; private set; }
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
    public string?[] StartPotions { get; private set; } = [];
    internal CombatAssetSnapshot InitialAssets { get; private set; } = null!;
    internal CombatAssetSnapshot? FinalAssets { get; private set; }
    public double SettlementSeconds { get; private set; }
    public bool HasPendingChoice => _request is not null;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CombatSession(Scenario? scenario, PublicKnowledge knowledge)
    {
        _scenario=scenario is null ? null : scenario with { Deck=scenario.Deck?.ToArray(),Potions=scenario.Potions?.ToArray(),
            Relics=scenario.Relics?.ToArray(),Enemies=scenario.Enemies?.ToArray() }; _knowledge=knowledge;
    }

    // The only native import path checks the reviewed certificate before making a
    // detached branch. It never rebuilds a fresh scenario from a mid-run inventory.
    internal static CombatSession ImportNative(NaturalSourceRoot root, NaturalSourceBoundary boundary)
    {
        var certificate = NativeBeliefCertificate.Certify(root, boundary);
        var session = new CombatSession(null, boundary.Knowledge.Copy())
        {
            NativeCertificate = certificate, StartHp = boundary.StartHp, StartMaxHp = boundary.StartMaxHp,
            StartGold = boundary.StartGold, StartPotions = boundary.StartPotions.ToArray(),
            InitialAssets = boundary.InitialAssets, _revision = boundary.Revision,
        };
        session.State = boundary.State.CloneForNosl(out var map, session._knowledge);
        session._knowledge.Rebind(map);
        session.Room = (CombatRoom)session.State.RunState.CurrentRoom!;
        session.State.CardSelectionSource = session;
        if (PublicJson.Serialize(session.Observe()) != PublicJson.Serialize(root.PublicRoot))
            throw new InvalidOperationException("Native detached import changed the public root");
        session._publicTrace.Add(PublicJson.Serialize(root.PublicRoot));
        return session;
    }

    private static T Model<T>(string name, string[] allowed) where T:AbstractModel
    {
        if(!allowed.Contains(name,StringComparer.Ordinal)) throw new NotSupportedException($"Unsupported {typeof(T).Name}: {name}");
        return (T)ModelDb.All<T>().Single(x=>x.GetType().Name==name).MutableClone();
    }
    public static async Task<CombatSession> CreateAsync(Scenario? scenario=null)
    {
        scenario??=new();
        lock(InitLock) { if(!ModelDb.Contains(typeof(Silent))) ModelDb.Init(ContentRegistry.AllTypes); }
        // Validate every requested model before mutating the new state.
        if(scenario.Encounter is not null && scenario.Enemies is not null) throw new ArgumentException("Choose a named encounter or a declared monster batch");
        var enemies=scenario.Encounter is null?(scenario.Enemies??[scenario.Enemy]).Select(id=>Model<MonsterModel>(id,SupportedEnemies)).ToArray():[];
        if(scenario.Encounter is null && enemies.Length==0) throw new ArgumentException("Empty monster batch");
        if(enemies.Length==1 && enemies[0] is Sts2Sim.Core.Models.Monsters.Nibbit nibbit) nibbit.IsAlone=true;
        if(scenario.Deck is {Length:0}) throw new ArgumentException("Empty scenario deck");
        var cards=scenario.Deck?.Select(x=>Model<CardModel>(x.TrimEnd('+'),SupportedCards)).ToArray();
        var potions=(scenario.Potions??[]).Select(x=>Model<PotionModel>(x,SupportedPotions)).ToArray();
        var relics=(scenario.Relics??[]).Select(x=>Model<RelicModel>(x,SupportedRelics)).ToArray();
        var run=EncounterCoverage.CreateRun(scenario.Seed,scenario.Encounter,10);
        var player=run.Players.Single();
        var session=new CombatSession(scenario,new());
        run.ConfigureCardSelectionSource(session);
        if(cards is not null)
        {
            foreach(var c in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(c);
            for(int i=0;i<cards.Length;i++)
            {
                cards[i].AssignOwner(player);
                int upgrades=scenario.Deck![i].Length-scenario.Deck[i].TrimEnd('+').Length;
                for(int level=0;level<upgrades;level++)
                { if(!cards[i].IsUpgradable) throw new ArgumentException($"Illegal upgrade level: {scenario.Deck[i]}"); cards[i].Upgrade(); }
                player.Deck.AddInternal(cards[i]);
            }
        }
        if(scenario.MaxHp is int max)
        { if(max<=0) throw new ArgumentOutOfRangeException(nameof(scenario.MaxHp)); player.Creature.SetMaxHpInternal(max); }
        if(scenario.Hp is int hp)
        { if(hp<=0 || hp>player.Creature.MaxHp) throw new ArgumentOutOfRangeException(nameof(scenario.Hp)); player.Creature.SetCurrentHpInternal(hp); }
        if(scenario.Gold is int gold) { if(gold<0) throw new ArgumentOutOfRangeException(nameof(scenario.Gold)); player.Gold=gold; }
        if(potions.Length>player.PotionSlots.Count) throw new ArgumentException("Potions exceed available slots");
        foreach(var potion in potions) { potion.AssignOwner(player); player.AddPotionInternal(potion); }
        foreach(var relic in relics) await RelicCmd.Obtain(relic,player);
        session.StartGold=player.Gold;
        session.StartHp=player.Creature.CurrentHp; session.StartMaxHp=player.Creature.MaxHp;
        session.StartPotions=player.PotionSlots.Select(x=>x?.GetType().Name).ToArray();
        session.InitialAssets=CombatAssetSnapshot.Capture(player);
        session._knowledge.BeginCombat();
        var room=scenario.Encounter is { } encounter?EncounterCoverage.CreateRoom(encounter,run):
            new CombatRoom(()=>(IReadOnlyList<MonsterModel>)enemies,RoomType.Monster,CombatRoom.ForcedEncounterName);
        room.ConfigureCardSelectionSource(session); room.ConfigureObserver(session._knowledge);
        if(scenario.EnemyHp is int enemyHp)
        {
            if(enemyHp<=0) throw new ArgumentOutOfRangeException(nameof(scenario.EnemyHp));
            room.ConfigureBeforeSetupDiagnostic((_,state)=>
            { foreach(var enemy in state.Enemies) { enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp); } });
        }
        run.PushRoom(room); session.Room=room;
        session._operation=room.Enter(run);
        session.State=room.Engine.State;
        await session.AwaitBoundaryAsync();
        if(session._request is null)
        { room.Engine.CheckWinCondition(); if(!room.Engine.IsInProgress) await session.SettleAsync(); }
        session.AssertScope(); session._publicTrace.Add(PublicJson.Serialize(session.Observe())); return session;
    }
    private void AssertScope()
    {
        // The simulator registry owns generated-content closure. Never delete an action or
        // substitute a simpler model because it is absent from a hand-maintained allowlist.
        if(State.Players.Count!=1) throw new NotSupportedException("The public contract is single-player");
    }
    private PublicChoice ChoiceView() => new(_request!.Source?.GetType().Name??"unknown",
        _request.MinCount,_request.MaxCount,_request.Cancelable,_request.Candidates.Select(PublicViews.Card).ToArray(),
        _choiceOrder,_request.Bundles?.Select(b=>b.Select(PublicViews.Card).ToArray()).ToArray());
    public DecisionPacket Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_fault is not null) return new("engine_error",null,[]);
        if(_terminal is not null) return new("terminal_settled",null,[]);
        if(!Room.Engine.IsInProgress) return new("terminal_pending_settlement",null,[]);
        AssertScope();
        if(_operation is {IsCompleted:false} && _request is null) throw new InvalidOperationException("No stable decision boundary");
        var choice=_request is null ? null : ChoiceView();
        return new(choice is null?"player_decision":"card_choice",PublicViews.Observe(State,_knowledge,StartHp,choice,StartGold),EnumerateActions());
    }
    private IReadOnlyList<Creature?> Targets(TargetType type, Player p) => type switch
    {
        TargetType.AnyEnemy => State.HittableEnemies.Cast<Creature?>().ToArray(),
        TargetType.Self => [p.Creature],
        TargetType.AnyPlayer => [p.Creature],
        TargetType.AnyAlly => State.Allies.Where(c=>c.IsPlayer && c.IsAlive && !ReferenceEquals(c,p.Creature)).Cast<Creature?>().ToArray(),
        TargetType.None or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies or TargetType.TargetedNoCreature => [null],
        _ => throw new NotSupportedException($"Unsupported target mechanism {type}"),
    };
    private int TargetSlot(Creature? target) => target is null?-1:_knowledge.Slot(target);
    private Creature? ResolveTarget(int target) => target==-1?null:target==-2?State.Players[0].Creature:State.Enemies.Concat(State.Allies).Single(c=>_knowledge.Slot(c)==target);
    public PublicAction[] EnumerateActions()
    {
        if(_fault is not null || _terminal is not null || !Room.Engine.IsInProgress) return [];
        if(_request is not null)
        {
            var result=new List<PublicAction>(); int n=_request.Candidates.Count;
            // Native selection order is semantic (Sly discard/autoplay, for example).
            // Enumerate ordered distinct selections rather than silently dropping permutations.
            const int maxCandidates=100000;
            void AddSize(int size)
            {
                var selected=new int[size];
                var used=new bool[n];
                void Visit(int depth)
                {
                    if(depth==size)
                    {
                        if(result.Count>=maxCandidates) throw new NotSupportedException("choice_action_space_exceeds_explicit_limit:100000");
                        result.Add(new(_revision,"choose",Selection:selected.ToArray())); return;
                    }
                    for(int index=0;index<n;index++)
                    { if(used[index]) continue; used[index]=true; selected[depth]=index; Visit(depth+1); used[index]=false; }
                }
                Visit(0);
            }
            if(_request.Cancelable && _request.MinCount>0) AddSize(0);
            for(int size=_request.MinCount;size<=_request.MaxCount;size++) AddSize(size);
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
            {
                foreach(var target in Targets(potion.TargetType,p))
                    if(PotionCmd.CanUseManually(State,p,potion,target)) actions.Add(new(_revision,"potion",i,TargetSlot(target)));
                if(p.CanUseOrRemovePotions) actions.Add(new(_revision,"discard_potion",i));
            }
        actions.Add(new(_revision,"end_turn")); return actions.ToArray();
    }
    public async Task<DecisionPacket> StepAsync(PublicAction action)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_operation is {IsCompleted:false} && _request is null) throw new InvalidOperationException("An action is still running");
        if(!EnumerateActions().Any(a=>PublicJson.Serialize(a)==PublicJson.Serialize(action))) throw new ArgumentException("Illegal or stale public action token");
        await CaptureChoiceOriginAsync(action);
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
                "discard_potion"=>PotionCmd.Discard(p.PotionSlots[action.Slot]!),
                _=>throw new ArgumentException("Unknown action"),
            };
        }
        try { await AwaitBoundaryAsync(); }
        catch(Exception e) { _fault=e; _operation=null; await ReleaseChoiceOriginAsync(); throw; }
        if(_request is null)
        {
            Room.Engine.CheckWinCondition();
            if(!Room.Engine.IsInProgress) await SettleAsync();
        }
        var result=Observe(); _publicTrace.Add(PublicJson.Serialize(result));
        await RecordChoiceSuffixAsync(action, result); return result;
    }
    private async Task AwaitBoundaryAsync()
    {
        await Task.WhenAny(_operation!,_choiceReady.Task);
        if(_operation!.IsCompleted) { await _operation; _operation=null; }
    }
    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        if(_request is not null) throw new InvalidOperationException("Overlapping choices");
        if(Room is null || Room.Engine is null)
            throw new NotSupportedException("setup_choice_outside_combat: provide the already-resolved public starting inventory");
        // Draw/deck selection reveals a set, never the real hidden order. Only the private
        // binding back to CardModel is reordered; the native effect/selection still executes.
        bool hiddenOrder=request.Candidates.Any(c=>c.Pile?.Type is PileType.Draw or PileType.Deck);
        _knowledge.Reveal(request.Candidates);
        _choiceOrder=hiddenOrder?"canonical_unordered_reveal":"public";
        _request=hiddenOrder?request with {Candidates=request.Candidates.OrderBy(c=>PublicJson.Serialize(PublicViews.Card(c)),StringComparer.Ordinal).ToArray()}:request;
        _selection=new(TaskCreationOptions.RunContinuationsAsynchronously);
        _knowledge.Events.Add(new("choice",PublicJson.Serialize(ChoiceView())));
        _choiceReady.TrySetResult(); return _selection.Task;
    }
    public void ObserveAutomaticSelection(CardSelectionRequest request,IReadOnlyList<CardModel> selected)
    {
        // Scenario acquisition is outside combat. Its resolved inventory is captured
        // by InitialAssets; do not mislabel acquisition choices as combat events.
        if (Room is null || Room.Engine is null) return;
        // Automatic draw/deck selection does not reveal hidden order or previously unknown
        // generated identities. Visible movements/effects will reveal their own outcomes.
        bool hiddenOrder=selected.Any(c=>c.Pile?.Type is PileType.Draw or PileType.Deck);
        var cards=selected.Where(c=>!_knowledge.IsUnrevealed(c)).Select(PublicViews.Card);
        if(hiddenOrder) cards=cards.OrderBy(PublicJson.Serialize,StringComparer.Ordinal);
        _knowledge.Events.Add(new("automatic_selection",PublicJson.Serialize(new
        {source=request.Source?.GetType().Name,cards=cards.ToArray(),unidentifiedCount=selected.Count(_knowledge.IsUnrevealed)})));
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
        var settlementTimer=Stopwatch.StartNew();
        await Room.ResolveOutcomeAsync(generateRewards:true);
        SettlementSeconds=settlementTimer.Elapsed.TotalSeconds;
        FinalAssets=CombatAssetSnapshot.Capture(p);
        _terminal=new(Room.Engine.Won?"win":"loss",StartHp,p.Creature.CurrentHp,StartMaxHp,p.Creature.MaxHp,
            p.PotionSlots.Select(x=>x?.GetType().Name).ToArray(),_knowledge.Events.ToArray(),"AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",0);
        return _terminal;
    }
    public CombatSession ForkExact()
    {
        if(_request is not null || _operation is not null) throw new InvalidOperationException("Pending coroutine is not cloneable; use replay to the choice");
        if(RequiresConcreteReplay()) throw new NotSupportedException("Native projection changes this content's lifecycle; use ForkForContinuationAsync for independent exact native replay");
        foreach(var relic in State.Players[0].Relics) PublicRelicDetails.AssertStable(relic);
        var branch=new CombatSession(_scenario,_knowledge.Copy()) {StartHp=StartHp,StartMaxHp=StartMaxHp,StartGold=StartGold,StartPotions=StartPotions.ToArray(),InitialAssets=InitialAssets,_revision=_revision,_sampledInPlace=_sampledInPlace,NativeCertificate=NativeCertificate};
        branch.State=State.CloneForNosl(out var map,branch._knowledge); branch._knowledge.Rebind(map); branch.Room=(CombatRoom)branch.State.RunState.CurrentRoom!;
        branch.State.CardSelectionSource=branch; branch._replay.AddRange(_replay); branch._publicTrace.AddRange(_publicTrace); return branch;
    }
    internal void ReseedFuture(ulong samplerSeed) { State.ReseedNoslFuture(samplerSeed); _sampledInPlace=true; }
    private bool RequiresConcreteReplay()
    {
        var player=State.Players[0];
        return player.Relics.Any(r=>r.GetType().Name is "FurCoat" or "LavaRock" or "Planisphere" or "GoldenCompass")
            || player.PlayerCombatState!.AllPiles.SelectMany(p=>p.Cards).Any(c=>c.GetType().Name=="TheHunt")
            || State.Allies.Concat(State.Enemies).SelectMany(c=>c.Powers).Any(p=>p.GetType().Name is "SwipePower" or "HeistPower" or "ForbiddenGrimoirePower");
    }
    /// <summary>Independent native replay preserves concrete RunState and all reward hooks.
    /// Exact replay is an execution primitive, not belief sampling or a source of policy input.</summary>
    public async Task<CombatSession> ForkForContinuationAsync()
    {
        if(HasConditionalChoiceOrigin) return await ForkConditionalChoiceAsync();
        if(_sampledInPlace || HasNativeProvenance) return ForkExact();
        var replay=await CreateAsync(_scenario);
        try
        {
            for(int index=0;index<=_replay.Count;index++)
            {
                if(PublicJson.Serialize(replay.Observe())!=_publicTrace[index])
                    throw new InvalidOperationException("Native replay diverged from its public decision transcript");
                if(index<_replay.Count) await replay.StepAsync(_replay[index]);
            }
            return replay;
        }
        catch { await replay.DisposeAsync(); throw; }
    }
    public async Task<CombatSession> ReplayToChoiceAsync()
    {
        if(HasConditionalChoiceOrigin) return await ForkConditionalChoiceAsync();
        if(HasNativeProvenance) throw new NotSupportedException("native_prior_mismatch: fresh Scenario choice replay is forbidden");
        if(_request is null) throw new InvalidOperationException("No pending choice");
        if(_sampledInPlace) throw new NotSupportedException("Sampled-world choice replay requires its own sampled provenance; original scenario replay is forbidden");
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
        try
        {
            if(_operation is not null) { try { await _operation; } catch(OperationCanceledException) { } }
        }
        finally { await ReleaseChoiceOriginAsync(); }
    }
}
