using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Reporting;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NaturalSourceTests
{
    [Fact]
    public async Task NaturalRootsComeFromNativeProgressAndPolicyReceivesOnlyDetachedPublicData()
    {
        const string seedPrefix = "natural-source-private-seed-regression";
        var policy = new InspectingPolicy(seedPrefix);
        var result = await NaturalSourceCollector.CollectAsync(new(Runs: 2, MaxFloors: 8,
            MaxRoots: 80, MaxRootsPerCombat: 4, SeedPrefix: seedPrefix), policy);
        Assert.NotEmpty(result.Roots);
        Assert.All(result.Runs, r => Assert.Null(r.Error));
        Assert.True(result.Runs.Sum(r => r.FloorsResolved) >= 2);
        Assert.Contains(result.Runs.SelectMany(r => r.Trace), t => t.Kind == "event_choice");
        Assert.Contains(result.Runs.SelectMany(r => r.Trace), t => t.Kind == "map_choice");
        Assert.Contains(result.Runs.SelectMany(r => r.Trace), t => t.Kind == "combat_resolved");
        Assert.Contains(result.Runs.SelectMany(r => r.Trace), t => t.Kind == "reward_choice");
        Assert.True(result.Roots.Select(r => r.SourceCombatId).Distinct().Count() >= 2);
        Assert.Equal(result.Roots.Length, result.NaturalRawRoots);
        Assert.Equal(0, result.NaturalLabeledRoots);
        Assert.Equal(0, result.SourceDistribution["constructed"]);
        Assert.Equal(result.Roots.Length, result.UnsupportedPosteriorReasons[NaturalSourceRoot.PosteriorReason]);
        Assert.True(policy.Calls >= result.Roots.Length);
        Assert.All(result.Roots, root =>
        {
            Assert.NotEmpty(root.PermanentDeck);
            Assert.True(root.Floor >= 2);
            Assert.Contains(root.SourceTrace, t => t.Kind == "combat_entry");
            Assert.Equal(root.StartHp, root.PublicRoot.Observation!.StartHp);
            Assert.Equal(10, root.PublicRoot.Observation.Ascension);
            Assert.Contains(root.PublicRoot.Observation.History, e => e.Kind == "combat_started");
            Assert.Contains(root.PublicRoot.Observation.History, e => e.Kind == "draw");
            using var exported = JsonDocument.Parse(PublicJson.Serialize(root.ToSourceRecord()));
            var input = exported.RootElement.GetProperty("public_input").GetRawText();
            Assert.DoesNotContain(seedPrefix, input);
            Assert.DoesNotContain("POLICY_MUTATION_SENTINEL", input);
            Assert.Equal("nosl.natural-source.v2", exported.RootElement.GetProperty("schema_version").GetString());
            Assert.DoesNotContain("sourceRun", input);
            Assert.DoesNotContain("posterior", input);
            var audit = exported.RootElement.GetProperty("audit_only");
            Assert.Equal("natural", audit.GetProperty("source_kind").GetString());
            Assert.Equal(root.ActualSeed, audit.GetProperty("actual_seed").GetString());
            Assert.Equal(root.SourceCombatId + "/native-root-family", audit.GetProperty("branch_family").GetString());
            Assert.False(audit.GetProperty("posterior_supported").GetBoolean());
            Assert.Equal("not_evaluated", audit.GetProperty("posterior_evaluation").GetString());
            Assert.Equal("native_posterior_not_evaluated_by_raw_collector", audit.GetProperty("posterior_reason").GetString());
            Assert.False(audit.GetProperty("trainable").GetBoolean());
            Assert.Equal("raw_unlabeled", audit.GetProperty("label_status").GetString());
            Assert.Empty(exported.RootElement.GetProperty("targets").GetProperty("actions").EnumerateArray());
        });
    }

    [Fact]
    public async Task ReadOnlyObserverPreservesNativeResultRecorderAndRandomStreams()
    {
        if (!ModelDb.Contains(typeof(Silent))) ModelDb.Init(ContentRegistry.AllTypes);
        async Task<(string Manifest, string Logs, string Random, int PublicEvents, int HpEvents, bool HpComplete)> Run(bool decorate)
        {
            var state = new RunState("natural-native-observer-invariance", ascensionLevel: 10);
            state.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), state));
            var recorder = new RunRecorder(() => DateTimeOffset.UnixEpoch);
            var driver = new RunDriver(state, new EndTurnSource(), recorder: recorder);
            PublicKnowledge? knowledge = null;
            if (decorate) ForwardingPublicObserver.Install(driver, state.Players.Single(), () => knowledge = new PublicKnowledge());
            await driver.RunAsync(4);
            return (PublicJson.Serialize(recorder.BuildManifest()), PublicJson.Serialize(recorder.CombatLogs),
                JsonSerializer.Serialize(state.Rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true }),
                knowledge?.Events.Count ?? 0, knowledge?.OutcomeLedger.HpEvents.Count ?? 0, knowledge?.OutcomeLedger.HpComplete ?? false);
        }
        var plain = await Run(false); var observed = await Run(true);
        Assert.Equal(plain.Manifest, observed.Manifest);
        Assert.Equal(plain.Logs, observed.Logs);
        Assert.Equal(plain.Random, observed.Random);
        Assert.True(observed.PublicEvents > 0);
        Assert.True(observed.HpEvents > 0);
        Assert.True(observed.HpComplete);
    }

    [Fact]
    public async Task CollectionLimitsKeepReachedRootsUnlabeledAndNeverInventTerminalOutcomes()
    {
        var result = await NaturalSourceCollector.CollectAsync(new(Runs: 3, MaxRoots: 2,
            MaxRootsPerCombat: 8, SeedPrefix: "natural-bound-check"));
        Assert.Equal(2, result.Roots.Length);
        var run = Assert.Single(result.Runs);
        Assert.Equal("root_limit_reached", run.Outcome);
        Assert.Null(run.Error);
        Assert.Equal(0, result.NaturalLabeledRoots);
        Assert.DoesNotContain(run.Trace, t => t.Kind == "run_ended");
    }

    [Fact]
    public async Task PrivateNativeCallbackPausesDriverAndPreservesPreEntryAssets()
    {
        int callbacks = 0;
        var result = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(
            new(MaxRoots: 12, MaxRootsPerCombat: 12, SeedPrefix: "natural-private-callback"), async (root, boundary) =>
            {
                callbacks++;
                Assert.IsType<RunState>(boundary.State.RunState);
                Assert.Same(boundary.Room, boundary.State.RunState.CurrentRoom);
                Assert.True(boundary.HistoryComplete);
                Assert.Equal(root.PublicRoot.Status == "player_decision", boundary.IsStable);
                Assert.Equal(root.StartHp, boundary.StartHp);
                Assert.Equal(root.StartMaxHp, boundary.StartMaxHp);
                Assert.Equal(root.StartGold, boundary.InitialAssets.Gold);
                Assert.Equal(root.PermanentDeck.Select(PublicJson.Serialize).Order(StringComparer.Ordinal), boundary.InitialAssets.Deck);
                var observed = PublicViews.Observe(boundary.State, boundary.Knowledge, boundary.StartHp,
                    root.PublicRoot.Observation!.Choice, boundary.StartGold);
                Assert.Equal(PublicJson.Serialize(root.PublicRoot.Observation), PublicJson.Serialize(observed));
                await Task.Yield();
                Assert.Equal(PublicJson.Serialize(observed), PublicJson.Serialize(PublicViews.Observe(boundary.State,
                    boundary.Knowledge, boundary.StartHp, root.PublicRoot.Observation.Choice, boundary.StartGold)));
                boundary.Knowledge.Events.Add(new("PRIVATE_COPY_PROBE", ""));
            });
        Assert.Equal(result.Roots.Length, callbacks);
        Assert.NotEmpty(result.Roots);
        Assert.All(result.Runs, r => Assert.Null(r.Error));
        Assert.All(result.Roots, r => Assert.DoesNotContain(r.PublicRoot.Observation!.History, e => e.Kind == "PRIVATE_COPY_PROBE"));
    }

    [Fact]
    public async Task HiddenSelectionOrderIsCanonicalAndOrderedChoiceTokensStayComplete()
    {
        await using var session = await CombatSession.CreateAsync();
        var player = session.State.Players.Single();
        var cards = player.PlayerCombatState!.DrawPile.Cards.ToArray();
        Assert.True(cards.Length > 2);
        var request = new CardSelectionRequest(player, cards, 1, 2, null, true,
            cards.Select(c => (IReadOnlyList<Sts2Sim.Core.Models.CardModel>)new[] { c }).ToArray());
        var a = NaturalSourceCollector.CanonicalizeSelection(request);
        var b = NaturalSourceCollector.CanonicalizeSelection(request with
        { Candidates = request.Candidates.Reverse().ToArray(), Bundles = request.Bundles!.Reverse().ToArray() });
        Assert.Equal(a.Candidates.Select(c => PublicJson.Serialize(PublicViews.Card(c))),
            b.Candidates.Select(c => PublicJson.Serialize(PublicViews.Card(c))));
        for (int i = 0; i < a.Candidates.Count; i++) Assert.Same(a.Candidates[i], a.Bundles![i][0]);
        var actions = NaturalSourceCollector.ChoiceActions(3, 1, 2, true, 4);
        Assert.Equal(10, actions.Length); // Cancel, three single selections, six ordered pairs.
        Assert.Contains(actions, x => x.Selection!.SequenceEqual(new[] { 0, 1 }));
        Assert.Contains(actions, x => x.Selection!.SequenceEqual(new[] { 1, 0 }));
        Assert.All(actions, a => Assert.Equal(4, a.Revision));
    }

    private sealed class InspectingPolicy(string privateSeed) : IPublicContinuationPolicy
    {
        public string Id => "natural-nosl-input-test";
        public int Calls;
        public PublicAction Choose(DecisionPacket packet)
        {
            Calls++;
            string json = PublicJson.Serialize(packet);
            Assert.DoesNotContain(privateSeed, json);
            Assert.DoesNotContain("actualSeed", json);
            Assert.DoesNotContain("rng", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nextMove", json, StringComparison.OrdinalIgnoreCase);
            var action = new PublicRulePolicy().Choose(packet);
            // Mutating the received arrays cannot mutate the stored root or native hand.
            if (packet.Observation!.Hand.Length > 0)
                packet.Observation.Hand[0] = packet.Observation.Hand[0] with { Id = "POLICY_MUTATION_SENTINEL" };
            return action;
        }
    }
    private sealed class EndTurnSource : IRunDecisionSource
    {
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) =>
            Task.FromResult(options.OrderBy(p => p.coord.col).ThenBy(p => p.coord.row).First());
        public Task<CombatDecision> ChooseCombatActionAsync(CombatState state) =>
            Task.FromResult<CombatDecision>(new CombatDecision.EndTurn());
    }
}
