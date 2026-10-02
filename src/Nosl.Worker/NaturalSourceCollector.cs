using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Reporting;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

public sealed record NaturalSourceOptions(int Runs = 1, int MaxFloors = 20, int MaxRoots = 200,
    int MaxRootsPerCombat = 8, int DecisionStride = 1, int MaxDecisionsPerRun = 10000,
    string SeedPrefix = "nosl-natural-v1", string SourceRunPrefix = "natural",
    string ContinuationPolicyId = PublicContinuationPolicies.LegacyId);
public sealed record NaturalSourceTrace(int Index, string Kind, string PublicDetail);
public sealed record NaturalRunAudit(string SourceRunGroup, string ActualSeed, string Outcome,
    int FloorsResolved, int CombatsEntered, int Decisions, int RootsCollected,
    int FinalHp, string? Error, NaturalSourceTrace[] Trace);

/// <summary>A native public root, not a teacher label or a reconstructed Scenario.</summary>
public sealed record NaturalSourceRoot(DecisionPacket PublicRoot, string SourceRunGroup, string SourceCombatId,
    int DecisionIndex, int Act, int Floor, string RoomType, string Encounter, string ActualSeed,
    int StartHp, int StartMaxHp, int StartGold, PublicCard[] PermanentDeck,
    NaturalSourceTrace[] SourceTrace, string CombatPolicy)
{
    public const string PosteriorReason = "native_posterior_not_evaluated_by_raw_collector";
    public object ToSourceRecord()
    {
        var input = new
        {
            schema_version = "nosl.student.public.v2", observation = PublicRoot.Observation,
            history_complete = true, controller_context = new { status = "inactive" },
            candidate_actions = PublicRoot.Actions, legal_mask = PublicRoot.Actions.Select(_ => true).ToArray(),
        };
        return new
        {
            schema_version = "nosl.natural-source.v2", record_kind = "natural_raw_source_candidate",
            public_input = input,
            targets = new { actions = Array.Empty<object>(), pairwise = Array.Empty<object>(), equivalent_action_set = Array.Empty<int>() },
            audit_only = new
            {
                source_kind = "natural", source_run_group = SourceRunGroup, source_combat_id = SourceCombatId,
                branch_family = SourceCombatId + "/native-root-family", source_prior = "native_sequential_run_silent_a10_public_entry_v2",
                outside_combat_script = NaturalSourceCollector.ScriptVersion, combat_policy = CombatPolicy,
                actual_seed = ActualSeed, decision_index = DecisionIndex, act = Act, floor = Floor,
                room_type = RoomType, encounter = Encounter, start_hp = StartHp, start_max_hp = StartMaxHp,
                start_gold = StartGold, permanent_deck = PermanentDeck, source_trace = SourceTrace,
                public_state_digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(input)))).ToLowerInvariant(),
                native_run = true, native_default_start = true, history_from_room_entry = true,
                constructed_hp_override = false, constructed_deck_override = false,
                posterior_supported = false, posterior_evaluation = "not_evaluated", posterior_reason = PosteriorReason,
                label_status = "raw_unlabeled", trainable = false, teacher_label_count = 0,
                simulator_commit = "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0", rules_version = "0.111.0",
            },
        };
    }
}
// Worker-private adapter input, never serialized or passed to a policy. The
// driver is paused at this boundary; consumers must clone, never mutate it.
internal sealed record NaturalSourceBoundary(CombatState State, CombatRoom Room, PublicKnowledge Knowledge,
    int StartHp, int StartMaxHp, int StartGold, string?[] StartPotions, CombatAssetSnapshot InitialAssets,
    int Revision, bool IsStable, bool HistoryComplete,
    NativeChoiceReplay? ChoiceReplay = null, string? ChoiceReplayRejection = null);
// Borrowed only for the duration of the boundary callback. Imports fork the owned
// stable origin and re-execute; no suspended source objects enter their graph.
internal sealed record NativeChoiceReplay(CombatSession Origin, PublicAction[] Actions, string[] Packets);
internal sealed record NaturalSourceSettlement(string SourceCombatId, int Hp, CombatAssetSnapshot Assets);

public sealed record NaturalSourceReport(NaturalSourceRoot[] Roots, NaturalRunAudit[] Runs,
    IReadOnlyDictionary<string, int> SourceDistribution, IReadOnlyDictionary<string, int> EncounterDistribution,
    IReadOnlyDictionary<string, int> UnsupportedPosteriorReasons, int NaturalRawRoots, int NaturalLabeledRoots,
    string Status);

/// <summary>
/// Runs the unmodified native startup, maps, events, rewards and combat rules. The
/// decision bridge alone owns native references; the combat policy sees DTOs only.
/// Natural carry-in posterior inference is deliberately not claimed here.
/// </summary>
public static class NaturalSourceCollector
{
    public const string ScriptVersion = "nosl-natural-public-script-v2";
    private static readonly object InitLock = new();

    public static Task<NaturalSourceReport> CollectAsync(NaturalSourceOptions? options = null,
        IPublicContinuationPolicy? policy = null, CancellationToken cancellationToken = default) =>
        CollectCoreAsync(options, policy, null, null, cancellationToken);

    internal static Task<NaturalSourceReport> CollectWithNativeBoundaryAsync(NaturalSourceOptions options,
        Func<NaturalSourceRoot, NaturalSourceBoundary, Task> onRoot, IPublicContinuationPolicy? policy = null,
        CancellationToken cancellationToken = default, Action<NaturalSourceSettlement>? onSettlement = null) =>
        CollectCoreAsync(options, policy, onRoot, onSettlement, cancellationToken);

    private static async Task<NaturalSourceReport> CollectCoreAsync(NaturalSourceOptions? options,
        IPublicContinuationPolicy? policy, Func<NaturalSourceRoot, NaturalSourceBoundary, Task>? onRoot,
        Action<NaturalSourceSettlement>? onSettlement, CancellationToken cancellationToken)
    {
        options ??= new();
        var configuredPolicy = PublicContinuationPolicies.Create(options.ContinuationPolicyId);
        policy ??= configuredPolicy;
        if (options.Runs <= 0 || options.MaxFloors <= 0 || options.MaxRoots <= 0 ||
            options.MaxRootsPerCombat <= 0 || options.DecisionStride <= 0 || options.MaxDecisionsPerRun <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Collection limits must be positive");
        if (string.IsNullOrWhiteSpace(options.SeedPrefix) || string.IsNullOrWhiteSpace(options.SourceRunPrefix))
            throw new ArgumentException("Source seed and grouping prefixes are required");
        lock (InitLock) { if (!ModelDb.Contains(typeof(Silent))) ModelDb.Init(ContentRegistry.AllTypes); }
        var roots = new List<NaturalSourceRoot>(); var audits = new List<NaturalRunAudit>();
        for (int index = 0; index < options.Runs && roots.Count < options.MaxRoots; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string seed = $"{options.SeedPrefix}:{index}";
            string group = $"{options.SourceRunPrefix}-{index:D6}";
            // Ordinary constructor: actual sequential RNG, real randomized acts/map,
            // native character startup. No map, HP, inventory or deck replacement.
            var run = new RunState(seed, ascensionLevel: 10);
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
            run.AddPlayer(player);
            var bridge = new SourceBridge(run, options, policy, roots, group, seed, onRoot, onSettlement, cancellationToken);
            var driver = new RunDriver(run, bridge, recorder: bridge, useAvailablePotions: false)
                { CombatObserverDecorator = bridge.Decorate, AutomaticCombatSettlementCompleted = bridge.CompleteOutcome };
            driver.OnRoomResolved += (_, _) => bridge.FloorsResolved++;
            string outcome; string? error = null;
            try { outcome = (await driver.RunAsync(options.MaxFloors)).Outcome.ToString(); }
            catch (CollectionBoundReached e) { outcome = e.Message; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) { outcome = "source_error"; error = $"{e.GetType().Name}: {e.Message}"; }
            finally { bridge.DetachOutcome(); await bridge.ReleaseChoiceOriginAsync(); }
            audits.Add(new(group, seed, outcome, bridge.FloorsResolved, bridge.CombatsEntered,
                bridge.Decisions, bridge.RootsCollected, player.Creature.CurrentHp, error, bridge.Trace.ToArray()));
        }
        return new(roots.ToArray(), audits.ToArray(), new Dictionary<string, int> { ["natural"] = roots.Count, ["constructed"] = 0 },
            roots.GroupBy(r => r.Encounter).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            roots.Count == 0 ? new Dictionary<string, int>() : new() { [NaturalSourceRoot.PosteriorReason] = roots.Count },
            roots.Count, 0, audits.Any(a => a.Error is not null) ? "collected_with_source_errors" : "raw_roots_collected_no_teacher_labels");
    }

    private sealed class CollectionBoundReached(string reason) : Exception(reason);

    private sealed class SourceBridge(RunState run, NaturalSourceOptions options, IPublicContinuationPolicy policy,
        List<NaturalSourceRoot> roots, string sourceRun, string actualSeed,
        Func<NaturalSourceRoot, NaturalSourceBoundary, Task>? onRoot,
        Action<NaturalSourceSettlement>? onSettlement, CancellationToken cancellationToken)
        : IRunDecisionSource, IAutomaticCardSelectionObserver, IRunRecorder
    {
        internal readonly List<NaturalSourceTrace> Trace = [];
        internal int FloorsResolved, CombatsEntered, Decisions, RootsCollected;
        private PublicKnowledge _knowledge = new();
        private int _revision, _combatDecision, _combatRoots, _startHp, _startMaxHp, _startGold;
        private PublicCard[] _permanentDeck = [];
        private string?[] _startPotions = [];
        private CombatAssetSnapshot _initialAssets = null!;
        private bool _settlementReported;
        private CombatSession? _choiceOrigin;
        private readonly List<PublicAction> _choiceActions = [];
        private readonly List<string> _choicePackets = [];
        private string? _choiceRejection;
        internal async ValueTask ReleaseChoiceOriginAsync()
        {
            var origin = _choiceOrigin; _choiceOrigin = null;
            _choiceActions.Clear(); _choicePackets.Clear(); _choiceRejection = null;
            if (origin is not null) await origin.DisposeAsync();
        }
        private void Log(string kind, object detail) => Trace.Add(new(Trace.Count, kind, PublicJson.Serialize(detail)));
        internal ICombatObserver Decorate(ICombatObserver original)
        {
            var player = run.Players.Single();
            _knowledge.OutcomeLedger.Detach();
            _knowledge = new(); _revision = _combatDecision = _combatRoots = 0; CombatsEntered++;
            _settlementReported = false;
            _startHp = player.Creature.CurrentHp; _startMaxHp = player.Creature.MaxHp; _startGold = player.Gold;
            _startPotions = player.PotionSlots.Select(p => p?.GetType().Name).ToArray();
            _initialAssets = CombatAssetSnapshot.Capture(player);
            _knowledge.OutcomeLedger.Begin(player);
            _knowledge.BeginCombat();
            _knowledge.Events.Add(new(NativeEntryAssets.EventKind,
                PublicJson.Serialize(NativeEntryAssets.Capture(_initialAssets, _startHp, _startPotions))));
            _permanentDeck = player.Deck.Cards.Select(PublicViews.Card).OrderBy(PublicJson.Serialize, StringComparer.Ordinal).ToArray();
            Log("combat_entry", new { combat = CombatsEntered, act = run.CurrentActIndex, floor = run.TotalFloor,
                hp = _startHp, maxHp = _startMaxHp, gold = _startGold, deck = _permanentDeck,
                relics = player.Relics.Select(r => r.GetType().Name).ToArray(), potions = player.PotionSlots.Select(p => p?.GetType().Name).ToArray() });
            return new ForwardingPublicObserver(original, _knowledge);
        }
        internal void CompleteOutcome()
        {
            var player = run.Players.Single();
            _knowledge.OutcomeLedger.Seal(player);
            // The native driver invokes this after automatic settlement and before
            // reward decisions, including owner return for no-reward forced fights.
            if (_settlementReported) return;
            _settlementReported = true;
            onSettlement?.Invoke(new($"{sourceRun}/combat-{CombatsEntered:D4}",
                player.Creature.CurrentHp, CombatAssetSnapshot.Capture(player)));
        }
        internal void DetachOutcome() => _knowledge.OutcomeLedger.Detach();
        private void CheckBound()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (roots.Count >= options.MaxRoots) throw new CollectionBoundReached("root_limit_reached");
            if (Decisions >= options.MaxDecisionsPerRun) throw new CollectionBoundReached("decision_limit_reached");
            Decisions++;
        }
        private async Task<PublicAction> DecideAsync(CombatState state, PublicAction[] actions, PublicChoice? choice = null)
        {
            CheckBound();
            var packet = new DecisionPacket(choice is null ? "player_decision" : "card_choice",
                PublicViews.Observe(state, _knowledge, _startHp, choice, _startGold), actions);
            if (choice is null) await ReleaseChoiceOriginAsync();
            else if (_choiceOrigin is not null) _choicePackets.Add(PublicJson.Serialize(packet));
            var room = (CombatRoom)run.CurrentRoom!;
            NaturalSourceRoot SnapshotRoot() => new(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet)),
                sourceRun, $"{sourceRun}/combat-{CombatsEntered:D4}", _combatDecision,
                run.CurrentActIndex, run.TotalFloor, room.RoomType.ToString(), room.EncounterName, actualSeed,
                _startHp, _startMaxHp, _startGold, _permanentDeck.ToArray(), Trace.ToArray(), policy.Id);
            NaturalSourceBoundary Boundary() => new(state, room, _knowledge.Copy(), _startHp, _startMaxHp, _startGold,
                _startPotions.ToArray(), _initialAssets, _revision, choice is null, true,
                _choiceOrigin is null ? null : new(_choiceOrigin,
                    _choiceActions.Select(a => a with { Selection = a.Selection?.ToArray() }).ToArray(), _choicePackets.ToArray()),
                _choiceRejection);
            if (_combatDecision % options.DecisionStride == 0 && _combatRoots < options.MaxRootsPerCombat)
            {
                // Detach all policy DTOs, including mutable event lists, from native state.
                var root = SnapshotRoot();
                roots.Add(root); _combatRoots++; RootsCollected++;
                if (onRoot is not null)
                    await onRoot(root, Boundary());
            }
            // JSON boundary prevents a policy from receiving or mutating native objects.
            var selected = policy.Choose(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet)));
            string token = PublicJson.Serialize(selected);
            var valid = actions.SingleOrDefault(a => PublicJson.Serialize(a) == token)
                ?? throw new InvalidOperationException("Natural policy returned an illegal public action");
            if (onRoot is not null && choice is null && CombatSession.IsReviewedChoiceAction(valid, packet))
            {
                try
                {
                    _choiceOrigin = CombatSession.ImportNative(SnapshotRoot(), Boundary());
                    _choicePackets.Add(PublicJson.Serialize(packet));
                }
                catch (NotSupportedException e) { _choiceRejection = e.Message; }
            }
            if (_choiceOrigin is not null) _choiceActions.Add(valid with { Selection = valid.Selection?.ToArray() });
            _knowledge.Events.Add(new("action", token));
            Log("combat_action", new { combat = CombatsEntered, decision = _combatDecision, action = valid });
            _revision++; _combatDecision++;
            return valid;
        }
        private IEnumerable<Creature?> Targets(TargetType type, CombatState state, Player player) => type switch
        {
            TargetType.AnyEnemy => state.HittableEnemies.Cast<Creature?>(),
            TargetType.Self or TargetType.AnyPlayer => [player.Creature],
            TargetType.AnyAlly => state.Allies.Where(c => c.IsPlayer && c.IsAlive && !ReferenceEquals(c, player.Creature)).Cast<Creature?>(),
            TargetType.None or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies or TargetType.TargetedNoCreature => [null],
            _ => throw new NotSupportedException($"Unsupported natural target mechanism: {type}"),
        };
        public async Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
        {
            var p = state.Players.Single(); var hand = p.PlayerCombatState!.Hand.Cards;
            var bindings = new List<(PublicAction Action, CombatDecision? Decision)>();
            int Slot(Creature? c) => c is null ? -1 : _knowledge.Slot(c);
            for (int i = 0; i < hand.Count; i++)
                if (hand[i].CanPlay(out _))
                    foreach (var target in Targets(hand[i].TargetType, state, p))
                        bindings.Add((new(_revision, "play", i, Slot(target)), new CombatDecision.PlayCard(hand[i], hand[i].TargetType == TargetType.Self ? null : target)));
            for (int i = 0; i < p.PotionSlots.Count; i++)
                if (p.PotionSlots[i] is { } potion)
                {
                    foreach (var target in Targets(potion.TargetType, state, p))
                        if (PotionCmd.CanUseManually(state, p, potion, target))
                            bindings.Add((new(_revision, "potion", i, Slot(target)), new CombatDecision.UsePotion(potion, target)));
                    if (p.CanUseOrRemovePotions) bindings.Add((new(_revision, "discard_potion", i), null));
                }
            bindings.Add((new(_revision, "end_turn"), new CombatDecision.EndTurn()));
            var selected = await DecideAsync(state, bindings.Select(x => x.Action).ToArray());
            if (selected.Kind == "discard_potion")
            {
                // The native RunDriver union has no discard token. Execute the actual
                // native command, then ask again at its new stable public boundary.
                await PotionCmd.Discard(p.PotionSlots[selected.Slot]!);
                return await ChooseCombatActionAsync(state);
            }
            return bindings.Single(x => x.Action == selected).Decision!;
        }
        public async Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            // Out-of-combat selections are public inventory/revealed option choices;
            // sort their visible signatures, never their actual hidden pile order.
            bool unordered = request.Candidates.Any(c => c.Pile?.Type is PileType.Draw or PileType.Deck);
            request = CanonicalizeSelection(request);
            var candidates = request.Candidates.ToArray();
            if (run.CurrentRoom is not CombatRoom room || room.Engine is null)
            {
                CheckBound();
                var selected = candidates.Take(request.MinCount).ToArray();
                Log("outside_card_choice", new { source = request.Source?.GetType().Name, min = request.MinCount,
                    max = request.MaxCount, selected = selected.Select(PublicViews.Card).ToArray() });
                return selected;
            }
            _knowledge.Reveal(candidates);
            var choice = new PublicChoice(request.Source?.GetType().Name ?? "unknown", request.MinCount,
                request.MaxCount, request.Cancelable, candidates.Select(PublicViews.Card).ToArray(),
                unordered ? "canonical_unordered_reveal" : "public",
                request.Bundles?.Select(b => b.Select(PublicViews.Card).ToArray()).ToArray());
            _knowledge.Events.Add(new("choice", PublicJson.Serialize(choice)));
            var actions = ChoiceActions(candidates.Length, request.MinCount, request.MaxCount, request.Cancelable, _revision);
            var action = await DecideAsync(room.Engine.State, actions, choice);
            return action.Selection!.Select(i => candidates[i]).ToArray();
        }
        public void ObserveAutomaticSelection(CardSelectionRequest request, IReadOnlyList<CardModel> selected)
        {
            if (run.CurrentRoom is not CombatRoom) return;
            bool unordered = selected.Any(c => c.Pile?.Type is PileType.Draw or PileType.Deck);
            var cards = selected.Where(c => !_knowledge.IsUnrevealed(c)).Select(PublicViews.Card);
            if (unordered) cards = cards.OrderBy(PublicJson.Serialize, StringComparer.Ordinal);
            _knowledge.Events.Add(new("automatic_selection", PublicJson.Serialize(new {
                source = request.Source?.GetType().Name, cards = cards.ToArray(), unidentifiedCount = selected.Count(_knowledge.IsUnrevealed) })));
        }
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> choices)
        {
            CheckBound();
            bool hurt = run.Players[0].Creature.CurrentHp * 2 < run.Players[0].Creature.MaxHp;
            int Priority(MapPoint p) => p.PointType switch {
                MapPointType.RestSite when hurt => -1, MapPointType.Monster => 0,
                MapPointType.Treasure => 1, MapPointType.RestSite => 2, MapPointType.Unknown => 3,
                MapPointType.Shop => 4, MapPointType.Elite => 5, _ => 6 };
            var choice = choices.OrderBy(Priority).ThenBy(p => p.coord.col).ThenBy(p => p.coord.row).First();
            Log("map_choice", new { options = choices.Select(p => new { col = p.coord.col, row = p.coord.row, type = p.PointType.ToString() }).ToArray(),
                chosen = new { col = choice.coord.col, row = choice.coord.row, type = choice.PointType.ToString() } });
            return Task.FromResult(choice);
        }
        public Task<EventOption> ChooseEventOptionAsync(IReadOnlyList<EventOption> choices)
        {
            CheckBound(); var choice = choices.First(o => !o.IsLocked);
            Log("event_choice", new { options = choices.Where(o => !o.IsLocked).Select(o => o.Key).ToArray(), chosen = choice.Key });
            return Task.FromResult(choice);
        }
        public Task<CustomEventDecision> ChooseCustomEventActionAsync(EventModel @event)
        {
            CheckBound(); var choice = CustomEventDecisionPolicy.ChooseDefault(@event);
            Log("custom_event_choice", new { @event = @event.GetType().Name, choice }); return Task.FromResult(choice);
        }
        public Task<RewardDecision> ChooseRewardActionAsync(RewardsSet rewards)
        {
            CheckBound(); var choice = RewardDecisionClassifier.ChooseDefault(rewards);
            Log("reward_choice", new { kind = choice.GetType().Name,
                card = choice is RewardDecision.TakeCard card ? PublicViews.Card(card.Card) : null });
            return Task.FromResult(choice);
        }
        public Task<ShopDecision> ChooseShopActionAsync(MerchantInventory inventory, Player player)
        { CheckBound(); Log("shop_choice", "leave"); return Task.FromResult<ShopDecision>(new ShopDecision.Leave()); }
        public Task<RestSiteDecision> ChooseRestSiteActionAsync(Player player, IReadOnlyList<RestSiteDecision> choices)
        {
            CheckBound();
            var choice = player.Creature.CurrentHp * 2 < player.Creature.MaxHp
                ? choices.FirstOrDefault(c => c is RestSiteDecision.Heal && c.IsEnabled) : null;
            choice ??= RestSiteDecisionPolicy.ChooseDefault(choices);
            Log("rest_choice", new { choice = choice.OptionId, card = choice is RestSiteDecision.Smith smith ? PublicViews.Card(smith.Card) : null });
            return Task.FromResult(choice);
        }
        public void BeginRun(RunState state) => Log("run_started", new { character = "Silent", ascension = 10,
            startup = "native_default", acts = state.Acts.Select(a => a.GetType().Name).ToArray() });
        public void EnterFloor(MapPoint point, RoomType roomType) => Log("floor_entered", new { act = run.CurrentActIndex,
            floor = run.TotalFloor, col = point.coord.col, row = point.coord.row, room = roomType.ToString() });
        public string BeginCombat(RunState state, RoomType encounterType, string encounterName, CombatState combatState)
        { Log("combat_started", new { combat = CombatsEntered, encounter = encounterName, room = encounterType.ToString() }); return $"{sourceRun}/combat-{CombatsEntered:D4}"; }
        public void RecordFloorDetail(FloorDetail detail) => Log("floor_detail", detail);
        public void RecordTurnStart(CombatState state) { }
        public void RecordDraw(CardModel card) { }
        public void RecordCardPlay(CardPlay play, PlayerSnapshot before, PlayerSnapshot after, IReadOnlyList<EnemySnapshot> enemiesBefore, IReadOnlyList<EnemySnapshot> enemiesAfter) { }
        public void RecordPotionUse(PotionModel potion, Creature? target, PlayerSnapshot before, PlayerSnapshot after, IReadOnlyList<EnemySnapshot> enemiesBefore, IReadOnlyList<EnemySnapshot> enemiesAfter) { }
        public void RecordEnemyAction(Creature source, string moveId, IReadOnlyList<ActionSnapshotSegment> segments) { }
        public void RecordEndTurn(PlayerSnapshot player, IReadOnlyList<EnemySnapshot> enemies) { }
        public void EndCombat(bool victory, PlayerSnapshot player, IReadOnlyList<EnemySnapshot> enemies, CombatRewards rewards)
            => Log("combat_resolved", new { combat = CombatsEntered, victory, hp = run.Players[0].Creature.CurrentHp });
        public void ExitFloor() => Log("floor_exited", new { act = run.CurrentActIndex, floor = run.TotalFloor });
        public void EndRun(bool won, int floorsVisited, int finalHp, bool reachedBoss = false, bool? survived = null, bool truncated = false, int actsCleared = 0)
            => Log("run_ended", new { won, floorsVisited, finalHp, reachedBoss, survived, truncated, actsCleared });
        // Raw engine manifests/logs contain seed and diagnostic hidden snapshots. This
        // collector exports only explicitly selected audit metadata and public roots.
        public RunManifest BuildManifest() => throw new NotSupportedException("Raw native logs are not policy/dataset inputs");
        public IReadOnlyDictionary<string, CombatLog> CombatLogs => new Dictionary<string, CombatLog>();
    }

    internal static CardSelectionRequest CanonicalizeSelection(CardSelectionRequest request)
    {
        if (!request.Candidates.Any(c => c.Pile?.Type is PileType.Draw or PileType.Deck)) return request;
        var indices = Enumerable.Range(0, request.Candidates.Count).OrderBy(i => PublicJson.Serialize(new
        {
            card = PublicViews.Card(request.Candidates[i]),
            bundle = request.Bundles?[i].Select(PublicViews.Card).ToArray(),
        }), StringComparer.Ordinal).ToArray();
        return request with
        {
            Candidates = indices.Select(i => request.Candidates[i]).ToArray(),
            Bundles = request.Bundles is null ? null : indices.Select(i => request.Bundles[i]).ToArray(),
        };
    }

    internal static PublicAction[] ChoiceActions(int count, int min, int max, bool cancelable, int revision)
    {
        var result = new List<PublicAction>();
        void Add(int size)
        {
            var selection = new int[size]; var used = new bool[count];
            void Visit(int depth)
            {
                if (depth == size)
                {
                    if (result.Count >= 100000) throw new NotSupportedException("choice_action_space_exceeds_explicit_limit:100000");
                    result.Add(new(revision, "choose", Selection: selection.ToArray())); return;
                }
                for (int i = 0; i < count; i++) if (!used[i])
                { used[i] = true; selection[depth] = i; Visit(depth + 1); used[i] = false; }
            }
            Visit(0);
        }
        if (cancelable && min > 0) Add(0);
        for (int size = min; size <= max; size++) Add(size);
        return result.ToArray();
    }
}

/// <summary>Forwards every native notification, including default interface methods.</summary>
internal sealed class ForwardingPublicObserver(ICombatObserver original, ICombatObserver observer) : ICombatObserver
{
    internal static void Install(RunDriver driver, Player player, Func<PublicKnowledge> createKnowledge)
    {
        PublicKnowledge? current = null;
        driver.AutomaticCombatSettlementCompleted = () => current?.OutcomeLedger.Seal(player);
        driver.CombatObserverDecorator = original =>
        {
            current?.OutcomeLedger.Detach();
            var knowledge = current = createKnowledge();
            knowledge.OutcomeLedger.Begin(player); knowledge.BeginCombat();
            return new ForwardingPublicObserver(original, knowledge);
        };
    }
    public void CombatStarted(CombatState s) { original.CombatStarted(s); observer.CombatStarted(s); }
    public void PlayerTurnPrepared(CombatState s) { original.PlayerTurnPrepared(s); observer.PlayerTurnPrepared(s); }
    public void PlayerTurnStarted(CombatState s) { original.PlayerTurnStarted(s); observer.PlayerTurnStarted(s); }
    public void CardDrawn(CardModel c) { original.CardDrawn(c); observer.CardDrawn(c); }
    public void CardMoved(CardModel c, PileType? f, PileType t, CardPilePosition p) { original.CardMoved(c, f, t, p); observer.CardMoved(c, f, t, p); }
    public void CardsShuffled(Player p) { original.CardsShuffled(p); observer.CardsShuffled(p); }
    public void CardEnteredCombat(CardModel c, PileType t, CardPilePosition p) { original.CardEnteredCombat(c, t, p); observer.CardEnteredCombat(c, t, p); }
    public void CardPlayStarted(CardModel c, Creature? t) { original.CardPlayStarted(c, t); observer.CardPlayStarted(c, t); }
    public void CardPlayFinished(CardModel c, Creature? t, CardPlay? p) { original.CardPlayFinished(c, t, p); observer.CardPlayFinished(c, t, p); }
    public void CardPlayAborted(CardModel c, Creature? t) { original.CardPlayAborted(c, t); observer.CardPlayAborted(c, t); }
    public void EnemyTurnStarting(CombatState s) { original.EnemyTurnStarting(s); observer.EnemyTurnStarting(s); }
    public void EnemyMoveStarted(Creature s, string m) { original.EnemyMoveStarted(s, m); observer.EnemyMoveStarted(s, m); }
    public void EnemyMoveFinished(Creature s, string m) { original.EnemyMoveFinished(s, m); observer.EnemyMoveFinished(s, m); }
    public void EnemyMoveAborted(Creature s, string m) { original.EnemyMoveAborted(s, m); observer.EnemyMoveAborted(s, m); }
    public void DamageResolved(Creature? d, DamageResult r) { original.DamageResolved(d, r); observer.DamageResolved(d, r); }
    public void PowerAmountChanged(PowerModel p, decimal a, Creature? c, CardModel? s) { original.PowerAmountChanged(p, a, c, s); observer.PowerAmountChanged(p, a, c, s); }
    public void PowerDurationTick(PowerModel p) { original.PowerDurationTick(p); observer.PowerDurationTick(p); }
    public void SideTurnEndListener(string p, AbstractModel l, CombatSide s, bool b) { original.SideTurnEndListener(p, l, s, b); observer.SideTurnEndListener(p, l, s, b); }
    public void PlayerTurnEnded(CombatState s) { original.PlayerTurnEnded(s); observer.PlayerTurnEnded(s); }
    public void PotionUseStarted(PotionModel p, Creature? t) { original.PotionUseStarted(p, t); observer.PotionUseStarted(p, t); }
    public void PotionUseFinished(PotionModel p, Creature? t) { original.PotionUseFinished(p, t); observer.PotionUseFinished(p, t); }
    public void PotionUseAborted(PotionModel p, Creature? t) { original.PotionUseAborted(p, t); observer.PotionUseAborted(p, t); }
}
