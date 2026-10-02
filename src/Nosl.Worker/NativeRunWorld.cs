using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

// Optional ownership seam; ordinary collection retains its original behavior.
internal interface INativeRunControl
{
    void CombatEntering(NativeEntryAssets entry) { }
    void BeforeDecision();
    Task<PublicAction?> DecideAsync(DecisionPacket packet, NaturalSourceBoundary boundary);
    void Settled(PublicKnowledge knowledge);
}

/// <summary>
/// Owns the complete native run and its suspended driver, including card-choice
/// coroutines. Forking replays an independent run from the same accepted proposal;
/// no native source graph, private content projection, or cloned coroutine is used.
/// </summary>
internal sealed class NativeRunWorld : ITeacherWorld, INativeRunControl
{
    private readonly NativeRunExecutionOptions _options;
    private readonly string _proposalSeed;
    private readonly int _selectedSlot;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<RunState, RunDriver, Task>? _constructedLifecycle;
    private readonly NativeLabelTape? _labelTape;
    private readonly int? _selectedCombat;
    private int _combatIndex = -1, _localDecision;
    private readonly List<PublicAction> _actions = [];
    private readonly List<string> _packets = [];
    private TaskCompletionSource<DecisionPacket?> _next = NewSignal();
    private TaskCompletionSource<PublicAction>? _pendingAction;
    private Task _execution = Task.CompletedTask;
    private NaturalSourceBoundary? _selectedBoundary;
    private NaturalSourceCollector.SourceBridge? _bridge;
    private DecisionPacket? _packet;
    private RolloutOutcome? _outcome;
    private Exception? _fault;
    private bool _disposed, _stepping;
    private int _sourceDecisions, _eligibleSlots;

    public int StartHp => Boundary.StartHp;
    public int StartMaxHp => Boundary.StartMaxHp;
    public string?[] StartPotions => Boundary.StartPotions.ToArray();
    // Timing is only the immutable settlement snapshot; the driver owns automatic
    // resolution before this hook. It is not presented as a full engine timing.
    public double SettlementSeconds { get; private set; }
    private NaturalSourceBoundary Boundary => _selectedBoundary
        ?? throw new InvalidOperationException("The selected native decision has not been reached");
    internal RunState NativeRun { get; }
    internal IReadOnlyList<NaturalSourceTrace> SourceTrace => _bridge?.Trace.ToArray() ?? [];
    internal int SourceDecisions => _sourceDecisions;
    internal int EligibleSlotsVisited => _eligibleSlots;
    internal string Encounter => Boundary.Room.EncounterName;
    internal CombatRoom NativeCombatRoom => Boundary.Room;
    internal PermanentChange[] StandardRewardOpportunities { get; private set; } = [];
    internal bool IsConstructedLifecycleFixture => _constructedLifecycle is not null;

    private static TaskCompletionSource<DecisionPacket?> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Copy<T>(T value) => PublicJson.Read<T>(PublicJson.Serialize(value));

    private NativeRunWorld(NativeRunExecutionOptions options, string seed, int slot, CancellationToken cancellationToken,
        Func<RunState, RunDriver, Task>? constructedLifecycle = null,
        NativeLabelTape? labelTape = null, int? selectedCombat = null)
    {
        if (options.MaxFloors <= 0 || options.SourceDecisionHorizon <= 0 || slot < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Native source horizons must be positive and the slot nonnegative");
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        _ = PublicContinuationPolicies.Create(options.SourcePolicyId);
        _ = options.ResolvedOutsideCombatScript;
        _ = options.EmitsPublicRunContext;
        _ = options.EmitsPublicEvidence;
        _options = options; _proposalSeed = seed; _selectedSlot = slot; _callerToken = cancellationToken;
        _constructedLifecycle = constructedLifecycle;
        _labelTape = labelTape; _selectedCombat = selectedCombat;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            NaturalSourceCollector.InitializeNativeModels();
            NativeRun = new RunState(seed, ascensionLevel: 10);
            NativeRun.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), NativeRun));
            _labelTape?.AttachHypotheticalRun(NativeRun);
        }
        catch
        {
            // OpenCoreAsync cannot dispose an object whose constructor failed.
            // Release the caller-token registration even when an early label hook
            // rejects or detects drift during native act/map construction.
            _lifetime.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Slots are zero-based over every stable/card-choice packet. Null means only
    /// that the declared source horizon or a genuine native run end preceded it.
    /// Engine errors and cancellation propagate and must not become prior rejection.
    /// </summary>
    internal static async Task<NativeRunWorld?> OpenAsync(NativeRunExecutionOptions options,
        string independentRunSeed, int selectedSlot, CancellationToken cancellationToken = default)
        => await OpenCoreAsync(options, independentRunSeed, selectedSlot, cancellationToken);

    // Explicitly separate law: ideal state-addressed primitive tape, not the
    // sequential whole-run seed posterior or the simulator's keyed RNG mode.
    internal static Task<NativeRunWorld?> OpenLabelTapeAsync(NativeRunExecutionOptions options,
        NativeTapeRecipe recipe, NativeLabelTape tape, CancellationToken cancellationToken = default)
        => OpenCoreAsync(options, recipe.IndependentRunSeed, recipe.DecisionIndex, cancellationToken,
            labelTape: tape, selectedCombat: recipe.CombatIndex);

    // Deliberately separate from the declared natural prior: explicit test-only
    // lifecycle setup can inject cards/rooms while retaining native driver rules.
    internal static Task<NativeRunWorld?> OpenConstructedLifecycleFixtureAsync(NativeRunExecutionOptions options,
        string seed, int selectedSlot, Func<RunState, RunDriver, Task> lifecycle,
        CancellationToken cancellationToken = default)
        => OpenCoreAsync(options, seed, selectedSlot, cancellationToken, lifecycle);

    private static async Task<NativeRunWorld?> OpenCoreAsync(NativeRunExecutionOptions options,
        string seed, int selectedSlot, CancellationToken cancellationToken,
        Func<RunState, RunDriver, Task>? constructedLifecycle = null,
        NativeLabelTape? labelTape = null, int? selectedCombat = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeRunWorld world;
        using (labelTape?.EnterScope())
            world = new NativeRunWorld(options, seed, selectedSlot, cancellationToken, constructedLifecycle,
                labelTape, selectedCombat);
        try
        {
            world._execution = world.ExecuteAsync();
            if (await world._next.Task is null) { await world.DisposeAsync(); return null; }
            return world;
        }
        catch { await world.DisposeAsync(); throw; }
    }

    private async Task ExecuteAsync()
    {
        using var labelScope = _labelTape?.EnterScope();
        var sourceOptions = new NaturalSourceOptions(MaxFloors: _options.MaxFloors,
            ContinuationPolicyId: _options.SourcePolicyId, OutsideCombatScript: _options.OutsideCombatScript,
            PublicContextProfile: _options.PublicContextProfile, PublicCombatHistoryMode: _options.PublicCombatHistoryMode,
            PublicEvidenceProfile: _options.PublicEvidenceProfile);
        _bridge = new(NativeRun, sourceOptions, PublicContinuationPolicies.Create(_options.SourcePolicyId), [],
            "owned-independent-proposal", _proposalSeed, null, null, _lifetime.Token, this,
            startsAtNativeRunBeginning: _constructedLifecycle is null);
        var driver = new RunDriver(NativeRun, _bridge, recorder: _bridge, useAvailablePotions: false)
        { CombatObserverDecorator = _bridge.Decorate, AutomaticCombatSettlementCompleted = _bridge.CompleteOutcome };
        driver.OnRoomResolved += (_, _) => _bridge.FloorsResolved++;
        try
        {
            RunDriver.Result? result = null;
            if (_constructedLifecycle is not null) await _constructedLifecycle(NativeRun, driver);
            else result = await driver.RunAsync(_options.MaxFloors);
            if (_selectedBoundary is not null)
                throw new InvalidOperationException("Native selected combat returned without a settlement notification");
            if (result?.Outcome == RunOutcome.MapExhausted)
                throw new InvalidOperationException("native_run_map_exhausted: run did not reach a declared horizon or terminal outcome");
            _next.TrySetResult(null);
        }
        catch (NativeSettlementReached) { }
        catch (NativeSelectedCombatHasNoRoot) { _next.TrySetResult(null); }
        catch (NativeSourceHorizonReached) when (_selectedBoundary is null) { _next.TrySetResult(null); }
        catch (OperationCanceledException e) when (_lifetime.IsCancellationRequested)
        { _fault = e; _next.TrySetCanceled(_lifetime.Token); }
        catch (Exception e) { _fault = e; _next.TrySetException(e); }
        finally
        {
            _bridge.DetachOutcome();
            await _bridge.ReleaseChoiceOriginAsync();
        }
    }

    void INativeRunControl.CombatEntering(NativeEntryAssets entry)
    {
        _combatIndex++; _localDecision = 0;
        _labelTape?.CombatEntering(_combatIndex, entry, NativeRun.Rng.Shuffle);
    }

    void INativeRunControl.BeforeDecision()
    {
        if (_outcome is not null) throw new NativeSettlementReached();
        if (_selectedBoundary is not null) return;
        if (_sourceDecisions >= _options.SourceDecisionHorizon) throw new NativeSourceHorizonReached();
        _sourceDecisions++;
    }

    async Task<PublicAction?> INativeRunControl.DecideAsync(DecisionPacket packet, NaturalSourceBoundary boundary)
    {
        _labelTape?.CheckPublicPrefix(packet.PublicEvidence);
        if (_selectedBoundary is null)
        {
            int globalSlot = _eligibleSlots++, localSlot = _localDecision++;
            if (_selectedCombat is int combat
                ? _combatIndex != combat || localSlot != _selectedSlot
                : globalSlot != _selectedSlot) return null;
        }
        if (_selectedBoundary is null)
        {
            _selectedBoundary = boundary;
            _packets.Add(PublicJson.Serialize(packet));
        }
        else if (!ReferenceEquals(Boundary.Room, boundary.Room))
            throw new InvalidOperationException("Native continuation crossed the selected combat settlement boundary");
        _packet = Copy(packet);
        var pending = _pendingAction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _next.TrySetResult(_packet);
        return await pending.Task.WaitAsync(_lifetime.Token);
    }

    void INativeRunControl.Settled(PublicKnowledge knowledge)
    {
        if (_selectedBoundary is null)
        {
            if (_selectedCombat == _combatIndex) throw new NativeSelectedCombatHasNoRoot();
            return;
        }
        var timer = Stopwatch.StartNew();
        var player = NativeRun.Players.Single();
        var finalAssets = CombatAssetSnapshot.Capture(player);
        // No-reward forced fights have already exited their combat room and
        // resumed their real owner. Capture its actual queued offers, without
        // selecting an offer or inspecting any future reward option identities.
        var eventOffers = new List<RewardsSet>();
        if (NativeRun.CurrentRoom is EventRoom eventRoom)
            while (eventRoom.Event.TryDequeuePendingRewardOffer(out var offer)) eventOffers.Add(offer);
        // V4 excludes ordinary postcombat options from terminal utility. Keep
        // their real counts in separate diagnostics; only extra/owner-return
        // opportunities enter the established permanent-change ledger.
        StandardRewardOpportunities = Opportunities(Boundary.Room.GeneratedRewards,
            "standard_combat_reward_opportunity:", includeExtras: false).ToArray();
        var permanent = CombatAssetSnapshot.Changes(Boundary.InitialAssets, finalAssets)
            .Concat(Boundary.Room.GeneratedRewards.SelectMany(set => set.ExtraRewards)
                .GroupBy(reward => reward.GetType().Name, StringComparer.Ordinal)
                .Select(group => new PermanentChange("earned_extra_reward_opportunity:" + group.Key, group.Count(),
                    "actual native offered extra rewards; option identities not inspected")))
            .Concat(Opportunities(eventOffers, "earned_event_reward_opportunity:", includeExtras: true)).ToArray();
        var ledger = knowledge.OutcomeLedger;
        _outcome = new()
        {
            TerminalKind = Boundary.Room.Engine.Won ? TerminalKind.Win : TerminalKind.Loss,
            PlayerAlive = player.Creature.CurrentHp > 0,
            HpAtCombatStart = StartHp, HpAfterSettlement = player.Creature.CurrentHp,
            MaxHpStart = StartMaxHp, MaxHpAfterSettlement = player.Creature.MaxHp,
            CumulativeHpDamage = ledger.HpComplete ? ledger.Damage : null,
            HealingReceived = ledger.HpComplete ? ledger.Healing : null,
            OtherHpAdjustment = ledger.HpComplete ? ledger.OtherHpAdjustment : null,
            HpEventDiagnosticsComplete = ledger.HpComplete,
            InventoryStart = Inventory(StartPotions),
            InventoryEnd = Inventory(player.PotionSlots.Select(p => p?.GetType().Name)),
            InventorySnapshotsComplete = true, ResourceEvents = ledger.ResourceEvents.ToArray(),
            ResourceProvenanceComplete = ledger.ResourcesComplete,
            PermanentChanges = permanent, PermanentChangesComplete = true,
            PersistentAssetsAtStartJson = PublicJson.Serialize(Boundary.InitialAssets),
            PersistentAssetsAfterSettlementJson = PublicJson.Serialize(finalAssets),
            AtomicActionsExecuted = knowledge.Events.Count(e => e.Kind == "action"),
            SettlementComplete = true,
            SettlementProfileId = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",
            Detail = (_constructedLifecycle is null ? "owned_native_run" : "constructed_native_lifecycle_fixture")
                + "; actual offered reward counts; settlement timing covers snapshot hook only",
        };
        SettlementSeconds = timer.Elapsed.TotalSeconds;
        _packet = new("terminal_settled", null, [], _bridge?.CaptureEvidence());
        _next.TrySetResult(_packet);
        // Synchronous notification is deliberately terminal. In particular the
        // driver cannot consume the first postcombat reward or event decision.
        throw new NativeSettlementReached();
    }

    private static IEnumerable<PermanentChange> Opportunities(IEnumerable<RewardsSet> sets, string prefix, bool includeExtras)
    {
        IEnumerable<Reward> Rewards(RewardsSet set)
        {
            if (set.Gold.Amount > 0) yield return set.Gold;
            if (set.Potion is not null) yield return set.Potion;
            if (set.Relic is not null) yield return set.Relic;
            if (!set.Card.IsResolved) yield return set.Card;
            if (includeExtras) foreach (var extra in set.ExtraRewards) yield return extra;
        }
        return sets.SelectMany(Rewards).GroupBy(reward => reward.GetType().Name, StringComparer.Ordinal)
            .Select(group => new PermanentChange(prefix + group.Key, group.Count(),
                "actual native reward offers at settlement; option identities not inspected"));
    }

    private static InventoryQuantity[] Inventory(IEnumerable<string?> potions) => potions.Where(p => p is not null)
        .GroupBy(p => p!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new InventoryQuantity(g.Key, g.Count())).ToArray();

    public DecisionPacket Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null) ExceptionDispatchInfo.Capture(_fault).Throw();
        if (_stepping || _packet is null) throw new InvalidOperationException("No paused native decision boundary");
        return Copy(_packet);
    }

    public async Task<DecisionPacket> StepAsync(PublicAction action)
    {
        var current = Observe();
        if (_pendingAction is null || !current.Actions.Any(a => PublicJson.Serialize(a) == PublicJson.Serialize(action)))
            throw new ArgumentException("Illegal or stale public action token", nameof(action));
        var signal = _pendingAction; _pendingAction = null;
        _actions.Add(Copy(action));
        _next = NewSignal(); _stepping = true;
        try
        {
            signal.SetResult(Copy(action));
            // Lifetime cancellation may finish the driver between Observe and
            // replacement of the completed boundary signal. The token prevents
            // that race from leaving this new signal waiting forever.
            var next = await _next.Task.WaitAsync(_lifetime.Token)
                ?? throw new InvalidOperationException("Selected native continuation unexpectedly ended");
            if (next.Status == "terminal_settled")
            {
                await _execution;
                if (_fault is not null) ExceptionDispatchInfo.Capture(_fault).Throw();
            }
            _packets.Add(PublicJson.Serialize(next));
            return Copy(next);
        }
        finally { _stepping = false; }
    }

    public async Task<ITeacherWorld> ForkForContinuationAsync()
    {
        _ = Observe();
        var fork = await OpenCoreAsync(_options, _proposalSeed, _selectedSlot, _callerToken, _constructedLifecycle,
            _labelTape?.ReplayCopy(), _selectedCombat)
            ?? throw new InvalidOperationException("Native replay lost its previously reached proposal slot");
        try
        {
            for (int i = 0; i < _packets.Count; i++)
            {
                if (PublicJson.Serialize(fork.Observe()) != _packets[i])
                    throw new InvalidOperationException($"Native proposal replay public packet diverged at owned prefix {i}");
                if (i < _actions.Count) await fork.StepAsync(_actions[i]);
            }
            return fork;
        }
        catch { await fork.DisposeAsync(); throw; }
    }

    public Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn)
    {
        _ = Observe();
        var outcome = _outcome ?? throw new InvalidOperationException("Native combat has not settled");
        return Task.FromResult(Copy(outcome with { ContinuationPolicyId = policyId, PlayerTurnsElapsed = lastPlayerTurn }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _execution;
        _lifetime.Dispose();
    }

    private sealed class NativeSourceHorizonReached : Exception;
    private sealed class NativeSelectedCombatHasNoRoot : Exception;
    private sealed class NativeSettlementReached : Exception;
}
